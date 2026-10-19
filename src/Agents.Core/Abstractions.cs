using System.Text.Json.Nodes;

namespace Agents.Core;

/// <summary>A model backend. The experiment code only ever talks to this interface.</summary>
public interface IModelClient
{
    string ModelId { get; }

    Task<ModelResponse> SendAsync(ModelRequest request, CancellationToken ct = default);
}

public sealed record ToolOutput(string Content, bool IsError = false);

/// <summary>
/// Where an agent's tools come from. Config A uses a direct (in-process) host,
/// configs B-E use an MCP-backed host. The agent loop cannot tell the difference,
/// which keeps the comparison between them fair.
/// </summary>
public interface IToolHost
{
    IReadOnlyList<ToolDef> Tools { get; }

    Task<ToolOutput> CallAsync(string name, JsonObject input, CancellationToken ct = default);
}
