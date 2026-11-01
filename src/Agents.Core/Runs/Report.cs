using System.Globalization;
using System.Text;

namespace Agents.Core.Runs;

public sealed record ConfigSummary(
    string Config,
    int Runs,
    int Completed,
    int Errors,
    int Evaluated,
    int Resolved,
    decimal TotalCostUsd,
    decimal MeanCostUsd,
    double MeanTurns,
    decimal MeanMcpShare,
    double MeanAgents,
    double MeanCalls);

/// <summary>Joins manifests and verdicts into one table (one row per run) and per-configuration totals.</summary>
public static class ReportBuilder
{
    private static readonly string[] Columns =
    {
        "run_id", "config", "repeat", "instance_id", "status", "stop_reason", "turns", "resolved",
        "input_tokens", "output_tokens", "cache_read_tokens", "cache_write_tokens", "total_tokens",
        "cost_usd", "duration_s", "patch_chars", "calls", "agents",
        "def_tokens", "call_tokens", "res_tokens", "sys_tokens", "text_tokens",
        "sys_usd", "def_usd", "call_usd", "res_usd", "text_usd", "mcp_usd", "mcp_share", "variant", "error",
    };

    /// <summary>MCP layer = tool definitions + tool calls + tool results; share of the attributed cost.</summary>
    private static decimal McpUsd(RunManifest m) => m.AttrDefUsd + m.AttrCallUsd + m.AttrResUsd;

    private static decimal McpShare(RunManifest m)
    {
        var total = m.AttrSysUsd + m.AttrDefUsd + m.AttrCallUsd + m.AttrResUsd + m.AttrTextUsd;
        return total == 0m ? 0m : McpUsd(m) / total;
    }

    public static string BuildCsv(RunStore store)
    {
        var inv = CultureInfo.InvariantCulture;
        var csv = new StringBuilder();
        csv.Append(string.Join(",", Columns)).Append('\n');

        foreach (var m in Ordered(store))
        {
            var verdict = store.ReadVerdict(m.RunId);
            var total = m.InputTokens + m.OutputTokens + m.CacheReadTokens + m.CacheWriteTokens;
            var cells = new[]
            {
                m.RunId,
                m.Config,
                m.Repeat.ToString(inv),
                m.InstanceId,
                m.Status,
                m.StopReason,
                m.Turns.ToString(inv),
                verdict is null ? string.Empty : verdict.Resolved ? "true" : "false",
                m.InputTokens.ToString(inv),
                m.OutputTokens.ToString(inv),
                m.CacheReadTokens.ToString(inv),
                m.CacheWriteTokens.ToString(inv),
                total.ToString(inv),
                m.CostUsd.ToString("0.000000", inv),
                (m.DurationMs / 1000.0).ToString("0.0", inv),
                m.PatchChars.ToString(inv),
                m.Calls.ToString(inv),
                m.Agents.ToString(inv),
                m.AttrDefTokens.ToString(inv),
                m.AttrCallTokens.ToString(inv),
                m.AttrResTokens.ToString(inv),
                m.AttrSysTokens.ToString(inv),
                m.AttrTextTokens.ToString(inv),
                m.AttrSysUsd.ToString("0.000000", inv),
                m.AttrDefUsd.ToString("0.000000", inv),
                m.AttrCallUsd.ToString("0.000000", inv),
                m.AttrResUsd.ToString("0.000000", inv),
                m.AttrTextUsd.ToString("0.000000", inv),
                McpUsd(m).ToString("0.000000", inv),
                McpShare(m).ToString("0.0000", inv),
                m.Variant,
                m.Error ?? string.Empty,
            };
            csv.Append(string.Join(",", cells.Select(Escape))).Append('\n');
        }

        return csv.ToString();
    }

    public static IReadOnlyList<ConfigSummary> Summarize(RunStore store)
    {
        var summaries = new List<ConfigSummary>();
        foreach (var group in Ordered(store).GroupBy(m => m.Config, StringComparer.Ordinal))
        {
            var runs = group.ToList();
            var completed = runs.Where(m => m.Status == "completed").ToList();
            var verdicts = runs.Select(m => store.ReadVerdict(m.RunId)).Where(v => v is not null).ToList();
            var total = runs.Sum(m => m.CostUsd);
            summaries.Add(new ConfigSummary(
                group.Key,
                runs.Count,
                completed.Count,
                runs.Count - completed.Count,
                verdicts.Count,
                verdicts.Count(v => v!.Resolved),
                total,
                completed.Count == 0 ? 0m : completed.Sum(m => m.CostUsd) / completed.Count,
                completed.Count == 0 ? 0.0 : completed.Average(m => m.Turns),
                completed.Count == 0 ? 0m : completed.Average(McpShare),
                completed.Count == 0 ? 0.0 : completed.Average(m => m.Agents),
                completed.Count == 0 ? 0.0 : completed.Average(m => m.Calls)));
        }

        return summaries;
    }

    private static IEnumerable<RunManifest> Ordered(RunStore store) =>
        store.ListManifests()
            .OrderBy(m => m.Config, StringComparer.Ordinal)
            .ThenBy(m => m.InstanceId, StringComparer.Ordinal)
            .ThenBy(m => m.Repeat);

    private static string Escape(string value) =>
        value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
}
