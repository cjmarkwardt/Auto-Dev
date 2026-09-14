using AutoDev.AiCli;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoDev.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly IAiProviderSelectionService providerSelection;

    public MainWindowViewModel(AuthGateViewModel authGate, MainShellViewModel shell, IAiProviderSelectionService providerSelection)
    {
        AuthGate = authGate;
        Shell = shell;
        this.providerSelection = providerSelection;
        AuthGate.Authenticated += OnAuthenticated;
    }

    public AuthGateViewModel AuthGate { get; }
    public MainShellViewModel Shell { get; }

    [ObservableProperty]
    private bool isAuthenticated;

    /// <summary>The persisted AI provider choice must be loaded before AuthGate checks anyone's login status - otherwise it would always gate on Claude (IAiProviderSelectionService's in-memory default) regardless of what was actually last selected.</summary>
    public async Task InitializeAsync()
    {
        await providerSelection.InitializeAsync();
        await AuthGate.CheckInitialStatusAsync();
    }

    private async void OnAuthenticated()
    {
        IsAuthenticated = true;
        await Shell.InitializeAsync();
    }

    public Task ShutdownAsync() => Shell.ShutdownAsync();
}
