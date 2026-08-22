using System.Collections.ObjectModel;
using AutoDev.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoDev.ViewModels.Sidebar;

public sealed partial class FileTreeNodeViewModel : ViewModelBase
{
    private readonly IFileTreeService? _fileTreeService;

    /// <summary>Resolves this node's own FileIgnoreOverride on demand - a closure supplied by FilesSectionViewModel (see its ResolveFileIgnore) rather than a fixed value, since it needs to reflect whatever .fileignore ruleset is *current* at the moment it's called, not just whatever was active when this node happened to be constructed. Threaded through to every child (see SyncChildren) so a folder expanded long after construction still resolves correctly.</summary>
    private readonly Func<FileTreeNodeViewModel, bool?>? _resolveFileIgnore;

    private bool _childrenLoaded;

    /// <summary>This path's own verdict from the current .fileignore ruleset (see _resolveFileIgnore) - null while no .fileignore is active in the workspace, in which case IsIgnored falls back to the git Status below instead.</summary>
    [ObservableProperty]
    private bool? _fileIgnoreOverride;

    partial void OnFileIgnoreOverrideChanged(bool? value) => OnPropertyChanged(nameof(IsIgnored));

    public FileTreeNodeViewModel(FileSystemEntry entry, IFileTreeService fileTreeService, Func<FileTreeNodeViewModel, bool?> resolveFileIgnore)
    {
        _fileTreeService = fileTreeService;
        _resolveFileIgnore = resolveFileIgnore;
        Name = entry.Name;
        FullPath = entry.FullPath;
        IsDirectory = entry.IsDirectory;
        if (!IsDirectory)
        {
            _childrenLoaded = true; // files never have children
        }
        else
        {
            // Seed a placeholder child so the TreeView shows an expander arrow before we know
            // (without eagerly scanning the whole tree) whether this folder actually has contents.
            // LoadChildren() replaces it with the real children the moment this node is expanded.
            Children.Add(new FileTreeNodeViewModel());
        }

        FileIgnoreOverride = resolveFileIgnore(this);
        _ = LoadStatusAsync();
    }

    /// <summary>Placeholder-only constructor - never shown, just makes an unexpanded directory node report that it has children.</summary>
    private FileTreeNodeViewModel()
    {
        Name = "";
        FullPath = "";
        IsDirectory = false;
        _childrenLoaded = true;
        IsPlaceholder = true;
    }

    public string Name { get; private set; }
    public string FullPath { get; private set; }
    public bool IsDirectory { get; }
    public bool IsPlaceholder { get; }

    /// <summary>Whether this is a .task file - shown with TaskFileIconGeometry instead of the plain file icon, and offered Run/Stop/View alongside the normal file context menu. See FilesSectionViewModel.</summary>
    public bool IsTaskFile => !IsDirectory && Path.GetExtension(FullPath).Equals(".task", StringComparison.OrdinalIgnoreCase);

    /// <summary>A file row gets exactly one icon - FolderIconGeometry, TaskFileIconGeometry, or (this) the plain FileIconGeometry - never more than one at once.</summary>
    public bool IsPlainFile => !IsDirectory && !IsTaskFile;

    public ObservableCollection<FileTreeNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Whether this .task file currently has a run in flight - drives the Run/Stop context-menu enablement. Maintained by FilesSectionViewModel from the scheduler's TaskRunStarted/TaskRunCompleted events, re-applied after every Refresh() since nodes get rebuilt.</summary>
    [ObservableProperty]
    private bool _isTaskRunning;

    /// <summary>This path's git status - drives the row's name text color (see GitFileStatus). Resolved asynchronously right after construction (a git subprocess call); also re-resolved on demand whenever .gitignore or the working tree itself changes - see RefreshStatusAsync.</summary>
    [ObservableProperty]
    private GitFileStatus _status = GitFileStatus.Unmodified;

    partial void OnStatusChanged(GitFileStatus value)
    {
        OnPropertyChanged(nameof(IsAdded));
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(IsIgnored));
    }

    public bool IsAdded => Status == GitFileStatus.Added;
    public bool IsModified => Status == GitFileStatus.Modified;

    /// <summary>Drives the Files section's dimming/hide-toggle (see FilesSectionView.axaml) - FileIgnoreOverride (a workspace-wide .fileignore, when present) takes over from the plain git Status.Ignored this used to always mean, entirely replacing it rather than combining with it.</summary>
    public bool IsIgnored => FileIgnoreOverride ?? Status == GitFileStatus.Ignored;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_childrenLoaded)
        {
            LoadChildren();
        }
    }

    private async Task LoadStatusAsync()
    {
        if (_fileTreeService is null)
        {
            return;
        }

        try
        {
            Status = await _fileTreeService.GetStatusAsync(FullPath);
        }
        catch
        {
            // Best-effort - a failed status check (e.g. no git binary) just leaves the row uncolored.
        }
    }

    /// <summary>Re-resolves Status for this node and every already-loaded descendant - called across the whole tree whenever .gitignore, the working tree, or which commit/branch is checked out changes (see FilesSectionViewModel.RefreshGitStatusAsync), since a change anywhere can affect any path. Collapsed folders that were never expanded hold only a placeholder child, so recursion naturally stops there - they'll resolve fresh (already up to date) whenever eventually expanded.</summary>
    public async Task RefreshGitStatusAsync()
    {
        if (IsPlaceholder)
        {
            return;
        }

        await Task.WhenAll([LoadStatusAsync(), .. Children.Select(c => c.RefreshGitStatusAsync())]);
    }

    /// <summary>Re-resolves FileIgnoreOverride for this node and every already-loaded descendant, via the same _resolveFileIgnore closure supplied at construction (so it picks up whatever the *current* .fileignore ruleset is, not whatever it was when each node was built) - called across the whole tree whenever .fileignore or .gitignore change (see FilesSectionViewModel.OnWatcherChanged). Collapsed folders that were never expanded hold only a placeholder child, so recursion naturally stops there - they resolve fresh (already up to date) whenever eventually expanded, same as RefreshGitStatusAsync.</summary>
    public void RefreshFileIgnoreState()
    {
        if (IsPlaceholder || _resolveFileIgnore is null)
        {
            return;
        }

        FileIgnoreOverride = _resolveFileIgnore(this);
        foreach (var child in Children)
        {
            child.RefreshFileIgnoreState();
        }
    }

    /// <summary>Collapses this node and every already-loaded descendant - see FilesSectionViewModel.CollapseAll. A folder never expanded holds only a placeholder child (not itself IsDirectory), so recursion naturally stops there without needing a _childrenLoaded check.</summary>
    public void CollapseAll()
    {
        IsExpanded = false;
        foreach (var child in Children.Where(c => c.IsDirectory))
        {
            child.CollapseAll();
        }
    }

    public void LoadChildren()
    {
        if (!IsDirectory || _fileTreeService is null)
        {
            return;
        }

        _childrenLoaded = true;
        Children.Clear(); // drop the placeholder
        RefreshChildren();
    }

    public void RefreshChildren()
    {
        if (!IsDirectory || !_childrenLoaded || _fileTreeService is null)
        {
            return;
        }

        var entries = _fileTreeService.GetChildren(FullPath);
        SyncChildren(entries);

        foreach (var child in Children.Where(c => c.IsDirectory && c._childrenLoaded))
        {
            child.RefreshChildren();
        }
    }

    private void SyncChildren(IReadOnlyList<FileSystemEntry> entries)
    {
        var entryPaths = entries.Select(e => e.FullPath).ToHashSet();
        for (var i = Children.Count - 1; i >= 0; i--)
        {
            if (!entryPaths.Contains(Children[i].FullPath))
            {
                Children.RemoveAt(i);
            }
        }

        var existingPaths = Children.Select(c => c.FullPath).ToHashSet();
        var insertIndex = 0;
        foreach (var entry in entries)
        {
            if (!existingPaths.Contains(entry.FullPath))
            {
                Children.Insert(Math.Min(insertIndex, Children.Count), new FileTreeNodeViewModel(entry, _fileTreeService!, _resolveFileIgnore!));
            }

            insertIndex++;
        }
    }

    public void RenameTo(string newName, string newFullPath)
    {
        Name = newName;
        FullPath = newFullPath;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(FullPath));
        OnPropertyChanged(nameof(IsTaskFile));
    }
}
