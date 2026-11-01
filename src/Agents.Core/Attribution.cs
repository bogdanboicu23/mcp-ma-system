using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Agents.Core;

/// <summary>
/// Where the tokens and the cost of ONE model call came from (RQ2). Sources: <c>Sys</c> = system prompt, task and
/// coordination messages; <c>Def</c> = tool definitions; <c>Call</c> = the agent's tool calls (name and arguments);
/// <c>Res</c> = tool results; <c>Text</c> = the agent's own text. The five prompt parts add up exactly to the
/// call's prompt tokens, and the five USD parts add up exactly to the call's cost.
/// MCP layer = Def + Call + Res. Output tokens are split between Text and Call.
/// </summary>
public sealed record TokenAttribution(
    long Sys, long Def, long Call, long Res, long Text,
    long OutText, long OutCall,
    decimal SysUsd, decimal DefUsd, decimal CallUsd, decimal ResUsd, decimal TextUsd)
{
    public long Prompt => Sys + Def + Call + Res + Text;

    public decimal TotalUsd => SysUsd + DefUsd + CallUsd + ResUsd + TextUsd;

    public decimal McpUsd => DefUsd + CallUsd + ResUsd;
}

/// <summary>
/// Splits a call's usage by source. Prompt order is tools, system prompt, then the messages; cache reads cover the
/// first tokens of that order, then cache writes, then uncached input, so each source is priced at the rate its
/// tokens actually had. Without exact numbers the size of every part is estimated from its character count and
/// scaled to the API's prompt total. Tool definitions can be measured exactly (token-counting endpoint) once per
/// tool set via <paramref name="measureDefinitions"/>.
/// </summary>
public sealed class TokenAttributor
{
    private enum Source { Sys, Def, Call, Res, Text }

    private readonly Pricing _pricing;
    private readonly Func<IReadOnlyList<ToolDef>, CancellationToken, Task<long>>? _measure;
    private readonly ConcurrentDictionary<string, Lazy<Task<long>>> _measured = new();

    public TokenAttributor(
        Pricing? pricing = null,
        Func<IReadOnlyList<ToolDef>, CancellationToken, Task<long>>? measureDefinitions = null)
    {
        _pricing = pricing ?? Pricing.Haiku55;
        _measure = measureDefinitions;
    }

    public async Task<TokenAttribution> AttributeAsync(
        ModelRequest request, ModelResponse response, CancellationToken ct = default)
    {
        var usage = response.Usage;
        var prompt = usage.PromptTokens;

        var parts = new List<(Source Src, long Chars)>();
        var defChars = request.Tools.Sum(t => (long)t.Name.Length + t.Description.Length + t.InputSchema.ToJsonString().Length);
        parts.Add((Source.Def, defChars));
        parts.Add((Source.Sys, request.System.Length));
        foreach (var message in request.Messages)
        {
            foreach (var block in message.Content)
            {
                switch (block)
                {
                    case TextBlock t:
                        parts.Add((message.Role == "assistant" ? Source.Text : Source.Sys, t.Text.Length));
                        break;
                    case ToolUseBlock u:
                        parts.Add((Source.Call, CallChars(u)));
                        break;
                    case ToolResultBlock r:
                        parts.Add((Source.Res, r.Content.Length));
                        break;
                }
            }
        }

        var tokens = Allocate(parts, prompt, await MeasuredDefinitionsAsync(request.Tools, prompt, ct).ConfigureAwait(false));

        // Price the segments in prompt order: cache reads first, then cache writes, then uncached input.
        var multiplier = _pricing.Multiplier(usage);
        var tiers = new (long Left, decimal PerM)[]
        {
            (usage.CacheReadTokens, _pricing.CacheReadPerM),
            (usage.CacheWriteTokens, _pricing.CacheWritePerM),
            (usage.InputTokens, _pricing.InputPerM),
        };
        var usd = new decimal[5];
        var sum = new long[5];
        for (var i = 0; i < parts.Count; i++)
        {
            var remaining = tokens[i];
            sum[(int)parts[i].Src] += remaining;
            for (var t = 0; t < tiers.Length && remaining > 0; t++)
            {
                var take = Math.Min(remaining, tiers[t].Left);
                if (take <= 0)
                {
                    continue;
                }

                usd[(int)parts[i].Src] += multiplier * take * tiers[t].PerM / 1_000_000m;
                tiers[t].Left -= take;
                remaining -= take;
            }
        }

        // Output tokens are split between the agent's text and its tool calls by character share.
        long textChars = 0, callChars = 0;
        foreach (var block in response.Content)
        {
            switch (block)
            {
                case TextBlock t:
                    textChars += t.Text.Length;
                    break;
                case ToolUseBlock u:
                    callChars += CallChars(u);
                    break;
            }
        }

        var outTokens = usage.OutputTokens;
        var outText = textChars + callChars == 0 ? outTokens : (long)Math.Round(outTokens * (double)textChars / (textChars + callChars));
        var outCall = outTokens - outText;
        var outputUsd = multiplier * _pricing.OutputPerM / 1_000_000m;
        usd[(int)Source.Text] += outText * outputUsd;
        usd[(int)Source.Call] += outCall * outputUsd;

        return new TokenAttribution(
            sum[(int)Source.Sys], sum[(int)Source.Def], sum[(int)Source.Call], sum[(int)Source.Res], sum[(int)Source.Text],
            outText, outCall,
            usd[(int)Source.Sys], usd[(int)Source.Def], usd[(int)Source.Call], usd[(int)Source.Res], usd[(int)Source.Text]);
    }

    private static long CallChars(ToolUseBlock u) => u.Name.Length + u.Input.ToJsonString().Length;

    private async Task<long?> MeasuredDefinitionsAsync(IReadOnlyList<ToolDef> tools, long prompt, CancellationToken ct)
    {
        if (_measure is null || tools.Count == 0)
        {
            return null;
        }

        var key = string.Join("|", tools.Select(t => t.Name + ":" + t.Description.Length + ":" + t.InputSchema.ToJsonString().Length));
        var value = await _measured.GetOrAdd(key, _ => new Lazy<Task<long>>(() => _measure(tools, ct))).Value.ConfigureAwait(false);
        return value > 0 && value < prompt ? value : null;
    }

    /// <summary>Token counts per part, in order, adding up to exactly <paramref name="prompt"/>.</summary>
    private static long[] Allocate(List<(Source Src, long Chars)> parts, long prompt, long? measuredDefinitions)
    {
        var result = new long[parts.Count];
        if (prompt <= 0)
        {
            return result;
        }

        var rest = prompt;
        var skipDef = false;
        if (measuredDefinitions is { } d)
        {
            result[0] = d; // part 0 is always the definitions
            rest -= d;
            skipDef = true;
        }

        var charTotal = 0L;
        for (var i = skipDef ? 1 : 0; i < parts.Count; i++)
        {
            charTotal += parts[i].Chars;
        }

        if (charTotal == 0)
        {
            result[skipDef ? 1 : 0] += rest; // nothing to go by: the system prompt slot takes the remainder
            return result;
        }

        // Cumulative rounding keeps the total exact.
        long cum = 0, assigned = 0;
        for (var i = skipDef ? 1 : 0; i < parts.Count; i++)
        {
            cum += parts[i].Chars;
            var upTo = (long)Math.Round(rest * (double)cum / charTotal);
            result[i] += upTo - assigned;
            assigned = upTo;
        }

        return result;
    }
}

/// <summary>Adds up the attribution of every call of a run (written into the manifest).</summary>
public sealed class AttributionTotals : ICallLog
{
    private readonly object _lock = new();
    private readonly ICallLog _inner;
    private readonly HashSet<string> _agents = new();

    public AttributionTotals(ICallLog inner) => _inner = inner;

    public int Calls { get; private set; }

    public int Agents
    {
        get
        {
            lock (_lock)
            {
                return _agents.Count;
            }
        }
    }

    public long SysTokens { get; private set; }

    public long DefTokens { get; private set; }

    public long CallTokens { get; private set; }

    public long ResTokens { get; private set; }

    public long TextTokens { get; private set; }

    public decimal SysUsd { get; private set; }

    public decimal DefUsd { get; private set; }

    public decimal CallUsd { get; private set; }

    public decimal ResUsd { get; private set; }

    public decimal TextUsd { get; private set; }

    public void Write(CallRecord record)
    {
        lock (_lock)
        {
            Calls++;
            _agents.Add(record.AgentId);
            if (record.Attribution is { } a)
            {
                SysTokens += a.Sys;
                DefTokens += a.Def;
                CallTokens += a.Call;
                ResTokens += a.Res;
                TextTokens += a.Text;
                SysUsd += a.SysUsd;
                DefUsd += a.DefUsd;
                CallUsd += a.CallUsd;
                ResUsd += a.ResUsd;
                TextUsd += a.TextUsd;
            }
        }

        _inner.Write(record);
    }
}
