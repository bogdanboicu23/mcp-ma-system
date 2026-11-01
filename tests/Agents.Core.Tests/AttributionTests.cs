using System.Text.Json.Nodes;
using Agents.Core.Runs;
using Agents.Core.Swe;
using Agents.Core.Tools;
using Xunit;

namespace Agents.Core.Tests;

public class AttributionTests
{
    private static ModelRequest SampleRequest() => new(
        "You are an engineer. Be economical.",
        new Message[]
        {
            Message.User("Fix the bug in calc.py please."),
            new("assistant", new ContentBlock[]
            {
                new TextBlock("Let me look at the file."),
                new ToolUseBlock("t1", ToolNames.ReadFile, new JsonObject { ["path"] = "calc.py" }),
            }),
            new("user", new ContentBlock[]
            {
                new ToolResultBlock("t1", "     1\tdef add(a, b):\n     2\t    return a - b\n"),
            }),
        },
        ToolCatalog.All,
        1024);

    private static ModelResponse SampleResponse(Usage usage) => new(
        new ContentBlock[]
        {
            new TextBlock("Found it."),
            new ToolUseBlock("t2", ToolNames.EditFile, new JsonObject { ["path"] = "calc.py", ["old_string"] = "a - b", ["new_string"] = "a + b" }),
        },
        "tool_use", usage);

    [Fact]
    public async Task Parts_add_up_exactly_to_the_api_prompt_tokens_and_to_the_cost()
    {
        foreach (var usage in new[]
                 {
                     new Usage(1500, 120, 4000, 0),
                     new Usage(300, 80, 0, 5200),
                     new Usage(777, 33, 1234, 56),
                     new Usage(0, 10, 0, 0),
                 })
        {
            var attribution = await new TokenAttributor().AttributeAsync(SampleRequest(), SampleResponse(usage));

            Assert.Equal(usage.PromptTokens, attribution.Prompt);
            Assert.Equal(usage.OutputTokens, attribution.OutText + attribution.OutCall);
            Assert.Equal(Pricing.Haiku55.Cost(usage), attribution.TotalUsd);
        }
    }

    [Fact]
    public async Task The_high_price_tier_multiplier_is_applied_to_every_part()
    {
        var usage = new Usage(90_000, 500, 20_000, 0); // prompt 110k, above the 100k tier
        var attribution = await new TokenAttributor().AttributeAsync(SampleRequest(), SampleResponse(usage));

        Assert.Equal(Pricing.Haiku55.Cost(usage), attribution.TotalUsd);
        Assert.True(attribution.TotalUsd > 5m * 0.00001m); // five times the base rate, not the base rate
    }

    [Fact]
    public async Task Cache_reads_are_priced_on_the_earliest_parts_of_the_prompt()
    {
        // The cached prefix is tools + system prompt: definitions must be charged at the cache-read price.
        var usage = new Usage(500, 20, 10_000, 0); // the cached prefix is longer than the definitions
        var a = await new TokenAttributor().AttributeAsync(SampleRequest(), SampleResponse(usage));

        var definitionRate = a.DefUsd / a.Def;
        Assert.Equal(Pricing.Haiku55.CacheReadPerM / 1_000_000m, definitionRate);
        Assert.True(a.Res > 0 && a.Call > 0 && a.Text > 0 && a.Sys > 0);
    }

    [Fact]
    public async Task Measured_definition_tokens_replace_the_character_estimate()
    {
        var usage = new Usage(500, 20, 3000, 0);
        var measured = 1234L;
        var attributor = new TokenAttributor(measureDefinitions: (_, _) => Task.FromResult(measured));

        var a = await attributor.AttributeAsync(SampleRequest(), SampleResponse(usage));

        Assert.Equal(measured, a.Def);
        Assert.Equal(usage.PromptTokens, a.Prompt);
    }

    [Fact]
    public async Task Each_tool_set_is_measured_only_once()
    {
        var calls = 0;
        var attributor = new TokenAttributor(measureDefinitions: (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(900L);
        });

        for (var i = 0; i < 3; i++)
        {
            await attributor.AttributeAsync(SampleRequest(), SampleResponse(new Usage(100, 5, 3000, 0)));
        }

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_run_records_attribution_per_call_and_totals_in_the_manifest_and_report()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mamcp-attr-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RunStore(dir);
            var prompts = PromptSet.FromTexts("system prompt", "Fix {{repo}}\n{{problem_statement}}");
            var model = new ScriptedModelClient(new[]
            {
                new ModelResponse(
                    new ContentBlock[] { new ToolUseBlock("a", ToolNames.ReadFile, new JsonObject { ["path"] = "calc.py" }) },
                    "tool_use", new Usage(1000, 50, 0, 3000)),
                new ModelResponse(new ContentBlock[] { new TextBlock("done") }, "end_turn", new Usage(200, 20, 3000, 600)),
            });
            var limits = new RunLimits();
            var system = new SingleAgentSystem("A", prompts, exec => new ContainerToolHost(exec), limits.MaxTurns);
            var env = new LocalEnvironmentProvider(_ => new Dictionary<string, string> { ["calc.py"] = "x = 1\n" });
            var task = new SweBenchTask
            {
                InstanceId = "demo__repo-1", Repo = "demo/repo", BaseCommit = "x", ProblemStatement = "p",
                FailToPass = new[] { "t" }, GoldPatch = "g",
            };

            var manifest = await new RunOrchestrator(model, system, env, store, prompts, limits, "sha").RunAsync(task, 1);

            Assert.Equal(2, manifest.Calls);
            Assert.Equal(1, manifest.Agents);
            Assert.True(manifest.AttrDefTokens > 0 && manifest.AttrResTokens > 0);
            var attributed = manifest.AttrSysUsd + manifest.AttrDefUsd + manifest.AttrCallUsd + manifest.AttrResUsd + manifest.AttrTextUsd;
            Assert.Equal(manifest.CostUsd, attributed);

            var csv = ReportBuilder.BuildCsv(store);
            Assert.True(csv.Split('\n')[0].Contains("mcp_share"));
            var summary = ReportBuilder.Summarize(store).Single();
            Assert.True(summary.MeanMcpShare > 0m && summary.MeanMcpShare < 1m);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
