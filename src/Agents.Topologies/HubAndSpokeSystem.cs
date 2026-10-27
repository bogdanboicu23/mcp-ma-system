using Agents.Core;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json.Nodes;
using Agents.Core.Execution;
using Agents.Core.Runs;
using Agents.Core.Swe;
using Agents.Core.Tools;

namespace Agents.Topologies;

/// <summary>Which tools each agent of a topology can see (the tool-exposure ablation).</summary>
public enum ToolExposure
{
    /// <summary>Every agent gets all six catalog tools (the orchestrator also gets delegate_task).</summary>
    Full,

    /// <summary>
    /// Role-restricted: the orchestrator gets read_file, list_dir, run_tests (+ delegate_task); workers get
    /// read_file, list_dir, edit_file, write_file, run_command. Fewer definitions per agent, same behaviour of each tool.
    /// </summary>
    Role,
}

public sealed record HubOptions(
    int Workers = 3,
    ToolExposure Exposure = ToolExposure.Full,
    int AgentMaxTurns = 50,
    int MaxTokensPerCall = 4096,
    int MaxSummaryChars = 2000);

/// <summary>
/// Config C: an orchestrator agent that splits the issue and calls <c>delegate_task</c>; each call starts up to N
/// worker agents in parallel (<see cref="Task.WhenAll(IEnumerable{Task})"/>). Every agent is a plain
/// <see cref="AgentLoop"/> run with its own context. All of them share the container, the run budget and the call log.
/// Only a short worker summary goes back to the orchestrator, which checks the result itself.
/// </summary>
public sealed class HubAndSpokeSystem : ISystem
{
    private static readonly IReadOnlySet<string> OrchestratorRole =
        new HashSet<string> { ToolNames.ReadFile, ToolNames.ListDir, ToolNames.RunTests };

    private static readonly IReadOnlySet<string> WorkerRole =
        new HashSet<string> { ToolNames.ReadFile, ToolNames.ListDir, ToolNames.EditFile, ToolNames.WriteFile, ToolNames.RunCommand };

    private readonly PromptSet _prompts;
    private readonly Func<ICommandExecutor, IReadOnlySet<string>?, IToolHost> _tools;
    private readonly HubOptions _options;

    /// <param name="tools">Builds a tool host for an executor, optionally limited to some tools (direct now, MCP later).</param>
    public HubAndSpokeSystem(
        string config, PromptSet prompts, Func<ICommandExecutor, IReadOnlySet<string>?, IToolHost> tools, HubOptions options)
    {
        if (options.Workers is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Workers must be between 1 and 8.");
        }

        Config = config;
        _prompts = prompts;
        _tools = tools;
        _options = options;
    }

    public string Config { get; }

    public string Variant => $"workers={_options.Workers};exposure={_options.Exposure.ToString().ToLowerInvariant()}";

    public async Task<SolveOutcome> SolveAsync(
        string taskPrompt, ICommandExecutor executor, RunContext run, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var hub = new HubRun(this, taskPrompt, executor, run, cts);

        var inner = _tools(executor, _options.Exposure == ToolExposure.Role ? OrchestratorRole : null);
        var host = new DelegationToolHost(inner, _options.Workers, hub.DelegateAsync);
        var spec = new AgentSpec(
            "orchestrator", "orchestrator", Compose(_prompts.HubOrchestratorAddendum), host,
            _options.AgentMaxTurns, _options.MaxTokensPerCall);

        AgentResult result;
        try
        {
            result = await AgentLoop.RunAsync(spec, taskPrompt, run, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (hub.Failure is not null && !ct.IsCancellationRequested)
        {
            // A worker hit an infrastructure error and cancelled the orchestrator: report that error, not the cancel.
            ExceptionDispatchInfo.Capture(hub.Failure).Throw();
            throw;
        }

        if (hub.Failure is not null)
        {
            ExceptionDispatchInfo.Capture(hub.Failure).Throw();
        }

        return new SolveOutcome(result.StopReason, result.Turns + hub.WorkerTurns);
    }

    private string Compose(string addendum) =>
        addendum.Length == 0 ? _prompts.SystemPrompt : _prompts.SystemPrompt + "\n\n" + addendum;

    private sealed class HubRun
    {
        private readonly HubAndSpokeSystem _system;
        private readonly string _taskPrompt;
        private readonly ICommandExecutor _executor;
        private readonly RunContext _run;
        private readonly CancellationTokenSource _cts;
        private int _workerCounter;
        private int _workerTurns;
        private Exception? _failure;

        public HubRun(
            HubAndSpokeSystem system, string taskPrompt, ICommandExecutor executor, RunContext run, CancellationTokenSource cts)
        {
            _system = system;
            _taskPrompt = taskPrompt;
            _executor = executor;
            _run = run;
            _cts = cts;
        }

        public Exception? Failure => Volatile.Read(ref _failure);

        public int WorkerTurns => Volatile.Read(ref _workerTurns);

        public async Task<ToolOutput> DelegateAsync(JsonObject input, CancellationToken ct)
        {
            if (input["tasks"] is not JsonArray array || array.Count == 0)
            {
                return new ToolOutput("'tasks' must be a non-empty array.", true);
            }

            var max = _system._options.Workers;
            if (array.Count > max)
            {
                return new ToolOutput($"At most {max} tasks per call (got {array.Count}). Merge tasks or call again later.", true);
            }

            var parsed = new List<(string Instructions, List<string> Files)>();
            foreach (var item in array)
            {
                var instructions = item?["instructions"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(instructions))
                {
                    return new ToolOutput("Every task needs non-empty 'instructions'.", true);
                }

                var files = item?["files"] is JsonArray f
                    ? f.Select(x => x?.GetValue<string>() ?? string.Empty).Where(x => x.Length > 0).ToList()
                    : new List<string>();
                parsed.Add((instructions, files));
            }

            for (var i = 0; i < parsed.Count; i++)
            {
                for (var j = i + 1; j < parsed.Count; j++)
                {
                    if (WriteScopeHost.Overlap(parsed[i].Files, parsed[j].Files))
                    {
                        return new ToolOutput(
                            $"Tasks {i + 1} and {j + 1} have overlapping files. Give every worker its own files, or merge the tasks.", true);
                    }
                }
            }

            var reports = await Task.WhenAll(parsed.Select(p => RunWorkerAsync(p.Instructions, p.Files, ct))).ConfigureAwait(false);

            var text = new StringBuilder();
            for (var i = 0; i < reports.Length; i++)
            {
                text.Append("Task ").Append(i + 1).Append(" (").Append(reports[i].Id).Append(", ")
                    .Append(reports[i].StopReason).Append(", ").Append(reports[i].Turns).Append(" turns):\n")
                    .Append(reports[i].Summary).Append("\n\n");
            }

            return new ToolOutput(text.ToString().TrimEnd(), Failure is not null);
        }

        private async Task<(string Id, string StopReason, int Turns, string Summary)> RunWorkerAsync(
            string instructions, List<string> files, CancellationToken ct)
        {
            var id = "worker-" + Interlocked.Increment(ref _workerCounter);
            try
            {
                var inner = _system._tools(
                    _executor, _system._options.Exposure == ToolExposure.Role ? WorkerRole : null);
                var host = new WriteScopeHost(inner, files);
                var spec = new AgentSpec(
                    id, "worker", _system.Compose(_system._prompts.HubWorkerAddendum), host,
                    _system._options.AgentMaxTurns, _system._options.MaxTokensPerCall);

                var result = await AgentLoop.RunAsync(spec, WorkerTask(instructions, files), _run, ct).ConfigureAwait(false);
                Interlocked.Add(ref _workerTurns, result.Turns);
                return (id, result.StopReason, result.Turns, Trim(result.FinalText));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested || Failure is not null)
            {
                return (id, "cancelled", 0, "(cancelled because another worker failed)");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Infrastructure error (API down, container gone): remember it, stop everyone, let SolveAsync rethrow it.
                Interlocked.CompareExchange(ref _failure, ex, null);
                _cts.Cancel();
                return (id, "error", 0, "(worker failed: " + ex.Message + ")");
            }
        }

        private string WorkerTask(string instructions, List<string> files)
        {
            var sb = new StringBuilder();
            sb.Append("## Overall issue\n").Append(_taskPrompt.Trim()).Append("\n\n## Your sub-task\n").Append(instructions.Trim()).Append("\n\n");
            if (files.Count > 0)
            {
                sb.Append("## Files you may modify\n");
                foreach (var f in files)
                {
                    sb.Append("- ").Append(f).Append('\n');
                }
            }
            else
            {
                sb.Append("No file restriction was given; keep your changes minimal.\n");
            }

            return sb.ToString();
        }

        private string Trim(string text)
        {
            var t = text.Trim();
            var max = _system._options.MaxSummaryChars;
            return t.Length <= max ? t : t[..max] + "\n[summary truncated]";
        }
    }
}
