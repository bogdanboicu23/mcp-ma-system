using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Agents.Core.Execution;
using Agents.Core.Swe;
using Agents.Core.Tools;
using Xunit;

namespace Agents.Core.Tests;

public class OpenAiCompatTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _replies;

        public FakeHandler(params Func<HttpResponseMessage>[] replies)
        {
            _replies = new Queue<Func<HttpResponseMessage>>(replies);
        }

        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return _replies.Dequeue()();
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string ToolCallReply = """
        {"choices":[{"finish_reason":"tool_calls","message":{"content":null,"tool_calls":[
          {"id":"","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"a.py\"}"}},
          {"id":"x2","type":"function","function":{"name":"list_dir","arguments":"not json"}}]}}],
         "usage":{"prompt_tokens":1000,"completion_tokens":30,"prompt_tokens_details":{"cached_tokens":600}}}
        """;

    private static ModelRequest SampleRequest() => new(
        "SYS",
        new Message[]
        {
            Message.User("fix it"),
            new("assistant", new ContentBlock[]
            {
                new TextBlock("looking"),
                new ToolUseBlock("c1", "read_file", new JsonObject { ["path"] = "a.py" }),
            }),
            new("user", new ContentBlock[] { new ToolResultBlock("c1", "print(1)") }),
        },
        ToolCatalog.All.Take(2).ToList(),
        512);

    [Fact]
    public void History_is_converted_to_the_chat_format()
    {
        var body = new OpenAiCompatClient("http://x", "m", null).BuildBody(SampleRequest());
        var roles = string.Join(",", body["messages"]!.AsArray().Select(m => m!["role"]!.GetValue<string>()));

        Assert.Equal("system,user,assistant,tool", roles);
        var assistant = body["messages"]![2]!;
        Assert.Equal("looking", assistant["content"]!.GetValue<string>());
        Assert.Equal("read_file", assistant["tool_calls"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("{\"path\":\"a.py\"}", assistant["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal("c1", body["messages"]![3]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal("function", body["tools"]![0]!["type"]!.GetValue<string>());
        Assert.Equal(ToolCatalog.All[0].Name, body["tools"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal(512, body["max_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task Tool_calls_usage_and_missing_ids_are_parsed_and_tolerant_of_broken_arguments()
    {
        var client = new OpenAiCompatClient("http://x", "m", "key", http: new HttpClient(new FakeHandler(() => Json(HttpStatusCode.OK, ToolCallReply))));

        var response = await client.SendAsync(SampleRequest());

        var calls = response.Content.OfType<ToolUseBlock>().ToList();
        Assert.Equal("tool_use", response.StopReason);
        Assert.Equal(2, calls.Count);
        Assert.Equal("call_1", calls[0].Id);
        Assert.Equal("a.py", calls[0].Input["path"]!.GetValue<string>());
        Assert.Equal("x2", calls[1].Id);
        Assert.Equal(0, calls[1].Input.Count);
        Assert.Equal(400, response.Usage.InputTokens);
        Assert.Equal(600, response.Usage.CacheReadTokens);
        Assert.Equal(30, response.Usage.OutputTokens);
    }

    [Fact]
    public async Task A_rate_limit_waits_for_retry_after_and_tries_again()
    {
        var limited = () =>
        {
            var r = Json(HttpStatusCode.TooManyRequests, """{"error":"slow down"}""");
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return r;
        };
        var ok = () => Json(HttpStatusCode.OK, """{"choices":[{"finish_reason":"stop","message":{"content":"done"}}],"usage":{"prompt_tokens":5,"completion_tokens":1}}""");
        var handler = new FakeHandler(limited, ok);
        var waits = new List<TimeSpan>();
        var client = new OpenAiCompatClient("http://x", "m", null, http: new HttpClient(handler), delay: (s, _) => { waits.Add(s); return Task.CompletedTask; });

        var response = await client.SendAsync(SampleRequest());

        Assert.Equal("end_turn", response.StopReason);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(7, (int)waits.Single().TotalSeconds);
    }

    [Fact]
    public async Task Client_errors_are_not_retried_and_an_error_body_with_status_200_is_not_a_completion()
    {
        var bad = new OpenAiCompatClient("http://x", "m", null,
            http: new HttpClient(new FakeHandler(() => Json(HttpStatusCode.Unauthorized, """{"error":"bad key"}"""))));
        var caught = "";
        try
        {
            await bad.SendAsync(SampleRequest());
        }
        catch (HttpRequestException ex)
        {
            caught = ex.Message;
        }

        Assert.True(caught.Contains("401"));

        var odd = new OpenAiCompatClient("http://x", "m", null,
            http: new HttpClient(new FakeHandler(() => Json(HttpStatusCode.OK, """{"error":{"code":400,"message":"no tools"}}"""))));
        caught = "";
        try
        {
            await odd.SendAsync(SampleRequest());
        }
        catch (HttpRequestException ex)
        {
            caught = ex.Message;
        }

        Assert.True(caught.Contains("no completion"));
    }

    [Fact]
    public async Task Calls_are_paced_by_the_minimum_interval()
    {
        var ok = () => Json(HttpStatusCode.OK, """{"choices":[{"finish_reason":"stop","message":{"content":"x"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""");
        var waits = new List<TimeSpan>();
        var client = new OpenAiCompatClient("http://x", "m", null, minIntervalMs: 5000,
            http: new HttpClient(new FakeHandler(ok, ok)), delay: (s, _) => { waits.Add(s); return Task.CompletedTask; });

        await client.SendAsync(SampleRequest());
        await client.SendAsync(SampleRequest());

        Assert.Equal(1, waits.Count); // the first call is immediate, the second waits almost the whole interval
        Assert.True(waits[0] > TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void Presets_are_found_by_name_and_unknown_names_are_rejected()
    {
        Assert.Equal("OPENROUTER_API_KEY", ProviderPreset.Find("OpenRouter").ApiKeyEnv);
        Assert.Equal("GROQ_API_KEY", ProviderPreset.Find("groq").ApiKeyEnv);
        var threw = false;
        try
        {
            ProviderPreset.Find("nope");
        }
        catch (ArgumentException)
        {
            threw = true;
        }

        Assert.True(threw);
    }

    [Fact]
    public async Task The_replay_model_solves_every_synthetic_task_through_the_real_agent_loop_and_tools()
    {
        var fromEnv = Environment.GetEnvironmentVariable("MAMCP_ROOT");
        var root = new DirectoryInfo(string.IsNullOrEmpty(fromEnv) ? AppContext.BaseDirectory : fromEnv);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "tasks", "synthetic")))
        {
            root = root.Parent;
        }

        var suite = Path.Combine(root!.FullName, "tasks", "synthetic");
        var tasks = TaskLoader.Load(Path.Combine(suite, "tasks.jsonl"));
        var prompts = PromptSet.Load();
        var failures = new List<string>();

        foreach (var task in tasks)
        {
            var work = Path.Combine(Path.GetTempPath(), "mamcp-replay-" + Guid.NewGuid().ToString("N"));
            try
            {
                CopyDir(Path.Combine(suite, task.InstanceId, "repo"), work);
                var run = new RunContext(
                    "r", "A", new ReplayModelClient(tasks, suite), new RunBudget(1_000_000, 5m, Pricing.Notional),
                    new ContextPolicy(), new MemoryCallLog());
                var agent = new AgentSpec("agent-1", "solo", prompts.SystemPrompt, new ContainerToolHost(new LocalExecutor(work)), 30);

                var result = await AgentLoop.RunAsync(agent, prompts.RenderTask(task), run);

                CopyDir(Path.Combine(suite, task.InstanceId, "hidden_tests"), Path.Combine(work, "hidden_tests"));
                if (result.StopReason != StopReasons.EndTurn || !PythonTestsPass(work))
                {
                    failures.Add($"{task.InstanceId}: stop={result.StopReason}");
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

        Assert.Equal(string.Empty, string.Join("; ", failures));
    }

    private static bool PythonTestsPass(string cwd)
    {
        var start = new ProcessStartInfo("python3", "-m unittest discover -s hidden_tests")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["PYTHONPATH"] = cwd;
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        using var p = Process.Start(start)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0;
    }

    private static void CopyDir(string from, string to)
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
