namespace Agents.Core.Execution;

/// <summary>
/// Runs commands inside a running container with <c>docker exec</c>.
///
/// The command is wrapped in the container's own <c>timeout</c>: killing the local <c>docker exec</c> process
/// does NOT stop what runs inside the container, so a hung test would otherwise keep eating CPU and
/// distort the timing of later steps. Exit code 124 means the in-container timeout fired.
/// </summary>
public sealed class DockerExecutor : ICommandExecutor
{
    private const int TimeoutExitCode = 124;
    private static readonly TimeSpan ClientGrace = TimeSpan.FromSeconds(30);

    private readonly string _container;
    private readonly string _workDir;
    private readonly string _prelude;

    /// <param name="prelude">
    /// Shell text placed before every command, for example activating the SWE-bench conda environment.
    /// It must end so that the command can follow it (for example with "&amp;&amp; ").
    /// </param>
    public DockerExecutor(string container, string workDir = "/testbed", string prelude = "")
    {
        _container = container;
        _workDir = workDir;
        _prelude = prelude;
    }

    public async Task<ExecResult> RunAsync(
        string command, TimeSpan timeout, string? stdin = null, CancellationToken ct = default)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));
        var result = await ProcessRunner
            .RunAsync("docker", BuildArgs(command, seconds), null, stdin, timeout + ClientGrace, ct)
            .ConfigureAwait(false);

        return result.ExitCode == TimeoutExitCode ? result with { TimedOut = true } : result;
    }

    internal IReadOnlyList<string> BuildArgs(string command, int timeoutSeconds) => new[]
    {
        "exec", "-i", "-w", _workDir, _container,
        "timeout", "-k", "5", timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "bash", "-c", _prelude + command,
    };
}
