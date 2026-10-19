using System.Diagnostics;

namespace Agents.Core;

public static class StopReasons
{
    public const string EndTurn = "end_turn";
    public const string BudgetExceeded = "budget_exceeded";
    public const string ContextOverflow = "context_overflow";
    public const string MaxTurns = "max_turns";
}

/// <summary>One agent: a role, a system prompt and a set of tools.</summary>
public sealed record AgentSpec(
    string AgentId,
    string Role,
    string System,
    IToolHost Tools,
    int MaxTurns = 40,
    int MaxTokensPerCall = 4096);

/// <summary>Everything shared by all agents of one run (one task on one configuration).</summary>
public sealed record RunContext(
    string RunId,
    string Config,
    IModelClient Model,
    RunBudget Budget,
    ContextPolicy Context,
    ICallLog Log);

public sealed record AgentResult(
    string FinalText,
    string StopReason,
    int Turns,
    Usage Usage,
    IReadOnlyList<Message> History);

/// <summary>
/// The single agent loop used by EVERY configuration: send the context, run the requested tools,
/// feed the results back, repeat. Topologies (hub-and-spoke, coder-reviewer, ...) are built by running
/// this loop several times with different specs; the loop itself never changes.
/// </summary>
public static class AgentLoop
{
    public static async Task<AgentResult> RunAsync(
        AgentSpec spec, string task, RunContext run, CancellationToken ct = default)
    {
        var history = new List<Message> { Message.User(task) };
        var usage = Usage.Zero;
        var finalText = string.Empty;

        for (var turn = 1; turn <= spec.MaxTurns; turn++)
        {
            if (run.Budget.Exceeded)
            {
                return Finish(StopReasons.BudgetExceeded, turn - 1);
            }

            var context = run.Context.Apply(spec.System, spec.Tools.Tools, history);
            if (context.Overflow)
            {
                return Finish(StopReasons.ContextOverflow, turn - 1);
            }

            var request = new ModelRequest(spec.System, context.Messages, spec.Tools.Tools, spec.MaxTokensPerCall);
            var clock = Stopwatch.StartNew();
            var response = await run.Model.SendAsync(request, ct).ConfigureAwait(false);
            clock.Stop();

            var cost = run.Budget.Record(response.Usage);
            usage += response.Usage;
            history.Add(new Message("assistant", response.Content));

            var toolUses = response.Content.OfType<ToolUseBlock>().ToList();
            run.Log.Write(new CallRecord(
                run.RunId, run.Config, spec.AgentId, spec.Role, turn, DateTimeOffset.UtcNow,
                clock.ElapsedMilliseconds, response.Usage, cost, context.EstimatedPromptTokens,
                context.StubbedToolResults, toolUses.Count, response.StopReason));

            finalText = string.Concat(response.Content.OfType<TextBlock>().Select(t => t.Text));

            if (toolUses.Count == 0)
            {
                return Finish(StopReasons.EndTurn, turn);
            }

            // Tool calls run one after another, which keeps runs reproducible.
            var results = new List<ContentBlock>(toolUses.Count);
            foreach (var use in toolUses)
            {
                results.Add(await ExecuteAsync(spec.Tools, use, ct).ConfigureAwait(false));
            }

            history.Add(new Message("user", results));
        }

        return Finish(StopReasons.MaxTurns, spec.MaxTurns);

        AgentResult Finish(string reason, int turns) => new(finalText, reason, turns, usage, history);
    }

    private static async Task<ToolResultBlock> ExecuteAsync(IToolHost host, ToolUseBlock use, CancellationToken ct)
    {
        try
        {
            var output = await host.CallAsync(use.Name, use.Input, ct).ConfigureAwait(false);
            return new ToolResultBlock(use.Id, output.Content, output.IsError);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failing tool is information for the model, not a crash of the experiment.
            return new ToolResultBlock(use.Id, $"Tool '{use.Name}' failed: {ex.Message}", true);
        }
    }
}
