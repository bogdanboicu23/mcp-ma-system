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
