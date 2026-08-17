using AutoDev.AiCli;
using AutoDev.AiCli.Models;

namespace AutoDev.CodexCli;

/// <summary>
/// Codex's CLI has no scriptable session/weekly rate-limit percentage the way Claude's `/usage` slash
/// command provides - see the plan doc's own probing session. Always returns null so HeaderViewModel falls
/// back to showing IUsageAggregatorService's raw cumulative token count instead, which is the closest
/// "whatever limits it has" equivalent actually available.
/// </summary>
public sealed class CodexUsageService : IAiUsageService
{
    public AiProvider Provider => AiProvider.Codex;

    public Task<UsageLimitStatus?> GetUsageStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<UsageLimitStatus?>(null);
}
