using System.Text.Json;
using System.Text.Json.Nodes;
using Agents.Core;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Agents.Mcp;

/// <summary>
/// The tools of one agent, reached over MCP: one stdio client connected to its own server process, so every agent
/// loads its own tool definitions (the cost mechanism of hypothesis H2). The tool list is read from the server,
/// never copied from <c>ToolCatalog</c>, so what the model sees is what the server really advertises.
/// </summary>
public sealed class McpToolHost : IToolHost, IAsyncDisposable
{
    private readonly McpClient _client;

    private McpToolHost(McpClient client, IReadOnlyList<ToolDef> tools)
    {
        _client = client;
        Tools = tools;
    }

    public IReadOnlyList<ToolDef> Tools { get; }

    public static async Task<McpToolHost> ConnectAsync(
        McpServerLaunch launch, Agents.Core.Tools.ToolServerSpec spec, CancellationToken ct = default)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "mamcp-tools",
            Command = launch.Command,
            Arguments = launch.ArgumentsFor(spec).ToList(),
        });

        var client = await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
        try
        {
            var listed = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
            var tools = listed
                .Select(t => new ToolDef(
                    t.Name,
                    t.Description ?? string.Empty,
                    JsonNode.Parse(t.JsonSchema.GetRawText())?.AsObject() ?? new JsonObject()))
                .ToList();
            return new McpToolHost(client, tools);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ToolOutput> CallAsync(string name, JsonObject input, CancellationToken ct = default)
    {
        var arguments = new Dictionary<string, object?>();
        foreach (var (key, value) in input)
        {
            arguments[key] = value is null ? null : JsonSerializer.Deserialize<JsonElement>(value.ToJsonString());
        }

        var result = await _client.CallToolAsync(name, arguments, cancellationToken: ct).ConfigureAwait(false);
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        return new ToolOutput(text, result.IsError == true);
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
