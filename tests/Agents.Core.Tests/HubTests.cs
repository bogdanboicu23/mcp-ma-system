using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Agents.Core.Execution;
using Agents.Core.Runs;
using Agents.Core.Swe;
using Agents.Core.Tools;
using Agents.Topologies;
using Xunit;

namespace Agents.Core.Tests;

public class HubTests
{
    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mamcp-hub-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>A stateless, thread-safe fake model: the reply is a pure function of the request, so parallel workers cannot race.</summary>
    private sealed class FuncModel : IModelClient
    {
        private readonly Func<ModelRequest, ModelResponse> _reply;

        public FuncModel(Func<ModelRequest, ModelResponse> reply) => _reply = reply;

        public string ModelId => "fake";

        public ConcurrentQueue<ModelRequest> Requests { get; } = new();

        public Task<ModelResponse> SendAsync(ModelRequest request, CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            return Task.FromResult(_reply(request));
        }
    }

    private static readonly Usage Small = new(500, 50, 0, 0);

    private static ModelResponse Call(string tool, JsonObject input) =>
        new(new ContentBlock[] { new ToolUseBlock("t" + Guid.NewGuid().ToString("N"), tool, input) }, "tool_use", Small);

    private static ModelResponse Say(string text) => new(new ContentBlock[] { new TextBlock(text) }, "end_turn", Small);

    private static bool IsOrchestrator(ModelRequest r) => r.System.Contains("ORCHESTRATOR");

    private static string FirstText(ModelRequest r) => ((TextBlock)r.Messages[0].Content[0]).Text;

    private static string? LastToolResult(ModelRequest r) =>
        r.Messages[^1].Content.OfType<ToolResultBlock>().FirstOrDefault()?.Content;

    private static JsonObject Delegate(params (string Instructions, string[] Files)[] tasks) => new()
    {
        ["tasks"] = new JsonArray(tasks.Select(t => (JsonNode)new JsonObject
        {
            ["instructions"] = t.Instructions,
            ["files"] = new JsonArray(t.Files.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
        }).ToArray()),
    };

    private static PromptSet Prompts() =>
        PromptSet.FromTexts("base prompt", "Fix {{repo}}\n{{problem_statement}}", "You are the ORCHESTRATOR.", "You are a WORKER.");

    private static SweBenchTask NewTask() => new()
    {
        InstanceId = "demo__repo-1",
        Repo = "demo/repo",
        BaseCommit = "unused",
        ProblemStatement = "two functions are wrong.",
        FailToPass = new[] { "tests/test_x.py::test_x" },
        GoldPatch = "secret",
    };

    private static LocalEnvironmentProvider TwoFileRepo() => new(_ => new Dictionary<string, string>
    {
        ["a.py"] = "def add(a, b):\n    return a - b\n",
        ["b.py"] = "def mul(a, b):\n    return a + b\n",
    });

    private static HubAndSpokeSystem Hub(HubOptions options) =>
        new("C", Prompts(), (exec, allowed) => new ContainerToolHost(exec, allowed), options);

    private static RunOrchestrator Orchestrator(IModelClient model, ISystem system, RunStore store) =>
        new(model, system, TwoFileRepo(), store, Prompts(), new RunLimits(), "test-sha");

    /// <summary>A model that plays a fixed plan: orchestrator delegates once, each worker makes one edit, everyone then stops.</summary>
    private static FuncModel TwoWorkerPlan(JsonObject delegation) => new(r =>
    {
        if (IsOrchestrator(r))
        {
            return r.Messages.Count == 1 ? Call(DelegationToolHost.DelegateTool, delegation) : Say("All done.");
        }

        if (r.Messages.Count > 1)
        {
            return Say("Changed my file.");
        }

        var text = FirstText(r);
        return text.Contains("fix add")
            ? Call(ToolNames.EditFile, new JsonObject { ["path"] = "a.py", ["old_string"] = "a - b", ["new_string"] = "a + b" })
            : Call(ToolNames.EditFile, new JsonObject { ["path"] = "b.py", ["old_string"] = "a + b", ["new_string"] = "a * b" });
    });

    [Fact]
    public async Task Two_workers_run_in_parallel_and_their_edits_end_up_in_one_patch()
    {
        using var dir = new TempDir();
        var store = new RunStore(dir.Path);
        var model = TwoWorkerPlan(Delegate(("fix add", new[] { "a.py" }), ("fix mul", new[] { "b.py" })));

        var manifest = await Orchestrator(model, Hub(new HubOptions(Workers: 2)), store).RunAsync(NewTask(), 1);

        Assert.Equal("completed", manifest.Status);
        Assert.Equal("end_turn", manifest.StopReason);
        Assert.Equal("workers=2;exposure=full", manifest.Variant);
        Assert.Equal(6, manifest.Turns); // orchestrator 2 + two workers x 2

        var patch = File.ReadAllText(Path.Combine(store.RunDir(manifest.RunId), "patch.diff"));
        Assert.True(patch.Contains("+    return a + b"));
        Assert.True(patch.Contains("+    return a * b"));

        var calls = File.ReadAllLines(Path.Combine(store.RunDir(manifest.RunId), "calls.jsonl"));
        Assert.Equal(6, calls.Length);
        Assert.Equal(2, calls.Count(l => l.Contains("\"agentId\":\"orchestrator\"")));
        Assert.Equal(2, calls.Count(l => l.Contains("\"agentId\":\"worker-1\"")));
        Assert.Equal(2, calls.Count(l => l.Contains("\"agentId\":\"worker-2\"")));
        Assert.Equal(4, calls.Count(l => l.Contains("\"role\":\"worker\"")));

        // One shared budget: the manifest total is the sum of all six calls (500 in / 50 out each).
        Assert.Equal(3000, manifest.InputTokens);
        Assert.Equal(300, manifest.OutputTokens);
    }

    [Fact]
    public async Task A_worker_sees_the_issue_and_its_sub_task_but_not_the_orchestrators_history()
    {
        using var dir = new TempDir();
        var model = TwoWorkerPlan(Delegate(("fix add", new[] { "a.py" }), ("fix mul", new[] { "b.py" })));

        await Orchestrator(model, Hub(new HubOptions(Workers: 2)), new RunStore(dir.Path)).RunAsync(NewTask(), 1);

        var worker = model.Requests.First(r => !IsOrchestrator(r) && FirstText(r).Contains("fix add"));
        var text = FirstText(worker);
        Assert.True(text.Contains("two functions are wrong."));
        Assert.True(text.Contains("## Your sub-task"));
        Assert.True(text.Contains("- a.py"));
        Assert.False(text.Contains("fix mul"));
        Assert.False(text.Contains("secret"));
        Assert.True(worker.System.StartsWith("base prompt"));
        Assert.True(worker.System.Contains("WORKER"));
        Assert.False(worker.System.Contains("ORCHESTRATOR"));
    }

    [Fact]
    public async Task Overlapping_file_scopes_are_rejected_before_any_worker_starts()
    {
        using var dir = new TempDir();
        var model = new FuncModel(r => IsOrchestrator(r)
            ? (r.Messages.Count == 1
                ? Call(DelegationToolHost.DelegateTool, Delegate(("one", new[] { "pkg/" }), ("two", new[] { "pkg/x.py" })))
                : Say("giving up"))
            : throw new InvalidOperationException("no worker should start"));

        var manifest = await Orchestrator(model, Hub(new HubOptions(Workers: 3)), new RunStore(dir.Path)).RunAsync(NewTask(), 1);

        Assert.Equal("completed", manifest.Status);
        Assert.Equal(2, manifest.Turns);
        var second = model.Requests.Last(IsOrchestrator);
        Assert.True(LastToolResult(second)!.Contains("overlapping"));
    }

    [Fact]
    public async Task More_tasks_than_workers_are_rejected()
    {
        using var dir = new TempDir();
        var model = new FuncModel(r => IsOrchestrator(r)
            ? (r.Messages.Count == 1
                ? Call(DelegationToolHost.DelegateTool, Delegate(("1", new[] { "a.py" }), ("2", new[] { "b.py" }), ("3", new[] { "c.py" })))
                : Say("ok"))
            : throw new InvalidOperationException("no worker should start"));

        await Orchestrator(model, Hub(new HubOptions(Workers: 2)), new RunStore(dir.Path)).RunAsync(NewTask(), 1);

        Assert.True(LastToolResult(model.Requests.Last(IsOrchestrator))!.Contains("At most 2 tasks"));
    }

    [Fact]
    public async Task A_worker_cannot_write_outside_its_scope_and_the_rejection_is_visible_to_it()
    {
        using var dir = new TempDir();
        var store = new RunStore(dir.Path);
        var model = new FuncModel(r =>
        {
            if (IsOrchestrator(r))
            {
                return r.Messages.Count == 1
                    ? Call(DelegationToolHost.DelegateTool, Delegate(("fix add", new[] { "a.py" })))
                    : Say("done");
            }

            // The worker wrongly tries to edit b.py, then reports.
            return r.Messages.Count == 1
                ? Call(ToolNames.EditFile, new JsonObject { ["path"] = "b.py", ["old_string"] = "a + b", ["new_string"] = "a * b" })
                : Say("could not edit b.py");
        });

        var manifest = await Orchestrator(model, Hub(new HubOptions(Workers: 2)), store).RunAsync(NewTask(), 1);

        var workerSecond = model.Requests.Last(r => !IsOrchestrator(r));
        Assert.True(LastToolResult(workerSecond)!.Contains("outside your assigned files"));
        var patch = File.ReadAllText(Path.Combine(store.RunDir(manifest.RunId), "patch.diff"));
        Assert.Equal(string.Empty, patch.Trim());
    }

    [Fact]
    public void Write_scope_matches_files_and_directories_and_refuses_escapes()
    {
        var scope = new WriteScopeHost(new NoopHost(), new[] { "src/a.py", "pkg/" });

        Assert.True(scope.Allows("src/a.py"));
        Assert.True(scope.Allows("./src/a.py"));
        Assert.True(scope.Allows("pkg/deep/x.py"));
        Assert.False(scope.Allows("src/b.py"));
        Assert.False(scope.Allows("pkgx/x.py"));
        Assert.False(scope.Allows("/abs/src/a.py"));
        Assert.False(scope.Allows("pkg/../src/b.py"));
        Assert.True(WriteScopeHost.Overlap(new[] { "pkg/" }, new[] { "pkg/x.py" }));
        Assert.False(WriteScopeHost.Overlap(new[] { "a.py" }, new[] { "b.py" }));
        Assert.False(WriteScopeHost.Overlap(Array.Empty<string>(), new[] { "b.py" }));
    }

    [Fact]
    public async Task A_worker_infrastructure_failure_surfaces_as_an_error_run_not_as_a_result()
    {
        using var dir = new TempDir();
        var store = new RunStore(dir.Path);
        var model = new FuncModel(r => IsOrchestrator(r)
            ? (r.Messages.Count == 1
                ? Call(DelegationToolHost.DelegateTool, Delegate(("fix add", new[] { "a.py" })))
                : Say("done"))
            : throw new InvalidOperationException("api is down"));

        var manifest = await Orchestrator(model, Hub(new HubOptions(Workers: 2)), store).RunAsync(NewTask(), 1);

        Assert.Equal("error", manifest.Status);
        Assert.True(manifest.Error!.Contains("api is down"));
        Assert.False(store.IsComplete(manifest.RunId));
    }

    [Fact]
    public async Task Tool_exposure_controls_which_tools_each_agent_is_sent()
    {
        foreach (var (exposure, orchestratorTools, workerTools) in new[]
                 {
                     (ToolExposure.Full,
                         "read_file,list_dir,edit_file,write_file,run_command,run_tests,delegate_task",
                         "read_file,list_dir,edit_file,write_file,run_command,run_tests"),
                     (ToolExposure.Role,
                         "read_file,list_dir,run_tests,delegate_task",
                         "read_file,list_dir,edit_file,write_file,run_command"),
                 })
        {
            using var dir = new TempDir();
            var model = TwoWorkerPlan(Delegate(("fix add", new[] { "a.py" }), ("fix mul", new[] { "b.py" })));

            var manifest = await Orchestrator(model, Hub(new HubOptions(Workers: 2, Exposure: exposure)), new RunStore(dir.Path))
                .RunAsync(NewTask(), 1);

            Assert.Equal("completed", manifest.Status);
            Assert.Equal(orchestratorTools, string.Join(",", model.Requests.First(IsOrchestrator).Tools.Select(t => t.Name)));
            Assert.Equal(workerTools, string.Join(",", model.Requests.First(r => !IsOrchestrator(r)).Tools.Select(t => t.Name)));
        }
    }

    [Fact]
    public void Unknown_worker_counts_are_refused()
    {
        var threw = false;
        try
        {
            _ = Hub(new HubOptions(Workers: 0));
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }

        Assert.True(threw);
    }

    private sealed class NoopHost : IToolHost
    {
        public IReadOnlyList<ToolDef> Tools => Array.Empty<ToolDef>();

        public Task<ToolOutput> CallAsync(string name, JsonObject input, CancellationToken ct = default) =>
            Task.FromResult(new ToolOutput("ok"));
    }
}
