using AutoDev.ClaudeCli;
using AutoDev.CodexCli;

namespace AutoDev.AiCli;

/// <inheritdoc cref="IAiSessionClientFactory" />
public sealed class AiSessionClientFactory(ClaudeSessionClientFactory claudeFactory, CodexSessionClientFactory codexFactory) : IAiSessionClientFactory
{
    public IAiSessionClient Create(AiProvider provider, string workspacePath, string model, string? effort = null) => provider switch
    {
        AiProvider.Claude => claudeFactory.Create(workspacePath, model, effort),
        AiProvider.Codex => codexFactory.Create(workspacePath, model, effort),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };
}
