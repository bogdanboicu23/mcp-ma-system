using System.Text.Json.Nodes;
using Agents.Core.Swe;
using Agents.Core.Tools;

namespace Agents.Core;

/// <summary>
/// A stand-in for a model that "solves" the synthetic tasks by replaying the reference solution through the real tools
/// (write_file for each solution file, a git status, then done). It makes real tool calls in a real container, so
/// it exercises Docker, MCP, logging, evaluation, reports and the UI without any API, key or cost.
/// It is a plumbing test only: it never fails a task, so its results say NOTHING about any architecture.
/// Single-agent configurations only (A, B): it recognises the task from the first user message.
/// </summary>
public sealed class ReplayModelClient : IModelClient
{
    public const string Id = "replay-oracle";

    private readonly IReadOnlyList<SweBenchTask> _tasks;
    private readonly string _suiteDir;

    public ReplayModelClient(IReadOnlyList<SweBenchTask> tasks, string suiteDir)
    {
        _tasks = tasks;
        _suiteDir = suiteDir;
    }

    public string ModelId => Id;

    public Task<ModelResponse> SendAsync(ModelRequest request, CancellationToken ct = default)
    {
        var prompt = string.Concat(request.Messages[0].Content.OfType<TextBlock>().Select(t => t.Text));
        var task = _tasks.FirstOrDefault(t => prompt.Contains(t.ProblemStatement.Trim(), StringComparison.Ordinal))
                   ?? throw new InvalidOperationException(
                       "The replay model only solves synthetic tasks, in single-agent configurations (A, B).");

        var solution = Path.Combine(_suiteDir, task.InstanceId, "solution");
        var files = Directory.GetFiles(solution, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(solution, f).Replace('\\', '/'))
            .Where(f => !f.Contains("__pycache__", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var step = request.Messages.Count(m => m.Role == "assistant");
        var promptChars = request.System.Length + request.Messages.Sum(m => m.Content.Sum(Length));
        var promptTokens = promptChars / 4;
        var cached = step == 0 ? 0 : promptTokens * 7 / 10;
        var usage = new Usage(promptTokens - cached, 40, cached, 0);

        if (step < files.Count)
        {
            var text = File.ReadAllText(Path.Combine(solution, files[step]));
            return Task.FromResult(ToolCall(step, ToolNames.WriteFile, new JsonObject { ["path"] = files[step], ["content"] = text }, usage));
        }

        if (step == files.Count)
        {
            return Task.FromResult(ToolCall(step, ToolNames.RunCommand, new JsonObject { ["command"] = "git status --short" }, usage));
        }

        return Task.FromResult(new ModelResponse(
            new ContentBlock[] { new TextBlock("Replayed the reference solution.") }, "end_turn", usage));
    }

    private static ModelResponse ToolCall(int step, string name, JsonObject input, Usage usage) =>
        new(new ContentBlock[] { new ToolUseBlock($"replay_{step}", name, input) }, "tool_use", usage);

    private static int Length(ContentBlock block) => block switch
    {
        TextBlock t => t.Text.Length,
        ToolUseBlock u => u.Name.Length + u.Input.ToJsonString().Length,
        ToolResultBlock r => r.Content.Length,
        _ => 0,
    };
}
