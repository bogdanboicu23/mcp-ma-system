# MaMcp: multi-agent + MCP experiment harness

Experimental system for the practice report *"Research on the efficiency of multi-agent architectures based on the
Model Context Protocol in automating software engineering tasks"* (UTM, TI-251M).

It compares a single agent with several multi-agent topologies on the same model (Claude Haiku 5.5), the same
tasks and the same budget, and measures how much of the cost belongs to the MCP layer. The full plan is in
[`docs/plan.md`](docs/plan.md); design decisions are logged in [`docs/decisions.md`](docs/decisions.md).

## Layout

| Path | Purpose |
|---|---|
| `src/Agents.Core` | The one agent loop, context policy, budget/pricing, call log, model clients |
| `src/Agents.Topologies` | Hub-and-spoke, coder-reviewer, sequential (empty for now) |
| `src/Mcp.Server` | Container-bound MCP server (placeholder for now) |
| `src/Runner` | CLI: smoke test now; task loader and evaluator hand-off next |
| `tests/Agents.Core.Tests` | xunit tests for the core |
| `tasks/`, `tools/`, `results/` | Task subset and labels, helper scripts, experiment output |

## Run it

```bash
dotnet restore
dotnet test
dotnet run --project src/Runner -- dry     # scripted model, no API key, no cost
export ANTHROPIC_API_KEY=...               # PowerShell: $env:ANTHROPIC_API_KEY = "..."
dotnet run --project src/Runner -- live    # real call to claude-haiku-5-5, well under $0.01
```

## Status

Done: agent loop, context policy (80k cap, same for all systems), run budget with the Haiku 5.5 100k pricing tier,
JSONL call log, `HttpClient` Messages API client, scripted model for dry runs, 9 unit tests.

Not done yet: MCP server and MCP-backed tool host, direct tool host for config A, Docker task containers,
SWE-bench loader and evaluator hand-off, topologies, token attribution by source (RQ2).

Caveats:
- Pricing in `Pricing.cs` comes from secondary reporting. Verify it on Anthropic's pricing page.
- `AnthropicHttpClient` follows the documented API but has not been run against the live API yet. Run `live` once.
- The context-size estimate is a character heuristic. Compare `estimatedPromptTokens` with real usage in the pilot
  and recalibrate before trusting the 80k cap.
