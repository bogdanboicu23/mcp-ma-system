using Agents.Core.Runs;
using Agents.Core.Swe;
using Agents.Core.Tools;

/// <summary>Maps a configuration name to its system. New configurations are added here as they are built.</summary>
internal static class Configs
{
    public static ISystem Create(string config, PromptSet prompts, RunLimits limits) =>
        config.ToUpperInvariant() switch
        {
            "A" => new SingleAgentSystem("A", prompts, executor => new ContainerToolHost(executor), limits.MaxTurns),
            "B" => throw new NotSupportedException(
                "Config B (single agent + MCP) needs the MCP server, which is workstream 3 and not built yet."),
            "C" or "D" or "E" => throw new NotSupportedException(
                $"Config {config} is a multi-agent topology (workstream 2) and is not built yet."),
            _ => throw new ArgumentException($"Unknown config '{config}'. Available now: A."),
        };
}
