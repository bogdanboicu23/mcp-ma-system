using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Agents.Core;

/// <summary>
/// Minimal Messages API client built on HttpClient. It exists so that the harness controls exactly which
/// usage numbers are read (including cache reads/writes) and has no dependency on a preview SDK.
/// The system prompt and the last tool definition carry a cache_control marker, identically for every system.
/// NOTE: written against the documented Messages API; not yet exercised against the live API.
/// </summary>
public sealed class AnthropicHttpClient : IModelClient
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";
    private const int MaxAttempts = 5;

    private readonly HttpClient _http;

    public AnthropicHttpClient(string apiKey, string modelId = "claude-haiku-5-5", HttpClient? http = null)
    {
        ModelId = modelId;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.Remove("x-api-key");
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Remove("anthropic-version");
        _http.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
    }

    public string ModelId { get; }

    public async Task<ModelResponse> SendAsync(ModelRequest request, CancellationToken ct = default)
    {
        var payload = BuildBody(request).ToJsonString();

        for (var attempt = 1; ; attempt++)
        {
            using var content = new StringContent(payload, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
            using var response = await _http.PostAsync(Endpoint, content, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return ParseResponse(text);
            }

            var retryable = response.StatusCode == HttpStatusCode.TooManyRequests
                            || (int)response.StatusCode >= 500;
            if (!retryable || attempt >= MaxAttempts)
            {
                throw new HttpRequestException(
                    $"Anthropic API returned {(int)response.StatusCode}: {text}");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct).ConfigureAwait(false);
        }
    }

    internal JsonObject BuildBody(ModelRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = ModelId,
            ["max_tokens"] = request.MaxTokens,
            ["system"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = request.System,
                ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
            }),
            ["messages"] = new JsonArray(request.Messages.Select(ToJson).ToArray<JsonNode?>()),
        };

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            for (var i = 0; i < request.Tools.Count; i++)
            {
                var t = request.Tools[i];
                var tool = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["input_schema"] = t.InputSchema.DeepClone(),
                };
                if (i == request.Tools.Count - 1)
                {
                    tool["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
                }

                tools.Add(tool);
            }

            body["tools"] = tools;
        }

        return body;
    }

    private static JsonObject ToJson(Message message) => new()
    {
        ["role"] = message.Role,
        ["content"] = new JsonArray(message.Content.Select(ToJson).ToArray<JsonNode?>()),
    };

    private static JsonObject ToJson(ContentBlock block) => block switch
    {
        TextBlock t => new JsonObject { ["type"] = "text", ["text"] = t.Text },
        ToolUseBlock u => new JsonObject
        {
            ["type"] = "tool_use",
            ["id"] = u.Id,
            ["name"] = u.Name,
            ["input"] = u.Input.DeepClone(),
        },
        ToolResultBlock r => new JsonObject
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = r.ToolUseId,
            ["content"] = r.Content,
            ["is_error"] = r.IsError,
        },
        _ => throw new NotSupportedException($"Unknown block type {block.GetType().Name}"),
    };

    internal static ModelResponse ParseResponse(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject()
                   ?? throw new InvalidOperationException("Empty response from the API.");

        var blocks = new List<ContentBlock>();
        foreach (var node in root["content"]?.AsArray() ?? new JsonArray())
        {
            var obj = node!.AsObject();
            switch (obj["type"]?.GetValue<string>())
            {
                case "text":
                    blocks.Add(new TextBlock(obj["text"]?.GetValue<string>() ?? string.Empty));
                    break;
                case "tool_use":
                    blocks.Add(new ToolUseBlock(
                        obj["id"]!.GetValue<string>(),
                        obj["name"]!.GetValue<string>(),
                        (obj["input"] as JsonObject)?.DeepClone().AsObject() ?? new JsonObject()));
                    break;
            }
        }

        var u = root["usage"]?.AsObject();
        long Read(string name) => u?[name]?.GetValue<long>() ?? 0;
        var usage = new Usage(
            Read("input_tokens"),
            Read("output_tokens"),
            Read("cache_read_input_tokens"),
            Read("cache_creation_input_tokens"));

        return new ModelResponse(blocks, root["stop_reason"]?.GetValue<string>() ?? "unknown", usage);
    }
}
