using Agents.Core;
using Agents.Core.Execution;
using Agents.Core.Runs;
using Agents.Core.Swe;
using Agents.Core.Tools;

namespace Agents.Topologies;

public sealed record ReviewOptions(
    int MaxRounds = 3,
    int AgentMaxTurns = 50,
    int MaxTokensPerCall = 4096,
    int MaxDiffChars = 20_000,
    int MaxFeedbackChars = 3_000);

/// <summary>
/// Config D: a coder (all six tools) and a reviewer (read_file, list_dir, run_tests) alternate. After the coder
/// stops, the reviewer gets the issue and the current diff in a FRESH context and ends with VERDICT: APPROVE or
/// VERDICT: CHANGES. On CHANGES the coder is CONTINUED with its own history plus the feedback. At most
/// <see cref="ReviewOptions.MaxRounds"/> review rounds; the last revision is not reviewed again.
/// </summary>
public sealed class CoderReviewerSystem : ISystem
{
    private readonly PromptSet _prompts;
    private readonly Func<ICommandExecutor, IReadOnlySet<string>?, IToolHost> _tools;
    private readonly ReviewOptions _options;

    public CoderReviewerSystem(
        string config, PromptSet prompts, Func<ICommandExecutor, IReadOnlySet<string>?, IToolHost> tools, ReviewOptions options)
    {
        if (options.MaxRounds is < 1 or > 6)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxRounds must be between 1 and 6.");
        }

        Config = config;
        _prompts = prompts;
        _tools = tools;
        _options = options;
    }

    public string Config { get; }

    public string Variant => $"rounds={_options.MaxRounds}";

    public async Task<SolveOutcome> SolveAsync(
        string taskPrompt, ICommandExecutor executor, RunContext run, CancellationToken ct = default)
    {
        var coderSpec = new AgentSpec(
            "coder", "coder", _prompts.Compose("coder"), _tools(executor, null),
            _options.AgentMaxTurns, _options.MaxTokensPerCall);

        var coder = await AgentLoop.RunAsync(coderSpec, taskPrompt, run, ct).ConfigureAwait(false);
        var turns = coder.Turns;
        var stop = coder.StopReason;

        for (var round = 1; round <= _options.MaxRounds && !TopologyKit.IsTerminal(stop); round++)
        {
            var patch = await PatchExtractor.GetPatchAsync(executor, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(patch))
            {
                break; // nothing to review
            }

            var reviewerSpec = new AgentSpec(
                $"reviewer-{round}", "reviewer", _prompts.Compose("reviewer"),
                _tools(executor, TopologyKit.ReviewerTools), _options.AgentMaxTurns, _options.MaxTokensPerCall);
            var review = await AgentLoop.RunAsync(reviewerSpec, ReviewTask(taskPrompt, patch), run, ct).ConfigureAwait(false);
            turns += review.Turns;
            if (TopologyKit.IsTerminal(review.StopReason))
            {
                stop = review.StopReason;
                break;
            }

            if (TopologyKit.Verdict(review.FinalText) == "APPROVE")
            {
                break;
            }

            coder = await AgentLoop.ContinueAsync(
                coderSpec, coder.History, Feedback(review.FinalText), run, ct).ConfigureAwait(false);
            turns += coder.Turns;
            stop = coder.StopReason;
        }

        return new SolveOutcome(stop, turns);
    }

    private string ReviewTask(string taskPrompt, string patch) =>
        "## Issue\n" + taskPrompt.Trim() + "\n\n## The coder's current diff\n```diff\n" +
        TopologyKit.Truncate(patch, _options.MaxDiffChars, "[diff truncated]") + "\n```";

    private string Feedback(string reviewText) =>
        "A reviewer examined your change and wrote:\n\n" +
        TopologyKit.Truncate(reviewText, _options.MaxFeedbackChars) +
        "\n\nAddress the comments, check your work with the relevant tests, then stop with a short summary.";
}
