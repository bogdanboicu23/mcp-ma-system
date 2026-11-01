using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Agents.Core.Runs;
using Agents.Core.Swe;
using Agents.Core.Tools;
using Agents.Topologies;
using Xunit;

namespace Agents.Core.Tests;

/// <summary>Offline tests for the coder-reviewer (D) and sequential (E) topologies.</summary>
public class TopologyTests
{
    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mamcp-topo-" + Guid.NewGuid().ToString("N"));
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

    private static PromptSet Prompts() => PromptSet.FromTexts(
        "base prompt", "Fix {{repo}}\n{{problem_statement}}", addenda: new Dictionary<string, string>
        {
            ["coder"] = "You are the CODER.",
            ["reviewer"] = "You are the REVIEWER.",
            ["planner"] = "You are the PLANNER.",
            ["seqcoder"] = "You are the SEQCODER.",
            ["tester"] = "You are the TESTER.",
        });

    private static SweBenchTask NewTask() => new()
    {
        InstanceId = "demo__repo-1",
        Repo = "demo/repo",
        BaseCommit = "unused",
        ProblemStatement = "add() subtracts instead of adding.",
        FailToPass = new[] { "tests/test_x.py::test_x" },
        GoldPatch = "secret",
    };

    private static LocalEnvironmentProvider Repo() => new(_ => new Dictionary<string, string>
    {
        ["calc.py"] = "def add(a, b):\n    return a - b\n",
    });

    private static RunOrchestrator Orchestrator(IModelClient model, ISystem system, RunStore store) =>
        new(model, system, Repo(), store, Prompts(), new RunLimits(), "test-sha");

    private static string Role(ModelRequest r) =>
        new[] { "SEQCODER", "CODER", "REVIEWER", "PLANNER", "TESTER" }.First(x => r.System.Contains(x));

    private static string FirstText(ModelRequest r) => ((TextBlock)r.Messages[0].Content[0]).Text;

    private static string? LastUserText(ModelRequest r) =>
        r.Messages[^1].Content.OfType<TextBlock>().FirstOrDefault()?.Text;

    private static JsonObject FixAdd() =>
        new() { ["path"] = "calc.py", ["old_string"] = "a - b", ["new_string"] = "a + b" };

    private static CoderReviewerSystem D(int rounds) => new(
        "D", Prompts(), (exec, allowed) => new ContainerToolHost(exec, allowed), new ReviewOptions(rounds));

    private static SequentialSystem E() => new(
        "E", Prompts(), (exec, allowed) => new ContainerToolHost(exec, allowed), new SequentialOptions());

    [Fact]
    public async Task Continue_keeps_history_and_numbers_turns_on()
    {
        var model = new FuncModel(r => r.Messages.Count == 1 ? Say("first") : Say("second"));
        var log = new MemoryCallLog();
        var run = new RunContext("r", "X", model, new RunBudget(1_000_000, 10m), new ContextPolicy(), log);
        var spec = new AgentSpec("a", "a", "sys", new ContainerToolHost(new Agents.Core.Execution.LocalExecutor(Path.GetTempPath())), 5);

        var first = await AgentLoop.RunAsync(spec, "hello", run);
        var second = await AgentLoop.ContinueAsync(spec, first.History, "more", run);

        Assert.Equal(1, first.Turns);
        Assert.Equal(1, second.Turns);
        Assert.Equal(4, second.History.Count); // user, assistant, user, assistant
        Assert.Equal("1,2", string.Join(",", log.Records.Select(x => x.Turn)));
        Assert.Equal("second", second.FinalText);
    }

    [Fact]
    public async Task Coder_reviewer_stops_on_approve_without_another_coder_pass()
    {
        using var dir = new TempDir();
        var model = new FuncModel(r => Role(r) switch
        {
            "CODER" => r.Messages.Count == 1 ? Call(ToolNames.EditFile, FixAdd()) : Say("fixed"),
            _ => Say("Looks right.\nVERDICT: APPROVE"),
        });

        var manifest = await Orchestrator(model, D(3), new RunStore(dir.Path)).RunAsync(NewTask(), 1);

        Assert.Equal("completed", manifest.Status);
        Assert.Equal("rounds=3", manifest.Variant);
        Assert.Equal(3, manifest.Turns); // coder: edit + final text = 2, reviewer = 1
        var calls = File.ReadAllLines(Path.Combine(new RunStore(dir.Path).RunDir(manifest.RunId), "calls.jsonl"));
        Assert.Equal(3, calls.Length);
        Assert.Equal(1, calls.Count(l => l.Contains("\"agentId\":\"reviewer-1\"")));
    }

    [Fact]
    public async Task Reviewer_comments_reach_the_coder_who_continues_with_its_history()
    {
        using var dir = new TempDir();
        var reviews = 0;
        var model = new FuncModel(r =>
        {
            if (Role(r) == "CODER")
            {
                return r.Messages.Count switch
                {
                    1 => Call(ToolNames.EditFile, FixAdd()),
                    2 => Say("done v1"),
                    _ => Say("done v2"),
                };
            }

            return Interlocked.Increment(ref reviews) == 1
                ? Say("Please add a docstring.\nVERDICT: CHANGES")
                : Say("Good.\nVERDICT: APPROVE");
        });

        var manifest = await Orchestrator(model, D(3), new RunStore(dir.Path)).RunAsync(NewTask(), 1);

        var coderRequests = model.Requests.Where(r => Role(r) == "CODER").ToList();
        Assert.Equal(3, coderRequests.Count);
        var third = coderRequests[2];
        Assert.True(LastUserText(third)!.Contains("Please add a docstring."));
        Assert.True(third.Messages.Count > 3); // keeps its earlier messages
        var reviewerRequest = model.Requests.First(r => Role(r) == "REVIEWER");
        Assert.True(FirstText(reviewerRequest).Contains("+    return a + b"));
        Assert.Equal("read_file,list_dir,run_tests", string.Join(",", reviewerRequest.Tools.Select(t => t.Name)));
        Assert.Equal(2, model.Requests.Count(r => Role(r) == "REVIEWER"));
        Assert.Equal("end_turn", manifest.StopReason);
    }

    [Fact]
    public async Task Review_rounds_are_capped_and_the_last_revision_is_not_reviewed()
    {
        using var dir = new TempDir();
        var model = new FuncModel(r => Role(r) == "CODER"
            ? (r.Messages.Count == 1 ? Call(ToolNames.EditFile, FixAdd()) : Say("revised"))
            : Say("Still wrong.\nVERDICT: CHANGES"));

        var manifest = await Orchestrator(model, D(2), new RunStore(dir.Path)).RunAsync(NewTask(), 1);

        Assert.Equal("completed", manifest.Status);
        Assert.Equal(2, model.Requests.Count(r => Role(r) == "REVIEWER"));
        Assert.Equal(4, model.Requests.Count(r => Role(r) == "CODER")); // edit, done, revise, revise
    }

    [Fact]
    public async Task Nothing_is_reviewed_when_the_coder_changed_nothing()
    {
        using var dir = new TempDir();
        var model = new FuncModel(r => Say("I could not find the problem."));

        var manifest = await Orchestrator(model, D(3), new RunStore(dir.Path)).RunAsync(NewTask(), 1);

        Assert.Equal("completed", manifest.Status);
        Assert.Equal(1, manifest.Turns);
        Assert.Equal(0, model.Requests.Count(r => Role(r) == "REVIEWER"));
    }

    [Fact]
    public async Task Sequential_hands_the_plan_to_the_coder_and_the_diff_to_the_tester()
    {
        using var dir = new TempDir();
        var model = new FuncModel(r => Role(r) switch
        {
            "PLANNER" => Say("FILES: calc.py add()\nSTEPS: 1. use +\nTESTS: none"),
            "SEQCODER" => r.Messages.Count == 1 ? Call(ToolNames.EditFile, FixAdd()) : Say("changed the operator"),
            _ => Say("Ran the tests.\nVERDICT: PASS"),
        });

        var manifest = await Orchestrator(model, E(), new RunStore(dir.Path)).RunAsync(NewTask(), 1);

        Assert.Equal("completed", manifest.Status);
        Assert.Equal(1 + 2 + 1, manifest.Turns);
        var coderFirst = model.Requests.First(r => Role(r) == "SEQCODER");
        Assert.True(FirstText(coderFirst).Contains("## Plan from the planner"));
        Assert.True(FirstText(coderFirst).Contains("STEPS: 1. use +"));
        var testerFirst = model.Requests.First(r => Role(r) == "TESTER");
        Assert.True(FirstText(testerFirst).Contains("changed the operator"));
        Assert.True(FirstText(testerFirst).Contains("+    return a + b"));
        Assert.Equal(
            "read_file,list_dir,run_command",
            string.Join(",", model.Requests.First(r => Role(r) == "PLANNER").Tools.Select(t => t.Name)));
    }

    [Fact]
    public async Task Sequential_gives_the_coder_exactly_one_fix_pass_after_a_failed_test_report()
    {
        using var dir = new TempDir();
        var model = new FuncModel(r => Role(r) switch
        {
            "PLANNER" => Say("FILES: calc.py"),
            "SEQCODER" => r.Messages.Count switch
            {
                1 => Call(ToolNames.EditFile, FixAdd()),
                2 => Say("v1"),
                _ => Say("v2 after the tester"),
            },
            _ => Say("test_add still fails on negatives.\nVERDICT: FAIL test_add fails"),
        });

        var manifest = await Orchestrator(model, E(), new RunStore(dir.Path)).RunAsync(NewTask(), 1);

        Assert.Equal("completed", manifest.Status);
        var coderRequests = model.Requests.Where(r => Role(r) == "SEQCODER").ToList();
        Assert.Equal(3, coderRequests.Count);
        Assert.True(LastUserText(coderRequests[2])!.Contains("test_add still fails on negatives."));
        Assert.Equal(1, model.Requests.Count(r => Role(r) == "TESTER"));
    }

    [Fact]
    public void Verdicts_are_parsed_from_the_last_marker()
    {
        Assert.Equal("APPROVE", TopologyKit.Verdict("blah\nVERDICT: APPROVE"));
        Assert.Equal("CHANGES", TopologyKit.Verdict("VERDICT: APPROVE?? no.\nverdict: changes"));
        Assert.Equal("FAIL", TopologyKit.Verdict("ok VERDICT: FAIL because x"));
        Assert.Equal(null, TopologyKit.Verdict("no verdict here"));
    }
}
