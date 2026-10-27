using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agents.Core.Runs;

public sealed record PredictionsFile(string Path, int Count, IReadOnlyList<RunManifest> Runs, string ModelName);

/// <summary>Builds the <c>predictions.jsonl</c> the official SWE-bench evaluator reads.</summary>
public static class Predictions
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ModelName(string config) => $"mamcp-{config}";

    /// <summary>One file per configuration and repeat, because the evaluator expects one prediction per instance.</summary>
    public static PredictionsFile Build(RunStore store, string config, int repeat)
    {
        var runs = store.ListManifests()
            .Where(m => m.Config == config && m.Repeat == repeat && m.Status == "completed")
            .OrderBy(m => m.InstanceId, StringComparer.Ordinal)
            .ToList();

        Directory.CreateDirectory(store.PredictionsDir);
        var path = System.IO.Path.Combine(store.PredictionsDir, $"{config}-r{repeat}.jsonl");

        var text = new StringBuilder();
        foreach (var run in runs)
        {
            var patchPath = System.IO.Path.Combine(store.RunDir(run.RunId), "patch.diff");
            var patch = File.Exists(patchPath) ? File.ReadAllText(patchPath) : string.Empty;
            var line = new Dictionary<string, string>
            {
                ["instance_id"] = run.InstanceId,
                ["model_name_or_path"] = ModelName(config),
                ["model_patch"] = patch,
            };
            text.Append(JsonSerializer.Serialize(line, Options)).Append('\n');
        }

        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        return new PredictionsFile(path, runs.Count, runs, ModelName(config));
    }
}

public sealed record EvalResults(IReadOnlySet<string> Resolved, IReadOnlySet<string> Evaluated);

/// <summary>
/// Reads what the official evaluator wrote. Two layouts are supported because the exact file layout of the installed
/// <c>swebench</c> version is unconfirmed: a summary file (<c>*.&lt;run_id&gt;.json</c> with resolved_ids, ...) and
/// per-instance <c>report.json</c> files under <c>logs/</c>.
/// </summary>
public static class EvalResultsParser
{
    public static EvalResults ParseSummary(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("Empty evaluation summary.");

        HashSet<string> Ids(string key) =>
            root[key] is JsonArray array
                ? array.Select(node => node!.GetValue<string>()).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

        var resolved = Ids("resolved_ids");
        var evaluated = new HashSet<string>(resolved, StringComparer.Ordinal);
        evaluated.UnionWith(Ids("unresolved_ids"));
        evaluated.UnionWith(Ids("completed_ids"));
        return new EvalResults(resolved, evaluated);
    }

    public static EvalResults ParseInstanceReports(IEnumerable<string> jsonDocuments)
    {
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        var evaluated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var json in jsonDocuments)
        {
            var root = JsonNode.Parse(json)?.AsObject();
            if (root is null)
            {
                continue;
            }

            foreach (var (instanceId, node) in root)
            {
                if (node is not JsonObject report)
                {
                    continue;
                }

                evaluated.Add(instanceId);
                if (report["resolved"] is JsonValue value && value.TryGetValue<bool>(out var isResolved) && isResolved)
                {
                    resolved.Add(instanceId);
                }
            }
        }

        return new EvalResults(resolved, evaluated);
    }

    /// <summary>Finds the evaluator's output for one run id inside the evaluation working directory.</summary>
    public static EvalResults Load(string evalDirectory, string evalRunId)
    {
        if (Directory.Exists(evalDirectory))
        {
            var summary = Directory
                .EnumerateFiles(evalDirectory, $"*.{evalRunId}.json", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
            if (summary is not null)
            {
                return ParseSummary(File.ReadAllText(summary));
            }

            var logs = System.IO.Path.Combine(evalDirectory, "logs");
            if (Directory.Exists(logs))
            {
                var reports = Directory.EnumerateFiles(logs, "report.json", SearchOption.AllDirectories)
                    .Where(p => p.Contains(evalRunId, StringComparison.Ordinal))
                    .ToList();
                if (reports.Count > 0)
                {
                    return ParseInstanceReports(reports.Select(File.ReadAllText));
                }
            }
        }

        throw new InvalidOperationException(
            $"No evaluation results found for run '{evalRunId}' under {evalDirectory}. " +
            "The evaluator may have failed; check its output above.");
    }
}
