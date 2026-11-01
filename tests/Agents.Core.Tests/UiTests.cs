using System.Text.Json;
using Agents.Core.Runs;
using Agents.Core.Ui;
using Xunit;

namespace Agents.Core.Tests;

public class UiTests
{
    private static string NewDir() =>
        Path.Combine(Path.GetTempPath(), "mamcp-ui-" + Guid.NewGuid().ToString("N"));

    private static readonly IReadOnlyDictionary<string, string> NoQuery = new Dictionary<string, string>();

    [Fact]
    public void Demo_data_is_served_with_labels_verdicts_and_calls()
    {
        var dir = NewDir();
        try
        {
            var n = DemoData.Generate(dir, tasks: 4, repeats: 1);
            var ui = new UiServer(new RunStore(dir));

            var page = ui.Handle("/", NoQuery);
            Assert.Equal(200, page.Status);
            Assert.True(System.Text.Encoding.UTF8.GetString(page.Body).Contains("<title>MCP multi-agent experiment</title>"));

            var data = JsonDocument.Parse(ui.Handle("/api/data", NoQuery).Body).RootElement;
            Assert.Equal(n, data.GetProperty("runs").GetArrayLength());
            Assert.True(data.GetProperty("labelsFound").GetBoolean());
            var first = data.GetProperty("runs")[0];
            Assert.Equal("demo-fake", first.GetProperty("manifest").GetProperty("model").GetString());
            Assert.True(first.GetProperty("label").GetString() is "parallel" or "sequential");

            var id = first.GetProperty("manifest").GetProperty("runId").GetString()!;
            var detail = JsonDocument.Parse(ui.Handle("/api/run", new Dictionary<string, string> { ["id"] = id }).Body).RootElement;
            Assert.True(detail.GetProperty("calls").GetArrayLength() > 0);
            Assert.True(detail.GetProperty("patch").GetString()!.Contains("diff --git"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Run_endpoint_rejects_unknown_ids_and_path_tricks()
    {
        var dir = NewDir();
        try
        {
            DemoData.Generate(dir, tasks: 2, repeats: 1);
            var ui = new UiServer(new RunStore(dir));

            Assert.Equal(404, ui.Handle("/api/run", new Dictionary<string, string> { ["id"] = "../../etc/passwd" }).Status);
            Assert.Equal(400, ui.Handle("/api/run", NoQuery).Status);
            Assert.Equal(404, ui.Handle("/nothing", NoQuery).Status);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Demo_attribution_adds_up_to_the_billed_cost()
    {
        var dir = NewDir();
        try
        {
            DemoData.Generate(dir, tasks: 3, repeats: 1);
            foreach (var m in new RunStore(dir).ListManifests())
            {
                var parts = m.AttrSysUsd + m.AttrDefUsd + m.AttrCallUsd + m.AttrResUsd + m.AttrTextUsd;
                Assert.True(Math.Abs(parts - m.CostUsd) <= m.CostUsd * 0.001m);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
