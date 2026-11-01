using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Agents.Core.Swe;

public sealed record TaskLabel(
    string InstanceId, int Files, int Hunks, int Components, int ChangedLines, double PHat, string Label)
{
    public const string Parallel = "parallel";
    public const string Sequential = "sequential";
}

/// <summary>
/// Labels the structure of a task from its reference patch (RQ3), by the rule written in report section 3.2.2.
/// The hunks of the patch are nodes of a graph. Two hunks are connected when they are in the same file, or when one
/// of them defines (def / class) a name that the other one changes or uses. A name that is defined in more than one
/// hunk (for example <c>is_valid</c> in three modules) says nothing about a dependency and is ignored.
/// With connected-component sizes s_i (changed lines), the parallel fraction is
/// <c>p = 1 - max(s_i) / sum(s_i)</c>: with unlimited workers, the largest component is the critical path.
/// The label is <c>parallel</c> when there are at least two components and p reaches <c>theta</c>.
/// The rule is fixed BEFORE any experiment result is seen; the reference patch is only a proxy for the structure of
/// the solution an agent will actually write.
/// </summary>
public static class TaskLabeler
{
    /// <summary>Proposed threshold, to be confirmed and then frozen before the main runs.</summary>
    public const double DefaultTheta = 0.4;

    private static readonly Regex Identifier = new(@"[A-Za-z_][A-Za-z0-9_]{2,}", RegexOptions.Compiled);
    private static readonly Regex Definition = new(@"\b(?:def|class)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        "self", "cls", "return", "None", "True", "False", "import", "from", "for", "not", "and", "elif", "else",
        "def", "class", "raise", "try", "except", "finally", "with", "yield", "lambda", "pass", "break", "continue",
        "while", "assert", "str", "int", "len", "list", "dict", "set", "tuple", "isinstance", "super", "print",
        "args", "kwargs", "__init__", "__str__", "__repr__", "__eq__", "__hash__", "__call__",
    };

    private sealed class Hunk
    {
        public string File = string.Empty;
        public int Changed;
        public HashSet<string> Defined = new(StringComparer.Ordinal);
        public HashSet<string> Used = new(StringComparer.Ordinal);
    }

    public static TaskLabel Label(string instanceId, string patch, double theta = DefaultTheta)
    {
        var hunks = Parse(patch);
        var files = hunks.Select(h => h.File).Distinct(StringComparer.Ordinal).Count();
        if (hunks.Count == 0)
        {
            return new TaskLabel(instanceId, 0, 0, 0, 0, 0.0, TaskLabel.Sequential);
        }

        var parent = Enumerable.Range(0, hunks.Count).ToArray();
        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        void Union(int a, int b) => parent[Find(a)] = Find(b);

        // Same file: connected.
        for (var i = 0; i < hunks.Count; i++)
        {
            for (var j = i + 1; j < hunks.Count; j++)
            {
                if (hunks[i].File == hunks[j].File)
                {
                    Union(i, j);
                }
            }
        }

        // A name defined in exactly one hunk connects that hunk to every hunk that uses the name.
        var definedIn = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < hunks.Count; i++)
        {
            foreach (var name in hunks[i].Defined)
            {
                if (!definedIn.TryGetValue(name, out var list))
                {
                    definedIn[name] = list = new List<int>();
                }

                list.Add(i);
            }
        }

        foreach (var (name, owners) in definedIn)
        {
            if (owners.Count != 1)
            {
                continue;
            }

            for (var j = 0; j < hunks.Count; j++)
            {
                if (j != owners[0] && hunks[j].Used.Contains(name))
                {
                    Union(owners[0], j);
                }
            }
        }

        var sizes = hunks
            .Select((h, i) => (Root: Find(i), h.Changed))
            .GroupBy(x => x.Root)
            .Select(g => Math.Max(1, g.Sum(x => x.Changed)))
            .ToList();
        var total = sizes.Sum();
        var pHat = sizes.Count < 2 ? 0.0 : 1.0 - (double)sizes.Max() / total;
        var label = sizes.Count >= 2 && pHat >= theta ? TaskLabel.Parallel : TaskLabel.Sequential;
        return new TaskLabel(instanceId, files, hunks.Count, sizes.Count, total, pHat, label);
    }

    public static string ToCsv(IEnumerable<TaskLabel> labels, double theta)
    {
        var inv = CultureInfo.InvariantCulture;
        var csv = new StringBuilder();
        csv.Append("# theta=").Append(theta.ToString("0.00", inv)).Append('\n');
        csv.Append("instance_id,files,hunks,components,changed_lines,p_hat,label\n");
        foreach (var l in labels)
        {
            csv.Append(string.Join(",", l.InstanceId, l.Files, l.Hunks, l.Components, l.ChangedLines,
                l.PHat.ToString("0.000", inv), l.Label)).Append('\n');
        }

        return csv.ToString();
    }

    public static IReadOnlyDictionary<string, TaskLabel> ReadCsv(string path)
    {
        var result = new Dictionary<string, TaskLabel>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith('#') || line.StartsWith("instance_id", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var c = line.Split(',');
            result[c[0]] = new TaskLabel(
                c[0], int.Parse(c[1], CultureInfo.InvariantCulture), int.Parse(c[2], CultureInfo.InvariantCulture),
                int.Parse(c[3], CultureInfo.InvariantCulture), int.Parse(c[4], CultureInfo.InvariantCulture),
                double.Parse(c[5], CultureInfo.InvariantCulture), c[6]);
        }

        return result;
    }

    private static List<Hunk> Parse(string patch)
    {
        var hunks = new List<Hunk>();
        var file = string.Empty;
        Hunk? current = null;
        foreach (var raw in patch.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                var m = Regex.Match(raw, @" b/(.+)$");
                file = m.Success ? m.Groups[1].Value : raw;
                current = null;
            }
            else if (raw.StartsWith("@@", StringComparison.Ordinal))
            {
                current = new Hunk { File = file };
                hunks.Add(current);
                var end = raw.IndexOf("@@", 2, StringComparison.Ordinal);
                if (end >= 0 && end + 2 < raw.Length)
                {
                    Collect(current, raw[(end + 2)..]); // the function the hunk sits in
                }
            }
            else if (current is not null && raw.Length > 0 && (raw[0] == '+' || raw[0] == '-')
                     && !raw.StartsWith("+++", StringComparison.Ordinal) && !raw.StartsWith("---", StringComparison.Ordinal))
            {
                current.Changed++;
                Collect(current, raw[1..]);
            }
        }

        return hunks;
    }

    private static void Collect(Hunk hunk, string text)
    {
        foreach (Match m in Definition.Matches(text))
        {
            if (!Ignored.Contains(m.Groups[1].Value) && m.Groups[1].Value.Length >= 3)
            {
                hunk.Defined.Add(m.Groups[1].Value);
            }
        }

        foreach (Match m in Identifier.Matches(text))
        {
            if (!Ignored.Contains(m.Value))
            {
                hunk.Used.Add(m.Value);
            }
        }
    }
}
