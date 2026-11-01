# Implementation plan: single agent, multi-agent, MCP

Detailed breakdown of `docs/plan.md` into three workstreams. Each task has an acceptance check, and a note on where
it can be verified: **offline** (unit tests with `ScriptedModelClient` + `LocalExecutor`, runs anywhere) or **PC**
(needs Docker, SWE-bench images, or the real API).

Deadlines: milestone config A on one real task about **25 Oct**, code freeze **8 Nov**, results and report **20 Nov**.

## Recommended order and why

The three workstreams are independent code-wise: every agent only sees an `IToolHost`, so a topology does not care
whether its tools are direct or MCP-backed. Recommended build order: **1 Single agent, then 3 MCP, then 2 Multi-agent.**
- MCP is the biggest technical risk (preview SDK, never compiled in the planning sandbox), so find problems early.
- Config B (single agent + MCP) is the cleanest measurement of MCP overhead and needs only workstreams 1 and 3.
- Multi-agent logic is testable offline and can be wired to MCP last.
- If you prefer to build multi-agent before MCP, that works too (build it on direct tools first, flag `--transport`),
  but it moves the MCP risk into the final week before code freeze.

| Dates | Workstream | Milestone |
|---|---|---|
| 20-25 Oct | 1 Single agent | Config A resolves-or-fails one real SWE-bench task end to end, with logs and an evaluator verdict |
| 26 Oct - 1 Nov | 3 MCP | Config B on the same task; tool definitions identical to A; token attribution; pilot on ~10 tasks |
| 2-8 Nov | 2 Multi-agent | Hub-and-spoke, coder-reviewer, (sequential); ablation flags; **code freeze 8 Nov** |
| 9-15 Nov | Main runs | Batch runs on the PC, failure cases collected |
| 16-20 Nov | Analysis + writing | Tables, figures, sections 2.1, 3.1, 3.2, chapter 4, conclusions |

## Workstream 1: Single agent (config A)

Goal: a reliable runner that takes a SWE-bench task and produces a patch, a log and a verdict. Everything later reuses it.

| # | Task | Acceptance | Verify |
|---|---|---|---|
| 1.1 | `tools/fetch_tasks.py`: one script that downloads SWE-bench Verified (Hugging Face `datasets`) into `tasks/swebench_verified.jsonl` (instance_id, repo, base_commit, problem_statement, FAIL_TO_PASS, PASS_TO_PASS, gold patch). Only used for downloading and labeling. | File has 500 rows with all fields | PC |
| 1.2 | `SweBenchTask` record and `TaskLoader` | Round-trips a sample row | offline |
| 1.3 | `SweBenchImage`: image name `swebench/sweb.eval.x86_64.<owner>_1776_<repo>-<id>:latest` (instance id with `__` replaced by `_1776_`; naming matches Docker Hub listings, confirm with one `docker pull`) and the conda prelude, expected `source /opt/miniconda3/bin/activate && conda activate testbed && ` (**unconfirmed**) | `docker run` of one image: `git status` clean at base commit, `python --version`, test command works through the prelude | PC |
| 1.4 | Evaluator sanity: run the official harness on the **gold** patch for one instance (`python -m swebench.harness.run_evaluation --dataset_name princeton-nlp/SWE-bench_Verified --predictions_path gold --instance_ids <id> --run_id sanity`) | Reports the instance as resolved. Proves Docker + evaluator before any of our code is involved | PC |
| 1.5 | Prompts: one system prompt and one task prompt template, shared by all configs, in `prompts/` (hashed into the run manifest). Never shows FAIL_TO_PASS tests to the agent. | Prompt files exist; hash recorded | offline |
| 1.6 | `RunOrchestrator.RunAsync(task, config)`: start container (`--network none`), run `AgentLoop` with `ContainerToolHost`, get the patch with `PatchExtractor`, write a `predictions.jsonl` line (`instance_id`, `model_name_or_path`, `model_patch`; field names to confirm with the gold run), write a **run manifest** (config, model, budget, context cap, temperature, prompt hashes, harness git SHA, task id), always remove the container | A fake task with `LocalExecutor` + scripted model gives a patch, a manifest and a log | offline, then PC |
| 1.7 | CLI: `Runner run --config A --task <id>`, `Runner batch --config A --tasks <file> --repeats N` (resumes: skips finished runs), `Runner eval --predictions <file>` (wraps the harness, parses `logs/evaluation/<run_id>/results.json` into `results/*.csv`) | One command per step; batch is restartable | PC |
| 1.8 | Aggregation: `Runner report` joins logs + verdicts into one CSV (per run: resolved, tokens by category, cost, time, stop reason) | CSV matches a hand check of one run | offline |

Done when: one real task runs end to end on the PC under config A and the CSV row is correct.

## Workstream 3: MCP (config B and the MCP layer)

Needs NuGet, so build and verify it in Claude Code / Rider on a machine with package access (not in the planning sandbox).

| # | Task | Acceptance | Verify |
|---|---|---|---|
| 3.1 | `src/Mcp.Server`: stdio server (`ModelContextProtocol`, preview: **pin the version**). Use the low-level tool handlers to advertise the `ToolCatalog` definitions **verbatim**; attribute-generated schemas would differ and break the fairness rule. Dispatches to `ContainerToolHost` over `DockerExecutor`. Args: `--container`, `--workdir`, `--prelude`, `--tools` (role subset), `--local-root` (test mode). Logs to stderr only (stdout carries the protocol). | `tools/list` returns exactly the catalog | PC / dev machine |
| 3.2 | `McpToolHost : IToolHost`: spawns one server process per agent through `StdioClientTransport`, `McpClient.CreateAsync`, `ListToolsAsync` to `ToolDef`, `CallToolAsync` to `ToolOutput`; disposal kills the process | Same loop code runs with it, nothing else changed | dev machine |
| 3.3 | Equivalence tests: tool names, descriptions and schemas from MCP equal `ToolCatalog`; the same call gives the same result through direct and MCP hosts for all six tools (using `--local-root`) | Tests green | dev machine |
| 3.4 | Measure what MCP adds: serialized size of definitions as received vs the catalog, result envelope size, and per-call latency (process start + stdio round trip) recorded in the log | Numbers in the log; B vs A comparable on one task | PC |
| 3.5 | **Token attribution (RQ2)**: split each call's prompt tokens into system prompt, tool definitions, agent text, tool-call arguments, tool results. Tool-definition tokens are measured exactly once per agent with the token-counting endpoint (count with vs without tools); the rest is allocated by character share and scaled so the categories sum to the API-reported total, including cache reads and writes. | Categories sum to the API total for every call (unit test); definitions measured | offline + PC |
| 3.6 | Define the cost split in the report: **MCP layer share** = tool definitions + calls + results as a share of total cost; **MCP overhead** = cost of B minus cost of A on the same tasks | Both appear in `Runner report` | offline |

Done when: config B and config A run on the same task, tool text is identical, and the report shows both numbers.

## Workstream 2: Multi-agent (configs C, D, E)

Needs one core change first (applies to every config): `AgentLoop` gains a session object that keeps its history, so an
agent can be continued with a new message (coder after reviewer feedback). Single-agent behaviour does not change.

| # | Task | Acceptance | Verify |
|---|---|---|---|
| 2.1 | `AgentSession` (history + usage) and `AgentLoop.ContinueAsync`; `AgentFactory` that gives each agent its own tool host (`--transport direct\|mcp`, `--tools full\|role`) and logs agent id and role | Existing tests still pass; continuation keeps history | offline |
| 2.2 | **Hub-and-spoke (C)**: orchestrator has read-only tools + `delegate_task(description)`; up to N workers (2, 3, 4) run in parallel with `Task.WhenAll`, each with its own context and tool host; only the worker's final summary returns to the orchestrator (this is the coordination tax); orchestrator runs the tests itself and ends. All workers share one container and one budget. | Scripted test: two workers run concurrently; budget shared; summaries returned | offline, then PC |
| 2.3 | **Coder-reviewer loop (D)**: coder (full tools) and reviewer (read_file, list_dir, run_tests) alternate for at most K = 3 rounds; reviewer gets the current diff and answers APPROVE or comments; coder keeps its history across rounds | Terminates on APPROVE and at K; feedback reaches the coder | offline, then PC |
| 2.4 | **Sequential (E, optional, dropped first if behind)**: planner (read-only) to coder (full) to tester (run tests), structured hand-offs, at most one fix pass | Hand-offs carried through in a scripted test | offline |
| 2.5 | Tool-exposure ablation on C: `--tools full` vs `--tools role` (read-only for the orchestrator and reviewer) | Both modes run; logs show different tool-definition tokens | offline, then PC |
| 2.6 | Role prompts in `prompts/`, hashed in the manifest; per-agent rows and per-role totals in `Runner report` (agents, delegations, rounds) | Report has the columns | offline |

Done when: C (N = 2), D and the ablation each produce a patch on the same real task as A and B.

## Cross-cutting

- **Task labeling (RQ3)**: rule fixed **before** any results are seen (decision 38): `p = 1 - max(s_i) / sum(s_i)` over connected components of gold-patch hunks; `parallel` if there are at least 2 components and `p >= 0.4`, otherwise `sequential`. `Runner label` writes `tasks/labels.csv`. Plus the synthetic suite of 10 hand-written tasks (`tasks/synthetic`) as a sanity check.
- **Pilot (end of week 2)**: about 10 tasks on A, B, C (N = 2), D. Measure cost per run and resolve rate, check whether Verified is saturated for Haiku 5.5, compare `estimatedPromptTokens` with real usage and recalibrate the 80k cap, then fix the per-run caps and the subset size.
- **Quality metrics**: linter and cyclomatic complexity on the generated patches (small Python CLI scripts), plus a manual look at a small sample. Analysis week.
- **Each workstream also updates**: `docs/decisions.md` (every fairness-relevant choice), the README status, and notes for report section 2.1.

## Open decisions (recommended defaults in bold)

1. **Temperature**: one fixed value for all systems, **default sampling unless the pilot shows too much variance**; record it in the manifest.
2. **Extra control**: also run C with direct tools (`--transport direct`) to separate the topology effect from the MCP effect. Recommended **if the pilot cost allows**, since it doubles C's runs.
3. **Parallel workers sharing one repo** can collide on the same file. **Instruct disjoint scopes** and treat `edit_file` failures as data; list it as a threat to validity in the report.
4. **Subset size and repeats**: decided after the pilot from the measured cost per run.
