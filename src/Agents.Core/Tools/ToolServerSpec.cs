using System.Text;
using System.Text.Json;
using Agents.Core.Execution;

namespace Agents.Core.Tools;

/// <summary>
/// Everything an MCP tool-server process needs to know: where the commands run (a Docker container, or a local
/// folder for tests) and which of the six catalog tools it exposes. The spec travels as ONE base64 argument, so
/// shell quoting in the prelude (conda activation and the like) cannot be damaged on the way.
/// </summary>
public sealed record ToolServerSpec(
    string? Container,
    string WorkDir,
    string Prelude,
    string? LocalRoot,
    IReadOnlyList<string>? Tools)
{
    public const string ArgName = "--spec";

    public string Encode() => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this)));

    public static ToolServerSpec Decode(string encoded) =>
        JsonSerializer.Deserialize<ToolServerSpec>(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)))
        ?? throw new ArgumentException("Empty tool server spec.");

    /// <summary>Reads <c>--spec &lt;base64&gt;</c> from the command line of the server process.</summary>
    public static ToolServerSpec FromArgs(IReadOnlyList<string> args)
    {
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] == ArgName)
            {
                return Decode(args[i + 1]);
            }
        }

        throw new ArgumentException($"Missing {ArgName} <base64>.");
    }

    /// <summary>The spec that makes a server behave exactly like the given executor.</summary>
    public static ToolServerSpec For(ICommandExecutor executor, IReadOnlySet<string>? allowed = null)
    {
        // Catalog order, so the server advertises the tools in the same order as the direct host.
        var tools = allowed is null ? null : ToolCatalog.All.Select(t => t.Name).Where(allowed.Contains).ToList();
        return executor switch
        {
            DockerExecutor d => new ToolServerSpec(d.Container, d.WorkDir, d.Prelude, null, tools),
            LocalExecutor l => new ToolServerSpec(null, string.Empty, string.Empty, l.Root, tools),
            _ => throw new NotSupportedException(
                $"Cannot run an MCP server for executor type {executor.GetType().Name}."),
        };
    }

    public ICommandExecutor CreateExecutor() =>
        Container is not null
            ? new DockerExecutor(Container, WorkDir, Prelude)
            : new LocalExecutor(LocalRoot ?? throw new ArgumentException("The spec has neither a container nor a local root."));

    /// <summary>The same host class the direct configuration uses; the server only adds the transport.</summary>
    public ContainerToolHost CreateHost() =>
        new(CreateExecutor(), Tools is null ? null : new HashSet<string>(Tools));
}
