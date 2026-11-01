using Agents.Core;
using Agents.Core.Execution;
using Agents.Core.Runs;
using Agents.Core.Swe;
using Agents.Core.Tools;

namespace Agents.Topologies;

public sealed record SequentialOptions(
    int AgentMaxTurns = 50,
    int MaxTokensPerCall = 4096,
    int MaxHandoffChars = 4_000,
    int MaxDiffChars = 20_000);

/// <summary>
/// Config E: planner (read_file, list_dir, run_command; it does not edit) -> coder (all six tools) -> tester
/// (read_file, list_dir, run_tests). Each hand-off is one structured text: the plan, then the coder's summary and
/// the diff. If the tester ends with VERDICT: FAIL the coder is continued ONCE with the tester's report.
/// </summary>
public sealed class SequentialSystem : ISystem
{
    private readonly PromptSet _prompts;
    private readonly Func<ICommandExecutor, IReadOnlySet<string>?, IToolHost> _tools;
    private readonly SequentialOptions _options;

    public SequentialSystem(
        string config, PromptSet prompts, Func<ICommandExecutor, IReadOnlySet<string>?, IToolHost> tools, SequentialOptions options)
    {
        Config = config;
        _prompts = prompts;
        _tools = tools;
        _options = options;
    }

    public string Config { get; }

    public string Variant => "stages=planner,coder,tester;fix_passes=1";

    public async Task<SolveOutcome> SolveAsync(
        string taskPrompt, ICommandExecutor executor, RunContext run, CancellationToken ct = default)
    {
        var plannerSpec = Spec("planner", "planner", TopologyKit.PlannerTools, executor);
        var planner = await AgentLoop.RunAsync(plannerSpec, taskPrompt, run, ct).ConfigureAwait(false);
        var turns = planner.Turns;
        if (TopologyKit.IsTerminal(planner.StopReason))
        {
            return new SolveOutcome(planner.StopReason, turns);
        }

        var plan = TopologyKit.Truncate(planner.FinalText, _options.MaxHandoffChars, "[plan truncated]");
        var coderSpec = Spec("coder", "seqcoder", null, executor);
        var coder = await AgentLoop.RunAsync(
            coderSpec, taskPrompt.Trim() + "\n\n## Plan from the planner\n" + plan, run, ct).ConfigureAwait(false);
        turns += coder.Turns;
        var stop = coder.StopReason;
        if (TopologyKit.IsTerminal(stop))
        {
            return new SolveOutcome(stop, turns);
        }

        var patch = await PatchExtractor.GetPatchAsync(executor, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(patch))
        {
            return new SolveOutcome(stop, turns); // nothing to test
        }

        var testerSpec = Spec("tester", "tester", TopologyKit.ReviewerTools, executor);
        var tester = await AgentLoop.RunAsync(testerSpec, TesterTask(taskPrompt, coder.FinalText, patch), run, ct).ConfigureAwait(false);
        turns += tester.Turns;
        if (TopologyKit.IsTerminal(tester.StopReason))
        {
            return new SolveOutcome(tester.StopReason, turns);
        }

        if (TopologyKit.Verdict(tester.FinalText) == "FAIL")
        {
            var fix = await AgentLoop.ContinueAsync(
                coderSpec, coder.History,
                "A tester checked your change and reported:\n\n" +
                TopologyKit.Truncate(tester.FinalText, _options.MaxHandoffChars) +
                "\n\nFix the problem, check it with the relevant tests, then stop with a short summary. This is the last fix pass.",
                run, ct).ConfigureAwait(false);
            turns += fix.Turns;
            stop = fix.StopReason;
        }

        return new SolveOutcome(stop, turns);
    }

    private AgentSpec Spec(string agentId, string promptRole, IReadOnlySet<string>? allowed, ICommandExecutor executor) =>
        new(agentId, agentId, _prompts.Compose(promptRole), _tools(executor, allowed),
            _options.AgentMaxTurns, _options.MaxTokensPerCall);

    private string TesterTask(string taskPrompt, string coderSummary, string patch) =>
        "## Issue\n" + taskPrompt.Trim() +
        "\n\n## The coder's summary\n" + TopologyKit.Truncate(coderSummary, _options.MaxHandoffChars) +
        "\n\n## The current diff\n```diff\n" + TopologyKit.Truncate(patch, _options.MaxDiffChars, "[diff truncated]") + "\n```";
}
