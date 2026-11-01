using Agents.Core.Execution;
using Agents.Core.Tools;
using Xunit;

namespace Agents.Core.Tests;

public class McpSupportTests
{
    private sealed class Disposable : IAsyncDisposable
    {
        public int Disposed;

        public ValueTask DisposeAsync()
        {
            Disposed++;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void Spec_round_trips_a_prelude_with_quotes_and_spaces()
    {
        var prelude = "source /opt/miniconda3/bin/activate && conda activate 'testbed' && ";
        var spec = ToolServerSpec.For(new DockerExecutor("c1", "/testbed", prelude), new HashSet<string> { "run_tests", "read_file" });

        var back = ToolServerSpec.Decode(spec.Encode());

        Assert.Equal("c1", back.Container);
        Assert.Equal("/testbed", back.WorkDir);
        Assert.Equal(prelude, back.Prelude);
        Assert.Equal("read_file,run_tests", string.Join(",", back.Tools!));
    }

    [Fact]
    public void Spec_for_a_local_executor_and_full_toolset_has_no_tool_filter()
    {
        var spec = ToolServerSpec.For(new LocalExecutor("/tmp/x"));

        Assert.Null(spec.Container);
        Assert.Equal("/tmp/x", spec.LocalRoot);
        Assert.Null(spec.Tools);
        Assert.Equal(ToolCatalog.All.Count, spec.CreateHost().Tools.Count);
    }

    [Fact]
    public void Spec_is_read_from_server_arguments()
    {
        var spec = ToolServerSpec.For(new LocalExecutor("/tmp/y"));

        var parsed = ToolServerSpec.FromArgs(new[] { "ignored", ToolServerSpec.ArgName, spec.Encode() });

        Assert.Equal("/tmp/y", parsed.LocalRoot);
        var threw = false;
        try
        {
            ToolServerSpec.FromArgs(new[] { "--other" });
        }
        catch (ArgumentException)
        {
            threw = true;
        }

        Assert.True(threw);
    }

    [Fact]
    public void Server_host_advertises_the_catalog_verbatim_in_catalog_order()
    {
        var host = ToolServerSpec.For(new LocalExecutor("/tmp/z")).CreateHost();

        Assert.Equal(string.Join(",", ToolCatalog.All.Select(t => t.Name)), string.Join(",", host.Tools.Select(t => t.Name)));
        for (var i = 0; i < host.Tools.Count; i++)
        {
            Assert.Equal(ToolCatalog.All[i].Description, host.Tools[i].Description);
            Assert.Equal(ToolCatalog.All[i].InputSchema.ToJsonString(), host.Tools[i].InputSchema.ToJsonString());
        }
    }

    [Fact]
    public async Task Tracker_disposes_the_hosts_of_one_executor_once_and_leaves_others()
    {
        var tracker = new ToolHostTracker();
        var a = new LocalExecutor("/a");
        var b = new LocalExecutor("/b");
        var h1 = tracker.Track(a, new Disposable());
        var h2 = tracker.Track(a, new Disposable());
        var h3 = tracker.Track(b, new Disposable());

        await tracker.ReleaseAsync(a);
        await tracker.ReleaseAsync(a);

        Assert.Equal(1, h1.Disposed);
        Assert.Equal(1, h2.Disposed);
        Assert.Equal(0, h3.Disposed);
        Assert.Equal(1, tracker.Tracked);
    }
}
