using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agents.Core.Swe;

/// <summary>One SWE-bench instance, as written by <c>tools/fetch_tasks.py</c> (one JSON object per line).</summary>
public sealed record SweBenchTask
{
    [JsonPropertyName("instance_id")]
    public string InstanceId { get; init; } = string.Empty;

    [JsonPropertyName("repo")]
    public string Repo { get; init; } = string.Empty;

    [JsonPropertyName("base_commit")]
    public string BaseCommit { get; init; } = string.Empty;

    [JsonPropertyName("problem_statement")]
    public string ProblemStatement { get; init; } = string.Empty;

    /// <summary>Tests that must go from failing to passing. NEVER shown to the agent.</summary>
    [JsonPropertyName("fail_to_pass")]
    public IReadOnlyList<string> FailToPass { get; init; } = Array.Empty<string>();

    [JsonPropertyName("pass_to_pass")]
    public IReadOnlyList<string> PassToPass { get; init; } = Array.Empty<string>();

    /// <summary>The reference fix. Only used to label task structure (RQ3), NEVER shown to the agent.</summary>
    [JsonPropertyName("patch")]
    public string GoldPatch { get; init; } = string.Empty;

    [JsonPropertyName("test_patch")]
    public string TestPatch { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;
}

public static class TaskLoader
{
    public static IReadOnlyList<SweBenchTask> Load(string path)
    {
        var tasks = new List<SweBenchTask>();
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            SweBenchTask? task;
            try
            {
                task = JsonSerializer.Deserialize<SweBenchTask>(line);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{path}, line {lineNumber}: {ex.Message}", ex);
            }

            if (task is null || task.InstanceId.Length == 0)
            {
                throw new InvalidDataException($"{path}, line {lineNumber}: the row has no instance_id.");
            }

            tasks.Add(task);
        }

        return tasks;
    }

    /// <summary>Reads instance ids, one per line. Text after '#' is a comment.</summary>
    public static IReadOnlyList<string> LoadIds(string path) =>
        File.ReadLines(path)
            .Select(line => line.Split('#')[0].Trim())
            .Where(line => line.Length > 0)
            .ToList();

    /// <summary>Returns the tasks for the given ids, in the order of the ids.</summary>
    public static IReadOnlyList<SweBenchTask> Pick(IReadOnlyList<SweBenchTask> all, IEnumerable<string> ids)
    {
        var byId = all.ToDictionary(t => t.InstanceId, StringComparer.Ordinal);
        var picked = new List<SweBenchTask>();
        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var task))
            {
                throw new KeyNotFoundException($"Task '{id}' is not in the task file.");
            }

            picked.Add(task);
        }

        return picked;
    }
}

/// <summary>Picks a reproducible sample (for the pilot) that is spread over the repositories.</summary>
public static class TaskSelector
{
    public static IReadOnlyList<string> Select(IReadOnlyList<SweBenchTask> tasks, int count, int seed)
    {
        var rng = new Random(seed);
        var groups = tasks
            .GroupBy(t => t.Repo, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => Shuffle(g.Select(t => t.InstanceId).OrderBy(id => id, StringComparer.Ordinal).ToList(), rng))
            .ToList();

        // Round-robin over repositories, so a sample of 10 is not 10 Django issues.
        var result = new List<string>();
        for (var round = 0; result.Count < count; round++)
        {
            var added = false;
            foreach (var group in groups)
            {
                if (round < group.Count && result.Count < count)
                {
                    result.Add(group[round]);
                    added = true;
                }
            }

            if (!added)
            {
                break;
            }
        }

        return result;
    }

    private static List<string> Shuffle(List<string> items, Random rng)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }

        return items;
    }
}
