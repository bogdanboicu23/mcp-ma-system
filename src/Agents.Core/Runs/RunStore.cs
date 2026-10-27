using System.Text.Json;

namespace Agents.Core.Runs;

/// <summary>
/// Everything known about one finished (or failed) run. One file per run: <c>results/runs/&lt;runId&gt;/manifest.json</c>.
/// It records the settings that must be identical across configurations, so fairness can be checked afterwards.
/// </summary>
public sealed record RunManifest
{
    public string RunId { get; init; } = string.Empty;

    public string Config { get; init; } = string.Empty;

    public int Repeat { get; init; }

    public string InstanceId { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public string Image { get; init; } = string.Empty;

    public DateTimeOffset StartedAt { get; init; }

    public long DurationMs { get; init; }

    /// <summary>"completed" (the agent ran and stopped, for whatever reason) or "error" (infrastructure failure).</summary>
    public string Status { get; init; } = "completed";

    public string? Error { get; init; }

    public string StopReason { get; init; } = string.Empty;

    public int Turns { get; init; }

    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public long CacheReadTokens { get; init; }

    public long CacheWriteTokens { get; init; }

    public decimal CostUsd { get; init; }

    public int PatchChars { get; init; }

    public int MaxPromptTokens { get; init; }

    public long MaxTotalTokens { get; init; }

    public decimal MaxCostUsd { get; init; }

    public int MaxTurns { get; init; }

    public string SystemPromptHash { get; init; } = string.Empty;

    public string TaskPromptHash { get; init; } = string.Empty;

    public string HarnessVersion { get; init; } = string.Empty;
}

/// <summary>The evaluator's verdict for one run, written by <c>Runner eval</c>.</summary>
public sealed record Verdict(bool Resolved, string EvalRunId, DateTimeOffset At);

/// <summary>Files on disk: one folder per run under <c>runs/</c>, plus predictions and evaluator output.</summary>
public sealed class RunStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public RunStore(string rootDirectory)
    {
        Root = Path.GetFullPath(rootDirectory);
    }

    public string Root { get; }

    public string RunsDir => Path.Combine(Root, "runs");

    public string PredictionsDir => Path.Combine(Root, "predictions");

    public string EvalDir => Path.Combine(Root, "eval");

    public static string MakeRunId(string config, string instanceId, int repeat) =>
        $"{config}.{instanceId}.r{repeat}";

    public string RunDir(string runId) => Path.Combine(RunsDir, runId);

    public string ManifestPath(string runId) => Path.Combine(RunDir(runId), "manifest.json");

    public string VerdictPath(string runId) => Path.Combine(RunDir(runId), "verdict.json");

    /// <summary>A run counts as done only if it ended normally. Infrastructure errors are retried by the batch command.</summary>
    public bool IsComplete(string runId) => ReadManifest(runId) is { Status: "completed" };

    public void WriteManifest(RunManifest manifest)
    {
        Directory.CreateDirectory(RunDir(manifest.RunId));
        WriteAtomically(ManifestPath(manifest.RunId), JsonSerializer.Serialize(manifest, Json));
    }

    public RunManifest? ReadManifest(string runId) => Read<RunManifest>(ManifestPath(runId));

    public IReadOnlyList<RunManifest> ListManifests()
    {
        if (!Directory.Exists(RunsDir))
        {
            return Array.Empty<RunManifest>();
        }

        return Directory.EnumerateDirectories(RunsDir)
            .Select(dir => Read<RunManifest>(Path.Combine(dir, "manifest.json")))
            .Where(m => m is not null)
            .Select(m => m!)
            .ToList();
    }

    public void WriteVerdict(string runId, Verdict verdict)
    {
        Directory.CreateDirectory(RunDir(runId));
        WriteAtomically(VerdictPath(runId), JsonSerializer.Serialize(verdict, Json));
    }

    public Verdict? ReadVerdict(string runId) => Read<Verdict>(VerdictPath(runId));

    private static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
        }
        catch (JsonException)
        {
            return null; // a half-written file means the run did not finish
        }
    }

    // Write to a temp file and rename, so a crash can never leave a half-written manifest that looks complete.
    private static void WriteAtomically(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
