namespace Agents.Core;

/// <summary>
/// Per-million-token prices. Haiku 5.5 bills a whole request at a higher rate when its
/// prompt (including cached tokens) exceeds <see cref="TierThreshold"/>.
/// NOTE: the defaults come from secondary reporting (Oct 2026). Verify them against
/// Anthropic's official pricing page before the main experiment.
/// </summary>
public sealed record Pricing(
    decimal InputPerM,
    decimal OutputPerM,
    decimal CacheReadPerM,
    decimal CacheWritePerM,
    long TierThreshold,
    decimal HighTierMultiplier)
{
    /// <summary>Haiku 5.5, 5-minute cache writes.</summary>
    public static Pricing Haiku55 { get; } = new(0.10m, 0.50m, 0.01m, 0.125m, 100_000, 5m);

    public decimal Cost(Usage u)
    {
        var multiplier = u.PromptTokens > TierThreshold ? HighTierMultiplier : 1m;
        var raw = u.InputTokens * InputPerM
                  + u.OutputTokens * OutputPerM
                  + u.CacheReadTokens * CacheReadPerM
                  + u.CacheWriteTokens * CacheWritePerM;
        return multiplier * raw / 1_000_000m;
    }
}

/// <summary>
/// Budget for ONE run (one task on one configuration). It is shared by every agent in the run,
/// so a multi-agent system spends from the same pot as a single agent. Hitting either limit ends the
/// run, and the task counts as unresolved.
/// </summary>
public sealed class RunBudget
{
    private readonly object _lock = new();
    private readonly Pricing _pricing;

    public RunBudget(long maxTotalTokens, decimal maxCostUsd, Pricing? pricing = null)
    {
        MaxTotalTokens = maxTotalTokens;
        MaxCostUsd = maxCostUsd;
        _pricing = pricing ?? Pricing.Haiku55;
    }

    public long MaxTotalTokens { get; }

    public decimal MaxCostUsd { get; }

    public Usage Total { get; private set; } = Usage.Zero;

    public decimal CostUsd { get; private set; }

    public bool Exceeded
    {
        get
        {
            lock (_lock)
            {
                return Total.TotalTokens >= MaxTotalTokens || CostUsd >= MaxCostUsd;
            }
        }
    }

    /// <summary>Adds one call's usage and returns that call's cost.</summary>
    public decimal Record(Usage usage)
    {
        var cost = _pricing.Cost(usage);
        lock (_lock)
        {
            Total += usage;
            CostUsd += cost;
        }

        return cost;
    }
}
