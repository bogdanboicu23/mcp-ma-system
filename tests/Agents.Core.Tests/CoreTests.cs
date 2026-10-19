using System.Text.Json.Nodes;
using Agents.Core;
using Xunit;

namespace Agents.Core.Tests;

public class CoreTests
{
    private sealed class CountingHost : IToolHost
    {
        public int Calls { get; private set; }

        public IReadOnlyList<ToolDef> Tools { get; } = new[]
        {
            new ToolDef("echo", "Echoes its input.", new JsonObject { ["type"] = "object" }),
        };

        public Task<ToolOutput> CallAsync(string name, JsonObject input, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new ToolOutput("ok"));
        }
    }

    private static ModelResponse ToolCall(Usage usage) => new(
        new ContentBlock[] { new ToolUseBlock("id" + Guid.NewGuid().ToString("N"), "echo", new JsonObject()) },
        "tool_use", usage);

    private static ModelResponse Text(string text, Usage usage) =>
        new(new ContentBlock[] { new TextBlock(text) }, "end_turn", usage);

    private static RunContext NewRun(IModelClient model, RunBudget budget, ICallLog log, int maxPrompt = 80_000) =>
        new("run-1", "test", model, budget, new ContextPolicy(maxPrompt), log);

    private static AgentSpec NewAgent(IToolHost host, int maxTurns = 40) =>
        new("a1", "solo", "system prompt", host, maxTurns);

    [Fact]
    public async Task Loop_runs_the_tool_then_finishes()
    {
        var host = new CountingHost();
        var log = new MemoryCallLog();
        var model = new ScriptedModelClient(new[]
        {
            ToolCall(new Usage(100, 10, 0, 0)),
            Text("done", new Usage(120, 5, 0, 0)),
        });

        var result = await AgentLoop.RunAsync(NewAgent(host), "task", NewRun(model, new RunBudget(1_000_000, 10m), log));

        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.Equal("done", result.FinalText);
        Assert.Equal(2, result.Turns);
        Assert.Equal(1, host.Calls);
        Assert.Equal(2, log.Records.Count);
    }

    [Fact]
    public async Task Loop_stops_when_the_budget_is_exceeded()
    {
        var host = new CountingHost();
        var model = new ScriptedModelClient(new[]
        {
            ToolCall(new Usage(900, 200, 0, 0)),
            Text("never reached", new Usage(1, 1, 0, 0)),
        });

        var result = await AgentLoop.RunAsync(
            NewAgent(host), "task", NewRun(model, new RunBudget(1_000, 10m), new NullCallLog()));

        Assert.Equal(StopReasons.BudgetExceeded, result.StopReason);
        Assert.Equal(1, model.Requests.Count);
    }

    [Fact]
    public async Task Loop_stops_at_max_turns()
    {
        var host = new CountingHost();
        var model = new ScriptedModelClient(Enumerable.Range(0, 5).Select(_ => ToolCall(new Usage(10, 1, 0, 0))));

        var result = await AgentLoop.RunAsync(
            NewAgent(host, maxTurns: 3), "task", NewRun(model, new RunBudget(1_000_000, 10m), new NullCallLog()));

        Assert.Equal(StopReasons.MaxTurns, result.StopReason);
        Assert.Equal(3, host.Calls);
    }

    [Fact]
    public async Task Logged_usage_adds_up_to_the_run_budget_total()
    {
        var log = new MemoryCallLog();
        var budget = new RunBudget(1_000_000, 10m);
        var model = new ScriptedModelClient(new[]
        {
            ToolCall(new Usage(100, 10, 50, 5)),
            ToolCall(new Usage(200, 20, 60, 0)),
            Text("done", new Usage(300, 30, 70, 0)),
        });

        await AgentLoop.RunAsync(NewAgent(new CountingHost()), "task", NewRun(model, budget, log));

        var logged = log.Records.Aggregate(Usage.Zero, (acc, r) => acc + r.Usage);
        Assert.Equal(budget.Total, logged);
        Assert.Equal(budget.CostUsd, log.Records.Sum(r => r.CostUsd));
    }

    [Fact]
    public void Pricing_applies_the_5x_tier_above_100k_prompt_tokens()
    {
        var p = Pricing.Haiku55;
        var below = p.Cost(new Usage(90_000, 1_000, 0, 0));
        var above = p.Cost(new Usage(110_000, 1_000, 0, 0));

        Assert.Equal(0.0095m, below);
        Assert.Equal(0.0575m, above);
    }

    [Fact]
    public void Cached_tokens_count_towards_the_pricing_tier()
    {
        var p = Pricing.Haiku55;
        var cost = p.Cost(new Usage(1_000, 0, 100_000, 0));

        // 101k prompt tokens => high tier: (1000 * 0.10 + 100000 * 0.01) / 1e6 * 5
        Assert.Equal(0.0055m, cost);
    }

    [Fact]
    public void Context_policy_stubs_old_tool_results_and_keeps_the_latest_message()
    {
        var big = new string('x', 60_000);
        var messages = new List<Message>
        {
            Message.User("task"),
            new("assistant", new ContentBlock[] { new ToolUseBlock("1", "echo", new JsonObject()) }),
            new("user", new ContentBlock[] { new ToolResultBlock("1", big) }),
            new("assistant", new ContentBlock[] { new ToolUseBlock("2", "echo", new JsonObject()) }),
            new("user", new ContentBlock[] { new ToolResultBlock("2", big) }),
        };

        var policy = new ContextPolicy(maxPromptTokens: 30_000);
        var result = policy.Apply("system", Array.Empty<ToolDef>(), messages);

        Assert.Equal(1, result.StubbedToolResults);
        Assert.False(result.Overflow);
        var oldResult = (ToolResultBlock)result.Messages[2].Content[0];
        var newResult = (ToolResultBlock)result.Messages[4].Content[0];
        Assert.True(oldResult.Content.StartsWith(ContextPolicy.StubPrefix, StringComparison.Ordinal));
        Assert.Equal(big, newResult.Content);
        // The caller's full history is untouched.
        Assert.Equal(big, ((ToolResultBlock)messages[2].Content[0]).Content);
    }

    [Fact]
    public void Context_policy_reports_overflow_when_nothing_is_left_to_shrink()
    {
        var messages = new List<Message> { Message.User(new string('x', 600_000)) };

        var result = new ContextPolicy(maxPromptTokens: 80_000).Apply("system", Array.Empty<ToolDef>(), messages);

        Assert.True(result.Overflow);
        Assert.Equal(0, result.StubbedToolResults);
    }

    [Fact]
    public void Http_client_builds_a_cacheable_request_and_parses_usage_and_tool_calls()
    {
        var client = new AnthropicHttpClient("test-key");
        var request = new ModelRequest(
            "sys",
            new[] { Message.User("hi") },
            new[]
            {
                new ToolDef("a", "first", new JsonObject { ["type"] = "object" }),
                new ToolDef("b", "second", new JsonObject { ["type"] = "object" }),
            },
            256);

        var body = client.BuildBody(request);

        Assert.Equal("claude-haiku-5-5", body["model"]!.GetValue<string>());
        Assert.Null(body["tools"]![0]!["cache_control"]);
        Assert.NotNull(body["tools"]![1]!["cache_control"]);
        Assert.Equal("ephemeral", body["system"]![0]!["cache_control"]!["type"]!.GetValue<string>());

        const string reply = """
            {"content":[{"type":"text","text":"hello"},{"type":"tool_use","id":"tu1","name":"a","input":{"x":1}}],
             "stop_reason":"tool_use",
             "usage":{"input_tokens":11,"output_tokens":22,"cache_read_input_tokens":33,"cache_creation_input_tokens":44}}
            """;
        var parsed = AnthropicHttpClient.ParseResponse(reply);

        Assert.Equal("tool_use", parsed.StopReason);
        Assert.Equal(new Usage(11, 22, 33, 44), parsed.Usage);
        Assert.Equal(2, parsed.Content.Count);
        var use = Assert.IsType<ToolUseBlock>(parsed.Content[1]);
        Assert.Equal("tu1", use.Id);
        Assert.Equal(1, use.Input["x"]!.GetValue<int>());
    }
}
