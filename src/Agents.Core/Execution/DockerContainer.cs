using System.Globalization;

namespace Agents.Core.Execution;

/// <summary>
/// One throw-away container per run. It has no network, limited memory and CPU, and is removed afterwards.
/// This is the isolation the report promises for the MCP tools ("trusted server, limited access, isolated environment").
/// NOTE: needs a running Docker daemon, so it is exercised on the experiment PC, not in unit tests.
/// </summary>
public sealed class DockerContainer : IAsyncDisposable
{
    private DockerContainer(string name, DockerExecutor executor)
    {
        Name = name;
        Executor = executor;
    }

    public string Name { get; }

    public ICommandExecutor Executor { get; }

    public static async Task<DockerContainer> StartAsync(
        string image,
        string name,
        string workDir = "/testbed",
        string prelude = "",
        int memoryGb = 4,
        double cpus = 2,
        string? platform = null,
        CancellationToken ct = default)
    {
        var args = new List<string> { "run", "-d", "--rm", "--name", name };
        if (platform is not null)
        {
            args.AddRange(new[] { "--platform", platform });
        }

        args.AddRange(new[]
        {
            "--network", "none",
            "--memory", $"{memoryGb}g",
            "--cpus", cpus.ToString(CultureInfo.InvariantCulture),
            "-w", workDir,
            image, "sleep", "infinity",
        });

        var result = await ProcessRunner
            .RunAsync("docker", args, null, null, TimeSpan.FromMinutes(10), ct)
            .ConfigureAwait(false);
        if (!result.Ok)
        {
            throw new InvalidOperationException(
                $"docker run failed for image '{image}' (exit {result.ExitCode}): {result.Output}");
        }

        return new DockerContainer(name, new DockerExecutor(name, workDir, prelude));
    }

    public async ValueTask DisposeAsync()
    {
        // Best effort: a failed cleanup must never hide the experiment result.
        try
        {
            await ProcessRunner
                .RunAsync("docker", new[] { "rm", "-f", Name }, null, null, TimeSpan.FromSeconds(60), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }
}
