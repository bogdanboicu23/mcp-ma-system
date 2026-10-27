using System.Diagnostics;
using Agents.Core.Swe;
using Agents.Core.Tools;

namespace Agents.Core.Runs;

/// <summary>
/// The limits that must be identical for every configuration. The defaults are PLACEHOLDERS to be calibrated in the
/// pilot (real cost per run), not measured values.
/// </summary>
public sealed record RunLimits(
    int MaxPromptTokens = 80_000,
    long MaxTotalTokens = 3_000_000,
    decimal MaxCostUsd = 0.50m,
    int MaxTurns = 50);

/// <summary>
/// One run = one task on one configuration: start a fresh environment, let the system work, extract the patch,
/// write everything down, remove the environment. Infrastructure failures are recorded, not thrown, so a batch continues.
/// </summary>
public sealed class RunOrchestrator
{
    private readonly IModelClient _model;
    private readonly ISystem _system;
    private readonly ITaskEnvironmentProvider _environments;
    private readonly RunStore _store;
    private readonly PromptSet _prompts;
    private readonly RunLimits _limits;
    private readonly string _harnessVersion;

    public RunOrchestrator(
        IModelClient model,
        ISystem system,
        ITaskEnvironmentProvider environments,
        RunStore store,
        PromptSet prompts,
        RunLimits limits,
        string harnessVersion)
    {
        _model = model;
        _system = system;
        _environments = environments;
        _store = store;
        _prompts = prompts;
        _limits = limits;
        _harnessVersion = harnessVersion;
    }

    public async Task<RunManifest> RunAsync(SweBenchTask task, int repeat, CancellationToken ct = default)
    {
        var runId = RunStore.MakeRunId(_system.Config, task.InstanceId, repeat);
        var dir = _store.RunDir(runId);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true); // a repeated or failed run starts from a clean folder
        }

        Directory.CreateDirectory(dir);

        var budget = new RunBudget(_limits.MaxTotalTokens, _limits.MaxCostUsd);
        var log = new JsonlCallLog(Path.Combine(dir, "calls.jsonl"));
        var run = new RunContext(runId, _system.Config, _model, budget, new ContextPolicy(_limits.MaxPromptTokens), log);

        var started = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        var status = "completed";
        string? error = null;
        var stopReason = string.Empty;
        var image = string.Empty;
        var turns = 0;
        var patch = string.Empty;

        try
        {
            await using var environment = await _environments.StartAsync(task, runId, ct).ConfigureAwait(false);
            image = environment.Description;

            var outcome = await _system
                .SolveAsync(_prompts.RenderTask(task), environment.Executor, run, ct)
                .ConfigureAwait(false);
            stopReason = outcome.StopReason;
            turns = outcome.Turns;

            patch = await PatchExtractor.GetPatchAsync(environment.Executor, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // no manifest is written, so the run is retried later
        }
        catch (Exception ex)
        {
            status = "error";
            error = ex.Message;
            stopReason = "error";
        }

        clock.Stop();
        File.WriteAllText(Path.Combine(dir, "patch.diff"), patch);

        var total = budget.Total;
        var manifest = new RunManifest
        {
            RunId = runId,
            Config = _system.Config,
            Repeat = repeat,
            InstanceId = task.InstanceId,
            Model = _model.ModelId,
            Image = image,
            StartedAt = started,
            DurationMs = clock.ElapsedMilliseconds,
            Status = status,
            Error = error,
            StopReason = stopReason,
            Turns = turns,
            InputTokens = total.InputTokens,
            OutputTokens = total.OutputTokens,
            CacheReadTokens = total.CacheReadTokens,
            CacheWriteTokens = total.CacheWriteTokens,
            CostUsd = budget.CostUsd,
            PatchChars = patch.Length,
            MaxPromptTokens = _limits.MaxPromptTokens,
            MaxTotalTokens = _limits.MaxTotalTokens,
            MaxCostUsd = _limits.MaxCostUsd,
            MaxTurns = _limits.MaxTurns,
            SystemPromptHash = _prompts.SystemHash,
            TaskPromptHash = _prompts.TaskHash,
            TopologyPromptHash = _prompts.TopologyHash,
            Variant = _system.Variant,
            HarnessVersion = _harnessVersion,
        };

        _store.WriteManifest(manifest); // written last: its presence marks the run as finished
        return manifest;
    }
}
