using System.Text.RegularExpressions;
using Agents.Core;
using Agents.Core.Tools;

namespace Agents.Topologies;

/// <summary>Small helpers shared by the topologies.</summary>
public static class TopologyKit
{
    public static readonly IReadOnlySet<string> ReviewerTools =
        new HashSet<string> { ToolNames.ReadFile, ToolNames.ListDir, ToolNames.RunTests };

    public static readonly IReadOnlySet<string> PlannerTools =
        new HashSet<string> { ToolNames.ReadFile, ToolNames.ListDir, ToolNames.RunCommand };

    /// <summary>Budget and context exhaustion end a whole run; other stop reasons are just how one agent finished.</summary>
    public static bool IsTerminal(string stopReason) =>
        stopReason is StopReasons.BudgetExceeded or StopReasons.ContextOverflow;

    public static string Truncate(string text, int max, string note = "[truncated]")
    {
        var t = text.Trim();
        return t.Length <= max ? t : t[..max] + "\n" + note;
    }

    /// <summary>The last "VERDICT: X" in an agent's reply, upper-cased, or null when there is none.</summary>
    public static string? Verdict(string text)
    {
        var matches = Regex.Matches(text, @"VERDICT:\s*(APPROVE|CHANGES|PASS|FAIL)", RegexOptions.IgnoreCase);
        return matches.Count == 0 ? null : matches[^1].Groups[1].Value.ToUpperInvariant();
    }
}
