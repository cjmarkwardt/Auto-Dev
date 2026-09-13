using AutoDev.AiCli.Models;

namespace AutoDev.AiCli;

/// <summary>Reads an AI provider's own rate-limit usage, if it exposes one - see HeaderViewModel, which falls back to IUsageAggregatorService's raw token counts for a provider that returns null (or a status with both periods null, e.g. Codex, which has no scriptable usage-percentage API).</summary>
public interface IAiUsageService
{
    AiProvider Provider { get; }

    Task<UsageLimitStatus?> GetUsageStatusAsync(CancellationToken cancellationToken = default);
}
