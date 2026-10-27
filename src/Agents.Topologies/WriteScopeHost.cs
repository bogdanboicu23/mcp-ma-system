using Agents.Core;
using System.Text.Json.Nodes;
using Agents.Core.Tools;

namespace Agents.Topologies;

/// <summary>
/// Limits where a worker may WRITE when several workers share one repository checkout.
/// Reading and running commands are never restricted; only edit_file and write_file are checked, and only when a
/// scope was given. Tool definitions are passed through unchanged (same text, same token cost).
/// A rejected write is an ordinary tool error: the worker sees it and the call log counts it.
/// </summary>
public sealed class WriteScopeHost : IToolHost
{
    private readonly IToolHost _inner;
    private readonly IReadOnlyList<string> _scope;

    public WriteScopeHost(IToolHost inner, IReadOnlyList<string> scope)
    {
        _inner = inner;
        _scope = scope.Select(NormalizeScopeEntry).ToList();
    }

    public IReadOnlyList<ToolDef> Tools => _inner.Tools;

    /// <summary>Number of writes rejected because they fell outside the scope.</summary>
    public int Rejected { get; private set; }

    public Task<ToolOutput> CallAsync(string name, JsonObject input, CancellationToken ct = default)
    {
        if (_scope.Count > 0 && name is ToolNames.EditFile or ToolNames.WriteFile)
        {
            var path = input["path"]?.GetValue<string>() ?? string.Empty;
            if (!Allows(path))
            {
                Rejected++;
                return Task.FromResult(new ToolOutput(
                    $"Not allowed: '{path}' is outside your assigned files ({string.Join(", ", _scope)}). " +
                    "Another worker owns it; mention the problem in your summary instead.", true));
            }
        }

        return _inner.CallAsync(name, input, ct);
    }

    public bool Allows(string path)
    {
        var p = NormalizePath(path);
        return p is not null && _scope.Any(entry => Covers(entry, p));
    }

    /// <summary>True when two scopes could touch the same file.</summary>
    public static bool Overlap(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Select(NormalizeScopeEntry).Any(x => b.Select(NormalizeScopeEntry).Any(y => Covers(x, y) || Covers(y, x)));

    // An entry ending in '/' is a directory prefix; anything else is one exact file.
    private static bool Covers(string entry, string path) =>
        entry.EndsWith('/') ? path.StartsWith(entry, StringComparison.Ordinal) : path == entry;

    private static string NormalizeScopeEntry(string entry) => NormalizePath(entry) ?? entry.Trim();

    private static string? NormalizePath(string path)
    {
        var p = path.Trim().Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal))
        {
            p = p[2..];
        }

        // Absolute paths and parent references are never accepted, so a scope cannot be escaped.
        if (p.Length == 0 || p.StartsWith('/') || p.Split('/').Contains(".."))
        {
            return null;
        }

        return p;
    }
}
