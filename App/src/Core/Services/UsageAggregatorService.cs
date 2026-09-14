using System.Collections.Concurrent;
using AutoDev.AiCli.Models;

namespace AutoDev.Core.Services;

public sealed class UsageAggregatorService : IUsageAggregatorService
{
    private readonly ConcurrentDictionary<string, UsageSnapshot> bySessionOrRunId = new();

    public UsageSnapshot TotalUsage { get; private set; } = UsageSnapshot.Zero;

    public event Action? TotalUsageChanged;

    public void ReportUsage(string sessionOrRunId, UsageSnapshot snapshot)
    {
        bySessionOrRunId[sessionOrRunId] = snapshot;
        TotalUsage = bySessionOrRunId.Values.Aggregate(UsageSnapshot.Zero, (acc, s) => acc + s);
        TotalUsageChanged?.Invoke();
    }
}
