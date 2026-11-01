using System.Text.RegularExpressions;
using Agents.Core.Execution;
using Agents.Core.Runs;

namespace Agents.Core.Swe;

/// <summary>
/// Environments for the synthetic suite (<c>tasks/synthetic/&lt;id&gt;/repo</c>): a container from the small
/// <c>mamcp-synth</c> image (python + git, built from <c>tools/synthetic/Dockerfile</c>), the task's repository copied
/// to /work and committed as the base. Same isolation as the SWE-bench containers: no network, memory and CPU limits.
/// </summary>
public sealed class SyntheticEnvironmentProvider : ITaskEnvironmentProvider
{
    public const string WorkDir = "/work";
    private readonly string _suiteDir;
    private readonly string _image;
    private readonly int _memoryGb;
    private readonly double _cpus;

    public SyntheticEnvironmentProvider(string suiteDir, string image = "mamcp-synth", int memoryGb = 2, double cpus = 1)
    {
        _suiteDir = suiteDir;
        _image = image;
        _memoryGb = memoryGb;
        _cpus = cpus;
    }

    public async Task<ITaskEnvironment> StartAsync(SweBenchTask task, string runId, CancellationToken ct)
    {
        var name = "mamcp-" + Regex.Replace(runId, "[^a-zA-Z0-9_.-]", "-");
        await ProcessRunner.RunAsync("docker", new[] { "rm", "-f", name }, null, null, TimeSpan.FromSeconds(60), ct)
            .ConfigureAwait(false);

        var container = await DockerContainer
            .StartAsync(_image, name, WorkDir, string.Empty, _memoryGb, _cpus, null, ct)
            .ConfigureAwait(false);
        try
        {
            await SeedAsync(container, Path.Combine(_suiteDir, task.InstanceId, "repo"), ct).ConfigureAwait(false);
            var status = await container.Executor
                .RunAsync("git status --porcelain && git rev-parse --short HEAD", TimeSpan.FromSeconds(60), null, ct)
                .ConfigureAwait(false);
            if (!status.Ok || status.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length != 1)
            {
                throw new InvalidOperationException("The synthetic repository is not clean after seeding: " + status.Output);
            }

            return new Environment(container, $"{_image} (synthetic, {status.StdOut.Trim()})");
        }
        catch
        {
            await container.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Copies a folder into /work and commits it, so the patch is exactly what the agent changes.</summary>
    internal static async Task SeedAsync(DockerContainer container, string repoDir, CancellationToken ct)
    {
        if (!Directory.Exists(repoDir))
        {
            throw new DirectoryNotFoundException($"Synthetic repository not found: {repoDir}");
        }

        await CopyAsync(container, repoDir, WorkDir, ct).ConfigureAwait(false);
        var init = await container.Executor.RunAsync(
            "git init -q && git add -A && git -c user.name=t -c user.email=t@t commit -qm base",
            TimeSpan.FromSeconds(60), null, ct).ConfigureAwait(false);
        if (!init.Ok)
        {
            throw new InvalidOperationException("git init failed in the synthetic container: " + init.Output);
        }
    }

    internal static async Task CopyAsync(DockerContainer container, string hostDir, string containerDir, CancellationToken ct)
    {
        await container.Executor.RunAsync($"mkdir -p {containerDir}", TimeSpan.FromSeconds(30), null, ct).ConfigureAwait(false);
        var copy = await ProcessRunner
            .RunAsync("docker", new[] { "cp", Path.GetFullPath(hostDir) + Path.DirectorySeparatorChar + ".", $"{container.Name}:{containerDir}" },
                null, null, TimeSpan.FromMinutes(2), ct)
            .ConfigureAwait(false);
        if (!copy.Ok)
        {
            throw new InvalidOperationException($"docker cp failed (exit {copy.ExitCode}): {copy.Output}");
        }
    }

    private sealed class Environment : ITaskEnvironment
    {
        private readonly DockerContainer _container;

        public Environment(DockerContainer container, string description)
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
/// Decides the synthetic tasks with their own hidden tests: a FRESH container, the base repository, the run's patch
/// applied, the task's hidden_tests copied in, then <c>python -m unittest</c>. Resolved = exit code 0.
/// An empty or non-applying patch is unresolved.
/// </summary>
public static class SyntheticEvaluator
{
    public const string TestCommand = "PYTHONPATH=. python -m unittest discover -s hidden_tests";

    public static async Task<(int Evaluated, int Resolved)> EvaluateAsync(
        string suiteDir, RunStore store, string config, int repeat, string image, string evalRunId, CancellationToken ct)
    {
        var evaluated = 0;
        var resolved = 0;
        var manifests = store.ListManifests()
            .Where(m => m.Config == config && m.Repeat == repeat && m.Status == "completed")
            .Where(m => Directory.Exists(Path.Combine(suiteDir, m.InstanceId)));

        foreach (var m in manifests)
        {
            var patch = File.ReadAllText(Path.Combine(store.RunDir(m.RunId), "patch.diff"));
            var ok = !string.IsNullOrWhiteSpace(patch)
                     && await PassesAsync(suiteDir, m.InstanceId, patch, image, "eval-" + m.RunId, ct).ConfigureAwait(false);
            store.WriteVerdict(m.RunId, new Verdict(ok, evalRunId, DateTimeOffset.UtcNow));
            evaluated++;
            resolved += ok ? 1 : 0;
        }

        return (evaluated, resolved);
    }

    public static async Task<bool> PassesAsync(
        string suiteDir, string instanceId, string patch, string image, string containerName, CancellationToken ct)
    {
        var name = "mamcp-" + Regex.Replace(containerName, "[^a-zA-Z0-9_.-]", "-");
        await using var container = await DockerContainer
            .StartAsync(image, name, SyntheticEnvironmentProvider.WorkDir, string.Empty, 2, 1, null, ct)
            .ConfigureAwait(false);
        var taskDir = Path.Combine(suiteDir, instanceId);
        await SyntheticEnvironmentProvider.SeedAsync(container, Path.Combine(taskDir, "repo"), ct).ConfigureAwait(false);

        var apply = await container.Executor
            .RunAsync("git apply --whitespace=nowarn -", TimeSpan.FromSeconds(60), patch, ct).ConfigureAwait(false);
        if (!apply.Ok)
        {
            return false;
        }

        await SyntheticEnvironmentProvider.CopyAsync(container, Path.Combine(taskDir, "hidden_tests"), "/work/hidden_tests", ct)
            .ConfigureAwait(false);
        var tests = await container.Executor
            .RunAsync(TestCommand, TimeSpan.FromMinutes(5), null, ct).ConfigureAwait(false);
        return tests.Ok;
    }
}
