using Avalonia.Controls;
using AutoDev.Core.Models;
using AutoDev.ViewModels.Content;
using AutoDev.ViewModels.Sidebar;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoDev.ViewModels;

public sealed partial class WorkspaceTabViewModel : ViewModelBase, IAsyncDisposable
{
    public WorkspaceTabViewModel(WorkspaceInfo workspace, VersionSectionViewModel version, FilesSectionViewModel files, WorkspaceContentViewModel content, FileSearchViewModel fileSearch)
    {
        Workspace = workspace;
        Version = version;
        Files = files;
        Content = content;
        FileSearch = fileSearch;

        Files.FileSelected += path => _ = Content.OpenFileAsync(path);
        // A markdown link's relative-path target may resolve outside the workspace tree entirely (e.g. "../../other-repo/readme.md") -
        // goes straight through Content.OpenFileAsync rather than Files.SelectPath (which assumes a workspace-tree path to highlight).
        Content.Edit.OpenFileRequested += path => _ = Content.OpenFileAsync(path);
        Files.WorkspaceFilesChanged += () => _ = Content.Edit.CheckForExternalChangesAsync();
        Files.TaskOutputRequested += task =>
        {
            Content.Output.SelectTask(task.Path, task.Name);
            Content.SelectedTabIndex = WorkspaceContentViewModel.OutputTabIndex;
        };
        Files.SetCommandContextRequested += path => Content.Command.SetWorkingDirectory(path);
        FileSearch.FileChosen += path => Files.SelectPath(path); // also opens it in the Edit tab, via Files.FileSelected above
        // Deliberately bypasses Files.SelectPath (which would also raise FileSelected -> Content.OpenFileAsync(path)
        // without the line, racing/clobbering the seek) - a content-search open just opens+seeks directly, no
        // tree-row highlight.
        FileSearch.ContentResultChosen += (path, line) => _ = Content.OpenFileAsync(path, line);
        Version.FlushPendingEditBeforeMutation = () => Content.Edit.FlushPendingSaveAsync();
        Files.FlushPendingEditBeforeRun = () => Content.Edit.FlushPendingSaveAsync();
        void ApplyEditableState()
        {
            var isEditable = Version.Target?.Kind == GitTargetKind.Branch;
            Files.ApplyTargetState(isEditable);
            _ = Content.ApplyTargetStateAsync(Version.Target);

            // A commit/squash/merge/checkout/etc. can all change which files are Added/Modified/Unmodified
            // without touching the working tree's own files at all (a commit just records what's already
            // there into git) - the file watcher that normally drives Files section refreshes would never see
            // any of that, leaving every row's git-status color stale until something else happened to touch
            // a real file.
            _ = Files.RefreshGitStatusAsync();

            // Same reasoning, for Changes Mode's own tree - also exits Changes Mode entirely once there's
            // nothing pending left to show (e.g. right after a Commit or Reset).
            _ = Files.RefreshChangesModeAsync();
        }

        Files.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FilesSectionViewModel.HasRunningTasks))
            {
                Content.Generate.HasRunningTasks = Files.HasRunningTasks;
            }
        };

        Version.TargetChanged += _ => ApplyEditableState();
        Version.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VersionSectionViewModel.IsInteractionBlocked))
            {
                // Fires for either underlying flag (IsBusy or IsAiWorking) - see VersionSectionViewModel's
                // OnIsBusyChanged/OnIsAiWorkingChanged. Locks Files/Edit and blocks Generate from
                // starting a genuine turn any time a version action (AI-driven or plain git) is already
                // touching the working tree, closing the race either direction.
                Files.IsInteractionBlocked = Version.IsInteractionBlocked;
                Content.ApplyInteractionBlockedState(Version.IsBusy, Version.IsAiWorking);
                Content.Generate.IsVersionActionBusy = Version.IsBusy;
            }
        };
    }

    public WorkspaceInfo Workspace { get; }
    public VersionSectionViewModel Version { get; }
    public FilesSectionViewModel Files { get; }
    public WorkspaceContentViewModel Content { get; }
    public FileSearchViewModel FileSearch { get; }

    public string Title => Workspace.Name;
    public string TooltipPath => Workspace.FullPath;

    /// <summary>Sidebar column width, bound two-way from WorkspaceTabView.axaml's ColumnDefinition - persisted only in-memory for this workspace tab's lifetime, so a GridSplitter drag survives switching to a different open workspace tab and back (the View, not this VM, is torn down/rebuilt on that switch - see WorkspaceContentViewModel.EditColumnWidth's identical reasoning).</summary>
    [ObservableProperty]
    private GridLength _sidebarWidth = new(260);

    /// <summary>True from tab creation until InitializeAsync finishes - the View covers everything with a loading screen while this is true, since the sidebar/content would otherwise render prematurely empty (no repo state yet, etc.). Opening a workspace never blocks the rest of the app: this tab is added and selected immediately, InitializeAsync just runs its own async I/O in the background while every other open tab stays fully interactive.</summary>
    [ObservableProperty]
    private bool _isLoading = true;

    public event Action<WorkspaceTabViewModel>? CloseRequested;

    /// <summary>Raised by MoveLeftCommand/MoveRightCommand (offset -1/+1) - see MainShellViewModel.OnTabMoveRequested, which reorders this tab within its own Tabs collection. A safe no-op there if already at that end of the strip.</summary>
    public event Action<WorkspaceTabViewModel, int>? MoveRequested;

    public async Task InitializeAsync()
    {
        try
        {
            await Version.EnsureRepoAsync();
            await Content.Output.LoadAsync();

            // Selecting (rather than just opening) also highlights it in the Files tree, matching what
            // clicking it there would do - same as FileSearch's own FileChosen -> Files.SelectPath wiring.
            var readmePath = Path.Combine(Workspace.FullPath, "README.md");
            if (File.Exists(readmePath))
            {
                Files.SelectPath(readmePath);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this);

    [RelayCommand]
    private void MoveLeft() => MoveRequested?.Invoke(this, -1);

    [RelayCommand]
    private void MoveRight() => MoveRequested?.Invoke(this, 1);

    public async ValueTask DisposeAsync()
    {
        await Content.Edit.FlushPendingSaveAsync();
        await Content.DisposeAsync();
        Files.Dispose();
        Version.Dispose();
    }
}
