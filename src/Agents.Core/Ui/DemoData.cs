using System.Globalization;
using System.Text;
using Agents.Core.Runs;

namespace Agents.Core.Ui;

/// <summary>
/// FAKE results for looking at the UI before any paid run exists. Every manifest has model "demo-fake" and harness
/// "demo" and the UI shows a banner for them. The numbers are invented from a simple generative model; they say
/// nothing about the real systems and must never be used in the report.
/// </summary>
public static class DemoData
{
    public const string ModelName = "demo-fake";

    public static int Generate(string root, int tasks = 16, int repeats = 2, int seed = 7)
    {
        var store = new RunStore(root);
        if (store.ListManifests().Any(m => m.Model != ModelName))
        {
            throw new InvalidOperationException($"{store.Root} already contains real runs; demo data goes into an empty folder.");
        }

        Directory.CreateDirectory(store.Root);
        var rng = new Random(seed);
        var inv = CultureInfo.InvariantCulture;
        var labels = new StringBuilder("# theta=0.40 (demo)\ninstance_id,files,hunks,components,changed_lines,p_hat,label\n");
        var written = 0;

        var taskInfo = Enumerable.Range(1, tasks).Select(i =>
        {
            var parallel = i % 2 == 0;
            var p = parallel ? 0.4 + rng.NextDouble() * 0.45 : rng.NextDouble() * 0.3;
            var hard = rng.NextDouble();
            var id = $"demo__repo-{i:00}";
            labels.Append(string.Join(",", id, parallel ? 3 : 1, parallel ? 5 : 2, parallel ? 3 : 1, 20 + i * 3,
                p.ToString("0.000", inv), parallel ? "parallel" : "sequential")).Append('\n');
            return (Id: id, Parallel: parallel, Hard: hard);
        }).ToList();
        File.WriteAllText(Path.Combine(store.Root, "labels.csv"), labels.ToString());

        foreach (var (config, workers) in new[] { ("A", 0), ("B", 0), ("C", 3), ("D", 1), ("E", 2) })
        {
            var agentCount = 1 + workers;
            foreach (var task in taskInfo)
            {
                for (var repeat = 1; repeat <= repeats; repeat++)
                {
                    var pResolve = 0.8 - 0.45 * task.Hard
                        + config switch
                        {
                            "C" => task.Parallel ? 0.10 : -0.06,
                            "D" => 0.05,
                            "E" => -0.03,
                            _ => 0.0,
                        };
                    var resolved = rng.NextDouble() < pResolve;
                    WriteRun(store, rng, config, agentCount, task.Id, repeat, resolved, task.Hard);
                    written++;
                }
            }
        }

        return written;
    }

    private static void WriteRun(
        RunStore store, Random rng, string config, int agentCount, string instance, int repeat, bool resolved, double hard)
    {
        var runId = RunStore.MakeRunId(config, instance, repeat);
        var dir = store.RunDir(runId);
        Directory.CreateDirectory(dir);
        var logPath = Path.Combine(dir, "calls.jsonl");
        File.Delete(logPath);
        var log = new JsonlCallLog(logPath);

        var started = new DateTimeOffset(2026, 11, 9, 8, 0, 0, TimeSpan.Zero).AddMinutes(rng.Next(0, 2000));
        var clock = started;
        Usage total = Usage.Zero;
        decimal cost = 0, sysUsd = 0, defUsd = 0, callUsd = 0, resUsd = 0, textUsd = 0;
        long sysT = 0, defT = 0, callT = 0, resT = 0, textT = 0;
        var calls = 0;

        for (var a = 1; a <= agentCount; a++)
        {
            var role = agentCount == 1 ? "solo" : a == 1 ? "orchestrator" : "worker";
            var turns = (int)((8 + 14 * hard + rng.Next(0, 8)) / (agentCount > 1 && a > 1 ? 1.4 : 1.0));
            for (var turn = 1; turn <= turns; turn++)
            {
                var sys = 900 + (a > 1 ? 250 : 0);
                var def = 1150;
                var call = 70 * turn;
                var res = (long)(380 * turn * (0.8 + rng.NextDouble() * 0.5));
                var text = 140 * turn;
                var prompt = sys + def + call + res + text;
                var cached = turn == 1 ? 0 : (long)(prompt * 0.72);
                var write = turn == 1 ? 0 : (long)(prompt * 0.18);
                var input = prompt - cached - write;
                var output = 160 + rng.Next(0, 240);
                var usage = new Usage(input, output, cached, write);
                var callCost = (input * 0.10m + cached * 0.01m + write * 0.125m + output * 0.50m) / 1_000_000m;

                // Split the cost by each source's share of the prompt; output goes to text and calls.
                decimal Share(long part) => callCost * part / (prompt + output);
                var attribution = new TokenAttribution(
                    sys, def, call, res, text, output * 3 / 4, output / 4,
                    Share(sys), Share(def), Share(call + output / 4), Share(res), Share(text + output * 3 / 4));

                clock = clock.AddMilliseconds(900 + rng.Next(0, 1800));
                log.Write(new CallRecord(runId, config, $"agent-{a}", role, turn, clock, 900 + rng.Next(0, 1800), usage,
                    callCost, prompt, 0, turn == turns ? 0 : 1 + rng.Next(0, 2), turn == turns ? "end_turn" : "tool_use", attribution));

                total += usage;
                cost += callCost;
                sysUsd += attribution.SysUsd;
                defUsd += attribution.DefUsd;
                callUsd += attribution.CallUsd;
                resUsd += attribution.ResUsd;
                textUsd += attribution.TextUsd;
                sysT += sys;
                defT += def;
                callT += call;
                resT += res;
                textT += text;
                calls++;
            }
        }

        var patch = $"diff --git a/pkg/module.py b/pkg/module.py\n--- a/pkg/module.py\n+++ b/pkg/module.py\n@@ -1,3 +1,3 @@\n-    return value\n+    return normalise(value)  # demo patch\n";
        File.WriteAllText(Path.Combine(dir, "patch.diff"), patch);

        store.WriteManifest(new RunManifest
        {
            RunId = runId,
            Config = config,
            Repeat = repeat,
            InstanceId = instance,
            Model = ModelName,
            Image = "demo (no container)",
            StartedAt = started,
            DurationMs = (long)(clock - started).TotalMilliseconds,
            Status = "completed",
            StopReason = "end_turn",
            Turns = calls,
            InputTokens = total.InputTokens,
            OutputTokens = total.OutputTokens,
            CacheReadTokens = total.CacheReadTokens,
            CacheWriteTokens = total.CacheWriteTokens,
            CostUsd = cost,
            PatchChars = patch.Length,
            MaxPromptTokens = 80_000,
            MaxTotalTokens = 3_000_000,
            MaxCostUsd = 0.5m,
            MaxTurns = 50,
            SystemPromptHash = "demo0001",
            TaskPromptHash = "demo0002",
            Variant = agentCount > 1 ? $"workers={agentCount - 1} transport=mcp" : config == "B" ? "transport=mcp" : "transport=direct",
            Calls = calls,
            Agents = agentCount,
            AttrDefTokens = defT,
            AttrCallTokens = callT,
            AttrResTokens = resT,
            AttrSysTokens = sysT,
            AttrTextTokens = textT,
            AttrSysUsd = sysUsd,
            AttrDefUsd = defUsd,
            AttrCallUsd = callUsd,
            AttrResUsd = resUsd,
            AttrTextUsd = textUsd,
            HarnessVersion = "demo",
        });
        store.WriteVerdict(runId, new Verdict(resolved, "demo", started));
    }
}
