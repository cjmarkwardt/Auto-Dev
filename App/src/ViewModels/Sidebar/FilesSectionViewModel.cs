using System.Collections.ObjectModel;
using AutoDev.Core.Models;
using AutoDev.Core.Services;
using AutoDev.ViewModels.Content;
using AutoDev.ViewModels.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoDev.ViewModels.Sidebar;

public sealed partial class FilesSectionViewModel : ViewModelBase, IDisposable
{
    private const string FileIgnoreFileName = ".fileignore";
    private const string GitIgnoreFileName = ".gitignore";

    /// <summary>A line in .fileignore consisting of exactly this (surrounding whitespace ignored) is replaced with .gitignore's own lines - see ReloadFileIgnore.</summary>
    private static readonly string gitIgnoreDirective = "$gitignore";

    /// <summary>How often OnWatcherChanged's own git status refresh is allowed to actually run - see ScheduleGitStatusRefresh.</summary>
    private static readonly TimeSpan gitStatusRefreshThrottle = TimeSpan.FromSeconds(5);

    private readonly string rootPath;
    private readonly IFileTreeService fileTreeService;
    private readonly IWorkspaceFileWatcher watcher;
    private readonly IDialogService dialogService;
    private readonly IUiDispatcher dispatcher;
    private readonly IExternalOpenService externalOpenService;
    private readonly IClipboardService clipboardService;
    private readonly IWorkspaceScriptRunner scriptRunner;
    private readonly IWorkspaceVersioningService versioningService;
    private readonly EditTabViewModel edit;

    /// <summary>Workspace-relative paths (see RelativePathOf) of every .cs file currently running - maintained from the script runner's events and re-applied to nodes after every Refresh() (which can recreate node instances). See ApplyRunningState.</summary>
    private readonly HashSet<string> runningScriptPaths = [];

    /// <summary>Null while no .fileignore exists at the workspace root, in which case every node's FileIgnoreOverride is also left null (falling back to its own git Status.Ignored) - see ReloadFileIgnore/ResolveFileIgnore.</summary>
    private FileIgnoreMatcher? fileIgnoreMatcher;

    /// <summary>Set for the duration of a pending/in-flight throttled git status refresh (see ScheduleGitStatusRefresh) - cancelled on Dispose so a refresh never runs against a torn-down workspace tab.</summary>
    private CancellationTokenSource? gitStatusRefreshThrottleCts;

    /// <summary>Whether this workspace's own tab is the currently-selected one - see SetActive. Starts true, matching a freshly opened workspace always becoming the selected tab immediately (see WorkspaceFactory/MainShellViewModel.OnWorkspaceOpened).</summary>
    private bool isActive = true;

    /// <summary>
    /// True for the whole duration of a Generate turn, OR any plain (non-AI) version action
    /// (Merge/Publish/Iterate/Update/a History switch/etc.) running its own git commands.
    /// Browsing/selecting/opening files stays available throughout (see CanMutate) - only actions that change
    /// the file tree (create, rename, delete) are blocked, so the user can't race the AI's own in-progress
    /// edits, or a checkout swapping the working tree out from under a rename/delete. Mirrors
    /// VersionSectionViewModel.IsInteractionBlocked.
    /// </summary>
    [ObservableProperty]
    private bool isInteractionBlocked;

    /// <summary>True while any .cs file in this workspace has a run in flight - mirrors _runningScriptPaths.Count > 0, kept in sync from OnScriptRunStarted/OnScriptRunCompleted. Forwarded to GenerateTabViewModel.HasRunningScripts by WorkspaceViewModel, since AI work should only ever start while nothing else is running against the same working tree.</summary>
    [ObservableProperty]
    private bool hasRunningScripts;

    /// <summary>
    /// Whether a branch is currently targeted - set via ApplyTargetState. Creating a new file/folder is only
    /// meaningful then; a detached tag/commit target is a read-only historical snapshot with nowhere to
    /// commit a new file to. Existing files stay renamable/deletable regardless (see CanMutateNode) - only
    /// creation is gated by this.
    /// </summary>
    private bool isEditableTarget;

    partial void OnIsInteractionBlockedChanged(bool value)
    {
        NewFileCommand.NotifyCanExecuteChanged();
        NewFolderCommand.NotifyCanExecuteChanged();
        NewFileInFolderCommand.NotifyCanExecuteChanged();
        NewFolderInFolderCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        RunScriptCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Mirrors OnIsInteractionBlockedChanged - a running script blocks tree mutations exactly like a busy version action or AI turn does (manual editing, script running, and AI working are meant to be mutually exclusive), and blocks starting a second script on top of it (see CanRunScript).</summary>
    partial void OnHasRunningScriptsChanged(bool value)
    {
        NewFileCommand.NotifyCanExecuteChanged();
        NewFolderCommand.NotifyCanExecuteChanged();
        NewFileInFolderCommand.NotifyCanExecuteChanged();
        NewFolderInFolderCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        RunScriptCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Called by WorkspaceViewModel whenever the targeted version/release/feature (or direct mode) changes.</summary>
    public void ApplyTargetState(bool isEditableTarget)
    {
        this.isEditableTarget = isEditableTarget;
        NewFileCommand.NotifyCanExecuteChanged();
        NewFolderCommand.NotifyCanExecuteChanged();
        NewFileInFolderCommand.NotifyCanExecuteChanged();
        NewFolderInFolderCommand.NotifyCanExecuteChanged();
    }

    private bool CanMutate() => !IsInteractionBlocked && !HasRunningScripts && !IsChangesMode && isEditableTarget;

    private bool CanMutateInFolder(FileTreeNodeViewModel? node) => !IsInteractionBlocked && !HasRunningScripts && !IsChangesMode && isEditableTarget;

    private bool CanMutateNode(FileTreeNodeViewModel? node) => !IsInteractionBlocked && !HasRunningScripts && !IsChangesMode;

    public FilesSectionViewModel(
        string rootPath,
        IFileTreeService fileTreeService,
        IWorkspaceFileWatcherFactory watcherFactory,
        IDialogService dialogService,
        IUiDispatcher dispatcher,
        IExternalOpenService externalOpenService,
        IClipboardService clipboardService,
        IWorkspaceScriptRunner scriptRunner,
        IWorkspaceVersioningService versioningService,
        EditTabViewModel edit)
    {
        this.rootPath = rootPath;
        this.fileTreeService = fileTreeService;
        this.dialogService = dialogService;
        this.dispatcher = dispatcher;
        this.externalOpenService = externalOpenService;
        this.clipboardService = clipboardService;
        this.scriptRunner = scriptRunner;
        this.versioningService = versioningService;
        this.edit = edit;
        watcher = watcherFactory.Create(rootPath);
        watcher.Changed += OnWatcherChanged;
        this.scriptRunner.ScriptRunStarted += OnScriptRunStarted;
        this.scriptRunner.ScriptRunCompleted += OnScriptRunCompleted;
        this.scriptRunner.Start();
        ReloadFileIgnore();
        Refresh();
    }

    /// <summary>Exposed for FilesSectionView's drop-on-empty-space handler, which needs a target directory when nothing under the pointer resolves to a specific node.</summary>
    public string RootPath => rootPath;

    public ObservableCollection<FileTreeNodeViewModel> RootNodes { get; } = [];

    /// <summary>The header toggle's own state - defaults to hidden, since gitignored content (build output, dependencies, etc.) is rarely what anyone's looking for in this tree. Purely a view-layer filter (see FilesSectionView.axaml's row IsVisible binding) - never affects Refresh()/RootNodes itself, so toggling it on/off is instant with no re-scan. Disabled entirely (see FilesSectionView.axaml) while IsChangesMode is on, since it has no effect there.</summary>
    [ObservableProperty]
    private bool showIgnoredFiles;

    /// <summary>
    /// The header toggle's own state for Changes Mode - an entirely separate read-only view of the tree
    /// (ChangedNodes, built from the workspace's current uncommitted changes) shown instead of the normal
    /// RootNodes while on. Mutating actions (New File/Folder, and by extension rename/delete/duplicate/drag -
    /// see CanMutate/CanMutateNode) are disabled the whole time: this mode is for reviewing what changed, not
    /// editing the tree, and a change list built once at toggle-on has no way to stay correct through
    /// mutations made while it's showing.
    /// </summary>
    [ObservableProperty]
    private bool isChangesMode;

    /// <summary>Populated only while IsChangesMode is on (see OnIsChangesModeChanged) - lazily loaded/unloaded exactly like a History tab timeline entry's own expanded changes tree, which this reuses the same ChangeTreeNode model as.</summary>
    public ObservableCollection<ChangeTreeNode> ChangedNodes { get; } = [];

    partial void OnIsChangesModeChanged(bool value)
    {
        NewFileCommand.NotifyCanExecuteChanged();
        NewFolderCommand.NotifyCanExecuteChanged();
        NewFileInFolderCommand.NotifyCanExecuteChanged();
        NewFolderInFolderCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();

        if (value)
        {
            _ = LoadChangesModeAsync();
        }
        else
        {
            ChangedNodes.Clear();
        }
    }

    private async Task LoadChangesModeAsync()
    {
        IReadOnlyList<GitChange> changes = await versioningService.GetWorkingTreeChangesAsync();
        if (!IsChangesMode)
        {
            return; // toggled off again while this was in flight
        }

        ApplyChangedNodes(changes);
    }

    /// <summary>
    /// Called by WorkspaceViewModel after any version-control action (commit, reset, squash, merge,
    /// checkout, ...) - none of those necessarily touch the working tree's own files, so the file watcher
    /// alone (see OnWatcherChanged, which only fires for changes it can actually see on disk) would otherwise
    /// leave the Changes Mode tree showing a stale set of changes, or even ones that no longer exist at all.
    /// Unlike LoadChangesModeAsync (used when the user turns Changes Mode on, or an on-disk change is
    /// detected while it's already on), this also turns Changes Mode off entirely once there's nothing left
    /// pending - e.g. right after a Commit or Reset clears everything - since a mode for reviewing changes has
    /// nothing left to review. A no-op unless Changes Mode is actually on.
    /// </summary>
    public async Task RefreshChangesModeAsync()
    {
        if (!IsChangesMode)
        {
            return;
        }

        IReadOnlyList<GitChange> changes = await versioningService.GetWorkingTreeChangesAsync();
        if (!IsChangesMode)
        {
            return; // toggled off while this was in flight
        }

        if (changes.Count == 0)
        {
            IsChangesMode = false; // also clears ChangedNodes - see OnIsChangesModeChanged
            return;
        }

        ApplyChangedNodes(changes);
    }

    private void ApplyChangedNodes(IReadOnlyList<GitChange> changes)
    {
        ChangedNodes.Clear();
        foreach (ChangeTreeNode node in ChangeTreeNode.Build(changes, commitHash: null))
        {
            ChangedNodes.Add(node);
        }
    }

    /// <summary>A file row in the Changes Mode tree - opens that file's HEAD-versus-on-disk content in the Edit tab's read-only Diff mode, exactly like a History tab timeline entry's own expanded changes tree (see HistoryTabViewModel.OpenChangeCommand). A no-op for a folder row (RelativePath is only ever set on a leaf - see ChangeTreeNode.Build).</summary>
    [RelayCommand]
    private async Task OpenChangeAsync(ChangeTreeNode node)
    {
        if (node.RelativePath is not { } path)
        {
            return;
        }

        FileDiffContent diff = await versioningService.GetWorkingTreeFileDiffAsync(path);
        await edit.LoadDiffAsync(Path.GetFileName(path), diff);
        edit.RequestFocus();
    }

    [ObservableProperty]
    private FileTreeNodeViewModel? selectedNode;

    /// <summary>Set around a HighlightPath call's own SelectedNode assignment - see OnSelectedNodeChanged, which checks this to avoid re-raising FileSelected (and so re-opening the file) for a selection change that's purely following an open that already happened some other way.</summary>
    private bool suppressFileSelected;

    public event Action<string>? FileSelected;

    /// <summary>
    /// Always re-activates the file in the Edit tab on click, even if it was already the selected node -
    /// SelectedNode's setter (CommunityToolkit-generated) only invokes OnSelectedNodeChanged on an actual
    /// value change, so clicking an already-selected file wouldn't otherwise raise FileSelected again, and
    /// switching to a different tab then clicking back on it did nothing. See FilesSectionView's
    /// PointerPressed handler, which calls this unconditionally alongside the normal TreeView selection.
    /// </summary>
    public void ActivateFile(string fullPath) => FileSelected?.Invoke(fullPath);

    /// <summary>Raw (non-debounced-per-file) change notification, forwarded so the owning workspace can also check the currently-open Edit tab file for external edits.</summary>
    public event Action? WorkspaceFilesChanged;

    /// <summary>Raised when a .cs file's Run or View is picked - the containing workspace tab activates the Script tab and switches its dropdown to this script.</summary>
    public event Action<(string Path, string Name)>? ScriptOutputRequested;

    /// <summary>Set by WorkspaceViewModel - flushes the Edit tab's debounced autosave before a run actually starts, so Run always uses whatever's currently shown there instead of a stale on-disk copy still mid-debounce.</summary>
    public Func<Task>? FlushPendingEditBeforeRun { get; set; }

    partial void OnSelectedNodeChanged(FileTreeNodeViewModel? value)
    {
        if (value is { IsDirectory: false } && !suppressFileSelected)
        {
            FileSelected?.Invoke(value.FullPath);
        }
    }

    public void Refresh()
    {
        IReadOnlyList<FileSystemEntry> entries = fileTreeService.GetChildren(rootPath);
        HashSet<string> entryPaths = entries.Select(e => e.FullPath).ToHashSet();

        for (int i = RootNodes.Count - 1; i >= 0; i--)
        {
            if (!entryPaths.Contains(RootNodes[i].FullPath))
            {
                RootNodes.RemoveAt(i);
            }
        }

        HashSet<string> existingPaths = RootNodes.Select(n => n.FullPath).ToHashSet();
        int insertIndex = 0;
        foreach (FileSystemEntry entry in entries)
        {
            if (!existingPaths.Contains(entry.FullPath))
            {
                RootNodes.Insert(Math.Min(insertIndex, RootNodes.Count), new FileTreeNodeViewModel(entry, fileTreeService, ResolveFileIgnore));
            }

            insertIndex++;
        }

        foreach (FileTreeNodeViewModel? node in RootNodes.Where(n => n.IsDirectory))
        {
            node.RefreshChildren();
        }

        ReapplyRunningStates();
    }

    /// <summary>
    /// Pauses (Deactivate) or resumes (Activate) every purely-reactive background service this section owns
    /// - the file watcher and its throttled git-status refresh (see ScheduleGitStatusRefresh) - while this
    /// workspace's own tab isn't the one currently selected, so an open-but-backgrounded workspace costs
    /// nothing at idle. AI work, an in-flight manual git action, and a running .cs script are deliberately
    /// NOT paused by this - none of those are driven by anything here (see GenerateTabViewModel's own
    /// timers, VersionSectionViewModel.RunBusyAsync, IWorkspaceScriptRunner, none of which this section
    /// touches), so they keep running to completion regardless of which tab is selected.
    /// </summary>
    public void SetActive(bool active)
    {
        if (active)
        {
            Activate();
        }
        else
        {
            Deactivate();
        }
    }

    private void Activate()
    {
        if (isActive)
        {
            return;
        }

        isActive = true;
        watcher.Resume();

        // Nothing was watched while paused (Resume alone doesn't replay anything missed) - re-resolve
        // everything from scratch rather than assuming nothing changed, exactly like OnWatcherChanged does
        // for a single detected change, just unconditionally rather than only for a .fileignore/.gitignore
        // edit, since any of it could have changed while this wasn't watching.
        ReloadFileIgnore();
        Refresh();
        foreach (FileTreeNodeViewModel node in RootNodes)
        {
            node.RefreshFileIgnoreState();
        }

        _ = RefreshGitStatusAsync();
        _ = RefreshChangesModeAsync();
    }

    private void Deactivate()
    {
        if (!isActive)
        {
            return;
        }

        isActive = false;
        watcher.Pause();
        gitStatusRefreshThrottleCts?.Cancel();
    }

    /// <summary>Supplied to every FileTreeNodeViewModel at construction (see FileTreeNodeViewModel._resolveFileIgnore) - a closure rather than a one-off computed value so it keeps reflecting whatever _fileIgnoreMatcher is *current* whenever it's actually called, including long after the node itself was built.</summary>
    private bool? ResolveFileIgnore(FileTreeNodeViewModel node) =>
        fileIgnoreMatcher?.IsMatch(RelativePathOf(node), node.IsDirectory);

    /// <summary>
    /// Reads .fileignore from the workspace root (if present) into _fileIgnoreMatcher, expanding any line
    /// that's exactly "$gitignore" into .gitignore's own lines first - called once at construction and again
    /// whenever either file changes (see OnWatcherChanged/RefreshFileIgnore). Leaves _fileIgnoreMatcher null
    /// (every node falls back to its own git Status.Ignored - see ResolveFileIgnore) when .fileignore doesn't
    /// exist at all;
    /// an empty or unreadable .fileignore still counts as present (ignores nothing, but takes over from
    /// .gitignore entirely) except for a transient read failure, which leaves the previous ruleset in place
    /// rather than guessing.
    /// </summary>
    private void ReloadFileIgnore()
    {
        string fileIgnorePath = Path.Combine(rootPath, FileIgnoreFileName);
        if (!File.Exists(fileIgnorePath))
        {
            fileIgnoreMatcher = null;
            return;
        }

        IReadOnlyList<string> lines;
        try
        {
            lines = File.ReadAllLines(fileIgnorePath);
        }
        catch
        {
            return; // best-effort - a transient read failure leaves the previous ruleset (if any) in place
        }

        List<string> expanded = [];
        foreach (string line in lines)
        {
            if (line.Trim() != gitIgnoreDirective)
            {
                expanded.Add(line);
                continue;
            }

            string gitIgnorePath = Path.Combine(rootPath, GitIgnoreFileName);
            if (!File.Exists(gitIgnorePath))
            {
                continue;
            }

            try
            {
                expanded.AddRange(File.ReadAllLines(gitIgnorePath));
            }
            catch
            {
                // best-effort - a transient read failure just skips the merge this time
            }
        }

        fileIgnoreMatcher = FileIgnoreMatcher.Parse(expanded);
    }


    /// <summary>Re-stamps IsScriptRunning on whatever node currently represents each still-running script path - Refresh() can recreate node instances (SyncChildren), so a running script's freshly-inserted node would otherwise default back to not-running.</summary>
    private void ReapplyRunningStates()
    {
        foreach (string path in runningScriptPaths)
        {
            ApplyRunningState(RootNodes, path, running: true);
        }
    }

    private void ApplyRunningState(IEnumerable<FileTreeNodeViewModel> nodes, string scriptPath, bool running)
    {
        foreach (FileTreeNodeViewModel node in nodes)
        {
            if (node.IsScriptFile && RelativePathOf(node) == scriptPath)
            {
                node.IsScriptRunning = running;
            }

            if (node.IsDirectory)
            {
                ApplyRunningState(node.Children, scriptPath, running);
            }
        }
    }

    private string RelativePathOf(FileTreeNodeViewModel node) => Path.GetRelativePath(rootPath, node.FullPath).Replace('\\', '/');

    /// <summary>Expands ancestor folders as needed and selects the node for an absolute path - used by F2 quick-open (filename mode, where this is also what opens the file, via FileSelected below). A no-op if fullPath doesn't resolve to a loaded node under this workspace (e.g. it's outside the tree entirely, or under a folder never expanded).</summary>
    public void SelectPath(string fullPath)
    {
        if (FindNode(fullPath) is { } node)
        {
            SelectedNode = node;
        }
    }

    /// <summary>
    /// Same lookup/expand/select as SelectPath, but never re-raises FileSelected - for a caller that's
    /// opening the file itself (already read from disk, possibly seeking to a specific line) and just wants
    /// the tree to visually follow along, since SelectPath's own open (no seek line, and a second concurrent
    /// LoadFileAsync call) would otherwise race/clobber whatever the caller's own open is doing. This is what
    /// actually makes "whenever a file is opened it becomes selected in Files" true in general - see
    /// WorkspaceViewModel's subscription to EditTabViewModel.CurrentFilePath, the one place every kind of
    /// file open (a tree click, F1 quick-open in either mode, a markdown link, Edit's own Alt+Left/Alt+Right
    /// history navigation, ...) already funnels through regardless of how it got there.
    /// </summary>
    public void HighlightPath(string fullPath)
    {
        if (FindNode(fullPath) is not { } node)
        {
            return;
        }

        suppressFileSelected = true;
        try
        {
            SelectedNode = node;
        }
        finally
        {
            suppressFileSelected = false;
        }
    }

    /// <summary>Expands ancestor folders as needed and returns the leaf (file, never a folder) node for an absolute path - shared lookup behind SelectPath/HighlightPath. Null if any path segment doesn't resolve under this workspace's currently-loaded tree (outside the workspace entirely, or nested under a folder never expanded).</summary>
    private FileTreeNodeViewModel? FindNode(string fullPath)
    {
        string relative = Path.GetRelativePath(rootPath, fullPath);
        string[] segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        ObservableCollection<FileTreeNodeViewModel> currentLevel = RootNodes;
        FileTreeNodeViewModel? node = null;
        foreach (string segment in segments)
        {
            node = currentLevel.FirstOrDefault(n => n.Name == segment);
            if (node is null)
            {
                return null;
            }

            if (node.IsDirectory)
            {
                node.IsExpanded = true; // synchronously loads Children
                currentLevel = node.Children;
            }
        }

        return node is { IsDirectory: false } ? node : null;
    }

    /// <summary>The FILES heading's own "New File"/"New Folder" always target the workspace root, regardless of whatever's currently selected in the tree - the per-node context menu (NewFileInFolderAsync/NewFolderInFolderAsync below) is the way to create inside a specific folder instead.</summary>
    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task NewFileAsync()
    {
        string? name = await dialogService.ShowInputDialogAsync("New File", "File name", "untitled.txt");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        fileTreeService.CreateFile(rootPath, name);
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task NewFolderAsync()
    {
        string? name = await dialogService.ShowInputDialogAsync("New Folder", "Folder name", "New Folder");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        fileTreeService.CreateFolder(rootPath, name);
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanMutateInFolder))]
    private async Task NewFileInFolderAsync(FileTreeNodeViewModel node)
    {
        string? name = await dialogService.ShowInputDialogAsync("New File", "File name", "untitled.txt");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        fileTreeService.CreateFile(node.FullPath, name);
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanMutateInFolder))]
    private async Task NewFolderInFolderAsync(FileTreeNodeViewModel node)
    {
        string? name = await dialogService.ShowInputDialogAsync("New Folder", "Folder name", "New Folder");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        fileTreeService.CreateFolder(node.FullPath, name);
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanMutateNode))]
    private async Task RenameAsync(FileTreeNodeViewModel node)
    {
        string? newName = await dialogService.ShowInputDialogAsync("Rename", "New name", node.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == node.Name)
        {
            return;
        }

        fileTreeService.Rename(node.FullPath, newName);
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanMutateNode))]
    private void Duplicate(FileTreeNodeViewModel node)
    {
        fileTreeService.Duplicate(node.FullPath, node.IsDirectory);
        Refresh();
    }

    /// <summary>The FILES heading's own "Open" always targets the workspace root - the per-node context menu (OpenFolder below) is the way to open a specific folder instead. Non-mutating (just launches the OS file manager), so unlike New File/Folder it's never gated on CanMutate.</summary>
    [RelayCommand]
    private void OpenInFileManager() => externalOpenService.OpenFolder(rootPath);

    /// <summary>Copies the workspace root's own absolute filesystem path to the clipboard - the header-level counterpart to a node's own "Copy Path" context menu item (CopyPath below). Non-mutating, so unlike New File/Folder it's never gated on CanMutate.</summary>
    [RelayCommand]
    private async Task CopyRootPath() => await clipboardService.SetTextAsync(rootPath);

    /// <summary>Used both for a folder's own "Open" (opens itself) and a file's "Open Folder" (opens its containing folder) - see the two separate, differently-labeled context menu items bound to this same command.</summary>
    [RelayCommand]
    private void OpenFolderInFileManager(FileTreeNodeViewModel node) =>
        externalOpenService.OpenFolder(node.IsDirectory ? node.FullPath : Path.GetDirectoryName(node.FullPath) ?? rootPath);

    /// <summary>Raised by a folder's "Set Command Context" context menu item - wired in WorkspaceViewModel to CommandTabViewModel.SetWorkingDirectory, pointing the Command tab's working directory at that folder. Non-mutating (just view state elsewhere), so unlike New File/Folder it's never gated on CanMutate.</summary>
    public event Action<string>? SetCommandContextRequested;

    [RelayCommand]
    private void SetCommandContext(FileTreeNodeViewModel node) => SetCommandContextRequested?.Invoke(node.FullPath);

    /// <summary>Copies the node's absolute filesystem path to the clipboard - non-mutating, so unlike New File/Folder it's never gated on CanMutate.</summary>
    [RelayCommand]
    private async Task CopyPath(FileTreeNodeViewModel node) => await clipboardService.SetTextAsync(node.FullPath);

    /// <summary>Collapses every expanded folder in the tree back to the root level - non-mutating (just view state), so unlike New File/Folder it's never gated on CanMutate. Collapses both trees unconditionally rather than gating on IsChangesMode: whichever one isn't currently visible is either empty (ChangedNodes, outside Changes Mode) or about to be rebuilt fresh next time Changes Mode loads anyway, so there's no visible difference and no need to track which mode was active.</summary>
    [RelayCommand]
    private void CollapseAll()
    {
        foreach (FileTreeNodeViewModel? node in RootNodes.Where(n => n.IsDirectory))
        {
            node.CollapseAll();
        }

        foreach (ChangeTreeNode? node in ChangedNodes.Where(n => n.IsDirectory))
        {
            node.CollapseAll();
        }
    }

    [RelayCommand(CanExecute = nameof(CanMutateNode))]
    private async Task DeleteAsync(FileTreeNodeViewModel node)
    {
        bool confirmed = await dialogService.ShowConfirmDialogAsync("Delete", $"Delete '{node.Name}'? This cannot be undone.");
        if (!confirmed)
        {
            return;
        }

        fileTreeService.Delete(node.FullPath, node.IsDirectory);
        Refresh();
    }

    /// <summary>
    /// A script can only start while nothing else is already using the working tree: not this same script, not
    /// a different one (only one script total runs at a time per workspace - see IWorkspaceScriptRunner.RunNowAsync),
    /// and not a busy version action or an in-flight AI turn (IsInteractionBlocked).
    /// </summary>
    private bool CanRunScript(FileTreeNodeViewModel? node) => node is { IsScriptFile: true, IsScriptRunning: false } && !HasRunningScripts && !IsInteractionBlocked;

    private bool CanStopScript(FileTreeNodeViewModel? node) => node is { IsScriptFile: true, IsScriptRunning: true };

    /// <summary>AllowConcurrentExecutions is required: RunScriptCommand is one shared IAsyncRelayCommand instance across every row (bound via CommandParameter), and CommunityToolkit's default only allows one execution of a given async command in flight at a time regardless of parameter - without this, running script B while script A's run was still in flight would silently no-op instead of starting B.</summary>
    [RelayCommand(CanExecute = nameof(CanRunScript), AllowConcurrentExecutions = true)]
    private async Task RunScriptAsync(FileTreeNodeViewModel node)
    {
        string scriptPath = RelativePathOf(node);
        string scriptName = Path.GetFileNameWithoutExtension(node.Name);
        ScriptOutputRequested?.Invoke((scriptPath, scriptName));

        if (node.IsScriptRunning)
        {
            return; // already running (e.g. started elsewhere) - View was still worth raising above
        }

        // If this script is open in the Edit tab with a debounced autosave still pending, flush it first -
        // otherwise a run started right after typing could read the stale on-disk copy instead of what's
        // actually showing in the editor.
        if (FlushPendingEditBeforeRun is not null)
        {
            await FlushPendingEditBeforeRun();
        }

        await scriptRunner.RunNowAsync(new ScriptRef(scriptPath, scriptName));
    }

    [RelayCommand(CanExecute = nameof(CanStopScript))]
    private void StopScript(FileTreeNodeViewModel node) => scriptRunner.StopRun(RelativePathOf(node));

    [RelayCommand]
    private void ViewScript(FileTreeNodeViewModel node) => ScriptOutputRequested?.Invoke((RelativePathOf(node), Path.GetFileNameWithoutExtension(node.Name)));

    /// <summary>
    /// Moves file/folder paths dragged in from outside the app (e.g. the OS's own file manager - see
    /// FilesSectionView.axaml.cs's Drop handler) into destinationDirectory, one at a time so a single
    /// collision doesn't abort the rest of the batch. Gated the same as any other tree mutation (CanMutate) -
    /// checked once up front since there's no single node to hang a [RelayCommand]'s CanExecute off of here.
    /// </summary>
    public async Task MoveExternalItemsAsync(IReadOnlyList<string> sourcePaths, string destinationDirectory)
    {
        if (!CanMutate())
        {
            return;
        }

        foreach (string sourcePath in sourcePaths)
        {
            await MoveOneExternalItemAsync(sourcePath, destinationDirectory);
        }

        Refresh();
    }

    private async Task MoveOneExternalItemAsync(string sourcePath, string destinationDirectory)
    {
        try
        {
            fileTreeService.Move(sourcePath, destinationDirectory);
            return;
        }
        catch (IOException)
        {
            // falls through to the overwrite prompt below
        }
        catch (Exception)
        {
            return; // e.g. UnauthorizedAccessException - nothing a retry prompt would fix
        }

        string name = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        bool overwrite = await dialogService.ShowConfirmDialogAsync("Item Already Exists", $"'{name}' already exists in the destination. Overwrite it?", "Overwrite");
        if (!overwrite)
        {
            return;
        }

        try
        {
            fileTreeService.Move(sourcePath, destinationDirectory, overwrite: true);
        }
        catch (Exception)
        {
            // best-effort - a second failure (e.g. the file is in use) just leaves this one item unmoved
        }
    }

    private void OnWatcherChanged(IReadOnlySet<string> changedPaths) => dispatcher.Post(() =>
    {
        // Reloaded before Refresh() (not after) so any brand new FileTreeNodeViewModel it constructs
        // resolves its own FileIgnoreOverride against the up to date ruleset immediately, rather than
        // whatever was current a moment ago.
        bool fileIgnoreChanged = changedPaths.Any(p => Path.GetFileName(p) is FileIgnoreFileName or GitIgnoreFileName);
        if (fileIgnoreChanged)
        {
            ReloadFileIgnore();
        }

        Refresh();
        WorkspaceFilesChanged?.Invoke();

        // .fileignore/.gitignore are both plain, freely-editable files (neither generated) - any edit to
        // either, from this app's Edit tab or externally, can change which paths are ignored anywhere in the
        // tree, so every already-loaded node's own FileIgnoreOverride needs recomputing, not just
        // newly-appeared ones (Refresh() above only resolves it for brand new node instances - see
        // FileTreeNodeViewModel's own constructor).
        if (fileIgnoreChanged)
        {
            foreach (FileTreeNodeViewModel node in RootNodes)
            {
                node.RefreshFileIgnoreState();
            }
        }

        // Any on-disk change at all - a file autosaved from this app's own Edit tab, one written
        // externally, a git command run outside this app, ... - can change that path's own git status
        // (Unmodified -> Modified/Added) and, since folders carry their own aggregate status too, any
        // ancestor folder's along with it. Refresh() above only resolves Status for brand new node
        // instances (see FileTreeNodeViewModel's own constructor) - every already-loaded node needs
        // recomputing too, not just on a .gitignore edit (which used to be the only trigger here).
        ScheduleGitStatusRefresh();

        // Keeps the Changes Mode tree honest while it's actually showing - a change made elsewhere (another
        // tool, a git command run outside this app) should appear/disappear from it just like it would from
        // `git status` itself.
        if (IsChangesMode)
        {
            _ = LoadChangesModeAsync();
        }
    });

    /// <summary>Re-resolves every already-loaded node's git status (added/modified/ignored/unmodified) immediately - called by WorkspaceViewModel after any version-control action or target switch (commit, squash, merge, checkout, ...), which should always be reflected right away, not on OnWatcherChanged's own throttle (see ScheduleGitStatusRefresh, the on-disk-change path this same query also backs). One shared IGitService.GetStatusesAsync call resolves every node at once - a separate git subprocess per already-loaded node here (there can easily be hundreds in a large, mostly-expanded tree) would otherwise re-spawn on every call, which is what used to make this the actual source of the "whole tree flickering"-class of bug FileSystemWatcherAdapter's own doc comment warns about.</summary>
    public async Task RefreshGitStatusAsync()
    {
        List<FileTreeNodeViewModel> nodes = RootNodes.SelectMany(n => n.SelfAndLoadedDescendants()).ToList();
        if (nodes.Count == 0)
        {
            return;
        }

        IReadOnlyDictionary<string, GitFileStatus> statuses = await fileTreeService.GetStatusesAsync(rootPath, [.. nodes.Select(n => n.FullPath)]);
        foreach (FileTreeNodeViewModel node in nodes)
        {
            if (statuses.TryGetValue(node.FullPath, out GitFileStatus status))
            {
                node.ApplyStatus(status);
            }
        }
    }

    /// <summary>
    /// Coalesces every on-disk change (see OnWatcherChanged) into at most one RefreshGitStatusAsync run per
    /// GitStatusRefreshThrottle window, rather than re-running it on every single change - matters most for a
    /// workspace with frequent background churn (a running build/dev server, a formatter-on-save loop, ...),
    /// where changes can otherwise arrive far more often than the status colors actually need to catch up.
    /// A change that lands while a refresh is already pending is covered by that same pending run (status is
    /// re-queried live, not replayed from a diff), so it's dropped here rather than queued.
    /// </summary>
    private void ScheduleGitStatusRefresh()
    {
        if (gitStatusRefreshThrottleCts is not null)
        {
            return;
        }

        gitStatusRefreshThrottleCts = new CancellationTokenSource();
        _ = ThrottledRefreshGitStatusAsync(gitStatusRefreshThrottleCts.Token);
    }

    private async Task ThrottledRefreshGitStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(gitStatusRefreshThrottle, cancellationToken);
            await RefreshGitStatusAsync();
        }
        catch (OperationCanceledException)
        {
            // Dispose - a workspace tab closed before the throttle window elapsed.
        }
        finally
        {
            gitStatusRefreshThrottleCts = null;
        }
    }

    private void OnScriptRunStarted(ScriptRef script) => dispatcher.Post(() =>
    {
        runningScriptPaths.Add(script.Path);
        ApplyRunningState(RootNodes, script.Path, running: true);
        RunScriptCommand.NotifyCanExecuteChanged();
        StopScriptCommand.NotifyCanExecuteChanged();
        HasRunningScripts = runningScriptPaths.Count > 0;
    });

    private void OnScriptRunCompleted(ScriptRunRecord record) => dispatcher.Post(() =>
    {
        runningScriptPaths.Remove(record.FilePath);
        ApplyRunningState(RootNodes, record.FilePath, running: false);
        RunScriptCommand.NotifyCanExecuteChanged();
        StopScriptCommand.NotifyCanExecuteChanged();
        HasRunningScripts = runningScriptPaths.Count > 0;
    });

    public void Dispose()
    {
        watcher.Changed -= OnWatcherChanged;
        watcher.Dispose();
        scriptRunner.ScriptRunStarted -= OnScriptRunStarted;
        scriptRunner.ScriptRunCompleted -= OnScriptRunCompleted;
        scriptRunner.Dispose();
        gitStatusRefreshThrottleCts?.Cancel();
    }
}
