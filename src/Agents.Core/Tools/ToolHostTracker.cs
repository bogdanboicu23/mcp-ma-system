using Agents.Core.Execution;
using System.Collections.Concurrent;

namespace Agents.Core.Tools;

/// <summary>
/// Remembers which disposable tool hosts (MCP clients and their server processes) were opened for which executor,
/// so the orchestrator can close them when a run ends. Direct hosts hold nothing and are never tracked.
/// </summary>
public sealed class ToolHostTracker
{
    public static ToolHostTracker Shared { get; } = new();

    private readonly ConcurrentDictionary<ICommandExecutor, List<IAsyncDisposable>> _hosts =
        new(ReferenceEqualityComparer.Instance);

    public int Tracked => _hosts.Values.Sum(l => { lock (l) { return l.Count; } });

    public T Track<T>(ICommandExecutor executor, T host)
        where T : IAsyncDisposable
    {
        var list = _hosts.GetOrAdd(executor, _ => new List<IAsyncDisposable>());
        lock (list)
        {
            list.Add(host);
        }

        return host;
    }

    /// <summary>Disposes every host opened for the executor. Safe to call repeatedly; never throws.</summary>
    public async Task ReleaseAsync(ICommandExecutor executor)
    {
        if (!_hosts.TryRemove(executor, out var list))
        {
            return;
        }

        IAsyncDisposable[] copy;
        lock (list)
        {
            copy = list.ToArray();
        }

        foreach (var host in copy)
        {
            try
            {
                await host.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Cleanup must not turn a finished run into an error.
            }
        }
    }
}
