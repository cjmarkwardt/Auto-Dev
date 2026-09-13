using AutoDev.AiCli.Models;

namespace AutoDev.Core.Services;

/// <summary>
/// Rolls up token usage across every AI session/run this app instance has touched, regardless of provider.
/// Each entry is keyed by session/run id and *replaced* (not added to) on every update, because a
/// ResultEvent's ModelUsage map is already cumulative for that session - see plan doc. HeaderViewModel
/// reads TotalUsage as the title bar's fallback usage display for a provider with no usage-percentage API
/// of its own (see CodexUsageService).
/// </summary>
public interface IUsageAggregatorService
{
    UsageSnapshot TotalUsage { get; }
    event Action? TotalUsageChanged;

    void ReportUsage(string sessionOrRunId, UsageSnapshot snapshot);
}
