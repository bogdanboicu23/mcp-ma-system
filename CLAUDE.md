# CLAUDE.md: context for working on this repo

## What this is
Experimental harness for Bogdan Boicu's practice report (UTM, group TI-251M, coordinator Cojocaru Sergiu):
*"Research on the efficiency of multi-agent architectures based on the Model Context Protocol in automating
software engineering tasks."* The report text is in Romanian; code, docs and commit messages are in English.
Chapter 1 (literature review) is finished. Still to write: sections 2.1, 3.1, 3.2, chapter 4 and the conclusions.

**Deadline: 20 Nov 2026** for the implementation AND the experiment results. **Code freeze: 8 Nov 2026.**

## Research questions and hypotheses
- RQ1: multi-agent + MCP vs. single agent, same model, same budget, same tasks: resolve rate and cost.
- RQ2: what share of cost is the MCP layer, and how does it grow with agent count?
- RQ3: does the effect depend on task structure (parallelizable vs. sequential)?
- H1 efficiency depends on task structure, not just agent count. H2 MCP cost grows with agent count because each
  agent loads its own tool definitions (the author's own hypothesis). H3 the multi-agent advantage shrinks as the
  single-agent baseline gets stronger.

## Read these first
- `docs/plan.md`: approved plan (architecture, configurations A-E, budget, schedule, verification).
- `docs/decisions.md`: every decision and why. Add a row whenever you make a fairness-relevant choice.
- `README.md`: layout, how to run, status, caveats.

## Hard rules (they protect the validity of the comparison)
- One agent loop (`AgentLoop`) for every configuration. Topologies only change prompts, tool sets and wiring.
- Same model (`claude-haiku-5-5`), same per-run budget (`RunBudget`, shared by all agents of a run), same context
  policy (80k prompt-token cap, identical compaction) for every system.
- The direct tool host (config A) and the future MCP server must advertise the SAME tools from `ToolCatalog`
  and behave identically. Only the transport differs.
- Orchestration stays custom, no agent framework (hidden prompts would contaminate cost attribution).
- MCP only on the agent-to-tools axis. Trusted server only, inside a Docker container with no network.
- Never commit API keys. Raw `results/*.jsonl` stays out of git.

## Environment
- C# / .NET 8. Author is not fluent in Python: keep Python to the `swebench` evaluator CLI and one dataset script.
- Develops on a MacBook Pro M4 (Rider); runs experiments on a Windows PC (32 GB RAM, 1 TB SSD, Docker Desktop + WSL2).
  SWE-bench images are x86, so experiments run on the PC, not the Mac.
- Budget about $100 on Haiku 5.5 (ceiling $150-200). Haiku 5.5 bills a whole request at 5x when its prompt exceeds
  100k tokens, which is why the context cap exists.
- Build/test: `dotnet build`, `dotnet test`. The tool tests need bash (macOS/Linux; on Windows use WSL).
- Smoke runs: `dotnet run --project src/Runner -- dry` (free) and `-- live` (needs `ANTHROPIC_API_KEY`, under $0.01).

## Status
Done: agent loop, context policy, budget/pricing, JSONL call log, `AnthropicHttpClient` (plain HttpClient),
`ScriptedModelClient`, `ToolCatalog` (6 tools), `ContainerToolHost`, local and Docker executors, `DockerContainer`,
`PatchExtractor`. Workstream 1 code (written 27 Oct, tested offline only): `SweBenchTask`/`TaskLoader`/`TaskSelector`,
`SweBenchImage`, embedded prompts (`PromptSet`), `EnvironmentVerifier`, `ISystem`/`SingleAgentSystem` (config A),
`RunOrchestrator`, `RunStore`, `Predictions` + `EvalResultsParser`, `ReportBuilder`, Runner commands
`select|check|run|batch|eval|report`, `tools/fetch_tasks.py`. Config C hub-and-spoke (27 Oct, offline only): `HubAndSpokeSystem`
(`--workers N`, `--exposure full|role`), `DelegationToolHost`, `WriteScopeHost`. 43 unit tests (offline).

Added 1 Nov (offline only, 72 tests): `CoderReviewerSystem` (D), `SequentialSystem` (E), token attribution (`Attribution.cs`),
conversation caching, `TaskLabeler` + `TaskSelector.SelectStratified`, synthetic suite (`tasks/synthetic`, `--synthetic`,
`SyntheticEnvironmentProvider`, `SyntheticEvaluator`, `tools/synthetic/Dockerfile`), MCP server (`src/Mcp.Server`), MCP client host
(`src/Agents.Mcp`: `McpToolHost`, `McpToolHostFactory`), config B, `--transport direct|mcp`, `mcp-check`, `ToolHostTracker`.
The MCP projects were NOT compiled (no NuGet in the sandbox): build them first on a dev machine.

Next, in order (details: `docs/implementation-plan.md`):
1. On the Mac: `dotnet build`, fix any MCP SDK mismatch (pin in `src/Mcp.Server` and `src/Agents.Mcp`), `dotnet test`, `mcp-check`.
2. On the Windows PC, once API credits exist: VERIFY workstream 1 (fetch tasks, `check`, `run --dry`, evaluator on a gold patch,
   one real config A run, `eval`, `report`; build `mamcp-synth` for the synthetic suite). Fix what the real environment breaks.
3. Pilot on about 10 tasks: real cost per run, saturation check, calibrate the character-based token estimate, check that
   attribution reconciles with API usage, choose theta and `RunLimits`.
4. Main runs (configs A-E, transport controls) by 8 Nov code freeze; results by 20 Nov.
5. Not built: per-call MCP latency (process start + round trip) in the log.

## Not yet verified (do not assume these work)
- Haiku 5.5 prices in `Pricing.cs` come from secondary reporting; check the official pricing page.
- `AnthropicHttpClient` has never been run against the live API.
- `DockerExecutor` / `DockerContainer` have never run against a Docker daemon.
- SWE-bench image naming (expected form `swebench/sweb.eval.x86_64.<owner>_1776_<repo>-<id>:latest`) and the conda
  activation prelude for the containers are unconfirmed; check on the PC.
- The evaluator's output file layout and the predictions field names (`instance_id`, `model_name_or_path`, `model_patch`).
- `RunLimits` defaults (3M tokens, $0.50, 50 turns) are placeholders until the pilot.
- The author's `dotnet test` and `live` smoke-test results were still pending as of 27 Oct.
- Nothing from D, E, attribution, labeler, synthetic suite or MCP has run against Docker or the live API; MCP code was never compiled.
