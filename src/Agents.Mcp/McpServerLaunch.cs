using Agents.Core.Tools;

namespace Agents.Mcp;

/// <summary>How to start the tool server process: <c>dotnet &lt;Mcp.Server.dll&gt; --spec &lt;base64&gt;</c>.</summary>
public sealed record McpServerLaunch(string ServerDll, string Command = "dotnet")
{
    public IEnumerable<string> ArgumentsFor(ToolServerSpec spec) =>
        new[] { ServerDll, ToolServerSpec.ArgName, spec.Encode() };

    /// <summary>
    /// Finds the built server: the explicit path, else <c>Mcp.Server.dll</c> next to the Runner, else the newest
    /// <c>src/Mcp.Server/bin/*/net8.0/Mcp.Server.dll</c> found by walking up from the Runner's folder.
    /// </summary>
    public static McpServerLaunch Locate(string? explicitPath = null)
    {
        if (explicitPath is not null)
        {
            return File.Exists(explicitPath)
                ? new McpServerLaunch(Path.GetFullPath(explicitPath))
                : throw new FileNotFoundException("MCP server not found at " + explicitPath);
        }

        var beside = Path.Combine(AppContext.BaseDirectory, "Mcp.Server.dll");
        if (File.Exists(beside))
        {
            return new McpServerLaunch(beside);
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var bin = Path.Combine(dir.FullName, "src", "Mcp.Server", "bin");
            if (!Directory.Exists(bin))
            {
                continue;
            }

            var newest = Directory.GetFiles(bin, "Mcp.Server.dll", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is not null)
            {
                return new McpServerLaunch(newest);
            }
        }

        throw new FileNotFoundException(
            "Mcp.Server.dll not found. Build it with 'dotnet build src/Mcp.Server' or pass --mcp-server <path>.");
    }
}
