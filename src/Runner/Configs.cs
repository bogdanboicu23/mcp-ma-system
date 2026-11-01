using Agents.Core.Runs;
using Agents.Core.Swe;
using Agents.Core.Tools;
using Agents.Topologies;

/// <summary>Maps a configuration name to its system. New configurations are added here as they are built.</summary>
internal static class Configs
{
    public static ISystem Create(
        string config, PromptSet prompts, RunLimits limits, HubOptions? hub = null, int reviewRounds = 3) =>
        config.ToUpperInvariant() switch
        {
            "A" => new SingleAgentSystem("A", prompts, executor => new ContainerToolHost(executor), limits.MaxTurns),
            "B" => throw new NotSupportedException(
                "Config B (single agent + MCP) needs the MCP server, which is workstream 3 and not built yet."),
            "C" => new HubAndSpokeSystem(
                "C", prompts,
                (executor, allowed) => new ContainerToolHost(executor, allowed),
                (hub ?? new HubOptions()) with { AgentMaxTurns = limits.MaxTurns }),
            "D" => new CoderReviewerSystem(
                "D", prompts, (executor, allowed) => new ContainerToolHost(executor, allowed),
                new ReviewOptions(reviewRounds, limits.MaxTurns)),
            "E" => new SequentialSystem(
                "E", prompts, (executor, allowed) => new ContainerToolHost(executor, allowed),
                new SequentialOptions(limits.MaxTurns)),
            _ => throw new ArgumentException($"Unknown config '{config}'. Available now: A, C, D, E (B needs the MCP server)."),
        };
}
