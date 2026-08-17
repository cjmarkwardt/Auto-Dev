namespace AutoDev.AiCli.Models;

/// <summary>
/// A point-in-time usage total. When sourced from a `result` event's `modelUsage` map,
/// this is already cumulative for the whole CLI session (verified empirically - the
/// top-level `usage` field on each event is per-turn, `modelUsage` is the running total).
/// </summary>
public sealed record UsageSnapshot(
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    decimal TotalCostUsd)
{
    public static UsageSnapshot Zero { get; } = new(0, 0, 0, 0, 0m);

    public static UsageSnapshot operator +(UsageSnapshot a, UsageSnapshot b) => new(
        a.InputTokens + b.InputTokens,
        a.OutputTokens + b.OutputTokens,
        a.CacheCreationTokens + b.CacheCreationTokens,
        a.CacheReadTokens + b.CacheReadTokens,
        a.TotalCostUsd + b.TotalCostUsd);

    public long TotalTokens => InputTokens + OutputTokens + CacheCreationTokens + CacheReadTokens;
}
