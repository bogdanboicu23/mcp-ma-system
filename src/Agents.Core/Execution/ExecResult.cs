namespace Agents.Core.Execution;

public sealed record ExecResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Ok => ExitCode == 0 && !TimedOut;

    /// <summary>What a person (or model) would see in a terminal: stdout, then stderr if there is any.</summary>
    public string Output
    {
        get
        {
            if (string.IsNullOrEmpty(StdErr))
            {
                return StdOut;
            }

            return StdOut.Length == 0 ? StdErr : StdOut.TrimEnd('\n') + "\n[stderr]\n" + StdErr;
        }
    }
}

/// <summary>Runs a shell command somewhere (a Docker container, a local folder in tests).</summary>
public interface ICommandExecutor
{
    Task<ExecResult> RunAsync(
        string command, TimeSpan timeout, string? stdin = null, CancellationToken ct = default);
}
