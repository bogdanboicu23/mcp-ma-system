using Agents.Core;
using System.Text.Json.Nodes;

namespace Agents.Topologies;

/// <summary>
/// The orchestrator's tool set: the catalog tools it is allowed (served by the inner host, i.e. directly or via MCP)
/// plus <c>delegate_task</c>. delegate_task is a topology tool handled in-process, NOT part of the shared
/// ToolCatalog: it is agent-layer cost, not MCP-layer cost, in the RQ2 attribution.
/// </summary>
public sealed class DelegationToolHost : IToolHost
{
    public const string DelegateTool = "delegate_task";

    private readonly IToolHost _inner;
    private readonly Func<JsonObject, CancellationToken, Task<ToolOutput>> _delegate;

    public DelegationToolHost(
        IToolHost inner, int maxWorkers, Func<JsonObject, CancellationToken, Task<ToolOutput>> delegateHandler)
    {
        _inner = inner;
        _delegate = delegateHandler;
        Tools = inner.Tools.Append(Definition(maxWorkers)).ToList();
    }

    public IReadOnlyList<ToolDef> Tools { get; }

    public Task<ToolOutput> CallAsync(string name, JsonObject input, CancellationToken ct = default) =>
        name == DelegateTool ? _delegate(input, ct) : _inner.CallAsync(name, input, ct);

    public static ToolDef Definition(int maxWorkers) => new(
        DelegateTool,
        $"Hand sub-tasks to workers. Up to {maxWorkers} tasks per call run in parallel; each worker returns a short summary.",
        JsonNode.Parse(Schema.Replace("__MAX__", maxWorkers.ToString(System.Globalization.CultureInfo.InvariantCulture)))!.AsObject());

    private const string Schema = """
        {"type":"object","properties":{
          "tasks":{"type":"array","minItems":1,"maxItems":__MAX__,"items":{"type":"object","properties":{
            "instructions":{"type":"string","description":"Self-contained instructions for this worker."},
            "files":{"type":"array","items":{"type":"string"},"description":"Files (or directories ending in /) this worker may modify. Must not overlap with other tasks."}},
           "required":["instructions"]}}},
         "required":["tasks"]}
        """;
}
