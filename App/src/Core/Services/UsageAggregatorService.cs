using System.Collections.Concurrent;
using AutoDev.AiCli.Models;

namespace AutoDev.Core.Services;

public sealed class UsageAggregatorService : IUsageAggregatorService
{
    private readonly ConcurrentDictionary<string, UsageSnapshot> _bySessionOrRunId = new();

    public UsageSnapshot TotalUsage { get; private set; } = UsageSnapshot.Zero;

    public event Action? TotalUsageChanged;

    public void ReportUsage(string sessionOrRunId, UsageSnapshot snapshot)
    {
        _bySessionOrRunId[sessionOrRunId] = snapshot;
        TotalUsage = _bySessionOrRunId.Values.Aggregate(UsageSnapshot.Zero, (acc, s) => acc + s);
        TotalUsageChanged?.Invoke();
    }
}
