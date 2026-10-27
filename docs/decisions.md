# Decisions log

Short record of what was decided and why. Useful later for report sections 2.1 and 3.2.

| # | Date | Decision | Reason |
|---|---|---|---|
| 1 | 2026-10-19 | C# / .NET 8 | Author is more fluent in C#; official MCP and Anthropic SDKs exist for it. Only the SWE-bench evaluator is Python (CLI). |
| 2 | 2026-10-19 | Claude Haiku 5.5, ~$100 budget (ceiling $150-200) | Cheapest current Claude model; $0.10 / $0.50 per M tokens (to verify). |
| 3 | 2026-10-19 | Custom orchestration, no framework | Frameworks add hidden prompts and calls, which would contaminate cost attribution (RQ2) and the fairness of the comparison. |
| 4 | 2026-10-19 | One agent loop for all configs; topologies only change prompts, tools and wiring | Only the architecture may differ between runs. |
| 5 | 2026-10-19 | Shared context cap of ~80k prompt tokens, identical compaction for all systems | Haiku 5.5 bills a request at 5x above 100k prompt tokens; the cap keeps that from becoming a confound. |
| 6 | 2026-10-19 | One budget per run, shared by all agents | A multi-agent system spends from the same pot as a single agent ("same budget" protocol). |
| 7 | 2026-10-19 | Messages API over plain `HttpClient` behind `IModelClient` (not the `Anthropic` NuGet SDK, for now) | Exact control of usage fields incl. cache tokens; no preview dependency; testable offline. The SDK can be swapped in behind the same interface. |
| 8 | 2026-10-19 | Topologies: hub-and-spoke, coder-reviewer, sequential (last, optional) | Chosen by the author; sequential is dropped first if the schedule slips. |
| 9 | 2026-10-19 | Tool-exposure ablation (full vs. role-restricted tools) on hub-and-spoke | Shows how much of the MCP cost is a tool-design effect (H2). |
| 10 | 2026-10-19 | Task structure labeled from SWE-bench gold patches, plus a small synthetic suite | Realistic main result with a cleaner sanity check (RQ3). |
| 11 | 2026-10-19 | Six tools defined once in `ToolCatalog`; the direct host and the future MCP server both use them | Any cost difference between config A and B must come from MCP, not from different tool text or behaviour. |
| 12 | 2026-10-19 | Tool descriptions kept short | They are sent on every request; their size is part of the MCP-layer cost that RQ2 measures. |
| 13 | 2026-10-19 | `edit_file` is done as exact-match replace (must match once), implemented in C# not in the shell | Same match rules everywhere; preserves line endings and missing final newlines; clear errors help the model retry. |
| 14 | 2026-10-19 | Task containers run with `--network none`, memory and CPU limits, removed afterwards | The isolation the report promises for MCP tools; also keeps runs from affecting each other. |
| 15 | 2026-10-19 | Commands run under the container's own `timeout` (exit 124) | Killing `docker exec` locally does not stop the process inside the container; a hung test would distort later timings. |
| 16 | 2026-10-19 | Non-zero exit codes are normal tool output, not tool errors | Failing tests are information for the agent; `IsError` is reserved for bad arguments, missing files and timeouts. |
| 17 | 2026-10-27 | Every configuration implements `ISystem`; the orchestrator is the same for all | Configs C-E (several agent loops) and B (MCP tools) plug in without touching the run pipeline, the logging or the evaluation. |
| 18 | 2026-10-27 | Prompts are embedded markdown files; their hashes (CRLF-normalized) go into every manifest | One prompt text for all configs, and any change is visible afterwards. Hashing ignores line endings because git on Windows may check files out with CRLF. |
| 19 | 2026-10-27 | The agent sees only the issue text. FAIL_TO_PASS tests and the reference patch are never shown | They are hidden by design in SWE-bench; showing them would invalidate the resolve rate. A test checks it. |
| 20 | 2026-10-27 | Each run starts a fresh container and verifies it: repo at the base commit, clean tree, python works | The patch must contain only what the agent wrote; a wrong prelude fails in seconds instead of after paying for a run. |
| 21 | 2026-10-27 | A run is `completed` when the agent stopped for any reason (done, budget, turns, context); `error` only for infrastructure failures, which are retried | Budget and turn exhaustion are results of the experiment (unresolved), whereas a Docker failure is not and must not count against a configuration. |
| 22 | 2026-10-27 | One predictions file per configuration and repeat; evaluator output is read in two known layouts | The evaluator wants one prediction per instance. Its exact output layout is unconfirmed, so both are parsed. |
| 23 | 2026-10-27 | `batch` skips finished runs and stops at `--max-usd` per session; 3 consecutive infrastructure errors abort | Safe to interrupt and resume; a broken setup cannot silently burn the budget. |
| 24 | 2026-10-27 | `run --dry` drives the real container and tools with a scripted model | Lets the PC setup be verified at zero API cost before the first paid run. |
| 25 | 2026-10-27 | Default per-run limits: 80k prompt tokens, 3M total tokens, $0.50, 50 turns (PLACEHOLDERS) | To be replaced by values calibrated in the pilot; whatever is chosen applies identically to every configuration. |
