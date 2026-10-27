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
    private PromptSet(string systemPrompt, string taskTemplate)
    {
        SystemPrompt = Normalize(systemPrompt);
        TaskTemplate = Normalize(taskTemplate);
        SystemHash = Hash(SystemPrompt);
        TaskHash = Hash(TaskTemplate);
    }

    public string SystemPrompt { get; }

    public string TaskTemplate { get; }

    public string SystemHash { get; }

    public string TaskHash { get; }

    public static PromptSet Load()
    {
        var assembly = typeof(PromptSet).Assembly;
        return new PromptSet(
            ReadResource(assembly, "Agents.Core.Prompts.system.md"),
            ReadResource(assembly, "Agents.Core.Prompts.task.md"));
    }

    public static PromptSet FromTexts(string systemPrompt, string taskTemplate) => new(systemPrompt, taskTemplate);

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
