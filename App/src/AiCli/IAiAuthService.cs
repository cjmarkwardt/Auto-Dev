using AutoDev.AiCli.Models;

namespace AutoDev.AiCli;

/// <summary>Reads/starts login for one AI provider's own account - see HeaderViewModel and AuthGateViewModel, both of which pick the implementation matching IAiProviderSelectionService.CurrentProvider.</summary>
public interface IAiAuthService
{
    AiProvider Provider { get; }

    /// <summary>Whether this provider's CLI is even on PATH - checked before GetStatusAsync bothers asking it anything, so the AuthGate can tell "not installed" apart from "installed but not signed in".</summary>
    bool IsInstalled { get; }

    Task<AiAuthStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts this provider's own sign-in flow (typically a browser OAuth flow) without waiting for it to finish - callers should poll <see cref="GetStatusAsync"/>.</summary>
    Task LoginAsync(CancellationToken cancellationToken = default);
}
