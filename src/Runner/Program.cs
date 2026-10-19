using System.Text.Json.Nodes;
using Agents.Core;

// Smoke test for the harness.
//   dotnet run --project src/Runner -- dry     (no API key, no cost: scripted model)
//   dotnet run --project src/Runner -- live    (needs ANTHROPIC_API_KEY, costs well under $0.01)
var mode = args.FirstOrDefault() ?? "dry";

IModelClient model;
if (mode == "live")
{
    var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    if (string.IsNullOrWhiteSpace(key))
    {
        Console.Error.WriteLine("Set ANTHROPIC_API_KEY first.");
        return 1;
    }

    model = new AnthropicHttpClient(key);
}
else
{
    model = new ScriptedModelClient(new ModelResponse[]
    {
        new(new ContentBlock[]
            {
                new ToolUseBlock("t1", "add", new JsonObject { ["a"] = 17, ["b"] = 25 }),
            },
            "tool_use", new Usage(900, 40, 0, 0)),
        new(new ContentBlock[] { new TextBlock("17 + 25 = 42") }, "end_turn", new Usage(60, 12, 900, 0)),
    });
}

var runId = $"smoke-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
var run = new RunContext(
    runId, "smoke", model,
    new RunBudget(maxTotalTokens: 50_000, maxCostUsd: 0.05m),
    new ContextPolicy(),
    new JsonlCallLog(Path.Combine("results", "smoke.jsonl")));

var agent = new AgentSpec(
    "agent-1", "solo",
    "You are a careful assistant. Use the provided tools instead of doing arithmetic yourself.",
    new AddToolHost());

var result = await AgentLoop.RunAsync(agent, "Use the add tool to compute 17 + 25 and tell me the result.", run);

Console.WriteLine($"model:      {model.ModelId}");
Console.WriteLine($"stop:       {result.StopReason} after {result.Turns} turn(s)");
Console.WriteLine($"answer:     {result.FinalText}");
Console.WriteLine($"tokens:     in={run.Budget.Total.InputTokens} out={run.Budget.Total.OutputTokens} " +
                  $"cacheRead={run.Budget.Total.CacheReadTokens} cacheWrite={run.Budget.Total.CacheWriteTokens}");
Console.WriteLine($"cost (USD): {run.Budget.CostUsd:F6}");
Console.WriteLine("log:        results/smoke.jsonl");
return 0;

/// <summary>A trivial in-process tool host, used only by the smoke test.</summary>
internal sealed class AddToolHost : IToolHost
{
    public IReadOnlyList<ToolDef> Tools { get; } = new[]
    {
        new ToolDef(
            "add",
            "Adds two integers and returns the sum.",
            JsonNode.Parse("""
                {"type":"object","properties":{"a":{"type":"integer"},"b":{"type":"integer"}},"required":["a","b"]}
                """)!.AsObject()),
    };

    public Task<ToolOutput> CallAsync(string name, JsonObject input, CancellationToken ct = default)
    {
        if (name != "add")
        {
            return Task.FromResult(new ToolOutput($"Unknown tool {name}", true));
        }

        var sum = input["a"]!.GetValue<long>() + input["b"]!.GetValue<long>();
        return Task.FromResult(new ToolOutput(sum.ToString()));
    }
}
