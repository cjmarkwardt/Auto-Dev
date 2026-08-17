using AutoDev.AiCli;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoDev.ViewModels;

/// <summary>
/// Blocks the app until the currently-selected AI provider (see IAiProviderSelectionService - Claude by
/// default) is signed in. Only ever checks/signs in that one provider - switching provider later from
/// inside the app (the title bar's own switcher) doesn't re-run this gate; an unauthenticated provider
/// picked that way instead surfaces as an ordinary failed turn the first time it's used, same as any other
/// CLI-side error.
/// </summary>
public sealed partial class AuthGateViewModel(IEnumerable<IAiAuthService> authServices, IAiProviderSelectionService providerSelection) : ViewModelBase
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMinutes(5);

    private readonly IAiAuthService[] _authServices = [.. authServices];

    [ObservableProperty]
    private bool _isLoggedIn;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "";

    public event Action? Authenticated;

    public string LoginButtonLabel => $"Sign in with {providerSelection.CurrentProvider.DisplayName()}";

    private IAiAuthService AuthService => _authServices.First(s => s.Provider == providerSelection.CurrentProvider);

    public async Task CheckInitialStatusAsync()
    {
        IsBusy = true;
        StatusMessage = $"Checking {providerSelection.CurrentProvider.DisplayName()} account status…";
        var status = await AuthService.GetStatusAsync();
        IsLoggedIn = status.LoggedIn;
        IsBusy = false;
        StatusMessage = IsLoggedIn ? "" : $"Sign in to your {providerSelection.CurrentProvider.DisplayName()} account to continue.";
        if (IsLoggedIn)
        {
            Authenticated?.Invoke();
        }
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        var authService = AuthService;
        IsBusy = true;
        StatusMessage = "Opening browser to sign in…";
        _ = authService.LoginAsync();

        var deadline = DateTimeOffset.UtcNow + PollTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(PollInterval);
            var status = await authService.GetStatusAsync();
            if (status.LoggedIn)
            {
                IsLoggedIn = true;
                IsBusy = false;
                StatusMessage = "";
                Authenticated?.Invoke();
                return;
            }
        }

        IsBusy = false;
        StatusMessage = "Still waiting for sign-in - try again if the browser didn't open.";
    }
}
