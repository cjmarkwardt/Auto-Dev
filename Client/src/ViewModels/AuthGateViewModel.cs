using System.Collections.ObjectModel;
using AutoDev.AiCli;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoDev.ViewModels;

/// <summary>
/// Blocks the app until an AI provider is ready to use. Checks every provider's own IAiAuthService up front:
/// if one is already installed and signed in, that provider is assumed (preferring the persisted choice - see
/// IAiProviderSelectionService - if it's among the signed-in ones) and the gate never shows at all. Otherwise
/// it shows either "neither is installed" (NeitherInstalled) or a login row per installed-but-signed-out
/// provider (LoginRows) - one, or both, depending what's actually on this machine.
/// </summary>
public sealed partial class AuthGateViewModel(IEnumerable<IAiAuthService> authServices, IAiProviderSelectionService providerSelection) : ViewModelBase
{
    private readonly IAiAuthService[] _authServices = [.. authServices];

    [ObservableProperty]
    private bool _isChecking = true;

    [ObservableProperty]
    private bool _neitherInstalled;

    public ObservableCollection<ProviderLoginRowViewModel> LoginRows { get; } = [];

    public event Action? Authenticated;

    public async Task CheckInitialStatusAsync()
    {
        IsChecking = true;

        var statusByProvider = new Dictionary<AiProvider, (bool Installed, bool LoggedIn)>();
        foreach (var authService in _authServices)
        {
            var installed = authService.IsInstalled;
            var loggedIn = installed && (await authService.GetStatusAsync()).LoggedIn;
            statusByProvider[authService.Provider] = (installed, loggedIn);
        }

        // Prefer the already-persisted provider choice if it's one of the signed-in ones, so switching
        // machines/CLIs doesn't silently flip which provider a returning user lands on.
        var signedInProvider = statusByProvider.TryGetValue(providerSelection.CurrentProvider, out var current) && current.LoggedIn
            ? providerSelection.CurrentProvider
            : statusByProvider.Where(kv => kv.Value.LoggedIn).Select(kv => (AiProvider?)kv.Key).FirstOrDefault();

        if (signedInProvider is { } provider)
        {
            await providerSelection.SetProviderAsync(provider);
            IsChecking = false;
            Authenticated?.Invoke();
            return;
        }

        NeitherInstalled = statusByProvider.Values.All(status => !status.Installed);

        LoginRows.Clear();
        foreach (var authService in _authServices)
        {
            if (!statusByProvider[authService.Provider].Installed)
            {
                continue;
            }

            var row = new ProviderLoginRowViewModel(authService);
            row.Authenticated += () => OnRowAuthenticated(authService.Provider);
            LoginRows.Add(row);
        }

        IsChecking = false;
    }

    private async void OnRowAuthenticated(AiProvider provider)
    {
        await providerSelection.SetProviderAsync(provider);
        Authenticated?.Invoke();
    }
}
