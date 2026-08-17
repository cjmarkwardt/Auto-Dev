using AutoDev.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoDev.ViewModels;

/// <summary>
/// One row in a changed-file tree - either a plain path-segment folder (Status null) or the actual changed
/// file at a leaf (Status set). Shared by two different "flat change list" sources: a specific historical
/// commit (HistoryTabViewModel's expanded timeline entries) and the current uncommitted working tree
/// (FilesSectionViewModel's Changes Mode). Built once when a tree/row expands and thrown away on collapse -
/// never mutated afterward (aside from IsExpanded, purely view state), so a plain List of children is enough
/// (no ObservableCollection needed).
/// </summary>
public sealed partial class ChangeTreeNode : ObservableObject
{
    public required string Name { get; init; }
    public required bool IsDirectory { get; init; }
    public GitChangeStatus? Status { get; init; }

    /// <summary>Only set for a leaf (file) node - the full repo-relative path (e.g. "src/a.txt", not just "a.txt"), everything OpenChangeCommand needs to fetch and diff this specific file's before/after content. Null for a plain folder node.</summary>
    public string? RelativePath { get; init; }

    /// <summary>The specific commit this leaf's change belongs to - null means an uncommitted working-tree change instead (see FilesSectionViewModel's Changes Mode, which diffs HEAD against what's on disk rather than a commit against its parent). Always null for a folder node.</summary>
    public string? CommitHash { get; init; }

    /// <summary>Defaults to expanded - both a Changes Mode tree and a History tab expanded-commit tree are usually small, so starting fully open reads better than requiring a click through every folder. Bound TwoWay from the TreeViewItem, so both a manual toggle and FilesSectionViewModel.CollapseAll (see CollapseAll below) actually stick instead of being immediately overridden by a hardcoded always-expanded style.</summary>
    [ObservableProperty]
    private bool _isExpanded = true;

    public List<ChangeTreeNode> Children { get; } = [];

    /// <summary>Collapses this node and every descendant - see FilesSectionViewModel.CollapseAll.</summary>
    public void CollapseAll()
    {
        IsExpanded = false;
        foreach (var child in Children)
        {
            child.CollapseAll();
        }
    }

    /// <summary>Groups a flat changed-file list into a folder tree by path segment, sorted folders-first then alphabetically at every level - mirrors how the Files sidebar presents a directory. `commitHash` is null for a working-tree change list (see CommitHash's own doc comment).</summary>
    public static IReadOnlyList<ChangeTreeNode> Build(IReadOnlyList<GitChange> changes, string? commitHash)
    {
        var root = new ChangeTreeNode { Name = "", IsDirectory = true };
        foreach (var change in changes)
        {
            var parts = change.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            for (var i = 0; i < parts.Length; i++)
            {
                var isLast = i == parts.Length - 1;
                var existing = current.Children.FirstOrDefault(c => c.Name == parts[i] && c.IsDirectory == !isLast);
                if (existing is null)
                {
                    existing = new ChangeTreeNode
                    {
                        Name = parts[i],
                        IsDirectory = !isLast,
                        Status = isLast ? change.Status : null,
                        RelativePath = isLast ? change.Path : null,
                        CommitHash = isLast ? commitHash : null,
                    };
                    current.Children.Add(existing);
                }

                current = existing;
            }
        }

        SortRecursive(root);
        return root.Children;
    }

    private static void SortRecursive(ChangeTreeNode node)
    {
        node.Children.Sort((a, b) => a.IsDirectory != b.IsDirectory
            ? (a.IsDirectory ? -1 : 1)
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        foreach (var child in node.Children)
        {
            SortRecursive(child);
        }
    }
}
