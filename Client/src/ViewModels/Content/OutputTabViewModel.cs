using System.Collections.ObjectModel;
using System.ComponentModel;
using AutoDev.Core.Models;
using AutoDev.Core.Services;
using AutoDev.ViewModels.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoDev.ViewModels.Content;

/// <summary>One dropdown entry - a task that is currently running or has run at least once before (see OutputTabViewModel.LoadAsync). IsRunning drives the same "●" indicator the Tasks sidebar row uses.</summary>
public sealed partial class OutputTaskEntry(string id, string name) : ViewModelBase
{
    public string Id { get; } = id;

    [ObservableProperty]
    private string _name = name;

    [ObservableProperty]
    private bool _isRunning;

    public void UpdateFrom(string name) => Name = name;
}

/// <summary>One task script's own output panel - independent Running/Succeeded/Failed/Stopped status and a "hide this panel" toggle, since a task's scripts run concurrently and can finish at different times (or never, for a long-lived one like a dev server). RequestedRow/RequestedColumn are the script's own optional Output tab grid placement (see TaskScript); ResolvedRow/ResolvedColumn are the concrete cell ScriptBlockGridLayout assigned it, which the View actually binds to.</summary>
public sealed partial class ScriptBlockPanelViewModel(string name, int? requestedRow = null, int? requestedColumn = null) : ViewModelBase
{
    public string Name { get; } = name;
    public int? RequestedRow { get; } = requestedRow;
    public int? RequestedColumn { get; } = requestedColumn;

    [ObservableProperty]
    private int _resolvedRow;

    [ObservableProperty]
    private int _resolvedColumn;

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private string _outputText = "";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private bool _failed;

    /// <summary>True only when this block was ended by an explicit user Stop (of the whole task) rather than exiting on its own - drives showing "Stopped" instead of "Failed" on this panel.</summary>
    [ObservableProperty]
    private bool _wasStopped;

    public bool ShowRunning => IsRunning;
    public bool ShowSucceeded => !IsRunning && HasResult && !Failed;
    public bool ShowStopped => !IsRunning && HasResult && Failed && WasStopped;
    public bool ShowFailed => !IsRunning && HasResult && Failed && !WasStopped;

    partial void OnIsRunningChanged(bool value) => RaiseStateChanged();
    partial void OnHasResultChanged(bool value) => RaiseStateChanged();
    partial void OnFailedChanged(bool value) => RaiseStateChanged();
    partial void OnWasStoppedChanged(bool value) => RaiseStateChanged();

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(ShowRunning));
        OnPropertyChanged(nameof(ShowSucceeded));
        OnPropertyChanged(nameof(ShowStopped));
        OnPropertyChanged(nameof(ShowFailed));
    }

    public void AppendLine(string line) => OutputText = OutputText.Length > 0 ? $"{OutputText}\n{line}" : line;

    public void ApplyResult(ScriptBlockRunRecord record)
    {
        IsRunning = false;
        HasResult = true;
        Failed = !record.Success;
        WasStopped = record.WasStopped;
    }
}

/// <summary>
/// Read-only view of task output, switchable via a dropdown between every task that is currently running or
/// has run at least once before (see LoadAsync/Entries) - the last run of a task not currently running stays
/// visible until that task is re-run. Subscribes to the workspace's one IWorkspaceTaskScheduler instance for
/// its whole lifetime, so multiple tasks can run concurrently with the dropdown/sidebar accurately reflecting
/// all of them regardless of which one is currently selected for viewing.
///
/// Every task's blocks run concurrently, each getting its own togglable output panel (see
/// ScriptBlocks/VisibleScriptBlocks/ScriptBlockPanelViewModel) rather than one shared log.
/// </summary>
public sealed partial class OutputTabViewModel : ViewModelBase, IDisposable
{
    private readonly string _workspacePath;
    private readonly IWorkspaceMetadataStore _metadataStore;
    private readonly IWorkspaceTaskScheduler _scheduler;
    private readonly IUiDispatcher _dispatcher;

    public OutputTabViewModel(string workspacePath, IWorkspaceMetadataStore metadataStore, IWorkspaceTaskScheduler scheduler, IUiDispatcher dispatcher)
    {
        _workspacePath = workspacePath;
        _metadataStore = metadataStore;
        _scheduler = scheduler;
        _dispatcher = dispatcher;

        Entries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasEntries));
        VisibleScriptBlocks.CollectionChanged += (_, _) => RecomputeGridLayout();

        _scheduler.TaskRunStarted += OnAnyRunStarted;
        _scheduler.TaskRunCompleted += OnAnyRunCompleted;
        _scheduler.ScriptTaskProgress += OnScriptProgress;
        _scheduler.ScriptBlockCompleted += OnScriptBlockCompleted;
    }

    public ObservableCollection<OutputTaskEntry> Entries { get; } = [];

    public bool HasEntries => Entries.Count > 0;

    [ObservableProperty]
    private OutputTaskEntry? _selectedEntry;

    [ObservableProperty]
    private bool _hasTask;

    [ObservableProperty]
    private string _taskName = "";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private bool _lastRunFailed;

    /// <summary>True only when the last run ended via an explicit user Stop - drives showing "Stopped" instead of "Failed" in the header.</summary>
    [ObservableProperty]
    private bool _lastRunWasStopped;

    /// <summary>Only ever set for a run-level failure that never got as far as any block (e.g. a script content parse error) - a per-block failure shows on that block's own panel instead.</summary>
    [ObservableProperty]
    private string _scriptRunError = "";

    public ObservableCollection<ScriptBlockPanelViewModel> ScriptBlocks { get; } = [];

    /// <summary>Same items as ScriptBlocks, filtered to IsVisible - what the panel grid actually renders, kept in sync via each panel's own PropertyChanged, so hidden panels don't reserve empty space (see OutputTabView.axaml's Grid). Every add/remove/visibility-toggle recomputes GridRowCount/GridColumnCount and each panel's ResolvedRow/ResolvedColumn via ScriptBlockGridLayout, then raises GridLayoutChanged (see the CollectionChanged hook in the constructor).</summary>
    public ObservableCollection<ScriptBlockPanelViewModel> VisibleScriptBlocks { get; } = [];

    /// <summary>Sized by ScriptBlockGridLayout - Grid.RowDefinitions/ColumnDefinitions have no bindable setter in Avalonia (only the XAML-literal string form works), so OutputTabView's code-behind reads these as plain counts and builds the actual RowDefinition/ColumnDefinition entries itself, same idea as ResolvedRow/ResolvedColumn below driving Grid.SetRow/SetColumn per container.</summary>
    [ObservableProperty]
    private int _gridRowCount = 1;

    [ObservableProperty]
    private int _gridColumnCount = 1;

    /// <summary>Fired every time GridRowCount/GridColumnCount and every panel's ResolvedRow/ResolvedColumn are (re)computed - OutputTabView's code-behind reacts by re-applying the full layout to every currently-realized container, rather than trying to track which specific containers moved (multiple containers, including already-realized ones, can shift position in a single recompute - see ScriptBlockGridLayout).</summary>
    public event Action? GridLayoutChanged;

    private void RecomputeGridLayout()
    {
        var (rows, columns) = ScriptBlockGridLayout.Apply(VisibleScriptBlocks);
        GridRowCount = Math.Max(rows, 1);
        GridColumnCount = Math.Max(columns, 1);
        GridLayoutChanged?.Invoke();
    }

    public bool ShowRunning => IsRunning;
    public bool ShowSucceeded => !IsRunning && HasResult && !LastRunFailed;
    public bool ShowStopped => !IsRunning && HasResult && LastRunFailed && LastRunWasStopped;
    public bool ShowFailed => !IsRunning && HasResult && LastRunFailed && !LastRunWasStopped;
    public bool HasScriptRunError => ScriptRunError.Length > 0;

    partial void OnIsRunningChanged(bool value)
    {
        RaiseStateChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasResultChanged(bool value) => RaiseStateChanged();
    partial void OnLastRunFailedChanged(bool value) => RaiseStateChanged();
    partial void OnLastRunWasStoppedChanged(bool value) => RaiseStateChanged();
    partial void OnHasTaskChanged(bool value) => RaiseStateChanged();
    partial void OnScriptRunErrorChanged(string value) => OnPropertyChanged(nameof(HasScriptRunError));

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(ShowRunning));
        OnPropertyChanged(nameof(ShowSucceeded));
        OnPropertyChanged(nameof(ShowStopped));
        OnPropertyChanged(nameof(ShowFailed));
    }

    /// <summary>Called once when a workspace tab is opened - seeds the dropdown from persisted run history (plus anything already running), so tasks run in a previous session still show up, not just ones touched this session. There's no central task registry to enumerate anymore (tasks are just .task files wherever the user put them) - LoadRunTaskRefsAsync derives the list from run history instead, so a task that's never been run doesn't appear until it is.</summary>
    public async Task LoadAsync()
    {
        var tasks = await _metadataStore.LoadRunTaskRefsAsync(_workspacePath);
        foreach (var task in tasks)
        {
            GetOrCreateEntry(task.Path, task.Name).IsRunning = _scheduler.IsRunning(task.Path);
        }
    }

    /// <summary>Raised by the sidebar's "View" action (or a row activation) - adds the task to the dropdown if it isn't there yet (a never-run task can still be viewed, showing the empty state) and selects it.</summary>
    public void SelectTask(string id, string name)
    {
        var entry = GetOrCreateEntry(id, name);
        entry.IsRunning = _scheduler.IsRunning(id);
        SelectedEntry = entry;
    }

    private OutputTaskEntry GetOrCreateEntry(string id, string name)
    {
        var existing = Entries.FirstOrDefault(e => e.Id == id);
        if (existing is not null)
        {
            existing.UpdateFrom(name);
            return existing;
        }

        var entry = new OutputTaskEntry(id, name);
        Entries.Add(entry);
        return entry;
    }

    partial void OnSelectedEntryChanged(OutputTaskEntry? value)
    {
        ScriptRunError = "";
        ClearScriptBlocks();

        if (value is null)
        {
            HasTask = false;
            IsRunning = false;
            HasResult = false;
            return;
        }

        TaskName = value.Name;
        HasTask = true;

        if (_scheduler.IsRunning(value.Id))
        {
            IsRunning = true;
            HasResult = true;

            foreach (var layout in _scheduler.GetScriptBlockLayouts(value.Id))
            {
                AddScriptBlockPanel(BuildLiveBlockPanel(value.Id, layout));
            }

            return;
        }

        IsRunning = false;
        _ = LoadMostRecentRunAsync(value.Id);
    }

    private ScriptBlockPanelViewModel BuildLiveBlockPanel(string taskId, ScriptBlockLayout layout)
    {
        var panel = new ScriptBlockPanelViewModel(layout.Name, layout.Row, layout.Column)
        {
            OutputText = string.Join("\n", _scheduler.GetScriptOutputSoFar(taskId, layout.Name)),
        };

        var completed = _scheduler.GetScriptBlockResult(taskId, layout.Name);
        if (completed is not null)
        {
            panel.ApplyResult(completed);
        }
        else
        {
            panel.IsRunning = true;
            panel.HasResult = panel.OutputText.Length > 0;
        }

        return panel;
    }

    private async Task LoadMostRecentRunAsync(string taskId)
    {
        var runs = await _metadataStore.LoadTaskRunsAsync(_workspacePath, taskId);

        // Selection moved on, or - the specific race this guards against - a new run of this very task
        // started while this disk read was in flight (e.g. Run re-selects the already-selected task, then
        // starts it, all before this load's await returns): without the second check, this stale historical
        // load would land after OnAnyRunStarted's reset and overwrite the fresh display with the *previous*
        // run's leftover text, which every following progress line would then get appended after.
        if (SelectedEntry?.Id != taskId || _scheduler.IsRunning(taskId))
        {
            return;
        }

        var mostRecentRun = runs.FirstOrDefault();
        if (mostRecentRun is not null)
        {
            HasResult = true;
            LastRunFailed = !mostRecentRun.Success;
            LastRunWasStopped = mostRecentRun.WasStopped;
            ApplyHistoricalScriptBlocks(mostRecentRun);
        }
        else
        {
            HasResult = false;
        }
    }

    private void ApplyHistoricalScriptBlocks(TaskRunRecord record)
    {
        var blocks = record.ScriptBlocks;
        if (blocks is null || blocks.Count == 0)
        {
            ScriptRunError = !record.Success ? BuildDisplayText(record) : "";
            return;
        }

        foreach (var block in blocks)
        {
            var panel = new ScriptBlockPanelViewModel(block.Name, block.Row, block.Column) { OutputText = block.Output };
            panel.ApplyResult(block);
            AddScriptBlockPanel(panel);
        }
    }

    private bool CanStop() => IsRunning && SelectedEntry is not null;

    /// <summary>Forcefully stops the currently-viewed task's run - see IWorkspaceTaskScheduler.StopRun.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        if (SelectedEntry is { } entry)
        {
            _scheduler.StopRun(entry.Id);
        }
    }

    private void OnAnyRunStarted(TaskRef task) => _dispatcher.Post(() =>
    {
        var entry = GetOrCreateEntry(task.Path, task.Name);
        entry.IsRunning = true;
        SelectedEntry ??= entry; // nothing viewed yet this session - default to the first task that starts running

        if (SelectedEntry?.Id != task.Path)
        {
            return;
        }

        // A run of the currently-viewed task just started - clear whatever the previous run left displayed
        // right now, unconditionally, rather than waiting for the first progress event to infer a new run
        // began from IsRunning having gone false. Re-running the same task that's already selected doesn't
        // change SelectedEntry (same reference, so OnSelectedEntryChanged never re-fires), so this is the
        // only reliable place left to reset for that case - without it, a re-run's output was appearing
        // appended after the previous run's leftover text instead of replacing it.
        ScriptRunError = "";
        ClearScriptBlocks();
        IsRunning = true;
        HasResult = true;
        LastRunFailed = false;
        LastRunWasStopped = false;
    });

    private void OnAnyRunCompleted(TaskRunRecord record) => _dispatcher.Post(() =>
    {
        var entry = Entries.FirstOrDefault(e => e.Id == record.TaskPath);
        if (entry is not null)
        {
            entry.IsRunning = false;
        }

        if (SelectedEntry?.Id != record.TaskPath)
        {
            return;
        }

        IsRunning = false;
        HasResult = true;
        LastRunFailed = !record.Success;
        LastRunWasStopped = record.WasStopped;

        if (record.ScriptBlocks is null || record.ScriptBlocks.Count == 0)
        {
            // Never got as far as any block (e.g. a script content parse error) - every panel was already
            // watching live progress, so this only matters when there's nothing to show per-block.
            ScriptRunError = !record.Success ? BuildDisplayText(record) : "";
        }
        else
        {
            // Reconciles every block's panel against the final record - a no-op for a block whose own
            // ScriptBlockCompleted already applied the same result, but the only place a Stop settles a
            // block that was killed mid-flight: cancellation makes RunBlockAsync throw instead of returning
            // normally, so onBlockCompleted (and the ScriptBlockCompleted event it drives) never fires for
            // it, and without this it would be stuck showing "Running…" forever.
            foreach (var block in record.ScriptBlocks)
            {
                var panel = ScriptBlocks.FirstOrDefault(p => p.Name == block.Name);
                if (panel is null)
                {
                    panel = new ScriptBlockPanelViewModel(block.Name, block.Row, block.Column) { OutputText = block.Output };
                    AddScriptBlockPanel(panel);
                }

                panel.ApplyResult(block);
            }
        }
    });

    /// <summary>OutputSummary is always the actual accumulated output text (see WorkspaceTaskSchedulerService), never discarded on failure or stop - ErrorMessage is only ever a short reason appended below it, so a failed or stopped run's real history stays fully reviewable.</summary>
    private static string BuildDisplayText(TaskRunRecord record) =>
        string.IsNullOrEmpty(record.ErrorMessage) ? record.OutputSummary
        : string.IsNullOrEmpty(record.OutputSummary) ? record.ErrorMessage
        : $"{record.OutputSummary}\n\n{record.ErrorMessage}";

    private void OnScriptProgress(string taskId, string blockName, string line)
    {
        if (taskId != SelectedEntry?.Id)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            IsRunning = true;
            HasResult = true;

            var panel = ScriptBlocks.FirstOrDefault(p => p.Name == blockName);
            if (panel is null)
            {
                var layout = _scheduler.GetScriptBlockLayouts(taskId).FirstOrDefault(l => l.Name == blockName);
                panel = new ScriptBlockPanelViewModel(blockName, layout?.Row, layout?.Column);
                AddScriptBlockPanel(panel);
            }

            panel.IsRunning = true;
            panel.HasResult = true;
            panel.AppendLine(line);
        });
    }

    private void OnScriptBlockCompleted(string taskId, ScriptBlockRunRecord result)
    {
        if (taskId != SelectedEntry?.Id)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            var panel = ScriptBlocks.FirstOrDefault(p => p.Name == result.Name);
            if (panel is null)
            {
                panel = new ScriptBlockPanelViewModel(result.Name, result.Row, result.Column) { OutputText = result.Output };
                AddScriptBlockPanel(panel);
            }

            panel.ApplyResult(result);
        });
    }

    private void AddScriptBlockPanel(ScriptBlockPanelViewModel panel)
    {
        ScriptBlocks.Add(panel);
        if (panel.IsVisible)
        {
            VisibleScriptBlocks.Add(panel);
        }

        panel.PropertyChanged += OnScriptBlockPanelPropertyChanged;
    }

    private void OnScriptBlockPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ScriptBlockPanelViewModel.IsVisible) || sender is not ScriptBlockPanelViewModel panel)
        {
            return;
        }

        if (panel.IsVisible)
        {
            if (!VisibleScriptBlocks.Contains(panel))
            {
                VisibleScriptBlocks.Add(panel);
            }
        }
        else
        {
            VisibleScriptBlocks.Remove(panel);
        }
    }

    private void ClearScriptBlocks()
    {
        ScriptBlocks.Clear();
        VisibleScriptBlocks.Clear();
    }

    public void Dispose()
    {
        _scheduler.TaskRunStarted -= OnAnyRunStarted;
        _scheduler.TaskRunCompleted -= OnAnyRunCompleted;
        _scheduler.ScriptTaskProgress -= OnScriptProgress;
        _scheduler.ScriptBlockCompleted -= OnScriptBlockCompleted;
    }
}
