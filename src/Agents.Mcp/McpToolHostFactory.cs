using Agents.Core;
using Agents.Core.Execution;
using Agents.Core.Tools;

namespace Agents.Mcp;

/// <summary>
/// Plugs MCP into the topologies: the same <c>(executor, allowedTools)</c> factory shape they already use for the
/// direct host. Every call starts a NEW server process, so a hub with three workers runs four servers. The hosts are
/// registered with <see cref="ToolHostTracker.Shared"/> and closed by the orchestrator when the run ends.
/// </summary>
public sealed class McpToolHostFactory
{
    private readonly McpServerLaunch _launch;

    public McpToolHostFactory(McpServerLaunch launch)
    {
        _launch = launch;
    }

    public IToolHost Create(ICommandExecutor executor, IReadOnlySet<string>? allowed = null)
    {
        var spec = ToolServerSpec.For(executor, allowed);

        // The topologies build hosts synchronously; this is a console program with no sync context, so blocking is safe.
        var host = McpToolHost.ConnectAsync(_launch, spec).GetAwaiter().GetResult();
        return ToolHostTracker.Shared.Track(executor, host);
    }
}
