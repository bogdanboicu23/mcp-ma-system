using System.Text.RegularExpressions;
using Agents.Core.Execution;
using Agents.Core.Swe;

namespace Agents.Core.Runs;

/// <summary>Where one run happens. Disposing it removes everything (container or temp folder).</summary>
public interface ITaskEnvironment : IAsyncDisposable
{
    ICommandExecutor Executor { get; }

    /// <summary>Short text for the manifest, for example the image name.</summary>
    string Description { get; }
}

public interface ITaskEnvironmentProvider
{
    Task<ITaskEnvironment> StartAsync(SweBenchTask task, string runId, CancellationToken ct);
}

/// <summary>
/// Fail-fast checks before an agent starts: the repository must be exactly at the task's base commit and clean
/// (otherwise the extracted patch would contain things the agent did not write), and python must work through the
/// container prelude (the conda activation line is unconfirmed for the SWE-bench images).
/// </summary>
public static class EnvironmentVerifier
{
    public static async Task<string> VerifyAsync(
        ICommandExecutor executor, SweBenchTask task, bool requirePython = true, CancellationToken ct = default)
    {
        var git = await executor
            .RunAsync("git rev-parse HEAD && git status --porcelain", TimeSpan.FromSeconds(60), null, ct)
            .ConfigureAwait(false);
        if (!git.Ok)
        {
            throw new InvalidOperationException($"git is not usable in the task environment: {git.Output}");
        }

        var lines = git.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var head = lines.FirstOrDefault() ?? string.Empty;
        if (!string.Equals(head, task.BaseCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The repository is at {head} but the task expects base commit {task.BaseCommit}.");
        }

        if (lines.Length > 1)
        {
            throw new InvalidOperationException(
                "The repository is not clean at the start: " + string.Join(", ", lines.Skip(1).Take(5)));
        }

        var python = "python not checked";
        if (requirePython)
        {
            var result = await executor.RunAsync("python --version 2>&1", TimeSpan.FromSeconds(60), null, ct)
                .ConfigureAwait(false);
            if (!result.Ok)
            {
                throw new InvalidOperationException(
                    "python does not work through the container prelude (the conda activation may differ for this image): "
                    + result.Output);
            }

            python = result.Output.Trim();
        }

        return $"HEAD {head[..Math.Min(8, head.Length)]}, clean, {python}";
    }
}

/// <summary>One Docker container per run, started from the task's SWE-bench instance image.</summary>
public sealed class DockerEnvironmentProvider : ITaskEnvironmentProvider
{
    private readonly SweBenchImageOptions _options;
    private readonly int _memoryGb;
    private readonly double _cpus;

    public DockerEnvironmentProvider(SweBenchImageOptions? options = null, int memoryGb = 4, double cpus = 2)
    {
        _options = options ?? new SweBenchImageOptions();
        _memoryGb = memoryGb;
        _cpus = cpus;
    }

    public async Task<ITaskEnvironment> StartAsync(SweBenchTask task, string runId, CancellationToken ct)
    {
        var image = SweBenchImage.Name(task.InstanceId, _options);
        var name = "mamcp-" + Regex.Replace(runId, "[^a-zA-Z0-9_.-]", "-");

        // A container left over from a crashed run would block the name.
        await ProcessRunner.RunAsync("docker", new[] { "rm", "-f", name }, null, null, TimeSpan.FromSeconds(60), ct)
            .ConfigureAwait(false);

        var container = await DockerContainer
            .StartAsync(image, name, _options.WorkDir, _options.Prelude, _memoryGb, _cpus, null, ct)
            .ConfigureAwait(false);
        try
        {
            var info = await EnvironmentVerifier.VerifyAsync(container.Executor, task, true, ct).ConfigureAwait(false);
            return new DockerEnvironment(container, $"{image} ({info})");
        }
        catch
        {
            await container.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class DockerEnvironment : ITaskEnvironment
    {
        private readonly DockerContainer _container;

        public DockerEnvironment(DockerContainer container, string description)
        {
            _container = container;
            Description = description;
        }

        public ICommandExecutor Executor => _container.Executor;

        public string Description { get; }

        public ValueTask DisposeAsync() => _container.DisposeAsync();
    }
}

/// <summary>
/// A throw-away git repository in a temp folder, built from a few files. For tests and for trying the harness
/// without Docker. Needs git and bash on the machine.
/// </summary>
public sealed class LocalEnvironmentProvider : ITaskEnvironmentProvider
{
    private readonly Func<SweBenchTask, IReadOnlyDictionary<string, string>> _files;

    public LocalEnvironmentProvider(Func<SweBenchTask, IReadOnlyDictionary<string, string>> files)
    {
        _files = files;
    }

    public async Task<ITaskEnvironment> StartAsync(SweBenchTask task, string runId, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mamcp-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (path, content) in _files(task))
        {
            var full = Path.Combine(dir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, content, ct).ConfigureAwait(false);
        }

        var executor = new LocalExecutor(dir);
        var init = await executor
            .RunAsync(
                "git init -q && git add -A && git -c user.name=t -c user.email=t@t commit -qm base",
                TimeSpan.FromSeconds(60), null, ct)
            .ConfigureAwait(false);
        if (!init.Ok)
        {
            throw new InvalidOperationException($"git init failed: {init.Output}");
        }

        return new LocalEnvironment(dir, executor);
    }

    private sealed class LocalEnvironment : ITaskEnvironment
    {
        private readonly string _dir;

        public LocalEnvironment(string dir, ICommandExecutor executor)
        {
            _dir = dir;
            Executor = executor;
        }

        public ICommandExecutor Executor { get; }

        public string Description => "local folder";

        public ValueTask DisposeAsync()
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }
}
