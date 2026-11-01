using System.Text.Json.Nodes;
using Agents.Core;
using Agents.Core.Execution;
using Agents.Core.Tools;
using Agents.Mcp;

/// <summary>
/// Free, Docker-less equivalence check of the two tool transports (the hard rule of the comparison):
/// the MCP server must advertise exactly the <see cref="ToolCatalog"/> tools and return exactly what the direct host
/// returns for the same calls. Runs both against throw-away local folders (needs bash; on Windows use WSL).
/// </summary>
internal static class McpCheck
{
    public static async Task<int> RunAsync(McpServerLaunch launch, CancellationToken ct)
    {
        var failures = new List<string>();
        var directDir = NewDir();
        var mcpDir = NewDir();
        try
        {
            var direct = new ContainerToolHost(new LocalExecutor(directDir));
            var mcpExecutor = new LocalExecutor(mcpDir);
            await using var mcp = await McpToolHost.ConnectAsync(launch, ToolServerSpec.For(mcpExecutor), ct);

            // 1. Same tools, same order, same text, same schemas.
            if (mcp.Tools.Count != ToolCatalog.All.Count)
            {
                failures.Add($"tool count: server {mcp.Tools.Count}, catalog {ToolCatalog.All.Count}");
            }

            for (var i = 0; i < Math.Min(mcp.Tools.Count, ToolCatalog.All.Count); i++)
            {
                var (a, b) = (ToolCatalog.All[i], mcp.Tools[i]);
                if (a.Name != b.Name || a.Description != b.Description || !JsonNode.DeepEquals(a.InputSchema, b.InputSchema))
                {
                    failures.Add($"definition differs for '{a.Name}'");
                }
            }

            Console.WriteLine($"Definitions: {mcp.Tools.Count} tools, {mcp.Tools.Sum(t => t.Name.Length + t.Description.Length + t.InputSchema.ToJsonString().Length)} chars.");

            // 2. Same behaviour on the same calls (successes and errors).
            var script = new (string Tool, string Json)[]
            {
                (ToolNames.WriteFile, """{"path":"pkg/a.txt","content":"one\ntwo\ntwo\n"}"""),
                (ToolNames.ReadFile, """{"path":"pkg/a.txt"}"""),
                (ToolNames.ReadFile, """{"path":"pkg/a.txt","start_line":2,"end_line":2}"""),
                (ToolNames.ListDir, """{"path":"pkg"}"""),
                (ToolNames.ListDir, """{}"""),
                (ToolNames.EditFile, """{"path":"pkg/a.txt","old_string":"one","new_string":"uno"}"""),
                (ToolNames.EditFile, """{"path":"pkg/a.txt","old_string":"two","new_string":"dos"}"""),
                (ToolNames.EditFile, """{"path":"pkg/a.txt","old_string":"zzz","new_string":"y"}"""),
                (ToolNames.ReadFile, """{"path":"missing.txt"}"""),
                (ToolNames.ReadFile, """{"path":"pkg/a.txt","start_line":"2"}"""),
                (ToolNames.ReadFile, """{}"""),
                (ToolNames.RunCommand, """{"command":"cat pkg/a.txt && echo done"}"""),
                (ToolNames.RunCommand, """{"command":"exit 3"}"""),
                (ToolNames.RunTests, """{"command":"echo tests ok"}"""),
                (ToolNames.WriteFile, """{"path":"unicode.txt","content":"zażółć \"quoted\" \\ back"}"""),
                (ToolNames.ReadFile, """{"path":"unicode.txt"}"""),
                ("no_such_tool", """{}"""),
            };

            foreach (var (tool, json) in script)
            {
                var input = JsonNode.Parse(json)!.AsObject();
                var expected = await direct.CallAsync(tool, JsonNode.Parse(json)!.AsObject(), ct);
                var actual = await mcp.CallAsync(tool, input, ct);
                var same = expected.Content == actual.Content && expected.IsError == actual.IsError;
                Console.WriteLine($"  {(same ? "ok  " : "DIFF")} {tool} {json}");
                if (!same)
                {
                    failures.Add($"{tool} {json}\n    direct: error={expected.IsError} {expected.Content}\n    mcp:    error={actual.IsError} {actual.Content}");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failures.Add("exception: " + ex);
        }
        finally
        {
            await ToolHostTracker.Shared.ReleaseAsync(new LocalExecutor(mcpDir));
            TryDelete(directDir);
            TryDelete(mcpDir);
        }

        if (failures.Count == 0)
        {
            Console.WriteLine("OK: the MCP server and the direct host are equivalent on this script.");
            return 0;
        }

        Console.Error.WriteLine("FAILED:\n- " + string.Join("\n- ", failures));
        return 1;
    }

    private static string NewDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "mamcp-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}
