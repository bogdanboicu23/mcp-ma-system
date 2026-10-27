using Agents.Core.Execution;
using Agents.Core.Swe;

namespace Agents.Core.Runs;

public sealed record SolveOutcome(string StopReason, int Turns);

/// <summary>
/// One configuration of the experiment (A, B, C, ...). The orchestrator treats all of them the same way:
/// give it the task text, a place to work and the shared run context, and it edits the repository.
/// A multi-agent topology implements this interface by running several <see cref="AgentLoop"/>s.
/// </summary>
public interface ISystem
{
    string Config { get; }

    /// <summary>Free-text parameters of the configuration (worker count, tool exposure), stored in the manifest.</summary>
    string Variant => string.Empty;

    Task<SolveOutcome> SolveAsync(
        string taskPrompt, ICommandExecutor executor, RunContext run, CancellationToken ct = default);
}

/// <summary>Config A (and, with an MCP-backed tool factory, config B): one agent with the full toolset.</summary>
public sealed class SingleAgentSystem : ISystem
{
    private readonly PromptSet _prompts;
    private readonly Func<ICommandExecutor, IToolHost> _tools;
    private readonly int _maxTurns;
    private readonly int _maxTokensPerCall;

    public SingleAgentSystem(
        string config,
        PromptSet prompts,
        Func<ICommandExecutor, IToolHost> tools,
        int maxTurns = 50,
        int maxTokensPerCall = 4096)
    {
        Config = config;
        _prompts = prompts;
        _tools = tools;
        _maxTurns = maxTurns;
        _maxTokensPerCall = maxTokensPerCall;
    }

    public string Config { get; }

    public async Task<SolveOutcome> SolveAsync(
        string taskPrompt, ICommandExecutor executor, RunContext run, CancellationToken ct = default)
    {
        var agent = new AgentSpec("agent-1", "solo", _prompts.SystemPrompt, _tools(executor), _maxTurns, _maxTokensPerCall);
        var result = await AgentLoop.RunAsync(agent, taskPrompt, run, ct).ConfigureAwait(false);
        return new SolveOutcome(result.StopReason, result.Turns);
    }
}
