# Plan: Experimental multi-agent + MCP system (C#) for the practice report

## Context
Chapter 1 of the report (TI-251M, Boicu Bogdan) is finished. Chapters 2-4 are placeholders that depend on a working
prototype. Deadline for the implementation **and** experiment results: **20 Nov 2026**. The system must test:
- **RQ1** multi-agent + MCP vs. single agent (same model, same budget, same tasks): resolve rate and cost.
- **RQ2** share of cost taken by the MCP layer, and how it grows with agent count (H2, the author's own hypothesis).
- **RQ3** dependence of the effect on task structure, parallelizable vs. sequential (H1, H3).

Fixed decisions: C# (.NET 8+), Claude Haiku 5.5 (`claude-haiku-5-5`), ~$100 budget (flexible to $150-200),
custom orchestration (no framework), MCP only on the agent-to-tools axis, trusted server + Docker sandbox,
GitHub repo (Mac for development, Windows PC 32 GB / 1 TB for experiments).

## Facts checked (verify pricing on the official page before relying on it)
- Haiku 5.5: $0.10 / $0.50 per M input / output tokens, cache read $0.01. **A request whose prompt exceeds 100k tokens is
  billed at 5x for every line.** Tokenizer yields ~30% more tokens than Haiku 4.5. (secondary source)
- SWE-bench Verified optimized images ~30 GiB total, 500/500 build on x86; ARM is best-effort, so run on the Windows PC.
- NuGet `Anthropic` is the official C# SDK (12.x, net8+). `IChatClient` support not confirmed, so call the Messages API
  directly to get exact usage (incl. cache tokens).
- NuGet `ModelContextProtocol` (+ `.Core`): `StdioClientTransport`, `McpClient.CreateAsync`, server over stdio. Preview, so pin the version.

## Architecture
```
Runner ──► Agent loop (identical in every config) ──► Anthropic Messages API
              │  per-agent McpClient (stdio)
              ▼
        MCP server (C#, on host) ──docker exec──► task container (SWE-bench image)
              │
        Instrumentation (JSONL per model call)      predictions.jsonl ──► swebench evaluator (Python CLI) ──► resolved/failed
```
Components:
1. **AgentLoop** (~150 lines): send messages, handle tool_use, execute via MCP, repeat. Identical across configs; only
   prompts, tool set and topology change.
2. **Context policy** (same for all systems): hard cap of ~80k prompt tokens per request with identical compaction, so no
   system crosses the 100k pricing cliff. Per-task total token cap, identical for all systems; hitting it = unresolved.
3. **MCP server** (C#): `read_file`, `write_file`, `edit_file`, `list_dir`, `run_command`, `run_tests`; all executed inside
   the task container. Config A uses the same operations as in-process direct tools (no MCP) to isolate MCP overhead.
4. **Topologies**
   - A: single agent, direct tools (baseline)
   - B: single agent + MCP (isolates MCP overhead)
   - C: hub-and-spoke (orchestrator `delegate_task` -> N parallel workers via `Task.WhenAll`), N = 2, 3, 4 for the H2 scaling test
   - D: coder-reviewer loop (max K rounds, reviewer can run tests)
   - E: sequential planner -> coder -> tester (structured hand-offs). Build last; drop if behind schedule.
5. **Tool exposure ablation** on C: full toolset for every agent vs. role-restricted subsets.
6. **Instrumentation**: one JSONL record per model call: run, config, agent, role, turn, input / cache-read / cache-write / output
   tokens, latency, cost. Cost split into agent text vs. MCP layer (tool definitions, call arguments, results); the split is
   estimated per component with the token-counting endpoint and reconciled against the API totals.
7. **Task labeling** (RQ3): (a) classify SWE-bench tasks from the gold patch (files touched, independent hunks);
   (b) a small hand-written synthetic suite (~10-15 tasks, clearly parallel or sequential) as a sanity check.
8. **Evaluation**: C# writes `predictions.jsonl`; official SWE-bench evaluator (the only Python, run as a CLI on the PC)
   reports resolved/failed. Quality metrics (linter, cyclomatic complexity, small manual sample) run on the patches.

## Repo layout (new GitHub repo)
```
src/Agents.Core        AgentLoop, context policy, Anthropic client wrapper, instrumentation
src/Agents.Topologies  Single, HubAndSpoke, CoderReviewer, Sequential
src/Mcp.Server         container-bound MCP server
src/Runner             task loader, container lifecycle, predictions writer, CLI
tools/                 dataset download + evaluator wrapper scripts, task classifier
tasks/                 selected subset, labels, synthetic suite
results/               raw JSONL, aggregated tables
docs/                  decisions log, notes for report sections 2.1, 3.1, 3.2
```

## Budget
Runs = configs x tasks x repeats; cost per run is set by the pilot, not guessed. Rough expectation $0.10-0.50 per run
(multi-agent higher). Hard per-run cost cap enforced in code; ~$10 pilot, ~$70 main, ~$20 reserve. Expand the subset only
if pilot cost allows (staying near $100 is the goal, $150-200 the ceiling).

## Schedule
| Dates | Goal |
|---|---|
| Oct 19-25 | Repo, .NET + Docker on the PC, one SWE-bench task end-to-end, config A working |
| Oct 26-Nov 1 | MCP server + config B, instrumentation, pilot on ~10 tasks (real cost, difficulty/saturation check), task classifier |
| Nov 2-8 | Topologies C, D (then E), tool-exposure ablation. **Code freeze Nov 8** |
| Nov 9-15 | Main runs on the PC (unattended), failure-case collection |
| Nov 16-20 | Analysis; write 2.1, 3.1, 3.2, chapter 4, conclusions (draft 3.1/3.2 earlier, during build weeks) |

## Risks
- SWE-bench Verified may be saturated for Haiku 5.5 (the pilot decides: harder subset or adjusted difficulty).
- The 100k cliff and tokenizer inflation raise costs; mitigated by the shared context cap and the per-run cost cap.
- MCP C# SDK is in preview; pin the version and keep the server small.
- Stochasticity: repeated runs on a core subset; report variance.

## Verification
- Unit: agent loop with a mocked API; context policy never emits a prompt over the cap.
- Smoke: one SWE-bench task through config A and config B; both produce a patch and the evaluator returns a verdict.
- Instrumentation: summed per-call tokens equal the API usage totals; MCP-layer share is non-zero for B and grows with N in C.
- Isolation: the MCP server cannot reach outside the container; no network from the task container.
- Fairness: identical model, token cap, context policy and task list across configs (checked from the run manifest).
