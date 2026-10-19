namespace Agents.Core;

/// <summary>
/// A model that replays prepared responses. Used for tests and for dry runs of the harness
/// (container lifecycle, logging, evaluator hand-off) without spending any API budget.
/// </summary>
public sealed class ScriptedModelClient : IModelClient
{
    private readonly Queue<ModelResponse> _script;

    public ScriptedModelClient(IEnumerable<ModelResponse> script, string modelId = "scripted")
    {
        _script = new Queue<ModelResponse>(script);
        ModelId = modelId;
    }

    public string ModelId { get; }

    /// <summary>Every request this model received, in order.</summary>
    public List<ModelRequest> Requests { get; } = new();

    public Task<ModelResponse> SendAsync(ModelRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        if (_script.Count == 0)
        {
            throw new InvalidOperationException("The scripted model ran out of responses.");
        }

        return Task.FromResult(_script.Dequeue());
    }
}
