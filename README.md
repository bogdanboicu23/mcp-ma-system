# MaMcp: multi-agent + MCP experiment harness

Experimental system for the practice report *"Research on the efficiency of multi-agent architectures based on the
Model Context Protocol in automating software engineering tasks"* (UTM, TI-251M).

It compares a single agent with several multi-agent topologies on the same model (Claude Haiku 5.5), the same
tasks and the same budget, and measures how much of the cost belongs to the MCP layer. The full plan is in
[`docs/plan.md`](docs/plan.md); design decisions are logged in [`docs/decisions.md`](docs/decisions.md).

## Layout

| Path | Purpose |
|---|---|
| `src/Agents.Core` | Agent loop, context policy, budget/pricing, call log, model clients, tools, SWE-bench tasks and prompts (`Swe/`, `Prompts/`), run pipeline (`Runs/`) |
| `src/Agents.Topologies` | Hub-and-spoke, coder-reviewer, sequential (empty for now) |
| `src/Mcp.Server` | Container-bound MCP server (placeholder for now) |
| `src/Runner` | CLI: `select`, `check`, `run`, `batch`, `eval`, `report` and the smoke tests |
| `tests/Agents.Core.Tests` | xunit tests (offline: scripted model + local git repo) |
| `tasks/`, `tools/`, `results/` | Task id lists, `tools/fetch_tasks.py`, experiment output |

## Run it

```bash
dotnet restore
dotnet test
dotnet run --project src/Runner -- dry     # scripted model, no API key, no cost
export ANTHROPIC_API_KEY=...               # PowerShell: $env:ANTHROPIC_API_KEY = "..."
dotnet run --project src/Runner -- live    # real call to claude-haiku-5-5, well under $0.01
```

### Config A on one real task (on the Windows PC: Docker Desktop + WSL2, Python 3.10+)

```bash
pip install datasets swebench
python tools/fetch_tasks.py                                   # tasks/swebench_verified.jsonl (500 tasks)
dotnet run --project src/Runner -- select --n 10 --seed 42     # tasks/pilot_ids.txt
dotnet run --project src/Runner -- check --task <instance_id>  # pulls the image, verifies git state, python, tools
dotnet run --project src/Runner -- run --dry --task <id>       # container + tools with a scripted model: costs nothing
dotnet run --project src/Runner -- run --task <id> --config A  # the real run (limits: see --help)
dotnet run --project src/Runner -- eval --config A --repeat 1  # official evaluator -> verdicts
dotnet run --project src/Runner -- report                      # results/report.csv + totals per config
```

`batch --config A --ids-file tasks/pilot_ids.txt --repeats 1 --max-usd 5` runs many tasks. It skips runs that are
already finished and stops when the session's spend reaches `--max-usd`, so it is safe to re-run after an interruption.
Every run writes `results/runs/<runId>/` (`manifest.json`, `calls.jsonl`, `patch.diff`, later `verdict.json`).

## Status

Done: agent loop, context policy (80k cap, same for all systems), run budget with the Haiku 5.5 100k pricing tier,
JSONL call log, `HttpClient` Messages API client, scripted model, the shared tool catalog (6 tools), the direct tool
host (`ContainerToolHost`), local and Docker executors, throw-away Docker containers (no network, limits), patch
extraction. Workstream 1 code: task loader and sampler, SWE-bench image naming, shared prompts, environment checks,
`ISystem` + `SingleAgentSystem` (config A), `RunOrchestrator`, run store, predictions + evaluator wrapper, report, CLI.
34 unit tests (offline).

Not done yet: everything that needs the PC to verify (see caveats), MCP server and MCP-backed tool host (config B),
token attribution by source (RQ2), topologies (configs C-E), task labeling for RQ3.

Caveats:
- Nothing has run against a real Docker daemon, a real SWE-bench image, the real evaluator or the live API yet. The
  pipeline is tested offline with a scripted model and a local git repo; the first PC run is the real test.
- Unconfirmed assumptions to check on the PC: the conda activation line (`--prelude`), the evaluator's output file
  layout (both known layouts are parsed), and the `predictions.jsonl` field names.
- `RunLimits` defaults (3M tokens, $0.50, 50 turns) are placeholders until the pilot measures real costs.
- `DockerExecutor` / `DockerContainer` are tested only for the arguments they build. Tool behaviour is tested against
  a local folder with bash (macOS/Linux; on Windows run the tests inside WSL).
- `DockerExecutor` / `DockerContainer` need a Docker daemon and have not been run yet; they are tested only for the
  arguments they build. Tool behaviour itself is tested against a local folder with bash (macOS/Linux; on Windows run
  the tests inside WSL).
- Pricing in `Pricing.cs` comes from secondary reporting. Verify it on Anthropic's pricing page.
- `AnthropicHttpClient` follows the documented API but has not been run against the live API yet. Run `live` once.
- The context-size estimate is a character heuristic. Compare `estimatedPromptTokens` with real usage in the pilot
  and recalibrate before trusting the 80k cap.
