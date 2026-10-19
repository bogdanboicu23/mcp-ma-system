using System.Text.Json.Nodes;
using Agents.Core.Execution;

namespace Agents.Core.Tools;

/// <summary>
/// Config A's tools: the six catalog tools executed directly through an <see cref="ICommandExecutor"/>
/// (a Docker container in experiments). The MCP server will wrap this same class, so tool behaviour is
/// identical in every configuration and only the transport differs.
/// </summary>
public sealed class ContainerToolHost : IToolHost
{
    public const int MaxReadChars = 12_000;
    public const int MaxCommandChars = 10_000;
    private const int DefaultCommandSeconds = 120;
    private const int DefaultTestSeconds = 600;
    private const int MaxSeconds = 900;

    private readonly ICommandExecutor _exec;
    private readonly IReadOnlySet<string> _allowed;

    /// <param name="allowedTools">
    /// Limits the tools this host exposes (role-restricted tool access). Null means all six.
    /// </param>
    public ContainerToolHost(ICommandExecutor executor, IReadOnlySet<string>? allowedTools = null)
    {
        _exec = executor;
        _allowed = allowedTools ?? ToolCatalog.AllNames;
        Tools = ToolCatalog.All.Where(t => _allowed.Contains(t.Name)).ToList();
    }

    public IReadOnlyList<ToolDef> Tools { get; }

    public async Task<ToolOutput> CallAsync(string name, JsonObject input, CancellationToken ct = default)
    {
        if (!_allowed.Contains(name))
        {
            return new ToolOutput($"Unknown tool '{name}'.", true);
        }

        try
        {
            return name switch
            {
                ToolNames.ReadFile => await ReadFileAsync(input, ct).ConfigureAwait(false),
                ToolNames.ListDir => await ListDirAsync(input, ct).ConfigureAwait(false),
                ToolNames.EditFile => await EditFileAsync(input, ct).ConfigureAwait(false),
                ToolNames.WriteFile => await WriteFileAsync(input, ct).ConfigureAwait(false),
                ToolNames.RunCommand => await RunAsync(input, DefaultCommandSeconds, ct).ConfigureAwait(false),
                ToolNames.RunTests => await RunAsync(input, DefaultTestSeconds, ct).ConfigureAwait(false),
                _ => new ToolOutput($"Unknown tool '{name}'.", true),
            };
        }
        catch (ArgumentException ex)
        {
            return new ToolOutput(ex.Message, true);
        }
    }

    private async Task<ToolOutput> ReadFileAsync(JsonObject input, CancellationToken ct)
    {
        var path = RequiredString(input, "path");
        var start = OptionalInt(input, "start_line") ?? 1;
        var end = OptionalInt(input, "end_line") ?? int.MaxValue;
        if (start < 1 || end < start)
        {
            return new ToolOutput("start_line must be >= 1 and end_line >= start_line.", true);
        }

        // Redirect instead of passing the path as an awk operand: awk would treat "--" or "a=b" specially.
        var command = $"awk 'NR>={start} && NR<={end} {{printf \"%6d\\t%s\\n\", NR, $0}}' < {Shell.Quote(path)}";
        var result = await _exec.RunAsync(command, TimeSpan.FromSeconds(30), null, ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            return new ToolOutput(Describe(result), true);
        }

        var text = result.StdOut;
        if (text.Length > MaxReadChars)
        {
            text = text[..MaxReadChars] + $"\n[output truncated at {MaxReadChars} chars; use start_line/end_line to read further]";
        }

        return new ToolOutput(text.Length == 0 ? "(empty file or range)" : text);
    }

    private async Task<ToolOutput> ListDirAsync(JsonObject input, CancellationToken ct)
    {
        var path = OptionalString(input, "path") ?? ".";
        var result = await _exec.RunAsync($"ls -1Ap -- {Shell.Quote(path)}", TimeSpan.FromSeconds(30), null, ct)
            .ConfigureAwait(false);
        return result.Ok
            ? new ToolOutput(OutputTrimmer.Trim(result.StdOut, MaxCommandChars))
            : new ToolOutput(Describe(result), true);
    }

    private async Task<ToolOutput> WriteFileAsync(JsonObject input, CancellationToken ct)
    {
        var path = RequiredString(input, "path");
        var content = RequiredString(input, "content", allowEmpty: true);
        return await WriteAsync(path, content, $"Wrote {content.Length} chars to {path}.", ct).ConfigureAwait(false);
    }

    private async Task<ToolOutput> EditFileAsync(JsonObject input, CancellationToken ct)
    {
        var path = RequiredString(input, "path");
        var oldText = RequiredString(input, "old_string");
        var newText = RequiredString(input, "new_string", allowEmpty: true);

        // Read the file exactly as stored; the edit is done here so the match rules are the same everywhere.
        var read = await _exec.RunAsync($"cat -- {Shell.Quote(path)}", TimeSpan.FromSeconds(30), null, ct)
            .ConfigureAwait(false);
        if (!read.Ok)
        {
            return new ToolOutput(Describe(read), true);
        }

        var original = read.StdOut;
        var first = original.IndexOf(oldText, StringComparison.Ordinal);
        if (first < 0)
        {
            return new ToolOutput($"old_string was not found in {path}. Read the file again and copy the text exactly.", true);
        }

        var count = 1;
        for (var i = original.IndexOf(oldText, first + oldText.Length, StringComparison.Ordinal);
             i >= 0;
             i = original.IndexOf(oldText, i + oldText.Length, StringComparison.Ordinal))
        {
            count++;
        }

        if (count > 1)
        {
            return new ToolOutput(
                $"old_string matches {count} places in {path}. Add surrounding lines so it matches exactly once.", true);
        }

        var edited = string.Concat(original.AsSpan(0, first), newText, original.AsSpan(first + oldText.Length));
        return await WriteAsync(path, edited, $"Edited {path}.", ct).ConfigureAwait(false);
    }

    private async Task<ToolOutput> WriteAsync(string path, string content, string success, CancellationToken ct)
    {
        var quoted = Shell.Quote(path);
        var command = $"mkdir -p -- \"$(dirname -- {quoted})\" && cat > {quoted}";
        var result = await _exec.RunAsync(command, TimeSpan.FromSeconds(30), content, ct).ConfigureAwait(false);
        return result.Ok ? new ToolOutput(success) : new ToolOutput(Describe(result), true);
    }

    private async Task<ToolOutput> RunAsync(JsonObject input, int defaultSeconds, CancellationToken ct)
    {
        var command = RequiredString(input, "command");
        var seconds = Math.Clamp(OptionalInt(input, "timeout_seconds") ?? defaultSeconds, 1, MaxSeconds);

        var result = await _exec.RunAsync(command, TimeSpan.FromSeconds(seconds), null, ct).ConfigureAwait(false);
        if (result.TimedOut)
        {
            var partial = OutputTrimmer.Trim(result.Output, MaxCommandChars);
            return new ToolOutput($"Command timed out after {seconds}s.\n{partial}", true);
        }

        // A non-zero exit code (for example failing tests) is normal information, not a tool error.
        var body = OutputTrimmer.Trim(result.Output, MaxCommandChars);
        return new ToolOutput($"exit code: {result.ExitCode}\n{body}");
    }

    private static string Describe(ExecResult result) =>
        result.TimedOut
            ? "The operation timed out."
            : $"Failed (exit code {result.ExitCode}): {OutputTrimmer.Trim(result.Output, 2_000)}";

    private static string RequiredString(JsonObject input, string key, bool allowEmpty = false)
    {
        if (input[key] is JsonValue v && v.TryGetValue<string>(out var s) && (allowEmpty || s.Length > 0))
        {
            return s;
        }

        throw new ArgumentException($"Missing or invalid argument '{key}'.");
    }

    private static string? OptionalString(JsonObject input, string key) =>
        input[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static int? OptionalInt(JsonObject input, string key)
    {
        if (input[key] is not JsonValue v)
        {
            return null;
        }

        if (v.TryGetValue<int>(out var i))
        {
            return i;
        }

        if (v.TryGetValue<long>(out var l))
        {
            return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
        }

        if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var parsed))
        {
            return parsed;
        }

        throw new ArgumentException($"Argument '{key}' must be an integer.");
    }
}
