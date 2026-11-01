namespace Agents.Core.Execution;

/// <summary>
/// Runs commands with bash in a local folder. For unit tests and local development only;
/// experiments always run inside a Docker container (see <see cref="DockerExecutor"/>).
/// </summary>
public sealed class LocalExecutor : ICommandExecutor
{
    private readonly string _root;

    public LocalExecutor(string rootDirectory)
    {
        _root = rootDirectory;
    }

    public string Root => _root;

    public Task<ExecResult> RunAsync(
        string command, TimeSpan timeout, string? stdin = null, CancellationToken ct = default) =>
        ProcessRunner.RunAsync("bash", new[] { "-c", command }, _root, stdin, timeout, ct);
}
