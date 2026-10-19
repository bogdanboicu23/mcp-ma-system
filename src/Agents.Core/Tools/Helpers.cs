using Agents.Core.Execution;

namespace Agents.Core.Tools;

public static class Shell
{
    /// <summary>Single-quotes a value for a POSIX shell, so paths with spaces or quotes stay one argument.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}

public static class OutputTrimmer
{
    /// <summary>
    /// Keeps the start and the end of long output (test failures usually show up at the end)
    /// and says how much was dropped.
    /// </summary>
    public static string Trim(string text, int maxChars, double headFraction = 0.3)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        var head = (int)(maxChars * headFraction);
        var tail = maxChars - head;
        var omitted = text.Length - head - tail;
        return string.Concat(text.AsSpan(0, head), $"\n[... {omitted} chars omitted ...]\n", text.AsSpan(text.Length - tail));
    }
}

public static class PatchExtractor
{
    /// <summary>
    /// The model's final answer: a unified diff of everything it changed in the repository, including new files
    /// (marked with intent-to-add first, otherwise git diff would not show them).
    /// </summary>
    public static async Task<string> GetPatchAsync(ICommandExecutor executor, CancellationToken ct = default)
    {
        await executor.RunAsync("git add -N . >/dev/null 2>&1; true", TimeSpan.FromSeconds(60), null, ct)
            .ConfigureAwait(false);

        var diff = await executor
            .RunAsync("git -c core.fileMode=false diff --no-color --no-ext-diff", TimeSpan.FromSeconds(120), null, ct)
            .ConfigureAwait(false);
        if (!diff.Ok)
        {
            throw new InvalidOperationException($"git diff failed (exit {diff.ExitCode}): {diff.Output}");
        }

        return diff.StdOut;
    }
}
