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
    private const string GitIgnoreDirective = "$gitignore";

    private readonly string _rootPath;
    private readonly IFileTreeService _fileTreeService;
    private readonly IWorkspaceFileWatcher _watcher;
    private readonly IDialogService _dialogService;
    private readonly IUiDispatcher _dispatcher;
    private readonly IExternalOpenService _externalOpenService;
    private readonly IClipboardService _clipboardService;
    private readonly IWorkspaceTaskScheduler _scheduler;
    private readonly IWorkspaceVersioningService _versioningService;
    private readonly EditTabViewModel _edit;

    /// <summary>Workspace-relative paths (see RelativePathOf) of every .task file currently running - maintained from the scheduler's events and re-applied to nodes after every Refresh() (which can recreate node instances). See ApplyRunningState.</summary>
    private readonly HashSet<string> _runningTaskPaths = [];

    /// <summary>Null while no .fileignore exists at the workspace root, in which case every node's FileIgnoreOverride is also left null (falling back to its own git Status.Ignored) - see ReloadFileIgnore/ResolveFileIgnore.</summary>
    private FileIgnoreMatcher? _fileIgnoreMatcher;

    /// <summary>
    /// True for the whole duration of a Generate turn, OR any plain (non-AI) version action
    /// (Merge/Publish/Iterate/Update/a History switch/etc.) running its own git commands.
    /// Browsing/selecting/opening files stays available throughout (see CanMutate) - only actions that change
    /// the file tree (create, rename, delete) are blocked, so the user can't race the AI's own in-progress
    /// edits, or a checkout swapping the working tree out from under a rename/delete. Mirrors
    /// VersionSectionViewModel.IsInteractionBlocked.
    /// </summary>
    [ObservableProperty]
    private bool _isInteractionBlocked;

    /// <summary>True while any .task file in this workspace has a run in flight - mirrors _runningTaskPaths.Count > 0, kept in sync from OnTaskRunStarted/OnTaskRunCompleted. Forwarded to GenerateTabViewModel.HasRunningTasks by WorkspaceTabViewModel, since AI work should only ever start while nothing else is running against the same working tree.</summary>
    [ObservableProperty]
    private bool _hasRunningTasks;

    /// <summary>
    /// Whether a branch is currently targeted - set via ApplyTargetState. Creating a new file/folder is only
    /// meaningful then; a detached tag/commit target is a read-only historical snapshot with nowhere to
    /// commit a new file to. Existing files stay renamable/deletable regardless (see CanMutateNode) - only
    /// creation is gated by this.
    /// </summary>
    private bool _isEditableTarget;

    partial void OnIsInteractionBlockedChanged(bool value)
    {
        NewFileCommand.NotifyCanExecuteChanged();
        NewFolderCommand.NotifyCanExecuteChanged();
        NewFileInFolderCommand.NotifyCanExecuteChanged();
        NewFolderInFolderCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        RunTaskCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Mirrors OnIsInteractionBlockedChanged - a running task blocks tree mutations exactly like a busy version action or AI turn does (manual editing, task running, and AI working are meant to be mutually exclusive), and blocks starting a second task on top of it (see CanRunTask).</summary>
    partial void OnHasRunningTasksChanged(bool value)
    {
        NewFileCommand.NotifyCanExecuteChanged();
        NewFolderCommand.NotifyCanExecuteChanged();
        NewFileInFolderCommand.NotifyCanExecuteChanged();
        NewFolderInFolderCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        RunTaskCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Called by WorkspaceTabViewModel whenever the targeted version/release/feature (or direct mode) changes.</summary>
    public void ApplyTargetState(bool isEditableTarget)
    {
        _isEditableTarget = isEditableTarget;
        NewFileCommand.NotifyCanExecuteChanged();
        NewFolderCommand.NotifyCanExecuteChanged();
        NewFileInFolderCommand.NotifyCanExecuteChanged();
        NewFolderInFolderCommand.NotifyCanExecuteChanged();
    }

    private bool CanMutate() => !IsInteractionBlocked && !HasRunningTasks && !IsChangesMode && _isEditableTarget;

    private bool CanMutateInFolder(FileTreeNodeViewModel? node) => !IsInteractionBlocked && !HasRunningTasks && !IsChangesMode && _isEditableTarget;

    private bool CanMutateNode(FileTreeNodeViewModel? node) => !IsInteractionBlocked && !HasRunningTasks && !IsChangesMode;

    public FilesSectionViewModel(
        string rootPath,
        IFileTreeService fileTreeService,
        IWorkspaceFileWatcherFactory watcherFactory,
        IDialogService dialogService,
        IUiDispatcher dispatcher,
        IExternalOpenService externalOpenService,
        IClipboardService clipboardService,
        IWorkspaceTaskScheduler scheduler,
        IWorkspaceVersioningService versioningService,
        EditTabViewModel edit)
    {
        _rootPath = rootPath;
        _fileTreeService = fileTreeService;
        _dialogService = dialogService;
        _dispatcher = dispatcher;
        _externalOpenService = externalOpenService;
        _clipboardService = clipboardService;
        _scheduler = scheduler;
        _versioningService = versioningService;
        _edit = edit;
        _watcher = watcherFactory.Create(rootPath);
        _watcher.Changed += OnWatcherChanged;
        _scheduler.TaskRunStarted += OnTaskRunStarted;
        _scheduler.TaskRunCompleted += OnTaskRunCompleted;
        _scheduler.Start();
        ReloadFileIgnore();
        Refresh();
    }

    /// <summary>Exposed for FilesSectionView's drop-on-empty-space handler, which needs a target directory when nothing under the pointer resolves to a specific node.</summary>
    public string RootPath => _rootPath;

    public ObservableCollection<FileTreeNodeViewModel> RootNodes { get; } = [];

    /// <summary>The header toggle's own state - defaults to hidden, since gitignored content (build output, dependencies, etc.) is rarely what anyone's looking for in this tree. Purely a view-layer filter (see FilesSectionView.axaml's row IsVisible binding) - never affects Refresh()/RootNodes itself, so toggling it on/off is instant with no re-scan. Disabled entirely (see FilesSectionView.axaml) while IsChangesMode is on, since it has no effect there.</summary>
    [ObservableProperty]
    private bool _showIgnoredFiles;

    /// <summary>
    /// The header toggle's own state for Changes Mode - an entirely separate read-only view of the tree
    /// (ChangedNodes, built from the workspace's current uncommitted changes) shown instead of the normal
    /// RootNodes while on. Mutating actions (New File/Folder, and by extension rename/delete/duplicate/drag -
    /// see CanMutate/CanMutateNode) are disabled the whole time: this mode is for reviewing what changed, not
    /// editing the tree, and a change list built once at toggle-on has no way to stay correct through
    /// mutations made while it's showing.
    /// </summary>
    [ObservableProperty]
    private bool _isChangesMode;

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
        var changes = await _versioningService.GetWorkingTreeChangesAsync();
        if (!IsChangesMode)
        {
            return; // toggled off again while this was in flight
        }

        ApplyChangedNodes(changes);
    }

    /// <summary>
    /// Called by WorkspaceTabViewModel after any version-control action (commit, reset, squash, merge,
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

        var changes = await _versioningService.GetWorkingTreeChangesAsync();
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
        foreach (var node in ChangeTreeNode.Build(changes, commitHash: null))
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

        var diff = await _versioningService.GetWorkingTreeFileDiffAsync(path);
        await _edit.LoadDiffAsync(Path.GetFileName(path), diff);
        _edit.RequestFocus();
    }

    [ObservableProperty]
    private FileTreeNodeViewModel? _selectedNode;

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

    /// <summary>Raised when a .task file's Run or View is picked - the containing workspace tab activates the Output tab and switches its dropdown to this task.</summary>
    public event Action<(string Path, string Name)>? TaskOutputRequested;

    /// <summary>Set by WorkspaceTabViewModel - flushes the Edit tab's debounced autosave before a run actually starts, so Run always uses whatever's currently shown there instead of a stale on-disk copy still mid-debounce.</summary>
    public Func<Task>? FlushPendingEditBeforeRun { get; set; }

    partial void OnSelectedNodeChanged(FileTreeNodeViewModel? value)
    {
        if (value is { IsDirectory: false })
        {
            FileSelected?.Invoke(value.FullPath);
        }
    }

    public void Refresh()
    {
        var entries = _fileTreeService.GetChildren(_rootPath);
        var entryPaths = entries.Select(e => e.FullPath).ToHashSet();

        for (var i = RootNodes.Count - 1; i >= 0; i--)
        {
            if (!entryPaths.Contains(RootNodes[i].FullPath))
            {
                RootNodes.RemoveAt(i);
            }
        }

        var existingPaths = RootNodes.Select(n => n.FullPath).ToHashSet();
        var insertIndex = 0;
        foreach (var entry in entries)
        {
            if (!existingPaths.Contains(entry.FullPath))
            {
                RootNodes.Insert(Math.Min(insertIndex, RootNodes.Count), new FileTreeNodeViewModel(entry, _fileTreeService, ResolveFileIgnore));
            }

            insertIndex++;
        }

        foreach (var node in RootNodes.Where(n => n.IsDirectory))
        {
            node.RefreshChildren();
        }

        ReapplyRunningStates();
    }

    /// <summary>Supplied to every FileTreeNodeViewModel at construction (see FileTreeNodeViewModel._resolveFileIgnore) - a closure rather than a one-off computed value so it keeps reflecting whatever _fileIgnoreMatcher is *current* whenever it's actually called, including long after the node itself was built.</summary>
    private bool? ResolveFileIgnore(FileTreeNodeViewModel node) =>
        _fileIgnoreMatcher?.IsMatch(RelativePathOf(node), node.IsDirectory);

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
        var fileIgnorePath = Path.Combine(_rootPath, FileIgnoreFileName);
        if (!File.Exists(fileIgnorePath))
        {
            _fileIgnoreMatcher = null;
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
        foreach (var line in lines)
        {
            if (line.Trim() != GitIgnoreDirective)
            {
                expanded.Add(line);
                continue;
            }

            var gitIgnorePath = Path.Combine(_rootPath, GitIgnoreFileName);
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

        _fileIgnoreMatcher = FileIgnoreMatcher.Parse(expanded);
    }


    /// <summary>Re-stamps IsTaskRunning on whatever node currently represents each still-running task path - Refresh() can recreate node instances (SyncChildren), so a running task's freshly-inserted node would otherwise default back to not-running.</summary>
    private void ReapplyRunningStates()
    {
        foreach (var path in _runningTaskPaths)
        {
            ApplyRunningState(RootNodes, path, running: true);
        }
    }

    private void ApplyRunningState(IEnumerable<FileTreeNodeViewModel> nodes, string taskPath, bool running)
    {
        foreach (var node in nodes)
        {
            if (node.IsTaskFile && RelativePathOf(node) == taskPath)
            {
                node.IsTaskRunning = running;
            }

            if (node.IsDirectory)
            {
                ApplyRunningState(node.Children, taskPath, running);
            }
        }
    }

    private string RelativePathOf(FileTreeNodeViewModel node) => Path.GetRelativePath(_rootPath, node.FullPath).Replace('\\', '/');

    /// <summary>Expands ancestor folders as needed and selects the node for an absolute path - used by F2 quick-open.</summary>
    public void SelectPath(string fullPath)
    {
        var relative = Path.GetRelativePath(_rootPath, fullPath);
        var segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        var currentLevel = RootNodes;
        FileTreeNodeViewModel? node = null;
        foreach (var segment in segments)
        {
            node = currentLevel.FirstOrDefault(n => n.Name == segment);
            if (node is null)
            {
                return;
            }

            if (node.IsDirectory)
            {
                node.IsExpanded = true; // synchronously loads Children
                currentLevel = node.Children;
            }
        }

        if (node is { IsDirectory: false })
        {
            SelectedNode = node;
        }
    }

    /// <summary>The FILES heading's own "New File"/"New Folder" always target the workspace root, regardless of whatever's currently selected in the tree - the per-node context menu (NewFileInFolderAsync/NewFolderInFolderAsync below) is the way to create inside a specific folder instead.</summary>
    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task NewFileAsync()
    {
        var name = await _dialogService.ShowInputDialogAsync("New File", "File name", "untitled.txt");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _fileTreeService.CreateFile(_rootPath, name);
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task NewFolderAsync()
    {
        var name = await _dialogService.ShowInputDialogAsync("New Folder", "Folder name", "New Folder");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _fileTreeService.CreateFolder(_rootPath, name);
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanMutateInFolder))]
    private async Task NewFileInFolderAsync(FileTreeNodeViewModel node)
    {
        var name = await _dialogService.ShowInputDialogAsync("New File", "File name", "untitled.txt");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _fileTreeService.CreateFile(node.FullPath, name);
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanMutateInFolder))]
    private async Task NewFolderInFolderAsync(FileTreeNodeViewModel node)
    {
        var name = await _dialogService.ShowInputDialogAsync("New Folder", "Folder name", "New Folder");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _fileTreeService.CreateFolder(node.FullPath, name);
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanMutateNode))]
    private async Task RenameAsync(FileTreeNodeViewModel node)
    {
        var newName = await _dialogService.ShowInputDialogAsync("Rename", "New name", node.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == node.Name)
        {
            return;
        }

        _fileTreeService.Rename(node.FullPath, newName);
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanMutateNode))]
    private void Duplicate(FileTreeNodeViewModel node)
    {
        _fileTreeService.Duplicate(node.FullPath, node.IsDirectory);
        Refresh();
    }

    /// <summary>The FILES heading's own "Open" always targets the workspace root - the per-node context menu (OpenFolder below) is the way to open a specific folder instead. Non-mutating (just launches the OS file manager), so unlike New File/Folder it's never gated on CanMutate.</summary>
    [RelayCommand]
    private void OpenInFileManager() => _externalOpenService.OpenFolder(_rootPath);

    /// <summary>Copies the workspace root's own absolute filesystem path to the clipboard - the header-level counterpart to a node's own "Copy Path" context menu item (CopyPath below). Non-mutating, so unlike New File/Folder it's never gated on CanMutate.</summary>
    [RelayCommand]
    private async Task CopyRootPath() => await _clipboardService.SetTextAsync(_rootPath);

    /// <summary>Used both for a folder's own "Open" (opens itself) and a file's "Open Folder" (opens its containing folder) - see the two separate, differently-labeled context menu items bound to this same command.</summary>
    [RelayCommand]
    private void OpenFolderInFileManager(FileTreeNodeViewModel node) =>
        _externalOpenService.OpenFolder(node.IsDirectory ? node.FullPath : Path.GetDirectoryName(node.FullPath) ?? _rootPath);

    /// <summary>Raised by a folder's "Set Command Context" context menu item - wired in WorkspaceTabViewModel to CommandTabViewModel.SetWorkingDirectory, pointing the Command tab's working directory at that folder. Non-mutating (just view state elsewhere), so unlike New File/Folder it's never gated on CanMutate.</summary>
    public event Action<string>? SetCommandContextRequested;

    [RelayCommand]
    private void SetCommandContext(FileTreeNodeViewModel node) => SetCommandContextRequested?.Invoke(node.FullPath);

    /// <summary>Copies the node's absolute filesystem path to the clipboard - non-mutating, so unlike New File/Folder it's never gated on CanMutate.</summary>
    [RelayCommand]
    private async Task CopyPath(FileTreeNodeViewModel node) => await _clipboardService.SetTextAsync(node.FullPath);

    /// <summary>Collapses every expanded folder in the tree back to the root level - non-mutating (just view state), so unlike New File/Folder it's never gated on CanMutate. Collapses both trees unconditionally rather than gating on IsChangesMode: whichever one isn't currently visible is either empty (ChangedNodes, outside Changes Mode) or about to be rebuilt fresh next time Changes Mode loads anyway, so there's no visible difference and no need to track which mode was active.</summary>
    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var node in RootNodes.Where(n => n.IsDirectory))
        {
            node.CollapseAll();
        }

        foreach (var node in ChangedNodes.Where(n => n.IsDirectory))
        {
            node.CollapseAll();
        }
    }

    [RelayCommand(CanExecute = nameof(CanMutateNode))]
    private async Task DeleteAsync(FileTreeNodeViewModel node)
    {
        var confirmed = await _dialogService.ShowConfirmDialogAsync("Delete", $"Delete '{node.Name}'? This cannot be undone.");
        if (!confirmed)
        {
            return;
        }

        _fileTreeService.Delete(node.FullPath, node.IsDirectory);
        Refresh();
    }

    /// <summary>
    /// A task can only start while nothing else is already using the working tree: not this same task, not a
    /// different one (only one task total runs at a time per workspace - see IWorkspaceTaskScheduler.RunNowAsync),
    /// and not a busy version action or an in-flight AI turn (IsInteractionBlocked).
    /// </summary>
    private bool CanRunTask(FileTreeNodeViewModel? node) => node is { IsTaskFile: true, IsTaskRunning: false } && !HasRunningTasks && !IsInteractionBlocked;

    private bool CanStopTask(FileTreeNodeViewModel? node) => node is { IsTaskFile: true, IsTaskRunning: true };

    /// <summary>AllowConcurrentExecutions is required: RunTaskCommand is one shared IAsyncRelayCommand instance across every row (bound via CommandParameter), and CommunityToolkit's default only allows one execution of a given async command in flight at a time regardless of parameter - without this, running task B while task A's run was still in flight would silently no-op instead of starting B.</summary>
    [RelayCommand(CanExecute = nameof(CanRunTask), AllowConcurrentExecutions = true)]
    private async Task RunTaskAsync(FileTreeNodeViewModel node)
    {
        var taskPath = RelativePathOf(node);
        var taskName = Path.GetFileNameWithoutExtension(node.Name);
        TaskOutputRequested?.Invoke((taskPath, taskName));

        if (node.IsTaskRunning)
        {
            return; // already running (e.g. started elsewhere) - View was still worth raising above
        }

        // If this task is open in the Edit tab with a debounced autosave still pending, flush it first -
        // otherwise a run started right after typing could read the stale on-disk copy instead of what's
        // actually showing in the editor.
        if (FlushPendingEditBeforeRun is not null)
        {
            await FlushPendingEditBeforeRun();
        }

        await _scheduler.RunNowAsync(new TaskRef(taskPath, taskName));
    }

    [RelayCommand(CanExecute = nameof(CanStopTask))]
    private void StopTask(FileTreeNodeViewModel node) => _scheduler.StopRun(RelativePathOf(node));

    [RelayCommand]
    private void ViewTask(FileTreeNodeViewModel node) => TaskOutputRequested?.Invoke((RelativePathOf(node), Path.GetFileNameWithoutExtension(node.Name)));

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

        foreach (var sourcePath in sourcePaths)
        {
            await MoveOneExternalItemAsync(sourcePath, destinationDirectory);
        }

        Refresh();
    }

    private async Task MoveOneExternalItemAsync(string sourcePath, string destinationDirectory)
    {
        try
        {
            _fileTreeService.Move(sourcePath, destinationDirectory);
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

        var name = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var overwrite = await _dialogService.ShowConfirmDialogAsync("Item Already Exists", $"'{name}' already exists in the destination. Overwrite it?", "Overwrite");
        if (!overwrite)
        {
            return;
        }

        try
        {
            _fileTreeService.Move(sourcePath, destinationDirectory, overwrite: true);
        }
        catch (Exception)
        {
            // best-effort - a second failure (e.g. the file is in use) just leaves this one item unmoved
        }
    }

    private void OnWatcherChanged(IReadOnlySet<string> changedPaths) => _dispatcher.Post(() =>
    {
        // Reloaded before Refresh() (not after) so any brand new FileTreeNodeViewModel it constructs
        // resolves its own FileIgnoreOverride against the up to date ruleset immediately, rather than
        // whatever was current a moment ago.
        var fileIgnoreChanged = changedPaths.Any(p => Path.GetFileName(p) is FileIgnoreFileName or GitIgnoreFileName);
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
            foreach (var node in RootNodes)
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
        _ = RefreshGitStatusAsync();

        // Keeps the Changes Mode tree honest while it's actually showing - a change made elsewhere (another
        // tool, a git command run outside this app) should appear/disappear from it just like it would from
        // `git status` itself.
        if (IsChangesMode)
        {
            _ = LoadChangesModeAsync();
        }
    });

    /// <summary>Re-resolves every already-loaded node's git status (added/modified/ignored/unmodified) - called on any on-disk change at all (see OnWatcherChanged, including a file autosaved from this app's own Edit tab), and by WorkspaceTabViewModel after any version-control action or target switch (commit, squash, merge, checkout, ...), none of which necessarily touch the working tree's own files, so the file watcher alone would otherwise never notice a status that's now stale.</summary>
    public async Task RefreshGitStatusAsync() =>
        await Task.WhenAll(RootNodes.Select(n => n.RefreshGitStatusAsync()));

    private void OnTaskRunStarted(TaskRef task) => _dispatcher.Post(() =>
    {
        _runningTaskPaths.Add(task.Path);
        ApplyRunningState(RootNodes, task.Path, running: true);
        RunTaskCommand.NotifyCanExecuteChanged();
        StopTaskCommand.NotifyCanExecuteChanged();
        HasRunningTasks = _runningTaskPaths.Count > 0;
    });

    private void OnTaskRunCompleted(TaskRunRecord record) => _dispatcher.Post(() =>
    {
        _runningTaskPaths.Remove(record.TaskPath);
        ApplyRunningState(RootNodes, record.TaskPath, running: false);
        RunTaskCommand.NotifyCanExecuteChanged();
        StopTaskCommand.NotifyCanExecuteChanged();
        HasRunningTasks = _runningTaskPaths.Count > 0;
    });

    public void Dispose()
    {
        _watcher.Changed -= OnWatcherChanged;
        _watcher.Dispose();
        _scheduler.TaskRunStarted -= OnTaskRunStarted;
        _scheduler.TaskRunCompleted -= OnTaskRunCompleted;
        _scheduler.Dispose();
    }
}
