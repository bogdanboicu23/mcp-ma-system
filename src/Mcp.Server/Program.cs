using System.Text.Json;
using System.Text.Json.Nodes;
using Agents.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// MCP tool server for the experiment: the six ToolCatalog tools over stdio, executed by the SAME ContainerToolHost
// that config A calls directly. It advertises the catalog verbatim (no SDK-generated schemas) and adds no behaviour,
// so the only difference between A and B is the transport. stdout carries the protocol; every log goes to stderr.
//
// Usage (started by the Runner, not by hand): dotnet Mcp.Server.dll --spec <base64 ToolServerSpec>

var spec = ToolServerSpec.FromArgs(args);
var host = spec.CreateHost();

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services
    .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "mamcp-tools", Version = "1.0.0" })
    .WithStdioServerTransport()
    .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
    {
        Tools = host.Tools.Select(t => new Tool
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = JsonSerializer.SerializeToElement(t.InputSchema),
        }).ToList(),
    }))
    .WithCallToolHandler(async (context, ct) =>
    {
        var name = context.Params?.Name ?? string.Empty;
        var input = new JsonObject();
        if (context.Params?.Arguments is { } arguments)
        {
            foreach (var (key, value) in arguments)
            {
                input[key] = JsonNode.Parse(value.GetRawText());
            }
        }

        var output = await host.CallAsync(name, input, ct).ConfigureAwait(false);
        return new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = output.Content } },
            IsError = output.IsError,
        };
    });

await builder.Build().RunAsync().ConfigureAwait(false);
