using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Agents.Core.Swe;

/// <summary>
/// The prompts shared by every configuration. They live in <c>src/Agents.Core/Prompts/*.md</c> and are embedded
/// in the assembly. Each run records their hashes in its manifest, so a changed prompt can never go unnoticed.
/// Line endings are normalized before hashing, because git on Windows may check the files out with CRLF.
/// </summary>
public sealed class PromptSet
{
    private PromptSet(
        string systemPrompt, string taskTemplate, string hubOrchestrator, string hubWorker,
        IReadOnlyDictionary<string, string>? addenda)
    {
        Addenda = (addenda ?? new Dictionary<string, string>())
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => Normalize(kv.Value));
        SystemPrompt = Normalize(systemPrompt);
        TaskTemplate = Normalize(taskTemplate);
        HubOrchestratorAddendum = Normalize(hubOrchestrator);
        HubWorkerAddendum = Normalize(hubWorker);
        SystemHash = Hash(SystemPrompt);
        TaskHash = Hash(TaskTemplate);
        TopologyHash = Hash(
            HubOrchestratorAddendum + "\n--\n" + HubWorkerAddendum + "\n--\n" +
            string.Join("\n--\n", Addenda.Select(kv => kv.Key + "=" + kv.Value)));
    }

    public string SystemPrompt { get; }

    public string TaskTemplate { get; }

    /// <summary>Hub-and-spoke only: appended to <see cref="SystemPrompt"/> for the orchestrator.</summary>
    public string HubOrchestratorAddendum { get; }

    /// <summary>Hub-and-spoke only: appended to <see cref="SystemPrompt"/> for each worker.</summary>
    public string HubWorkerAddendum { get; }

    /// <summary>Role addenda of the other topologies (coder, reviewer, planner, seqcoder, tester), by name.</summary>
    public IReadOnlyDictionary<string, string> Addenda { get; }

    /// <summary>The shared system prompt followed by the named role addendum (the bare prompt if there is none).</summary>
    public string Compose(string role) =>
        Addenda.TryGetValue(role, out var text) && text.Length > 0 ? SystemPrompt + "\n\n" + text : SystemPrompt;

    public string SystemHash { get; }

    public string TaskHash { get; }

    /// <summary>Hash of the topology addenda; recorded in the manifest next to the shared prompt hashes.</summary>
    public string TopologyHash { get; }

    public static PromptSet Load()
    {
        var assembly = typeof(PromptSet).Assembly;
        return new PromptSet(
            ReadResource(assembly, "Agents.Core.Prompts.system.md"),
            ReadResource(assembly, "Agents.Core.Prompts.task.md"),
            ReadResource(assembly, "Agents.Core.Prompts.hub_orchestrator.md"),
            ReadResource(assembly, "Agents.Core.Prompts.hub_worker.md"),
            RoleNames.ToDictionary(n => n, n => ReadResource(assembly, $"Agents.Core.Prompts.role_{n}.md")));
    }

    public static PromptSet FromTexts(
        string systemPrompt, string taskTemplate, string hubOrchestrator = "", string hubWorker = "",
        IReadOnlyDictionary<string, string>? addenda = null) =>
        new(systemPrompt, taskTemplate, hubOrchestrator, hubWorker, addenda);

    private static readonly string[] RoleNames = { "coder", "reviewer", "planner", "seqcoder", "tester" };

    /// <summary>The user message for a task: the issue text only. Tests and the reference patch are never included.</summary>
    public string RenderTask(SweBenchTask task) =>
        TaskTemplate
            .Replace("{{repo}}", task.Repo, StringComparison.Ordinal)
            .Replace("{{problem_statement}}", task.ProblemStatement.Trim(), StringComparison.Ordinal);

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException(
                               $"Embedded prompt '{name}' is missing. Found: {string.Join(", ", assembly.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
