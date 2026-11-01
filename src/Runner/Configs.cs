using Agents.Core;
using Agents.Core.Execution;
using Agents.Core.Runs;
using Agents.Core.Swe;
using Agents.Core.Tools;
using Agents.Mcp;
using Agents.Topologies;

/// <summary>How an agent reaches its tools: calling the host in-process, or through an MCP server of its own.</summary>
internal enum ToolTransport
{
    Direct,
    Mcp,
}

/// <summary>Maps a configuration name to its system. New configurations are added here as they are built.</summary>
internal static class Configs
{
    /// <summary>A is the direct baseline, B is A over MCP, C/D/E use MCP unless <c>--transport direct</c> is given.</summary>
    public static ToolTransport DefaultTransport(string config) =>
        config.ToUpperInvariant() is "A" ? ToolTransport.Direct : ToolTransport.Mcp;

    public static ISystem Create(
        string config,
        PromptSet prompts,
        RunLimits limits,
        HubOptions? hub = null,
        int reviewRounds = 3,
        ToolTransport? transport = null,
        McpToolHostFactory? mcp = null)
    {
        var name = config.ToUpperInvariant();
        var how = transport ?? DefaultTransport(name);
        if (name == "A" && how == ToolTransport.Mcp)
        {
            throw new ArgumentException("Config A is the direct baseline. Use config B for a single agent over MCP.");
        }

        if (name == "B" && how == ToolTransport.Direct)
        {
            throw new ArgumentException("Config B is defined as MCP tools. Use config A for direct tools.");
        }

        if (how == ToolTransport.Mcp && mcp is null)
        {
            throw new ArgumentException("MCP transport needs the MCP server (McpToolHostFactory).");
        }

        Func<ICommandExecutor, IReadOnlySet<string>?, IToolHost> tools = how == ToolTransport.Mcp
            ? mcp!.Create
            : (executor, allowed) => new ContainerToolHost(executor, allowed);

        ISystem system = name switch
        {
            "A" or "B" => new SingleAgentSystem(name, prompts, executor => tools(executor, null), limits.MaxTurns),
            "C" => new HubAndSpokeSystem(
                "C", prompts, (executor, allowed) => tools(executor, allowed),
                (hub ?? new HubOptions()) with { AgentMaxTurns = limits.MaxTurns }),
            "D" => new CoderReviewerSystem(
                "D", prompts, (executor, allowed) => tools(executor, allowed),
                new ReviewOptions(reviewRounds, limits.MaxTurns)),
            "E" => new SequentialSystem(
                "E", prompts, (executor, allowed) => tools(executor, allowed),
                new SequentialOptions(limits.MaxTurns)),
            _ => throw new ArgumentException($"Unknown config '{config}'. Available: A, B, C, D, E."),
        };

        return new TransportTagged(system, how);
    }

    /// <summary>Adds the transport to the system's variant, so the run manifest records how tools were reached.</summary>
    private sealed class TransportTagged : ISystem
    {
        private readonly ISystem _inner;
        private readonly ToolTransport _transport;

        public TransportTagged(ISystem inner, ToolTransport transport)
        {
            _inner = inner;
            _transport = transport;
        }

        public string Config => _inner.Config;

        public string Variant =>
            string.IsNullOrEmpty(_inner.Variant)
                ? $"transport={_transport.ToString().ToLowerInvariant()}"
                : $"{_inner.Variant} transport={_transport.ToString().ToLowerInvariant()}";

        public Task<SolveOutcome> SolveAsync(
            string taskPrompt, ICommandExecutor executor, RunContext run, CancellationToken ct = default) =>
            _inner.SolveAsync(taskPrompt, executor, run, ct);
    }
}
