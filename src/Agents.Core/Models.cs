using System.Text.Json.Nodes;

namespace Agents.Core;

public abstract record ContentBlock;

public sealed record TextBlock(string Text) : ContentBlock;

public sealed record ToolUseBlock(string Id, string Name, JsonObject Input) : ContentBlock;

public sealed record ToolResultBlock(string ToolUseId, string Content, bool IsError = false) : ContentBlock;

public sealed record Message(string Role, IReadOnlyList<ContentBlock> Content)
{
    public static Message User(string text) => new("user", new ContentBlock[] { new TextBlock(text) });
}

public sealed record ToolDef(string Name, string Description, JsonObject InputSchema);

/// <summary>
/// Token usage of one model call. <see cref="InputTokens"/> excludes cached tokens
/// (this matches the Messages API, where cache reads/writes are reported separately).
/// </summary>
public sealed record Usage(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens)
{
    public static readonly Usage Zero = new(0, 0, 0, 0);

    /// <summary>Everything the model had to read for this call; this is what the 100k pricing tier looks at.</summary>
    public long PromptTokens => InputTokens + CacheReadTokens + CacheWriteTokens;

    public long TotalTokens => PromptTokens + OutputTokens;

    public static Usage operator +(Usage a, Usage b) => new(
        a.InputTokens + b.InputTokens,
        a.OutputTokens + b.OutputTokens,
        a.CacheReadTokens + b.CacheReadTokens,
        a.CacheWriteTokens + b.CacheWriteTokens);
}

public sealed record ModelRequest(
    string System,
    IReadOnlyList<Message> Messages,
    IReadOnlyList<ToolDef> Tools,
    int MaxTokens);

public sealed record ModelResponse(IReadOnlyList<ContentBlock> Content, string StopReason, Usage Usage);
