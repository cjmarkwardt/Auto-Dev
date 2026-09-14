using System.Collections.ObjectModel;
using AutoDev.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoDev.ViewModels.Sidebar;

public enum FileSearchMode
{
    FileName,
    Content,
}

/// <summary>F1 quick-open/content-search overlay: FileName mode fuzzy-searches every file in the workspace by name; Content mode searches file contents instead - see ToggleMode. Ignore-filtering matches the Files section's own tree exactly, including its "Show Ignored Files" toggle - see LoadFilesAsync.</summary>
public sealed partial class FileSearchViewModel : ViewModelBase
{
    private static readonly int maxResults = 50;
    private static readonly int maxContentResults = 200;
    private static readonly TimeSpan contentSearchDebounce = TimeSpan.FromMilliseconds(200);

    private readonly string workspacePath;
    private readonly IGitService gitService;
    private readonly FilesSectionViewModel files;
    private List<string> allFiles = [];

    /// <summary>Bumped on every Open() and checked after the ignore-filter's await - guards against a slower-finishing call from an earlier Open() (e.g. F1 pressed, closed, pressed again quickly) overwriting a newer one's results.</summary>
    private int openToken;

    private CancellationTokenSource? contentSearchCts;

    public FileSearchViewModel(string workspacePath, IGitService gitService, FilesSectionViewModel files)
    {
        this.workspacePath = workspacePath;
        this.gitService = gitService;
        this.files = files;
    }

    [ObservableProperty]
    private bool isOpen;

    [ObservableProperty]
    private string query = "";

    [ObservableProperty]
    private FileSearchMode mode = FileSearchMode.FileName;

    [ObservableProperty]
    private FileSearchResultViewModel? selectedResult;

    [ObservableProperty]
    private ContentSearchResultViewModel? selectedContentResult;

    public ObservableCollection<FileSearchResultViewModel> Results { get; } = [];
    public ObservableCollection<ContentSearchResultViewModel> ContentResults { get; } = [];

    public bool IsFileNameMode => Mode == FileSearchMode.FileName;
    public bool IsContentMode => Mode == FileSearchMode.Content;
    public string Placeholder => Mode == FileSearchMode.FileName ? "Search files by name…" : "Search file contents…";

    public event Action<string>? FileChosen;
    public event Action<string, int>? ContentResultChosen;

    partial void OnModeChanged(FileSearchMode value)
    {
        OnPropertyChanged(nameof(IsFileNameMode));
        OnPropertyChanged(nameof(IsContentMode));
        OnPropertyChanged(nameof(Placeholder));
    }

    public void Open()
    {
        Mode = FileSearchMode.FileName;
        Query = "";
        Results.Clear();
        ContentResults.Clear();
        SelectedResult = null;
        SelectedContentResult = null;
        IsOpen = true;
        _ = LoadFilesAsync(++openToken);
    }

    /// <summary>F1 pressed again while already open - flips between filename and content search, keeping the overlay open with a cleared query.</summary>
    public void ToggleMode()
    {
        Mode = Mode == FileSearchMode.FileName ? FileSearchMode.Content : FileSearchMode.FileName;
        Query = "";
        Results.Clear();
        ContentResults.Clear();
        SelectedResult = null;
        SelectedContentResult = null;
    }

    private async Task LoadFilesAsync(int token)
    {
        List<string> candidates = EnumerateFiles(workspacePath);

        List<string> filtered;
        if (files.ShowIgnoredFiles)
        {
            // Matches the Files section's own tree exactly - with its "Show Ignored Files" toggle on,
            // everything shows there (dimmed but present, see FileTreeNodeViewModel.IsIgnored), so search
            // finds everything too, respecting neither .fileignore nor .gitignore. Read fresh here (not
            // reactively while the popup stays open) since a fresh Open() is exactly when this should be
            // re-evaluated - the same moment the file list itself gets rebuilt from scratch.
            filtered = candidates;
        }
        else if (FileIgnoreMatcher.LoadForWorkspace(workspacePath) is { } fileIgnoreMatcher)
        {
            // .fileignore, when present, takes over from .gitignore entirely for what this app itself
            // considers visible - same as the Files section's own tree (see
            // FilesSectionViewModel.ReloadFileIgnore) - so a file .gitignore excludes but .fileignore doesn't
            // is discoverable here again, and one .fileignore hides that .gitignore wouldn't have is excluded
            // from search too, not just dimmed in the tree, since the whole point of .fileignore is
            // controlling what this app surfaces at all.
            filtered = [.. candidates.Where(f => !fileIgnoreMatcher.IsMatch(Path.GetRelativePath(workspacePath, f), isDirectory: false))];
        }
        else
        {
            IReadOnlySet<string> ignored = await gitService.GetIgnoredPathsAsync(workspacePath, candidates);
            filtered = ignored.Count == 0 ? candidates : [.. candidates.Where(f => !ignored.Contains(f))];
        }

        if (token != openToken)
        {
            return; // superseded by a newer Open() call
        }

        allFiles = filtered;
        UpdateResults();
    }

    public void Close() => IsOpen = false;

    partial void OnQueryChanged(string value)
    {
        if (Mode == FileSearchMode.FileName)
        {
            UpdateResults();
        }
        else
        {
            ScheduleContentSearch();
        }
    }

    private void UpdateResults()
    {
        Results.Clear();

        IEnumerable<(string Path, string Relative, int Score)> ranked = allFiles
            .Select(path =>
            {
                string relative = Path.GetRelativePath(workspacePath, path).Replace(Path.DirectorySeparatorChar, '/');
                string fileName = Path.GetFileName(path);
                return (Path: path, Relative: relative, Score: Score(relative, fileName, Query));
            })
            .Where(x => Query.Length == 0 || x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Relative, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults);

        foreach ((string? path, string? relative, int _) in ranked)
        {
            Results.Add(new FileSearchResultViewModel(path, relative));
        }

        SelectedResult = Results.FirstOrDefault();
    }

    private void ScheduleContentSearch()
    {
        contentSearchCts?.Cancel();
        ContentResults.Clear();
        SelectedContentResult = null;

        if (Query.Length == 0)
        {
            return;
        }

        CancellationTokenSource cts = new CancellationTokenSource();
        contentSearchCts = cts;
        _ = DebounceContentSearchAsync(Query, cts.Token);
    }

    private async Task DebounceContentSearchAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(contentSearchDebounce, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await RunContentSearchAsync(query, cancellationToken);
    }

    private async Task RunContentSearchAsync(string query, CancellationToken cancellationToken)
    {
        // _allFiles is the same ignore-filtered candidate list LoadFilesAsync already built on Open() -
        // content search only ever searches what filename search would also find.
        List<string> candidates = allFiles;

        List<ContentSearchResultViewModel> results = await Task.Run(() =>
        {
            List<ContentSearchResultViewModel> found = new List<ContentSearchResultViewModel>();
            foreach (string path in candidates)
            {
                if (cancellationToken.IsCancellationRequested || found.Count >= maxContentResults)
                {
                    break;
                }

                if (BinaryFileDetector.IsLikelyBinary(path))
                {
                    continue;
                }

                IReadOnlyList<string> lines;
                try
                {
                    lines = File.ReadAllLines(path);
                }
                catch (Exception)
                {
                    continue; // unreadable (permissions, bad encoding, deleted mid-scan) - skip
                }

                string relative = Path.GetRelativePath(workspacePath, path).Replace(Path.DirectorySeparatorChar, '/');
                for (int i = 0; i < lines.Count; i++)
                {
                    if (found.Count >= maxContentResults)
                    {
                        break;
                    }

                    int index = lines[i].IndexOf(query, StringComparison.OrdinalIgnoreCase);
                    if (index < 0)
                    {
                        continue;
                    }

                    found.Add(new ContentSearchResultViewModel(path, relative, i + 1, lines[i].Trim()));
                }
            }

            return found;
        }, cancellationToken);

        if (cancellationToken.IsCancellationRequested)
        {
            return; // superseded by a newer keystroke
        }

        ContentResults.Clear();
        foreach (ContentSearchResultViewModel? result in results)
        {
            ContentResults.Add(result);
        }

        SelectedContentResult = ContentResults.FirstOrDefault();
    }

    public void MoveSelection(int delta)
    {
        if (Mode == FileSearchMode.Content)
        {
            if (ContentResults.Count == 0)
            {
                return;
            }

            int contentIndex = SelectedContentResult is null ? 0 : ContentResults.IndexOf(SelectedContentResult);
            contentIndex = Math.Clamp(contentIndex + delta, 0, ContentResults.Count - 1);
            SelectedContentResult = ContentResults[contentIndex];
            return;
        }

        if (Results.Count == 0)
        {
            return;
        }

        int index = SelectedResult is null ? 0 : Results.IndexOf(SelectedResult);
        index = Math.Clamp(index + delta, 0, Results.Count - 1);
        SelectedResult = Results[index];
    }

    public void ChooseSelected()
    {
        if (Mode == FileSearchMode.Content)
        {
            if (SelectedContentResult is { } contentResult)
            {
                ChooseContent(contentResult);
            }
            else
            {
                Close();
            }

            return;
        }

        if (SelectedResult is { } result)
        {
            FileChosen?.Invoke(result.FullPath);
        }

        Close();
    }

    public void Choose(FileSearchResultViewModel result)
    {
        FileChosen?.Invoke(result.FullPath);
        Close();
    }

    public void ChooseContent(ContentSearchResultViewModel result)
    {
        ContentResultChosen?.Invoke(result.FullPath, result.LineNumber);
        Close();
    }

    private static List<string> EnumerateFiles(string workspacePath)
    {
        List<string> results = new List<string>();
        Walk(workspacePath, results);
        return results;
    }

    private static void Walk(string directory, List<string> results)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory);
        }
        catch (Exception)
        {
            return; // permission errors etc. - skip this subtree
        }

        foreach (string entry in entries)
        {
            string name = Path.GetFileName(entry);
            if (name.StartsWith('.'))
            {
                continue;
            }

            if (Directory.Exists(entry))
            {
                // A symlinked directory can point anywhere, including back at one of its own ancestors - Walk
                // recursing into one unconditionally can walk forever (a real crash: unbounded recursion here
                // eventually overflows the stack). Skipping it entirely, rather than trying to detect cycles,
                // matches how most file-search tools handle a symlinked directory.
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                Walk(entry, results);
            }
            else
            {
                results.Add(entry);
            }
        }
    }

    /// <summary>Subsequence fuzzy match (VS Code Quick Open style) - rewards consecutive character runs, weights a filename match higher than a path-only match. 0 means no match.</summary>
    private static int Score(string relativePath, string fileName, string query)
    {
        if (query.Length == 0)
        {
            return 1;
        }

        int pathScore = FuzzyScore(relativePath, query);
        if (pathScore == 0)
        {
            return 0;
        }

        return pathScore + FuzzyScore(fileName, query) * 3;
    }

    private static int FuzzyScore(string text, string query)
    {
        string textLower = text.ToLowerInvariant();
        string queryLower = query.ToLowerInvariant();

        int textIndex = 0;
        int queryIndex = 0;
        int score = 0;
        int consecutive = 0;

        while (textIndex < textLower.Length && queryIndex < queryLower.Length)
        {
            if (textLower[textIndex] == queryLower[queryIndex])
            {
                consecutive++;
                score += 1 + consecutive;
                queryIndex++;
            }
            else
            {
                consecutive = 0;
            }

            textIndex++;
        }

        return queryIndex == queryLower.Length ? score : 0;
    }
}
