using System.Text.Json.Nodes;

namespace Agents.Core;

public sealed record ContextResult(
    IReadOnlyList<Message> Messages,
    long EstimatedPromptTokens,
    int StubbedToolResults,
    bool Overflow);

/// <summary>
/// The same context policy is applied to every system, so no configuration gets an unfair advantage
/// and none of them crosses the 100k pricing cliff by accident.
///
/// When the estimated prompt is over the cap, the OLDEST tool results are replaced by a short stub
/// until it fits. The most recent message is never touched. The full history is kept by the caller;
/// only the copy that is sent to the model is compacted, so the result is deterministic.
/// </summary>
public sealed class ContextPolicy
{
    public const string StubPrefix = "[tool result truncated";

    /// <summary>Conservative: the Haiku 5.5 tokenizer yields more tokens per character than older models.</summary>
    public const double CharsPerToken = 3.0;

    public ContextPolicy(int maxPromptTokens = 80_000)
    {
        MaxPromptTokens = maxPromptTokens;
    }

    public int MaxPromptTokens { get; }

    public static long Estimate(string system, IReadOnlyList<ToolDef> tools, IReadOnlyList<Message> messages)
    {
        long chars = system.Length;
        foreach (var t in tools)
        {
            chars += t.Name.Length + t.Description.Length + t.InputSchema.ToJsonString().Length;
        }

        foreach (var m in messages)
        {
            foreach (var b in m.Content)
            {
                chars += b switch
                {
                    TextBlock t => t.Text.Length,
                    ToolUseBlock u => u.Name.Length + u.Input.ToJsonString().Length,
                    ToolResultBlock r => r.Content.Length,
                    _ => 0,
                };
            }
        }

        return (long)Math.Ceiling(chars / CharsPerToken);
    }

    public ContextResult Apply(string system, IReadOnlyList<ToolDef> tools, IReadOnlyList<Message> messages)
    {
        var work = messages.ToList();
        var estimate = Estimate(system, tools, work);
        var stubbed = 0;

        // Oldest first; the last message holds the freshest tool results and stays intact.
        for (var i = 0; i < work.Count - 1 && estimate > MaxPromptTokens; i++)
        {
            var blocks = work[i].Content;
            var changed = false;
            var rebuilt = new List<ContentBlock>(blocks.Count);
            foreach (var b in blocks)
            {
                if (estimate > MaxPromptTokens
                    && b is ToolResultBlock r
                    && !r.Content.StartsWith(StubPrefix, StringComparison.Ordinal))
                {
                    var stub = $"{StubPrefix}: {r.Content.Length} chars removed to fit the context budget]";
                    estimate -= (long)Math.Ceiling((r.Content.Length - stub.Length) / CharsPerToken);
                    rebuilt.Add(r with { Content = stub });
                    stubbed++;
                    changed = true;
                }
                else
                {
                    rebuilt.Add(b);
                }
            }

            if (changed)
            {
                work[i] = new Message(work[i].Role, rebuilt);
            }
        }

        return new ContextResult(work, estimate, stubbed, estimate > MaxPromptTokens);
    }
}
