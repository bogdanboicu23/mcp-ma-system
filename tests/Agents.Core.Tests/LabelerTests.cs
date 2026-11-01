using Agents.Core.Swe;
using Xunit;

namespace Agents.Core.Tests;

public class LabelerTests
{
    private static string Diff(params (string File, string Header, string[] Lines)[] hunks)
    {
        var sb = new System.Text.StringBuilder();
        string? last = null;
        foreach (var (file, header, lines) in hunks)
        {
            if (file != last)
            {
                sb.Append($"diff --git a/{file} b/{file}\n--- a/{file}\n+++ b/{file}\n");
                last = file;
            }

            sb.Append($"@@ -1,3 +1,3 @@ {header}\n");
            foreach (var l in lines)
            {
                sb.Append(l).Append('\n');
            }
        }

        return sb.ToString();
    }

    [Fact]
    public void A_single_hunk_is_sequential_with_zero_parallel_fraction()
    {
        var label = TaskLabeler.Label("t", Diff(("a.py", "def f():", new[] { "-    return 1", "+    return 2" })));

        Assert.Equal(1, label.Components);
        Assert.Equal(0.0, label.PHat);
        Assert.Equal("sequential", label.Label);
    }

    [Fact]
    public void Two_hunks_in_one_file_stay_one_component()
    {
        var label = TaskLabeler.Label("t", Diff(
            ("a.py", "def f():", new[] { "-    x = 1", "+    x = 2" }),
            ("a.py", "def g():", new[] { "-    y = 1", "+    y = 2" })));

        Assert.Equal(1, label.Files);
        Assert.Equal(2, label.Hunks);
        Assert.Equal(1, label.Components);
        Assert.Equal("sequential", label.Label);
    }

    [Fact]
    public void Independent_files_are_parallel_even_when_they_define_the_same_name()
    {
        // is_valid is defined in all three hunks, so it carries no dependency information.
        var label = TaskLabeler.Label("t", Diff(
            ("email.py", "", new[] { "+def is_valid(s):", "+    return '@' in s" }),
            ("phone.py", "", new[] { "+def is_valid(s):", "+    return s.isdigit()" }),
            ("zip.py", "", new[] { "+def is_valid(s):", "+    return len(s) == 5" })));

        Assert.Equal(3, label.Components);
        Assert.Equal(3, label.Files);
        Assert.True(Math.Abs(label.PHat - 2.0 / 3.0) < 1e-9);
        Assert.Equal("parallel", label.Label);
    }

    [Fact]
    public void A_function_changed_in_one_file_and_used_in_another_links_the_hunks()
    {
        var label = TaskLabeler.Label("t", Diff(
            ("core.py", "", new[] { "-def parse_line(line):", "+def parse_line(line, strict=False):" }),
            ("report.py", "", new[] { "-    name, qty = parse_line(row)", "+    item = parse_line(row)" })));

        Assert.Equal(1, label.Components);
        Assert.Equal("sequential", label.Label);
    }

    [Fact]
    public void An_unbalanced_split_below_theta_is_sequential()
    {
        var big = Enumerable.Range(0, 8).Select(i => $"+    line_{i} = {i}").ToArray();
        var label = TaskLabeler.Label("t", Diff(
            ("a.py", "", big),
            ("b.py", "", new[] { "+    other_thing = 1" })));

        Assert.Equal(2, label.Components);
        Assert.True(label.PHat < TaskLabeler.DefaultTheta);
        Assert.Equal("sequential", label.Label);
    }

    [Fact]
    public void Theta_changes_the_label_but_not_the_fraction()
    {
        var patch = Diff(
            ("a.py", "", new[] { "+    one = 1", "+    two = 2", "+    three = 3" }),
            ("b.py", "", new[] { "+    four = 4", "+    five = 5" }));

        var strict = TaskLabeler.Label("t", patch, 0.5);
        var loose = TaskLabeler.Label("t", patch, 0.3);

        Assert.Equal(strict.PHat, loose.PHat);
        Assert.Equal("sequential", strict.Label);
        Assert.Equal("parallel", loose.Label);
    }

    [Fact]
    public void An_empty_patch_and_the_csv_round_trip_work()
    {
        var empty = TaskLabeler.Label("e", string.Empty);
        Assert.Equal(0, empty.Hunks);
        Assert.Equal("sequential", empty.Label);

        var path = Path.Combine(Path.GetTempPath(), "labels-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            var labels = new[]
            {
                TaskLabeler.Label("a", Diff(("a.py", "", new[] { "+x = 1" }), ("b.py", "", new[] { "+y = 2" }))),
                empty,
            };
            File.WriteAllText(path, TaskLabeler.ToCsv(labels, 0.4));

            var read = TaskLabeler.ReadCsv(path);

            Assert.Equal(2, read.Count);
            Assert.Equal("parallel", read["a"].Label);
            Assert.True(Math.Abs(read["a"].PHat - 0.5) < 1e-9);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_stratified_sample_takes_the_requested_number_of_parallel_tasks()
    {
        var tasks = Enumerable.Range(0, 40)
            .Select(i => new SweBenchTask { InstanceId = $"r{i % 4}__x-{i}", Repo = $"r{i % 4}/x" })
            .ToList();
        var labels = tasks.ToDictionary(
            t => t.InstanceId,
            t => new TaskLabel(t.InstanceId, 1, 1, 1, 1, 0, tasks.IndexOf(t) % 5 == 0 ? "parallel" : "sequential"));

        var ids = TaskSelector.SelectStratified(tasks, labels, 10, 4, 42);

        Assert.Equal(10, ids.Count);
        Assert.Equal(4, ids.Count(id => labels[id].Label == "parallel"));
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}
