using AutoDev.AiCli;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoDev.ViewModels;

/// <summary>
/// One installed-but-not-signed-in provider's own login row on the AuthGate screen - see AuthGateViewModel,
/// which creates one of these per provider that IAiAuthService.IsInstalled but not yet logged in. Polls the
/// same way AuthGateViewModel used to when it only ever gated on a single provider.
/// </summary>
public sealed partial class ProviderLoginRowViewModel(IAiAuthService authService) : ViewModelBase
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMinutes(5);

    public AiProvider Provider => authService.Provider;

    public string LoginButtonLabel => $"Sign in with {authService.Provider.DisplayName()}";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "";

    public event Action? Authenticated;

    [RelayCommand]
    private async Task LoginAsync()
    {
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
