using AutoDev.AiCli.Models;

namespace AutoDev.AiCli;

/// <summary>
/// Wraps one AI provider's turn-by-turn session, backing a workspace's Generate tab - the persistent,
/// multi-turn conversation regardless of whether the provider itself is implemented as one long-lived
/// subprocess (ClaudeSessionClient) or a fresh subprocess per turn (CodexSessionClient).
/// </summary>
public interface IAiSessionClient : IAsyncDisposable
{
    string SessionId { get; }
    bool IsRunning { get; }

    /// <summary>Starts the session. Pass a previously-known session id (in whatever form this provider's Start/SendUserMessageAsync recognizes) to resume that conversation.</summary>
    void Start(string? resumeSessionId = null);

    IAsyncEnumerable<AiStreamEvent> ReadAllEventsAsync(CancellationToken cancellationToken = default);

    Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Same as SendUserMessageAsync, but with image attachments (see GenerateTabViewModel.AddImageAttachment) sent alongside the text.</summary>
    Task SendUserMessageAsync(string text, IReadOnlyList<ImageAttachment> images, CancellationToken cancellationToken = default);
}

/// <summary>Creates an <see cref="IAiSessionClient"/> for whichever provider is requested - see AiSessionClientFactory, which dispatches to each provider's own concrete factory.</summary>
public interface IAiSessionClientFactory
{
    /// <summary>model/effort are passed straight through to the chosen provider's own CLI flags - see each provider's AvailableModels/AvailableEfforts in GenerateTabViewModel. effort null omits the flag entirely, letting the CLI use its own default.</summary>
    IAiSessionClient Create(AiProvider provider, string workspacePath, string model, string? effort = null);
}
