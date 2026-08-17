using System.Collections.ObjectModel;
using AutoDev.AiCli;
using AutoDev.AiCli.Models;
using AutoDev.Core.Models;
using AutoDev.Core.Services;
using AutoDev.ViewModels.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoDev.ViewModels;

public sealed partial class HeaderViewModel : ViewModelBase
{
    private static readonly TimeSpan UsagePollInterval = TimeSpan.FromSeconds(60);

    private readonly IAiAuthService[] _authServices;
    private readonly IAiUsageService[] _usageServices;
    private readonly IAiProviderSelectionService _providerSelection;
    private readonly IUsageAggregatorService _usageAggregator;
    private readonly IWorkspaceService _workspaceService;
    private readonly IGitService _gitService;
    private readonly IDialogService _dialogService;
    private readonly IUiDispatcher _dispatcher;

    /// <summary>Drives the title bar's provider-switcher popup - see ToggleProviderMenuCommand/SelectProviderCommand, mirroring IsRecentMenuOpen's own popup below it.</summary>
    public IReadOnlyList<AiProvider> AvailableProviders { get; } = [AiProvider.Claude, AiProvider.Codex];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProviderDisplayName))]
    private AiProvider _currentProvider = AiProvider.Claude;

    public string ProviderDisplayName => CurrentProvider.DisplayName();

    [ObservableProperty]
    private bool _isProviderMenuOpen;

    [ObservableProperty]
    private string _accountEmail = "";

    [ObservableProperty]
    private string _subscriptionType = "";

    /// <summary>True while the current provider has a real session/week percentage to show (Claude, via its own `/usage` command) - false falls back to TokenUsageDisplay instead (Codex, which has no scriptable usage-percentage API - see CodexUsageService).</summary>
    [ObservableProperty]
    private bool _hasPeriodUsage;

    [ObservableProperty]
    private string _sessionUsageDisplay = "Session —";

    /// <summary>True once session usage reaches 90% - drives a soft-red warning color on the usage text.</summary>
    [ObservableProperty]
    private bool _isSessionUsageCritical;

    [ObservableProperty]
    private string _sessionResetTooltip = "";

    [ObservableProperty]
    private string _sessionResetCountdown = "";

    [ObservableProperty]
    private string _weekUsageDisplay = "Week —";

    /// <summary>True once week usage reaches 90% - drives a soft-red warning color on the usage text.</summary>
    [ObservableProperty]
    private bool _isWeekUsageCritical;

    [ObservableProperty]
    private string _weekResetTooltip = "";

    [ObservableProperty]
    private string _weekResetCountdown = "";

    /// <summary>Shown instead of Session/Week whenever HasPeriodUsage is false - IUsageAggregatorService's raw cumulative token count (across every open workspace/provider this app session), the closest "whatever limits it has" equivalent available for a provider with no usage-percentage API.</summary>
    [ObservableProperty]
    private string _tokenUsageDisplay = "";

    public ObservableCollection<WorkspaceInfo> RecentWorkspaces { get; } = [];

    [ObservableProperty]
    private bool _isRecentMenuOpen;

    /// <summary>True while a clone is in flight - disables the folder/recent buttons in the View (a second clone/open shouldn't start on top of it) and swaps the Clone button for a Cancel one.</summary>
    [ObservableProperty]
    private bool _isCloning;

    private CancellationTokenSource? _cloneCts;

    /// <summary>Guards PollUsageLoopAsync so RefreshAccountAsync (called both at startup and on every provider switch) only ever starts the polling loop once - see RefreshAccountAsync's own doc comment for why it can't simply start in the constructor.</summary>
    private bool _usagePollStarted;

    public HeaderViewModel(
        IEnumerable<IAiAuthService> authServices,
        IEnumerable<IAiUsageService> usageServices,
        IAiProviderSelectionService providerSelection,
        IUsageAggregatorService usageAggregator,
        IWorkspaceService workspaceService,
        IGitService gitService,
        IDialogService dialogService,
        IUiDispatcher dispatcher)
    {
        _authServices = [.. authServices];
        _usageServices = [.. usageServices];
        _providerSelection = providerSelection;
        _usageAggregator = usageAggregator;
        _workspaceService = workspaceService;
        _gitService = gitService;
        _dialogService = dialogService;
        _dispatcher = dispatcher;
        CurrentProvider = providerSelection.CurrentProvider;
        _providerSelection.ProviderChanged += OnProviderChanged;
        _usageAggregator.TotalUsageChanged += OnTotalUsageChanged;
        RefreshTokenUsageDisplay();
    }

    public event Action<WorkspaceInfo>? WorkspaceOpened;

    /// <summary>
    /// Called once at startup (see MainShellViewModel.InitializeAsync) and again every time
    /// OnProviderChanged fires - re-reads CurrentProvider from IAiProviderSelectionService itself rather
    /// than trusting the constructor-time snapshot, since HeaderViewModel (a singleton) is constructed via
    /// DI before IAiProviderSelectionService.InitializeAsync has loaded the persisted choice - see
    /// MainWindowViewModel.InitializeAsync's own ordering.
    /// </summary>
    public async Task RefreshAccountAsync()
    {
        CurrentProvider = _providerSelection.CurrentProvider;
        var authService = _authServices.First(s => s.Provider == CurrentProvider);
        var status = await authService.GetStatusAsync();
        AccountEmail = status.Email ?? "";
        SubscriptionType = FormatSubscription(status.SubscriptionType);

        if (!_usagePollStarted)
        {
            _usagePollStarted = true;
            _ = PollUsageLoopAsync();
        }
    }

    [RelayCommand]
    private void ToggleProviderMenu() => IsProviderMenuOpen = !IsProviderMenuOpen;

    public void CloseProviderMenu() => IsProviderMenuOpen = false;

    [RelayCommand]
    private async Task SelectProviderAsync(AiProvider provider)
    {
        IsProviderMenuOpen = false;
        await _providerSelection.SetProviderAsync(provider);
    }

    /// <summary>
    /// Eagerly clears the period-usage display (rather than leaving the old provider's stale Session/Week
    /// numbers on screen) before kicking off the new provider's own account/usage refresh - those are
    /// async, so without this the switch would otherwise show a brief flash of the wrong provider's numbers
    /// still labeled as if they were current.
    /// </summary>
    private void OnProviderChanged(AiProvider provider) => _dispatcher.Post(() =>
    {
        CurrentProvider = provider;
        HasPeriodUsage = false;
        RefreshTokenUsageDisplay();
        _ = RefreshAccountAsync();
        _ = RefreshUsageLimitsAsync();
    });

    [RelayCommand]
    private async Task BrowseForFolderAsync()
    {
        var path = await _dialogService.PickFolderAsync(await _workspaceService.GetLastParentFolderAsync());
        if (path is null)
        {
            return;
        }

        if (Path.GetDirectoryName(path) is { Length: > 0 } parentDir)
        {
            await _workspaceService.SaveLastParentFolderAsync(parentDir);
        }

        await OpenWorkspaceAsync(path);
    }

    /// <summary>Opens a workspace folder by path with no picker UI involved - shared by BrowseForFolderAsync/CloneAsync/OpenRecentAsync's identical tail, and by MainShellViewModel.InitializeAsync for restoring previously-open tabs on launch.</summary>
    private async Task OpenWorkspaceAsync(string path)
    {
        var workspace = await _workspaceService.OpenOrCreateAsync(path);
        WorkspaceOpened?.Invoke(workspace);
        await RefreshRecentWorkspacesAsync();
    }

    /// <summary>Public entry point for MainShellViewModel's startup restore - see OpenWorkspaceAsync.</summary>
    public Task OpenPathAsync(string path) => OpenWorkspaceAsync(path);

    /// <summary>Clones a remote repository into a new folder under a user-picked parent directory, then opens it as a workspace - a normal `git clone`, so it comes with whatever branches (version/*, feature/*) the remote already has.</summary>
    [RelayCommand]
    private async Task CloneAsync()
    {
        var url = await _dialogService.ShowInputDialogAsync("Clone Repository", "Repository URL", "");
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var parentDir = await _dialogService.PickFolderAsync(await _workspaceService.GetLastParentFolderAsync());
        if (parentDir is null)
        {
            return;
        }

        await _workspaceService.SaveLastParentFolderAsync(parentDir);

        var name = DeriveRepoName(url.Trim());
        var destination = Path.Combine(parentDir, name);
        if (Directory.Exists(destination))
        {
            await _dialogService.ShowConfirmDialogAsync("Clone Repository", $"A folder named '{name}' already exists in {parentDir}.", confirmLabel: "OK", isDestructive: false);
            return;
        }

        _cloneCts = new CancellationTokenSource();
        IsCloning = true;
        bool cloned;
        var wasCanceled = false;
        try
        {
            cloned = await _gitService.CloneAsync(parentDir, url.Trim(), name, _cloneCts.Token);
        }
        catch (OperationCanceledException)
        {
            cloned = false;
            wasCanceled = true;
        }
        finally
        {
            IsCloning = false;
            _cloneCts.Dispose();
            _cloneCts = null;
        }

        if (!cloned)
        {
            TryDeletePartialClone(destination);
            if (!wasCanceled)
            {
                await _dialogService.ShowConfirmDialogAsync("Clone Repository", "Failed to clone the repository. Check the URL and your network connection.", confirmLabel: "OK", isDestructive: false);
            }

            return;
        }

        await OpenWorkspaceAsync(destination);
    }

    /// <summary>The only way to interrupt an in-flight clone - CliWrap forcefully kills the git process on cancellation, then CloneAsync's catch block cleans up the now-partial destination folder.</summary>
    [RelayCommand]
    private void CancelClone() => _cloneCts?.Cancel();

    private static void TryDeletePartialClone(string destination)
    {
        try
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort - a leftover partial clone folder is a minor annoyance, not worth surfacing an error for.
        }
    }

    private static string DeriveRepoName(string url)
    {
        var trimmed = url.TrimEnd('/');
        var lastSegment = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "repository";
        return lastSegment.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? lastSegment[..^4] : lastSegment;
    }

    [RelayCommand]
    private void ToggleRecentMenu() => IsRecentMenuOpen = !IsRecentMenuOpen;

    public void CloseRecentMenu() => IsRecentMenuOpen = false;

    [RelayCommand]
    private async Task OpenRecentAsync(WorkspaceInfo workspace)
    {
        IsRecentMenuOpen = false;
        await OpenWorkspaceAsync(workspace.FullPath);
    }

    [RelayCommand]
    private async Task RemoveRecentAsync(WorkspaceInfo workspace)
    {
        await _workspaceService.ForgetRecentAsync(workspace.FullPath);
        await RefreshRecentWorkspacesAsync();
    }

    public async Task RefreshRecentWorkspacesAsync()
    {
        var recents = await _workspaceService.GetRecentWorkspacesAsync();
        RecentWorkspaces.Clear();
        foreach (var workspace in recents)
        {
            RecentWorkspaces.Add(workspace);
        }
    }

    /// <summary>Codex's own already-titlecased plan names (Free/Plus/Pro/Team/Enterprise - see CodexAuthService.FormatPlan) pass through this unchanged; only Claude's lowercase subscriptionType values need mapping.</summary>
    private static string FormatSubscription(string? subscriptionType) => subscriptionType switch
    {
        null or "" => "",
        "pro" => "Pro",
        "max" => "Max",
        var other => other,
    };

    private async Task PollUsageLoopAsync()
    {
        // Claude's "/usage" query is handled locally by the CLI (no model call, no cost), so polling it
        // regularly is free - see ClaudeUsageService's doc comment. Codex's own IAiUsageService is a no-op
        // (always returns null - see CodexUsageService), so polling it costs nothing either.
        await RefreshUsageLimitsAsync();

        using var timer = new PeriodicTimer(UsagePollInterval);
        while (await timer.WaitForNextTickAsync())
        {
            await RefreshUsageLimitsAsync();
        }
    }

    /// <summary>Reads whichever IAiUsageService matches the provider currently selected at the moment this call started - see OnProviderChanged, which re-triggers this immediately on a switch rather than waiting for the next poll tick.</summary>
    private async Task RefreshUsageLimitsAsync()
    {
        var usageService = _usageServices.First(s => s.Provider == CurrentProvider);
        var status = await usageService.GetUsageStatusAsync();
        _dispatcher.Post(() => ApplyUsageStatus(status));
    }

    /// <summary>
    /// A failed/incomplete poll (transient CLI hiccup, timeout, unparseable report text, etc.) leaves the
    /// last known values on screen rather than blanking them to "—" - a temporary polling failure isn't
    /// evidence the limits reset, and flickering to "—" every so often while the app sits idle was itself
    /// the bug being fixed here. "—" now only ever shows before the very first successful poll. A status
    /// with both Session and Week null (Codex, always) leaves HasPeriodUsage false, keeping the
    /// TokenUsageDisplay fallback visible instead - see RefreshTokenUsageDisplay.
    /// </summary>
    private void ApplyUsageStatus(UsageLimitStatus? status)
    {
        if (status?.Session is { } session)
        {
            HasPeriodUsage = true;
            SessionUsageDisplay = $"Session {session.PercentUsed}%";
            IsSessionUsageCritical = session.PercentUsed >= 90;
            SessionResetTooltip = session.ResetsAtFull;
            SessionResetCountdown = FormatCountdown(session.ResetsAtUtc);
        }

        if (status?.Week is { } week)
        {
            HasPeriodUsage = true;
            WeekUsageDisplay = $"Week {week.PercentUsed}%";
            IsWeekUsageCritical = week.PercentUsed >= 90;
            WeekResetTooltip = week.ResetsAtFull;
            WeekResetCountdown = FormatCountdown(week.ResetsAtUtc);
        }
    }

    private void OnTotalUsageChanged() => _dispatcher.Post(RefreshTokenUsageDisplay);

    private void RefreshTokenUsageDisplay() => TokenUsageDisplay = $"{FormatTokenCount(_usageAggregator.TotalUsage.TotalTokens)} tokens";

    private static string FormatTokenCount(long tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000.0:0.#}M",
        >= 1_000 => $"{tokens / 1_000.0:0.#}K",
        _ => tokens.ToString(),
    };

    private static string FormatCountdown(DateTimeOffset? resetsAtUtc)
    {
        if (resetsAtUtc is not { } resetsAt)
        {
            return "";
        }

        var remaining = resetsAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return "resets soon";
        }

        if (remaining.TotalDays >= 1)
        {
            return $"{(int)remaining.TotalDays}d {remaining.Hours}h left";
        }

        if (remaining.TotalHours >= 1)
        {
            return $"{(int)remaining.TotalHours}h {remaining.Minutes}m left";
        }

        return $"{Math.Max(1, remaining.Minutes)}m left";
    }
}
