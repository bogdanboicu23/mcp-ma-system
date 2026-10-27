using System.Text.Json.Nodes;
using Agents.Core.Execution;
using Agents.Core.Runs;
using Agents.Core.Swe;
using Agents.Core.Tools;
using Xunit;

namespace Agents.Core.Tests;

public class RunTests
{
    private const string Calc = "def add(a, b):\n    return a - b\n";

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mamcp-run-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class FailingProvider : ITaskEnvironmentProvider
    {
        public Task<ITaskEnvironment> StartAsync(SweBenchTask task, string runId, CancellationToken ct) =>
            throw new InvalidOperationException("docker is not running");
    }

    private static SweBenchTask NewTask(string id = "demo__repo-1", string repo = "demo/repo") => new()
    {
        InstanceId = id,
        Repo = repo,
        BaseCommit = "unused-in-local-runs",
        ProblemStatement = "add() subtracts instead of adding.",
        FailToPass = new[] { "tests/test_calc.py::test_add" },
        GoldPatch = "the secret reference fix",
    };

    private static ModelResponse Tool(string name, JsonObject input, Usage usage) => new(
        new ContentBlock[] { new ToolUseBlock("t" + Guid.NewGuid().ToString("N"), name, input) }, "tool_use", usage);

    private static ModelResponse Text(string text, Usage usage) =>
        new(new ContentBlock[] { new TextBlock(text) }, "end_turn", usage);

    private static ScriptedModelClient FixingModel() => new(new[]
    {
        Tool(ToolNames.ReadFile, new JsonObject { ["path"] = "calc.py" }, new Usage(1000, 50, 0, 0)),
        Tool(ToolNames.EditFile,
            new JsonObject { ["path"] = "calc.py", ["old_string"] = "a - b", ["new_string"] = "a + b" },
            new Usage(200, 40, 1000, 0)),
        Text("Fixed the operator.", new Usage(100, 20, 1240, 0)),
    });

    private static RunOrchestrator NewOrchestrator(
        IModelClient model, ITaskEnvironmentProvider environments, RunStore store, RunLimits? limits = null)
    {
        var prompts = PromptSet.FromTexts("system prompt", "Fix {{repo}}\n{{problem_statement}}");
        var lim = limits ?? new RunLimits();
        var system = new SingleAgentSystem("A", prompts, exec => new ContainerToolHost(exec), lim.MaxTurns);
        return new RunOrchestrator(model, system, environments, store, prompts, lim, "test-sha");
    }

    private static LocalEnvironmentProvider CalcRepo() =>
        new(_ => new Dictionary<string, string> { ["calc.py"] = Calc });

    [Fact]
    public async Task Orchestrator_runs_a_task_end_to_end_and_records_everything()
    {
        using var dir = new TempDir();
        var store = new RunStore(dir.Path);
        var model = FixingModel();

        var manifest = await NewOrchestrator(model, CalcRepo(), store).RunAsync(NewTask(), 1);

        Assert.Equal("completed", manifest.Status);
        Assert.Equal("end_turn", manifest.StopReason);
        Assert.Equal(3, manifest.Turns);
        Assert.Equal("A.demo__repo-1.r1", manifest.RunId);
        Assert.Equal("test-sha", manifest.HarnessVersion);
        Assert.Equal(1300, manifest.InputTokens);
        Assert.Equal(110, manifest.OutputTokens);
        Assert.Equal(2240, manifest.CacheReadTokens);
        Assert.True(manifest.CostUsd > 0);
        Assert.True(store.IsComplete(manifest.RunId));

        var patch = File.ReadAllText(Path.Combine(store.RunDir(manifest.RunId), "patch.diff"));
        Assert.True(patch.Contains("+    return a + b"));
        Assert.True(patch.Contains("-    return a - b"));
        Assert.Equal(patch.Length, manifest.PatchChars);

        var calls = File.ReadAllLines(Path.Combine(store.RunDir(manifest.RunId), "calls.jsonl"));
        Assert.Equal(3, calls.Length);
    }

    [Fact]
    public async Task The_agent_sees_the_issue_but_never_the_tests_or_the_reference_patch()
    {
        using var dir = new TempDir();
        var model = FixingModel();

        await NewOrchestrator(model, CalcRepo(), new RunStore(dir.Path)).RunAsync(NewTask(), 1);

        var first = (TextBlock)model.Requests[0].Messages[0].Content[0];
        Assert.True(first.Text.Contains("Fix demo/repo"));
        Assert.True(first.Text.Contains("add() subtracts instead of adding."));
        Assert.False(first.Text.Contains("test_add"));
        Assert.False(first.Text.Contains("secret reference fix"));
    }

    [Fact]
    public async Task An_infrastructure_failure_is_recorded_as_an_error_and_can_be_retried()
    {
        using var dir = new TempDir();
        var store = new RunStore(dir.Path);
        var model = new ScriptedModelClient(Array.Empty<ModelResponse>());

        var manifest = await NewOrchestrator(model, new FailingProvider(), store).RunAsync(NewTask(), 1);

        Assert.Equal("error", manifest.Status);
        Assert.True(manifest.Error!.Contains("docker is not running"));
        Assert.Equal(0m, manifest.CostUsd);
        Assert.False(store.IsComplete(manifest.RunId));
    }

    [Fact]
    public async Task Exceeding_the_budget_ends_the_run_as_completed_but_unresolved()
    {
        using var dir = new TempDir();
        var store = new RunStore(dir.Path);
        var model = new ScriptedModelClient(new[]
        {
            Tool(ToolNames.ListDir, new JsonObject { ["path"] = "." }, new Usage(5_000_000, 100, 0, 0)),
            Text("never reached", new Usage(1, 1, 0, 0)),
        });

        var manifest = await NewOrchestrator(model, CalcRepo(), store).RunAsync(NewTask(), 1);

        Assert.Equal("completed", manifest.Status);
        Assert.Equal("budget_exceeded", manifest.StopReason);
        Assert.Equal(1, model.Requests.Count);
    }

    [Fact]
    public async Task Predictions_contain_only_completed_runs_of_the_requested_config_and_repeat()
    {
        using var dir = new TempDir();
        var store = new RunStore(dir.Path);
        await NewOrchestrator(FixingModel(), CalcRepo(), store).RunAsync(NewTask("demo__repo-1"), 1);
        await NewOrchestrator(FixingModel(), CalcRepo(), store).RunAsync(NewTask("demo__repo-2"), 2);
        await NewOrchestrator(new ScriptedModelClient(Array.Empty<ModelResponse>()), new FailingProvider(), store)
            .RunAsync(NewTask("demo__repo-3"), 1);

        var file = Predictions.Build(store, "A", 1);

        Assert.Equal(1, file.Count);
        Assert.Equal("mamcp-A", file.ModelName);
        var line = JsonNode.Parse(File.ReadAllLines(file.Path)[0])!.AsObject();
        Assert.Equal("demo__repo-1", line["instance_id"]!.GetValue<string>());
        Assert.Equal("mamcp-A", line["model_name_or_path"]!.GetValue<string>());
        Assert.True(line["model_patch"]!.GetValue<string>().Contains("+    return a + b"));
    }

    [Fact]
    public async Task Report_joins_manifests_and_verdicts()
    {
        using var dir = new TempDir();
        var store = new RunStore(dir.Path);
        var manifest = await NewOrchestrator(FixingModel(), CalcRepo(), store).RunAsync(NewTask(), 1);
        await NewOrchestrator(new ScriptedModelClient(Array.Empty<ModelResponse>()), new FailingProvider(), store)
            .RunAsync(NewTask("demo__repo-2"), 1);

        store.WriteVerdict(manifest.RunId, new Verdict(true, "eval-1", DateTimeOffset.UtcNow));
        var csv = ReportBuilder.BuildCsv(store).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var summary = ReportBuilder.Summarize(store).Single();

        var header = csv[0].Split(',').ToList();
        var row = csv.Single(l => l.StartsWith("A.demo__repo-1.r1", StringComparison.Ordinal)).Split(',');
        Assert.Equal("true", row[header.IndexOf("resolved")]);
        Assert.Equal("completed", row[header.IndexOf("status")]);
        Assert.True(csv.Any(l => l.Contains("docker is not running")));
        Assert.Equal(2, summary.Runs);
        Assert.Equal(1, summary.Completed);
        Assert.Equal(1, summary.Errors);
        Assert.Equal(1, summary.Evaluated);
        Assert.Equal(1, summary.Resolved);
    }

    [Fact]
    public void Evaluator_output_is_understood_in_both_layouts()
    {
        var summary = EvalResultsParser.ParseSummary(
            """{"resolved_ids":["a","b"],"unresolved_ids":["c"],"completed_ids":["a","b","c"],"error_ids":["d"]}""");
        var reports = EvalResultsParser.ParseInstanceReports(new[]
        {
            """{"a":{"patch_is_None":false,"resolved":true}}""",
            """{"c":{"patch_is_None":false,"resolved":false}}""",
        });

        Assert.Equal(2, summary.Resolved.Count);
        Assert.Equal(3, summary.Evaluated.Count);
        Assert.False(summary.Evaluated.Contains("d"));
        Assert.True(reports.Resolved.Contains("a"));
        Assert.False(reports.Resolved.Contains("c"));
        Assert.Equal(2, reports.Evaluated.Count);
    }

    [Fact]
    public async Task Evaluator_results_are_found_on_disk()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "mamcp-A.myrun.json"), """{"resolved_ids":["x"],"completed_ids":["x","y"]}""");

        var results = EvalResultsParser.Load(dir.Path, "myrun");

        Assert.True(results.Resolved.Contains("x"));
        Assert.Equal(2, results.Evaluated.Count);
        var threw = false;
        try
        {
            EvalResultsParser.Load(dir.Path, "other-run");
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Assert.True(threw);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Environment_check_accepts_a_clean_repo_at_the_base_commit_and_rejects_anything_else()
    {
        using var dir = new TempDir();
        var exec = new LocalExecutor(dir.Path);
        await exec.RunAsync(
            "git init -q && echo a > f.txt && git add . && git -c user.name=t -c user.email=t@t commit -qm base",
            TimeSpan.FromSeconds(30));
        var head = (await exec.RunAsync("git rev-parse HEAD", TimeSpan.FromSeconds(30))).StdOut.Trim();

        var ok = await EnvironmentVerifier.VerifyAsync(exec, NewTask() with { BaseCommit = head }, requirePython: false);
        var wrongCommit = await Failure(() => EnvironmentVerifier.VerifyAsync(
            exec, NewTask() with { BaseCommit = "0000000000000000000000000000000000000000" }, false));
        File.WriteAllText(Path.Combine(dir.Path, "stray.txt"), "x");
        var dirty = await Failure(() => EnvironmentVerifier.VerifyAsync(exec, NewTask() with { BaseCommit = head }, false));

        Assert.True(ok.Contains("clean"));
        Assert.True(wrongCommit.Contains("base commit"));
        Assert.True(dirty.Contains("not clean"));
    }

    [Fact]
    public void Task_files_load_pick_and_sample_reproducibly()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "tasks.jsonl");
        var rows = new List<string>();
        foreach (var repo in new[] { "django/django", "sympy/sympy", "astropy/astropy" })
        {
            for (var i = 1; i <= 10; i++)
            {
                var id = repo.Replace("/", "__") + "-" + i;
                rows.Add($$"""{"instance_id":"{{id}}","repo":"{{repo}}","base_commit":"abc","problem_statement":"p","fail_to_pass":["t1","t2"],"pass_to_pass":[],"patch":"","test_patch":"","version":"1.0"}""");
            }
        }

        File.WriteAllLines(path, rows);

        var tasks = TaskLoader.Load(path);
        var picked = TaskLoader.Pick(tasks, new[] { "sympy__sympy-3", "django__django-1" });
        var sample = TaskSelector.Select(tasks, 6, seed: 7);
        var again = TaskSelector.Select(tasks, 6, seed: 7);

        Assert.Equal(30, tasks.Count);
        Assert.Equal(2, tasks[0].FailToPass.Count);
        Assert.Equal("sympy__sympy-3", picked[0].InstanceId);
        Assert.Equal("django__django-1", picked[1].InstanceId);
        Assert.Equal(6, sample.Count);
        Assert.Equal(string.Join(",", sample), string.Join(",", again));
        Assert.Equal(3, sample.Select(id => id.Split("__")[0]).Distinct().Count());
        Assert.Equal(2, sample.Count(id => id.StartsWith("django", StringComparison.Ordinal)));
    }

    [Fact]
    public void Image_names_follow_the_swebench_naming_scheme()
    {
        Assert.Equal(
            "swebench/sweb.eval.x86_64.django_1776_django-14140:latest",
            SweBenchImage.Name("django__django-14140"));
        Assert.Equal(
            "swebench/sweb.eval.x86_64.pydata_1776_xarray-4094:latest",
            SweBenchImage.Name("pydata__xarray-4094"));
        Assert.Equal(
            "other/sweb.eval.arm64.sympy_1776_sympy-1:v2",
            SweBenchImage.Name("Sympy__Sympy-1", new SweBenchImageOptions { Namespace = "other", Arch = "arm64", Tag = "v2" }));
    }

    [Fact]
    public void Prompt_hashes_ignore_line_endings_and_the_embedded_prompts_load()
    {
        var unix = PromptSet.FromTexts("one\ntwo\n", "task {{repo}}");
        var windows = PromptSet.FromTexts("one\r\ntwo\r\n", "task {{repo}}");
        var real = PromptSet.Load();

        Assert.Equal(unix.SystemHash, windows.SystemHash);
        Assert.Equal(12, unix.SystemHash.Length);
        Assert.True(real.SystemPrompt.Contains("hidden tests"));
        Assert.True(real.TaskTemplate.Contains("{{problem_statement}}"));
        Assert.False(real.SystemPrompt.Contains("{{"));
    }

    private static async Task<string> Failure(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }

        return "no exception";
    }
}
