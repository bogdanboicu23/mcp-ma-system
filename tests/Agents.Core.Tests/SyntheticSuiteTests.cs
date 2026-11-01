using System.Diagnostics;
using System.Text.Json;
using Agents.Core.Swe;
using Xunit;

namespace Agents.Core.Tests;

/// <summary>Checks the hand-written synthetic suite (tasks/synthetic): its labels and that its tests discriminate.</summary>
public class SyntheticSuiteTests
{
    private static string SuiteDir()
    {
        var fromEnv = Environment.GetEnvironmentVariable("MAMCP_ROOT");
        var dir = new DirectoryInfo(string.IsNullOrEmpty(fromEnv) ? AppContext.BaseDirectory : fromEnv);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tasks", "synthetic")))
        {
            dir = dir.Parent;
        }

        return dir is null
            ? throw new DirectoryNotFoundException("tasks/synthetic was not found above " + AppContext.BaseDirectory)
            : Path.Combine(dir.FullName, "tasks", "synthetic");
    }

    [Fact]
    public void The_suite_has_ten_tasks_and_the_labeler_agrees_with_the_intended_label_of_each()
    {
        var suite = SuiteDir();
        var tasks = TaskLoader.Load(Path.Combine(suite, "tasks.jsonl"));
        Assert.Equal(10, tasks.Count);

        var mismatches = new List<string>();
        foreach (var task in tasks)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(suite, task.InstanceId, "task.json")));
            var intended = json.RootElement.GetProperty("intended_label").GetString();
            var label = TaskLabeler.Label(task.InstanceId, task.GoldPatch);
            if (label.Label != intended)
            {
                mismatches.Add($"{task.InstanceId}: intended {intended}, labeled {label.Label} (p={label.PHat:0.00}, k={label.Components})");
            }
        }

        Assert.Equal(string.Empty, string.Join("; ", mismatches));
        Assert.Equal(5, tasks.Count(t => TaskLabeler.Label(t.InstanceId, t.GoldPatch).Label == TaskLabel.Parallel));
    }

    [Fact]
    public void Hidden_tests_fail_on_the_base_repository_and_pass_with_the_reference_solution()
    {
        if (!Run("python3", new[] { "--version" }, Path.GetTempPath()))
        {
            return; // no python on this machine: the offline check is skipped
        }

        var suite = SuiteDir();
        var problems = new List<string>();
        foreach (var dir in Directory.GetDirectories(suite, "synth__*"))
        {
            var work = Path.Combine(Path.GetTempPath(), "mamcp-synth-" + Guid.NewGuid().ToString("N"));
            try
            {
                Copy(Path.Combine(dir, "repo"), work);
                Copy(Path.Combine(dir, "hidden_tests"), Path.Combine(work, "hidden_tests"));
                var base0 = Run("python3", new[] { "-m", "unittest", "discover", "-s", "hidden_tests" }, work, work);
                Copy(Path.Combine(dir, "solution"), work);
                var solved = Run("python3", new[] { "-m", "unittest", "discover", "-s", "hidden_tests" }, work, work);
                if (base0 || !solved)
                {
                    problems.Add($"{Path.GetFileName(dir)}: base passes={base0}, solution passes={solved}");
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(work, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }

        Assert.Equal(string.Empty, string.Join("; ", problems));
    }

    private static bool Run(string file, string[] args, string cwd, string? pythonPath = null)
    {
        var start = new ProcessStartInfo(file)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }

        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1"; // a same-size edit in the same second would reuse a stale .pyc
        if (pythonPath is not null)
        {
            start.Environment["PYTHONPATH"] = pythonPath;
        }

        try
        {
            using var p = Process.Start(start)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
