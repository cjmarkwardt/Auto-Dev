using Avalonia.Controls;
using AutoDev.Core.Models;
using AutoDev.ViewModels.Content;
using AutoDev.ViewModels.Sidebar;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoDev.ViewModels;

public sealed partial class WorkspaceViewModel : ViewModelBase, IAsyncDisposable
{
    public WorkspaceViewModel(WorkspaceInfo workspace, VersionSectionViewModel version, FilesSectionViewModel files, WorkspaceContentViewModel content, FileSearchViewModel fileSearch)
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
        Files.ScriptOutputRequested += script =>
        {
            Content.Script.SelectScript(script.Path, script.Name);
            Content.SelectedTabIndex = WorkspaceContentViewModel.ScriptTabIndex;
        };
        Files.SetCommandContextRequested += path => Content.Command.SetWorkingDirectory(path);
        FileSearch.FileChosen += path => Files.SelectPath(path); // also opens it in the Edit tab, via Files.FileSelected above
        // Deliberately bypasses Files.SelectPath (which would also raise FileSelected -> Content.OpenFileAsync(path)
        // without the line, racing/clobbering the seek) - a content-search open just opens+seeks directly. The
        // Content.Edit.CurrentFilePath subscription below still selects it in Files once the open actually lands.
        FileSearch.ContentResultChosen += (path, line) => _ = Content.OpenFileAsync(path, line);

        // The one place every kind of file open funnels through regardless of how it got there - a tree
        // click, either F1 quick-open mode, a markdown link, Edit's own Alt+Left/Alt+Right history navigation,
        // ... - so this alone is what makes "whenever a file is opened it becomes selected in Files" true in
        // general, without every individual open path above needing its own explicit selection call (most of
        // which either can't cheaply know the workspace-relative tree node, or would otherwise race the open
        // itself - see FilesSectionViewModel.HighlightPath's own doc comment for why this never re-opens the
        // file it's merely following). Skipped for null (Edit showing a diff or nothing at all - see
        // EditTabViewModel.LoadDiffAsync, which leaves CurrentFilePath untouched at null).
        Content.Edit.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditTabViewModel.CurrentFilePath) && Content.Edit.CurrentFilePath is { } openedPath)
            {
                Files.HighlightPath(openedPath);
            }
        };
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
            if (e.PropertyName == nameof(FilesSectionViewModel.HasRunningScripts))
            {
                Content.Generate.HasRunningScripts = Files.HasRunningScripts;

                // Manual editing, script running, and AI working are meant to be mutually exclusive states
                // over the same working tree - a running script locks Edit exactly like a busy version action
                // or an in-flight AI turn already does (see the IsInteractionBlocked handler below).
                Content.ApplyHasRunningScriptsState(Files.HasRunningScripts);

                // Also locks Commit/Merge/etc. and every History tab action (Version.IsInteractionBlocked folds
                // this in), so a running script can't race a git action mutating the same working tree either.
                Version.HasRunningScripts = Files.HasRunningScripts;
            }
        };

        // A merge-conflict-resolution turn (Rebase, Merge Into Current/Rebase Current Onto This, or a
        // stash-pop conflict from PullWithStashIfNeededAsync) is otherwise easy to miss entirely - IsAiWorking
        // locks the workspace with no other visible cue that Generate is where it's actually happening.
        Version.SwitchToGenerateRequested += () => Content.SelectedTabIndex = WorkspaceContentViewModel.GenerateTabIndex;

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

    /// <summary>Sidebar column width, bound two-way from WorkspaceView.axaml's ColumnDefinition.</summary>
    [ObservableProperty]
    private GridLength _sidebarWidth = new(260);

    /// <summary>True from creation until InitializeAsync finishes - the View covers everything with a loading screen while this is true, since the sidebar/content would otherwise render prematurely empty (no repo state yet, etc.).</summary>
    [ObservableProperty]
    private bool _isLoading = true;

    /// <summary>Called by MainShellViewModel when the app's single open workspace becomes (or stops being) active - see FilesSectionViewModel.SetActive/VersionSectionViewModel.SetActive, the two owners of this workspace's own purely-reactive background services (file watcher, periodic remote sync). AI work, an in-flight manual git action, and a running .cs script are all deliberately untouched by this - see those methods' own doc comments for why.</summary>
    public void SetActive(bool active)
    {
        Files.SetActive(active);
        Version.SetActive(active);
    }

    /// <summary>Applies a template to this workspace - see the title bar's Templates popup and VersionSectionViewModel.ApplyTemplateAsync.</summary>
    public Task ApplyTemplateAsync(string templateName, string templateContent) => Version.ApplyTemplateAsync(templateName, templateContent);

    public async Task InitializeAsync()
    {
        try
        {
            await Version.EnsureRepoAsync();

            // History is the tab shown by default (see WorkspaceContentViewModel.SelectedTabIndex) - its own
            // per-tab-open auto-refresh (see HistoryTabViewModel.RefreshFromRemoteAsync) otherwise only fires
            // from WorkspaceContentViewModel.OnSelectedTabIndexChanged, which never runs for this unchanged
            // initial selection, so the very first view of a freshly opened workspace needs this explicit
            // call to get the same "fetched, and pulled if the tree is clean" treatment as switching back to
            // History later does.
            await Content.History.RefreshFromRemoteAsync();
            await Content.Script.LoadAsync();

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

    public async ValueTask DisposeAsync()
    {
        await Content.Edit.FlushPendingSaveAsync();
        await Content.DisposeAsync();
        Files.Dispose();
        Version.Dispose();
    }
}
