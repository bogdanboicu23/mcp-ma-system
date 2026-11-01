using Agents.Mcp;
using Agents.Topologies;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Agents.Core;
using Agents.Core.Execution;
using Agents.Core.Runs;
using Agents.Core.Swe;
using Agents.Core.Tools;

internal static class Cli
{
    private const string UsageText = """
        Commands:
          dry | live                       smoke test of the agent loop (dry is free, live needs ANTHROPIC_API_KEY)
          select --n 10 --seed 42          pick a reproducible pilot sample, writes tasks/pilot_ids.txt
                 [--labels tasks/labels.csv --parallel-n 5]   stratified: that many parallel tasks (RQ3)
          label  [--theta 0.4]             label every task parallel/sequential from its reference patch -> tasks/labels.csv
          check  --task <id>               start the task's Docker image and verify it (git state, python, tools)
          run    --task <id> --config A    run one task (add --dry for a scripted model: tests Docker, costs nothing)
          batch  --config A --ids-file tasks/pilot_ids.txt --repeats 1 --max-usd 5
          eval   --config A --repeat 1     build predictions and run the official SWE-bench evaluator
          report                           write results/report.csv and print per-configuration totals

        Synthetic suite: add --synthetic [dir] to select, check, run, batch and eval (needs the image from tools/synthetic/Dockerfile)
        Common options: --tasks-file tasks/swebench_verified.jsonl  --out results  --model claude-haiku-5-5
        Limits (identical for all configs): --max-prompt-tokens 80000 --max-total-tokens 3000000 --max-cost 0.50 --max-turns 50
        Tools:  A = direct, B = single agent over MCP, C/D/E = MCP by default (--transport direct|mcp, --mcp-server <Mcp.Server.dll>)
        ui     [--out results] [--port 5080]   local web UI over the results folder (read-only, offline)
        demo-data [--out results-demo]   write FAKE runs so the UI can be tried before any paid run
        mcp-check                        free: the MCP server and the direct host must return identical tool output
        Config C: --workers 2|3|4 --exposure full|role     Config D: --rounds 3
        Container: --memory-gb 4 --cpus 2 --prelude "<shell prefix>" --workdir /testbed
        """;

    public static async Task<int> RunAsync(string[] argv)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.Error.WriteLine("Stopping... (the container is removed first)");
        };

        try
        {
            var args = CliArgs.Parse(argv);
            return args.Command switch
            {
                "dry" => await Smoke.RunAsync(live: false),
                "live" => await Smoke.RunAsync(live: true),
                "select" => Select(args),
                "label" => Label(args),
                "check" => await CheckAsync(args, cts.Token),
                "mcp-check" => await McpCheck.RunAsync(McpServerLaunch.Locate(args.Get("mcp-server")), cts.Token),
                "run" => await RunOneAsync(args, cts.Token),
                "batch" => await BatchAsync(args, cts.Token),
                "eval" => await EvalAsync(args, cts.Token),
                "report" => Report(args),
                "ui" => await UiAsync(args, cts.Token),
                "demo-data" => DemoDataCommand(args),
                _ => PrintUsage(),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException
                                       or FileNotFoundException or DirectoryNotFoundException or InvalidDataException
                                       or NotSupportedException or FormatException)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }

    private static int PrintUsage()
    {
        Console.WriteLine(UsageText);
        return 0;
    }

    // ---------------------------------------------------------------- select

    private static int Select(CliArgs args)
    {
        var tasks = TaskLoader.Load(TasksFile(args));
        var count = args.GetInt("n", 10);
        var seed = args.GetInt("seed", 42);
        var ids = args.Get("labels") is { } labelsPath
            ? TaskSelector.SelectStratified(
                tasks, TaskLabeler.ReadCsv(labelsPath), count, args.GetInt("parallel-n", count / 2), seed)
            : TaskSelector.Select(tasks, count, seed);
        var path = args.Get("out-file", Path.Combine("tasks", "pilot_ids.txt"));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllLines(path, new[] { $"# {ids.Count} tasks, seed {seed}, spread over repositories" }.Concat(ids));
        Console.WriteLine($"Wrote {ids.Count} ids to {path}:");
        foreach (var id in ids)
        {
            Console.WriteLine("  " + id);
        }

        return 0;
    }

    // ----------------------------------------------------------------- label

    /// <summary>Labels every task parallel or sequential from its reference patch (RQ3) and writes tasks/labels.csv.</summary>
    private static int Label(CliArgs args)
    {
        var tasks = TaskLoader.Load(TasksFile(args));
        var theta = args.GetDouble("theta", TaskLabeler.DefaultTheta);
        var labels = tasks.Select(t => TaskLabeler.Label(t.InstanceId, t.GoldPatch, theta)).ToList();
        var path = args.Get("out-file", Path.Combine("tasks", "labels.csv"));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, TaskLabeler.ToCsv(labels, theta));

        var parallel = labels.Count(l => l.Label == TaskLabel.Parallel);
        Console.WriteLine($"Wrote {labels.Count} labels to {path} (theta {theta:0.00}).");
        Console.WriteLine($"  parallel: {parallel}, sequential: {labels.Count - parallel}");
        Console.WriteLine($"  single-file patches: {labels.Count(l => l.Files == 1)}, single-hunk: {labels.Count(l => l.Hunks == 1)}");
        return 0;
    }

    // -------------------------------------------------------------------- ui

    private static async Task<int> UiAsync(CliArgs args, CancellationToken ct)
    {
        var store = new RunStore(args.Get("out", "results"));
        var port = args.GetInt("port", 5080);
        var url = $"http://127.0.0.1:{port}/";
        Console.WriteLine($"Results UI for {store.Root}: {url}   (Ctrl+C stops it; reload the page to see new runs)");
        if (!args.Has("no-open"))
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception)
            {
                // no default browser: the URL above is enough
            }
        }

        await new Agents.Core.Ui.UiServer(store, args.Get("labels")).ServeAsync(port, ct);
        return 0;
    }

    private static int DemoDataCommand(CliArgs args)
    {
        var dir = args.Get("out", "results-demo");
        var n = Agents.Core.Ui.DemoData.Generate(dir);
        Console.WriteLine($"Wrote {n} FAKE runs to {dir}. View them with: ui --out {dir}");
        return 0;
    }

    // ----------------------------------------------------------------- check

    private static async Task<int> CheckAsync(CliArgs args, CancellationToken ct)
    {
        var task = LoadTask(args);
        var options = ImageOptions(args);
        var image = SweBenchImage.Name(task.InstanceId, options);
        Console.WriteLine($"Starting {image} (the first start downloads the image, this can take minutes)...");

        var provider = Environments(args);
        await using var environment = await provider.StartAsync(task, "check-" + task.InstanceId, ct);
        Console.WriteLine("OK: " + environment.Description);

        var host = new ContainerToolHost(environment.Executor);
        var list = await host.CallAsync(ToolNames.ListDir, new JsonObject { ["path"] = "." }, ct);
        Console.WriteLine("list_dir .:\n" + Indent(Head(list.Content, 12)));
        var python = await host.CallAsync(
            ToolNames.RunCommand,
            new JsonObject { ["command"] = "python -c \"import sys; print(sys.version.split()[0], sys.executable)\"" }, ct);
        Console.WriteLine("python:\n" + Indent(python.Content));
        Console.WriteLine($"Task: {task.Repo}, {task.FailToPass.Count} FAIL_TO_PASS tests, problem statement {task.ProblemStatement.Length} chars.");
        return 0;
    }

    // ------------------------------------------------------------------- run

    private static async Task<int> RunOneAsync(CliArgs args, CancellationToken ct)
    {
        var task = LoadTask(args);
        var repeat = args.GetInt("repeat", 1);
        var dry = args.Has("dry");

        var orchestrator = await BuildOrchestratorAsync(args, dry);
        Console.WriteLine($"Running {args.Get("config", "A")} on {task.InstanceId} (repeat {repeat}){(dry ? " [dry: scripted model]" : string.Empty)}...");
        var manifest = await orchestrator.RunAsync(task, repeat, ct);

        Console.WriteLine(Describe(manifest));
        Console.WriteLine("Run folder: " + StoreFor(args, dry).RunDir(manifest.RunId));
        if (manifest.Error is not null)
        {
            Console.WriteLine("Error: " + manifest.Error);
        }

        return manifest.Status == "completed" ? 0 : 1;
    }

    // ----------------------------------------------------------------- batch

    private static async Task<int> BatchAsync(CliArgs args, CancellationToken ct)
    {
        var all = TaskLoader.Load(TasksFile(args));
        IReadOnlyList<string> ids = args.Get("ids") is { } list
            ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : TaskLoader.LoadIds(args.Get("ids-file", Path.Combine("tasks", "pilot_ids.txt")));
        if (args.Has("limit"))
        {
            ids = ids.Take(args.GetInt("limit", ids.Count)).ToList();
        }

        var tasks = TaskLoader.Pick(all, ids);
        var repeats = args.GetInt("repeats", 1);
        var maxUsd = args.GetDecimal("max-usd", 5m);
        var config = args.Get("config", "A");

        var orchestrator = await BuildOrchestratorAsync(args, dry: false);
        var store = StoreFor(args, dry: false);

        var total = tasks.Count * repeats;
        var index = 0;
        var skipped = 0;
        var spent = 0m;
        var consecutiveErrors = 0;

        foreach (var task in tasks)
        {
            for (var repeat = 1; repeat <= repeats; repeat++)
            {
                index++;
                var runId = RunStore.MakeRunId(config, task.InstanceId, repeat);
                if (store.IsComplete(runId))
                {
                    skipped++;
                    continue;
                }

                if (spent >= maxUsd)
                {
                    Console.WriteLine($"Stopping: spent ${spent:F2} in this session, the limit is ${maxUsd:F2}. Run the same command to continue.");
                    return PrintBatchSummary(skipped, spent);
                }

                Console.Write($"[{index}/{total}] {runId} ... ");
                var manifest = await orchestrator.RunAsync(task, repeat, ct);
                spent += manifest.CostUsd;
                Console.WriteLine(Describe(manifest));

                consecutiveErrors = manifest.Status == "error" ? consecutiveErrors + 1 : 0;
                if (consecutiveErrors >= 3)
                {
                    Console.Error.WriteLine($"Stopping: 3 runs in a row failed. Last error: {manifest.Error}");
                    return 1;
                }
            }
        }

        return PrintBatchSummary(skipped, spent);

        static int PrintBatchSummary(int skippedRuns, decimal spentUsd)
        {
            Console.WriteLine($"Done. Skipped {skippedRuns} finished run(s). Spent ${spentUsd:F4} in this session.");
            return 0;
        }
    }

    // ------------------------------------------------------------------ eval

    private static async Task<int> EvalAsync(CliArgs args, CancellationToken ct)
    {
        var store = StoreFor(args, dry: false);
        var config = args.Get("config", "A");
        var repeat = args.GetInt("repeat", 1);
        var evalRunId = args.Get("run-id", $"{config}-r{repeat}-{DateTime.UtcNow:yyyyMMdd-HHmm}");

        if (args.Has("synthetic"))
        {
            // The synthetic suite has its own hidden tests; no SWE-bench evaluator involved.
            var (done, solved) = await SyntheticEvaluator.EvaluateAsync(
                SyntheticDir(args), store, config, repeat, args.Get("synthetic-image", "mamcp-synth"), evalRunId, ct);
            Console.WriteLine($"Evaluated {done} synthetic runs; resolved {solved}.");
            return done == 0 ? 1 : 0;
        }

        var predictions = Predictions.Build(store, config, repeat);
        if (predictions.Count == 0)
        {
            Console.Error.WriteLine($"No completed runs for config {config}, repeat {repeat} in {store.Root}.");
            return 1;
        }

        Console.WriteLine($"Predictions: {predictions.Path} ({predictions.Count} instances)");
        Directory.CreateDirectory(store.EvalDir);

        var start = new ProcessStartInfo(args.Get("python", OperatingSystem.IsWindows() ? "python" : "python3"))
        {
            WorkingDirectory = store.EvalDir, // the evaluator writes logs/ and its report into the working directory
            UseShellExecute = false,
        };
        foreach (var part in new[]
                 {
                     "-m", "swebench.harness.run_evaluation",
                     "--dataset_name", args.Get("dataset", "princeton-nlp/SWE-bench_Verified"),
                     "--predictions_path", Path.GetFullPath(predictions.Path),
                     "--max_workers", args.GetInt("max-workers", 4).ToString(CultureInfo.InvariantCulture),
                     "--run_id", evalRunId,
                     "--cache_level", args.Get("cache-level", "env"),
                     "--instance_ids",
                 })
        {
            start.ArgumentList.Add(part);
        }

        foreach (var run in predictions.Runs)
        {
            start.ArgumentList.Add(run.InstanceId);
        }

        Console.WriteLine("Starting the official evaluator (this can take a long time on the first run)...");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start python.");
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        if (process.ExitCode != 0)
        {
            Console.Error.WriteLine($"The evaluator exited with code {process.ExitCode}.");
            return 1;
        }

        var results = EvalResultsParser.Load(store.EvalDir, evalRunId);
        var evaluated = 0;
        var resolved = 0;
        foreach (var run in predictions.Runs)
        {
            if (!results.Evaluated.Contains(run.InstanceId))
            {
                continue;
            }

            var isResolved = results.Resolved.Contains(run.InstanceId);
            store.WriteVerdict(run.RunId, new Verdict(isResolved, evalRunId, DateTimeOffset.UtcNow));
            evaluated++;
            resolved += isResolved ? 1 : 0;
        }

        Console.WriteLine($"Evaluated {evaluated} of {predictions.Count}; resolved {resolved}.");
        if (evaluated < predictions.Count)
        {
            Console.WriteLine("Runs without a verdict had an evaluator error or an empty patch; see the evaluator logs under " + store.EvalDir);
        }

        return 0;
    }

    // ---------------------------------------------------------------- report

    private static int Report(CliArgs args)
    {
        var store = StoreFor(args, dry: false);
        var csvPath = Path.Combine(store.Root, "report.csv");
        Directory.CreateDirectory(store.Root);
        File.WriteAllText(csvPath, ReportBuilder.BuildCsv(store));

        var summaries = ReportBuilder.Summarize(store);
        Console.WriteLine($"Wrote {csvPath}");
        if (summaries.Count == 0)
        {
            Console.WriteLine("No runs yet.");
            return 0;
        }

        Console.WriteLine($"{"config",-7}{"runs",5}{"done",6}{"err",5}{"eval",6}{"resolved",10}{"total $",10}{"mean $",9}{"turns",7}{"agents",8}{"calls",7}{"mcp%",7}");
        foreach (var s in summaries)
        {
            Console.WriteLine($"{s.Config,-7}{s.Runs,5}{s.Completed,6}{s.Errors,5}{s.Evaluated,6}{s.Resolved,10}{s.TotalCostUsd,10:F3}{s.MeanCostUsd,9:F4}{s.MeanTurns,7:F1}{s.MeanAgents,8:F1}{s.MeanCalls,7:F1}{s.MeanMcpShare * 100m,7:F1}");
        }

        return 0;
    }

    // --------------------------------------------------------------- helpers

    private static string TasksFile(CliArgs args) =>
        args.Get("tasks-file") is null && args.Has("synthetic")
            ? Path.Combine(SyntheticDir(args), "tasks.jsonl")
            : TasksFileDefault(args);

    private static string TasksFileDefault(CliArgs args) =>
        args.Get("tasks-file", Path.Combine("tasks", "swebench_verified.jsonl"));

    private static SweBenchTask LoadTask(CliArgs args)
    {
        var id = args.Require("task");
        return TaskLoader.Pick(TaskLoader.Load(TasksFile(args)), new[] { id })[0];
    }

    private static RunStore StoreFor(CliArgs args, bool dry) =>
        new(args.Get("out", dry ? Path.Combine("results", "dry") : "results"));

    private static RunLimits Limits(CliArgs args) => new(
        args.GetInt("max-prompt-tokens", 80_000),
        args.GetLong("max-total-tokens", 3_000_000),
        args.GetDecimal("max-cost", 0.50m),
        args.GetInt("max-turns", 50));

    /// <summary>Config C options: <c>--workers 2|3|4</c> and <c>--exposure full|role</c>.</summary>
    private static HubOptions Hub(CliArgs args) => new(
        args.GetInt("workers", 3),
        args.Get("exposure", "full").ToLowerInvariant() switch
        {
            "full" => ToolExposure.Full,
            "role" => ToolExposure.Role,
            var other => throw new ArgumentException($"--exposure must be 'full' or 'role', got '{other}'."),
        });

    /// <summary>SWE-bench images by default; with <c>--synthetic [dir]</c> the hand-written suite in its own small image.</summary>
    private static ITaskEnvironmentProvider Environments(CliArgs args) =>
        args.Has("synthetic")
            ? new SyntheticEnvironmentProvider(
                SyntheticDir(args), args.Get("synthetic-image", "mamcp-synth"), args.GetInt("memory-gb", 2), args.GetDouble("cpus", 1))
            : new DockerEnvironmentProvider(ImageOptions(args), args.GetInt("memory-gb", 4), args.GetDouble("cpus", 2));

    private static string SyntheticDir(CliArgs args) =>
        args.Get("synthetic") is { } v && v != "true" ? v : Path.Combine("tasks", "synthetic");

    private static SweBenchImageOptions ImageOptions(CliArgs args)
    {
        var defaults = new SweBenchImageOptions();
        return defaults with
        {
            Prelude = args.Get("prelude", defaults.Prelude),
            WorkDir = args.Get("workdir", defaults.WorkDir),
            Namespace = args.Get("registry", defaults.Namespace),
        };
    }

    private static async Task<RunOrchestrator> BuildOrchestratorAsync(CliArgs args, bool dry)
    {
        var prompts = PromptSet.Load();
        var limits = Limits(args);
        var config = args.Get("config", "A");
        var transport = args.Get("transport") is { } t
            ? t.ToLowerInvariant() switch
            {
                "direct" => ToolTransport.Direct,
                "mcp" => ToolTransport.Mcp,
                var other => throw new ArgumentException($"--transport must be 'direct' or 'mcp', got '{other}'."),
            }
            : Configs.DefaultTransport(config);
        var mcp = transport == ToolTransport.Mcp
            ? new McpToolHostFactory(McpServerLaunch.Locate(args.Get("mcp-server")))
            : null;
        var system = Configs.Create(config, prompts, limits, Hub(args), args.GetInt("rounds", 3), transport, mcp);
        IModelClient model = dry ? DryModel() : RealModel(args);
        var environments = Environments(args);
        // Live runs measure each tool set's definition tokens once, exactly (free token-counting endpoint).
        var attributor = model is AnthropicHttpClient live && !args.Has("no-measure")
            ? new TokenAttributor(measureDefinitions: (tools, ct) => live.MeasureDefinitionsAsync(tools, ct))
            : new TokenAttributor();
        return new RunOrchestrator(
            model, system, environments, StoreFor(args, dry), prompts, limits, await HarnessInfo.VersionAsync(), attributor);
    }

    private static IModelClient RealModel(CliArgs args)
    {
        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("Set the ANTHROPIC_API_KEY environment variable first.");
        }

        return new AnthropicHttpClient(key, args.Get("model", "claude-haiku-5-5"));
    }

    /// <summary>A model that only exercises the container and the tools: it changes nothing and costs nothing.</summary>
    private static IModelClient DryModel() => new ScriptedModelClient(new ModelResponse[]
    {
        new(new ContentBlock[]
            {
                new ToolUseBlock("d1", ToolNames.RunCommand,
                    new JsonObject { ["command"] = "git log -1 --oneline && python --version" }),
            },
            "tool_use", new Usage(1200, 60, 0, 0)),
        new(new ContentBlock[]
            {
                new ToolUseBlock("d2", ToolNames.ListDir, new JsonObject { ["path"] = "." }),
            },
            "tool_use", new Usage(300, 30, 1200, 0)),
        new(new ContentBlock[] { new TextBlock("Dry run: the tools work, nothing was changed.") },
            "end_turn", new Usage(200, 20, 1500, 0)),
    }, "scripted-dry");

    private static string Describe(RunManifest m) => string.Create(CultureInfo.InvariantCulture,
        $"{m.Status} stop={m.StopReason} turns={m.Turns} tokens={m.InputTokens + m.OutputTokens + m.CacheReadTokens + m.CacheWriteTokens} " +
        $"cost=${m.CostUsd:F4} patch={m.PatchChars} chars time={m.DurationMs / 1000.0:F0}s");

    private static string Head(string text, int lines) =>
        string.Join('\n', text.Split('\n').Take(lines));

    private static string Indent(string text) =>
        string.Join('\n', text.TrimEnd().Split('\n').Select(line => "  " + line));
}

internal static class HarnessInfo
{
    /// <summary>Short git commit of the harness (with "-dirty" for uncommitted changes), stored in every manifest.</summary>
    public static async Task<string> VersionAsync()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("MAMCP_GIT_SHA");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        try
        {
            var sha = await ProcessRunner.RunAsync(
                "git", new[] { "rev-parse", "--short", "HEAD" }, null, null, TimeSpan.FromSeconds(10), CancellationToken.None);
            if (!sha.Ok)
            {
                return "unknown";
            }

            var status = await ProcessRunner.RunAsync(
                "git", new[] { "status", "--porcelain", "--untracked-files=no" }, null, null,
                TimeSpan.FromSeconds(10), CancellationToken.None);
            return sha.StdOut.Trim() + (status.Ok && status.StdOut.Trim().Length > 0 ? "-dirty" : string.Empty);
        }
        catch (Exception)
        {
            return "unknown"; // git is not installed
        }
    }
}
