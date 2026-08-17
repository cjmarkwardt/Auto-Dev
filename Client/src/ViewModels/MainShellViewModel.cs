using System.Collections.ObjectModel;
using AutoDev.Core.Models;
using AutoDev.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace AutoDev.ViewModels;

public sealed partial class MainShellViewModel : ViewModelBase
{
    private readonly IWorkspaceTabFactory _tabFactory;
    private readonly IWorkspaceService _workspaceService;
    private readonly ILogger<MainShellViewModel> _logger;

    public MainShellViewModel(HeaderViewModel header, IWorkspaceTabFactory tabFactory, IWorkspaceService workspaceService, ILogger<MainShellViewModel> logger)
    {
        Header = header;
        _tabFactory = tabFactory;
        _workspaceService = workspaceService;
        _logger = logger;
        Header.WorkspaceOpened += OnWorkspaceOpened;
    }

    public HeaderViewModel Header { get; }

    /// <summary>The global tab strip's backing collection - one entry per open workspace.</summary>
    public ObservableCollection<WorkspaceTabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    private WorkspaceTabViewModel? _selectedTab;

    public async Task InitializeAsync()
    {
        await Header.RefreshAccountAsync();
        await Header.RefreshRecentWorkspacesAsync();

        // Sequential, not Task.WhenAll: JsonSettingsService does an unlocked read-modify-write over one
        // file per call, and OpenOrCreateAsync itself mutates the recents list as a side effect of opening -
        // concurrent opens here would race and silently drop entries from that list.
        foreach (var workspace in await _workspaceService.GetOpenWorkspacesAsync())
        {
            await Header.OpenPathAsync(workspace.FullPath);
        }
    }

    private async void OnWorkspaceOpened(WorkspaceInfo workspace)
    {
        var existing = Tabs.FirstOrDefault(t => t.Workspace.FullPath == workspace.FullPath);
        if (existing is not null)
        {
            SelectedTab = existing;
            return;
        }

        var tab = _tabFactory.Create(workspace);
        tab.CloseRequested += OnTabCloseRequested;
        tab.MoveRequested += OnTabMoveRequested;
        Tabs.Add(tab);
        SelectedTab = tab;
        await tab.InitializeAsync();
    }

    /// <summary>
    /// Reorders a tab within the strip - a safe no-op if it's already at that end (offset would move it out
    /// of bounds). The tab strip ListBox's SelectedItem is two-way bound to SelectedTab, and re-sorting the
    /// bound collection out from under it - even via a single Move notification, not a Remove+Add pair -
    /// still momentarily desyncs Avalonia's own selected-index tracking and nulls SelectedItem, which then
    /// writes back through the binding and nulls SelectedTab too: since SelectedTab == null hides both the
    /// whole tab strip and the main content area (see MainShellView.axaml), reordering ANY tab this way
    /// would otherwise blank the entire window. Restoring SelectedTab right after Move is the fix.
    /// </summary>
    private void OnTabMoveRequested(WorkspaceTabViewModel tab, int offset)
    {
        var index = Tabs.IndexOf(tab);
        var newIndex = index + offset;
        if (index < 0 || newIndex < 0 || newIndex >= Tabs.Count)
        {
            return;
        }

        var previousSelection = SelectedTab;
        Tabs.Move(index, newIndex);
        SelectedTab = previousSelection;
    }

    private async void OnTabCloseRequested(WorkspaceTabViewModel tab)
    {
        tab.CloseRequested -= OnTabCloseRequested;
        tab.MoveRequested -= OnTabMoveRequested;

        // Capture this before Remove, not after: the tab strip's ListBox is two-way bound to SelectedTab,
        // and removing the currently-selected item from Tabs synchronously nulls SelectedTab as a side
        // effect of that binding - checking ReferenceEquals(SelectedTab, tab) after Remove would then
        // always be false, skipping the fallback below and leaving SelectedTab stuck null (which hides the
        // whole tab strip, since it's only visible while SelectedTab is non-null - looking exactly like
        // every other open workspace closed too, even though they're still in Tabs).
        var wasSelected = ReferenceEquals(SelectedTab, tab);
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (wasSelected)
        {
            SelectedTab = index > 0 && index - 1 < Tabs.Count ? Tabs[index - 1] : Tabs.Count > 0 ? Tabs[0] : null;
        }

        // The tab is already removed from Tabs above - a disposal failure here (e.g. a locked file, a
        // process that wouldn't die) must not become an unhandled exception on this async void handler,
        // which would otherwise crash the whole app and take every other open workspace down with it.
        try
        {
            await tab.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fully dispose workspace tab {WorkspacePath} while closing it", tab.Workspace.FullPath);
        }
    }

    public async Task ShutdownAsync()
    {
        await _workspaceService.SaveOpenWorkspacesAsync(Tabs.Select(t => t.Workspace.FullPath).ToList());

        foreach (var tab in Tabs.ToList())
        {
            try
            {
                await tab.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fully dispose workspace tab {WorkspacePath} during shutdown", tab.Workspace.FullPath);
            }
        }
    }
}
