using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Agents.Core;

/// <summary>Where an OpenAI-compatible endpoint lives and how fast it may be called.</summary>
public sealed record ProviderPreset(
    string Name, string BaseUrl, string? ApiKeyEnv, string DefaultModel, int MinIntervalMs, string Note)
{
    public static readonly IReadOnlyList<ProviderPreset> All = new[]
    {
        new ProviderPreset(
            "openrouter", "https://openrouter.ai/api/v1", "OPENROUTER_API_KEY", "qwen/qwen3-coder:free", 3500,
            "Free models (':free') are limited to about 20 requests/minute and 50 requests/day (1000/day after a $10 top-up). Limits change."),
        new ProviderPreset(
            "groq", "https://api.groq.com/openai/v1", "GROQ_API_KEY", "llama-3.3-70b-versatile", 2200,
            "Free tier: about 30 requests/minute and a small tokens-per-minute limit; use a low --max-prompt-tokens (for example 8000-12000)."),
        new ProviderPreset(
            "ollama", "http://localhost:11434/v1", null, "qwen3-coder", 0,
            "Local model: no limits and no cost; speed depends on your hardware."),
    };

    public static ProviderPreset Find(string name) =>
        All.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown provider '{name}'. Available: anthropic, {string.Join(", ", All.Select(p => p.Name))}, replay.");
}

/// <summary>
/// Chat-completions client for any OpenAI-compatible endpoint (OpenRouter, Groq, Ollama, ...). It converts the
/// harness's Messages-style history (text, tool_use and tool_result blocks) to and from the chat format, so the agent
/// loop and every topology stay unchanged. Paces its calls (shared by all agents of a run) and retries 429/5xx
/// honouring Retry-After. Only HttpClient, no SDK.
/// </summary>
public sealed class OpenAiCompatClient : IModelClient
{
    private const int MaxAttempts = 8;

    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly TimeSpan _minInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastSend = DateTimeOffset.MinValue;

    public OpenAiCompatClient(
        string baseUrl,
        string modelId,
        string? apiKey,
        int minIntervalMs = 0,
        HttpClient? http = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ModelId = modelId;
        _endpoint = baseUrl.TrimEnd('/') + "/chat/completions";
        _minInterval = TimeSpan.FromMilliseconds(minIntervalMs);
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
        if (!string.IsNullOrEmpty(apiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    public string ModelId { get; }

    public async Task<ModelResponse> SendAsync(ModelRequest request, CancellationToken ct = default)
    {
        var payload = BuildBody(request).ToJsonString();

        for (var attempt = 1; ; attempt++)
        {
            await PaceAsync(ct).ConfigureAwait(false);

            string? failure = null;
            TimeSpan? wait = null;
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
                using var response = await _http.PostAsync(_endpoint, content, ct).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var node = JsonNode.Parse(text);
                    if (node?["choices"] is JsonArray { Count: > 0 })
                    {
                        return ParseResponse(node);
                    }

                    // Some gateways answer 200 with an error object instead of a completion.
                    failure = $"{ModelId} returned no completion: {Truncate(text)}";
                    if (!IsRetryableErrorBody(node))
                    {
                        throw new HttpRequestException(failure);
                    }
                }
                else
                {
                    failure = $"{(int)response.StatusCode} from {_endpoint}: {Truncate(text)}" +
                              (response.StatusCode == HttpStatusCode.NotFound ? "  (model names change: run 'models --provider <name>' and pass --model <id>)" : string.Empty);
                    var retryable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                    if (!retryable)
                    {
                        throw new HttpRequestException(failure);
                    }

                    if (response.Headers.RetryAfter?.Delta is { } delta)
                    {
                        wait = delta;
                    }
                }
            }
            catch (HttpRequestException) when (failure is null && attempt < MaxAttempts)
            {
                failure = "network error"; // connection reset, DNS, ...
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < MaxAttempts)
            {
                failure = "request timed out";
            }

            if (attempt >= MaxAttempts)
            {
                throw new HttpRequestException($"Giving up after {attempt} attempts. Last problem: {failure}");
            }

            var backoff = wait ?? TimeSpan.FromSeconds(Math.Min(60, 2 * Math.Pow(2, attempt - 1)));
            await _delay(TimeSpan.FromSeconds(Math.Min(120, backoff.TotalSeconds)), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Keeps at least the minimum interval between two requests of this client (rate-limit friendly).</summary>
    private async Task PaceAsync(CancellationToken ct)
    {
        if (_minInterval <= TimeSpan.Zero)
        {
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var due = _lastSend + _minInterval;
            var now = DateTimeOffset.UtcNow;
            if (due > now)
            {
                await _delay(due - now, ct).ConfigureAwait(false);
            }

            _lastSend = DateTimeOffset.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsRetryableErrorBody(JsonNode? node)
    {
        var code = node?["error"]?["code"];
        if (code is null)
        {
            return false;
        }

        return int.TryParse(code.ToString(), out var n) ? n == 429 || n >= 500 : false;
    }

    private static string Truncate(string text) => text.Length <= 400 ? text : text[..400] + "...";

    internal JsonObject BuildBody(ModelRequest request)
    {
        var messages = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = request.System });
        foreach (var message in request.Messages)
        {
            AppendMessage(messages, message);
        }

        var body = new JsonObject
        {
            ["model"] = ModelId,
            ["messages"] = messages,
            ["max_tokens"] = request.MaxTokens,
        };

        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(request.Tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.InputSchema.DeepClone(),
                },
            }).ToArray());
        }

        return body;
    }

    private static void AppendMessage(JsonArray messages, Message message)
    {
        if (message.Role == "assistant")
        {
            var text = string.Concat(message.Content.OfType<TextBlock>().Select(t => t.Text));
            var calls = message.Content.OfType<ToolUseBlock>().Select(u => (JsonNode)new JsonObject
            {
                ["id"] = u.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = u.Name, ["arguments"] = u.Input.ToJsonString() },
            }).ToArray();

            var assistant = new JsonObject { ["role"] = "assistant", ["content"] = text.Length == 0 ? null : text };
            if (calls.Length > 0)
            {
                assistant["tool_calls"] = new JsonArray(calls);
            }

            messages.Add(assistant);
            return;
        }

        // A user turn: tool results become one "tool" message each, then any plain text as a user message.
        foreach (var result in message.Content.OfType<ToolResultBlock>())
        {
            messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = result.ToolUseId, ["content"] = result.Content });
        }

        var userText = string.Concat(message.Content.OfType<TextBlock>().Select(t => t.Text));
        if (userText.Length > 0 || !message.Content.OfType<ToolResultBlock>().Any())
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = userText });
        }
    }

    internal static ModelResponse ParseResponse(JsonNode root)
    {
        var choice = root["choices"]![0]!;
        var message = choice["message"];
        var blocks = new List<ContentBlock>();

        if (message?["content"] is JsonNode c && c.GetValueKind() == System.Text.Json.JsonValueKind.String
            && c.GetValue<string>() is { Length: > 0 } text)
        {
            blocks.Add(new TextBlock(text));
        }

        var n = 0;
        foreach (var call in message?["tool_calls"] as JsonArray ?? new JsonArray())
        {
            n++;
            var id = call?["id"]?.GetValue<string>();
            var name = call?["function"]?["name"]?.GetValue<string>() ?? string.Empty;
            blocks.Add(new ToolUseBlock(string.IsNullOrEmpty(id) ? $"call_{n}" : id, name, ParseArguments(call?["function"]?["arguments"])));
        }

        var finish = choice["finish_reason"]?.GetValue<string>();
        var stop = blocks.OfType<ToolUseBlock>().Any() ? "tool_use" : finish == "length" ? "max_tokens" : "end_turn";

        var usage = root["usage"];
        var prompt = usage?["prompt_tokens"]?.GetValue<long>() ?? 0;
        var cached = Math.Min(prompt, usage?["prompt_tokens_details"]?["cached_tokens"]?.GetValue<long>() ?? 0);
        return new ModelResponse(blocks, stop, new Usage(prompt - cached, usage?["completion_tokens"]?.GetValue<long>() ?? 0, cached, 0));
    }

    /// <summary>Arguments arrive as a JSON string; a model that sends broken JSON gets an empty object, so the tool answers with a clear error.</summary>
    private static JsonObject ParseArguments(JsonNode? arguments)
    {
        try
        {
            if (arguments is JsonObject o)
            {
                return o;
            }

            var raw = arguments?.GetValue<string>();
            return string.IsNullOrWhiteSpace(raw) ? new JsonObject() : JsonNode.Parse(raw) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return new JsonObject();
        }
    }
}
