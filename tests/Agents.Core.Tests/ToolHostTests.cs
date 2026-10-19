using System.Diagnostics;
using System.Text.Json.Nodes;
using Agents.Core.Execution;
using Agents.Core.Tools;
using Xunit;

namespace Agents.Core.Tests;

public class ToolHostTests
{
    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mamcp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static JsonObject Args(params (string Key, JsonNode? Value)[] items)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in items)
        {
            obj[key] = value;
        }

        return obj;
    }

    [Fact]
    public async Task Write_then_read_file_round_trips_with_line_numbers()
    {
        using var dir = new TempDir();
        var host = new ContainerToolHost(new LocalExecutor(dir.Path));

        var write = await host.CallAsync(ToolNames.WriteFile, Args(("path", "src/app.py"), ("content", "one\ntwo\nthree\n")));
        var read = await host.CallAsync(ToolNames.ReadFile, Args(("path", "src/app.py")));
        var range = await host.CallAsync(ToolNames.ReadFile,
            Args(("path", "src/app.py"), ("start_line", 2), ("end_line", 2)));

        Assert.False(write.IsError);
        Assert.False(read.IsError);
        Assert.True(read.Content.Contains("     1\tone"));
        Assert.True(read.Content.Contains("     3\tthree"));
        Assert.True(range.Content.Contains("     2\ttwo"));
        Assert.False(range.Content.Contains("one"));
    }

    [Fact]
    public async Task Edit_file_changes_only_the_match_and_keeps_line_endings_and_missing_final_newline()
    {
        using var dir = new TempDir();
        var host = new ContainerToolHost(new LocalExecutor(dir.Path));
        await host.CallAsync(ToolNames.WriteFile, Args(("path", "crlf.txt"), ("content", "a\r\nb\r\nc")));

        var edit = await host.CallAsync(ToolNames.EditFile,
            Args(("path", "crlf.txt"), ("old_string", "b"), ("new_string", "B")));

        Assert.False(edit.IsError);
        Assert.Equal("a\r\nB\r\nc", File.ReadAllText(Path.Combine(dir.Path, "crlf.txt")));
    }

    [Fact]
    public async Task Edit_file_rejects_missing_and_ambiguous_matches_without_touching_the_file()
    {
        using var dir = new TempDir();
        var host = new ContainerToolHost(new LocalExecutor(dir.Path));
        await host.CallAsync(ToolNames.WriteFile, Args(("path", "f.txt"), ("content", "x = 1\nx = 1\n")));

        var missing = await host.CallAsync(ToolNames.EditFile,
            Args(("path", "f.txt"), ("old_string", "y = 2"), ("new_string", "z")));
        var ambiguous = await host.CallAsync(ToolNames.EditFile,
            Args(("path", "f.txt"), ("old_string", "x = 1"), ("new_string", "x = 2")));

        Assert.True(missing.IsError);
        Assert.True(ambiguous.IsError);
        Assert.True(ambiguous.Content.Contains("2 places"));
        Assert.Equal("x = 1\nx = 1\n", File.ReadAllText(Path.Combine(dir.Path, "f.txt")));
    }

    [Fact]
    public async Task Paths_with_spaces_and_quotes_are_handled()
    {
        using var dir = new TempDir();
        var host = new ContainerToolHost(new LocalExecutor(dir.Path));

        await host.CallAsync(ToolNames.WriteFile, Args(("path", "my dir/it's.txt"), ("content", "hi\n")));
        var read = await host.CallAsync(ToolNames.ReadFile, Args(("path", "my dir/it's.txt")));
        var list = await host.CallAsync(ToolNames.ListDir, Args(("path", "my dir")));

        Assert.True(read.Content.Contains("hi"));
        Assert.True(list.Content.Contains("it's.txt"));
    }

    [Fact]
    public async Task Missing_file_and_missing_argument_are_tool_errors_not_crashes()
    {
        using var dir = new TempDir();
        var host = new ContainerToolHost(new LocalExecutor(dir.Path));

        var missingFile = await host.CallAsync(ToolNames.ReadFile, Args(("path", "nope.txt")));
        var missingArg = await host.CallAsync(ToolNames.EditFile, Args(("path", "nope.txt")));
        var unknownTool = await host.CallAsync("format_disk", Args());

        Assert.True(missingFile.IsError);
        Assert.True(missingArg.IsError);
        Assert.True(missingArg.Content.Contains("old_string"));
        Assert.True(unknownTool.IsError);
    }

    [Fact]
    public async Task Read_file_truncates_very_long_output()
    {
        using var dir = new TempDir();
        var host = new ContainerToolHost(new LocalExecutor(dir.Path));
        var content = string.Join("\n", Enumerable.Range(1, 5_000).Select(i => "line number " + i));
        await host.CallAsync(ToolNames.WriteFile, Args(("path", "big.txt"), ("content", content)));

        var read = await host.CallAsync(ToolNames.ReadFile, Args(("path", "big.txt")));

        Assert.False(read.IsError);
        Assert.True(read.Content.Contains("output truncated"));
        Assert.True(read.Content.Length < ContainerToolHost.MaxReadChars + 200);
    }

    [Fact]
    public async Task Run_command_reports_the_exit_code_and_trims_long_output()
    {
        using var dir = new TempDir();
        var host = new ContainerToolHost(new LocalExecutor(dir.Path));

        var failing = await host.CallAsync(ToolNames.RunCommand, Args(("command", "echo oops >&2; exit 3")));
        var noisy = await host.CallAsync(ToolNames.RunTests,
            Args(("command", "for i in $(seq 1 3000); do echo line-$i-padding-padding; done; echo THE-END")));

        Assert.False(failing.IsError);
        Assert.True(failing.Content.Contains("exit code: 3"));
        Assert.True(failing.Content.Contains("oops"));
        Assert.True(noisy.Content.Contains("chars omitted"));
        Assert.True(noisy.Content.Contains("THE-END"));
        Assert.True(noisy.Content.Length < ContainerToolHost.MaxCommandChars + 200);
    }

    [Fact]
    public async Task Run_command_times_out_and_does_not_hang()
    {
        using var dir = new TempDir();
        var host = new ContainerToolHost(new LocalExecutor(dir.Path));
        var clock = Stopwatch.StartNew();

        var result = await host.CallAsync(ToolNames.RunCommand,
            Args(("command", "sleep 20"), ("timeout_seconds", 1)));

        Assert.True(result.IsError);
        Assert.True(result.Content.Contains("timed out"));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_command_waiting_for_input_gets_end_of_file_instead_of_hanging()
    {
        using var dir = new TempDir();
        var host = new ContainerToolHost(new LocalExecutor(dir.Path));

        var result = await host.CallAsync(ToolNames.RunCommand, Args(("command", "cat; echo finished")));

        Assert.False(result.IsError);
        Assert.True(result.Content.Contains("finished"));
    }

    [Fact]
    public async Task A_restricted_host_hides_and_rejects_tools_outside_its_role()
    {
        using var dir = new TempDir();
        var host = new ContainerToolHost(new LocalExecutor(dir.Path), ToolCatalog.ReadOnly);

        var names = host.Tools.Select(t => t.Name).OrderBy(n => n).ToArray();
        var rejected = await host.CallAsync(ToolNames.RunCommand, Args(("command", "echo hi")));

        Assert.Equal(2, names.Length);
        Assert.Equal(ToolNames.ListDir, names[0]);
        Assert.Equal(ToolNames.ReadFile, names[1]);
        Assert.True(rejected.IsError);
    }

    [Fact]
    public void Every_catalog_tool_has_a_valid_object_schema_and_required_fields_exist()
    {
        Assert.Equal(6, ToolCatalog.All.Count);
        foreach (var tool in ToolCatalog.All)
        {
            Assert.True(tool.Description.Length > 0);
            Assert.Equal("object", tool.InputSchema["type"]!.GetValue<string>());
            var properties = tool.InputSchema["properties"]!.AsObject();
            if (tool.InputSchema["required"] is JsonArray required)
            {
                foreach (var field in required)
                {
                    Assert.True(properties.ContainsKey(field!.GetValue<string>()));
                }
            }
        }
    }

    [Fact]
    public async Task Patch_extractor_returns_a_diff_that_includes_new_files()
    {
        using var dir = new TempDir();
        var exec = new LocalExecutor(dir.Path);
        await exec.RunAsync(
            "git init -q && echo a > f.txt && git add . && git -c user.name=t -c user.email=t@t commit -qm init",
            TimeSpan.FromSeconds(30));
        var host = new ContainerToolHost(exec);
        await host.CallAsync(ToolNames.WriteFile, Args(("path", "f.txt"), ("content", "b\n")));
        await host.CallAsync(ToolNames.WriteFile, Args(("path", "new/g.txt"), ("content", "fresh\n")));

        var patch = await PatchExtractor.GetPatchAsync(exec);

        Assert.True(patch.Contains("diff --git a/f.txt b/f.txt"));
        Assert.True(patch.Contains("+b"));
        Assert.True(patch.Contains("new/g.txt"));
        Assert.True(patch.Contains("+fresh"));
    }

    [Fact]
    public void Docker_exec_arguments_run_the_command_under_an_in_container_timeout()
    {
        var docker = new DockerExecutor("c1", "/testbed", "source env && ");

        var args = docker.BuildArgs("pytest -x", 90);

        Assert.Equal("exec", args[0]);
        Assert.Equal("/testbed", args[args.ToList().IndexOf("-w") + 1]);
        Assert.Equal("c1", args[args.ToList().IndexOf("-w") + 2]);
        Assert.Equal("timeout", args[5]);
        Assert.Equal("90", args[8]);
        Assert.Equal("source env && pytest -x", args[^1]);
    }
}
