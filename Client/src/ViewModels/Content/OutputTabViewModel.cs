using System.Collections.ObjectModel;
using System.ComponentModel;
using AutoDev.Core.Models;
using AutoDev.Core.Services;
using AutoDev.ViewModels.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Markwardt.TaskRunner;

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

/// <summary>
/// One script's own output panel within a task's run - Status maps directly onto Markwardt.TaskRunner's own
/// ScriptStatus (Running/Waiting/Completed/Failed), the same status a live Markwardt.TaskRunner.ScriptRunner
/// itself reports, so there's no separate app-level status vocabulary to keep in sync with the library's,
/// except for distinguishing an explicit Stop from a genuine failure (ShowStopped/ShowFailed) - the library
/// itself has no such concept (a cancelled script just ends up Failed, same as any other failure), so this is
/// purely AutoDev's own policy layered on top, mirroring TaskRunRecord.WasStopped. Built one of two ways:
/// live (see the ScriptRunner constructor overload), mirroring that script's own Status/LogText as they
/// change for as long as the run continues; or historical (the plain constructor plus ApplyFinal), from an
/// already-finished run's persisted ScriptRunRecord. IsVisible is this panel's own "hide this panel" toggle -
/// a task's scripts run concurrently and can finish at different times (or never, for a long-lived one like a
/// dev server).
/// </summary>
public sealed partial class ScriptPanelViewModel : ViewModelBase, IDisposable
{
    private readonly ScriptRunner? _liveRunner;
    private readonly PropertyChangedEventHandler? _liveHandler;

    /// <summary>Builds a panel from an already-finished run's persisted result - see ApplyFinal.</summary>
    public ScriptPanelViewModel(string name) => Name = name;

    /// <summary>Builds a panel that mirrors a currently-running script's own live Status/LogText - see Dispose.</summary>
    public ScriptPanelViewModel(ScriptRunner runner, IUiDispatcher dispatcher)
    {
        Name = runner.Name;
        Status = runner.Status;
        OutputText = runner.LogText;

        _liveRunner = runner;
        _liveHandler = (_, e) => dispatcher.Post(() =>
        {
            if (e.PropertyName == nameof(ScriptRunner.Status))
            {
                Status = runner.Status;
            }
            else if (e.PropertyName == nameof(ScriptRunner.LogText))
            {
                OutputText = runner.LogText;
            }
        });
        runner.PropertyChanged += _liveHandler;
    }

    public string Name { get; }

    [ObservableProperty]
    private int _resolvedRow;

    [ObservableProperty]
    private int _resolvedColumn;

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private ScriptStatus _status = ScriptStatus.Running;

    [ObservableProperty]
    private string _outputText = "";

    /// <summary>
    /// True only once ApplyFinal has settled this panel against a run that ended via an explicit user Stop -
    /// distinguishes ShowStopped from ShowFailed below for a script that was still in flight (Failed, per
    /// Markwardt.TaskRunner's own vocabulary - see ScriptStatus) when the stop happened, mirroring
    /// OutputTabViewModel.ShowStopped/ShowFailed's identical task-level distinction. Never true for a script
    /// that reached Completed before the stop, which keeps showing ShowSucceeded regardless - see ApplyFinal.
    /// </summary>
    private bool _wasStopped;

    public bool ShowRunning => Status == ScriptStatus.Running;
    public bool ShowWaiting => Status == ScriptStatus.Waiting;
    public bool ShowSucceeded => Status == ScriptStatus.Completed;
    public bool ShowStopped => Status == ScriptStatus.Failed && _wasStopped;
    public bool ShowFailed => Status == ScriptStatus.Failed && !_wasStopped;

    partial void OnStatusChanged(ScriptStatus value)
    {
        OnPropertyChanged(nameof(ShowRunning));
        OnPropertyChanged(nameof(ShowWaiting));
        OnPropertyChanged(nameof(ShowSucceeded));
        OnPropertyChanged(nameof(ShowStopped));
        OnPropertyChanged(nameof(ShowFailed));
    }

    /// <summary>
    /// Force-applies a final, authoritative Status/OutputText/wasStopped - used both to seed a panel built
    /// straight from a historical ScriptRunRecord (see TaskRunRecord.WasStopped), and to reconcile a live
    /// panel against its own run's final TaskRunRecord once the run completes (belt-and-suspenders against
    /// Dispatcher post-ordering between this panel's own live subscription and
    /// OutputTabViewModel.OnAnyRunCompleted - see that method). Explicitly re-raises ShowStopped/ShowFailed
    /// itself rather than relying solely on OnStatusChanged - Status may already equal `status` (e.g. this
    /// script's own live ScriptRunner already posted Failed moments before the run's final record confirms
    /// wasStopped), in which case the Status setter's own equality check would otherwise skip notifying and
    /// this script would keep showing "Failed" instead of settling on "Stopped".
    /// </summary>
    public void ApplyFinal(ScriptStatus status, string outputText, bool wasStopped)
    {
        _wasStopped = wasStopped;
        Status = status;
        OutputText = outputText;
        OnPropertyChanged(nameof(ShowStopped));
        OnPropertyChanged(nameof(ShowFailed));
    }

    /// <summary>Unsubscribes from the live ScriptRunner's PropertyChanged, if this panel was built from one - a no-op for a historical panel. Called once this panel is no longer shown (see OutputTabViewModel.ClearScriptBlocks), so a still-running task's continued progress doesn't keep posting into an orphaned view model after the viewer switches away from it.</summary>
    public void Dispose()
    {
        if (_liveRunner is not null && _liveHandler is not null)
        {
            _liveRunner.PropertyChanged -= _liveHandler;
        }
    }
}

/// <summary>
/// Read-only view of task output, switchable via a dropdown between every task that is currently running or
/// has run at least once before (see LoadAsync/Entries) - the last run of a task not currently running stays
/// visible until that task is re-run. Subscribes to the workspace's one IWorkspaceTaskScheduler instance for
/// its whole lifetime, so multiple tasks can run concurrently with the dropdown/sidebar accurately reflecting
/// all of them regardless of which one is currently selected for viewing.
///
/// Every task's scripts run concurrently, each getting its own togglable output panel (see
/// ScriptBlocks/VisibleScriptBlocks/ScriptPanelViewModel) rather than one shared log.
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
        _scheduler.TaskScriptsAvailable += OnTaskScriptsAvailable;
        _scheduler.TaskRunCompleted += OnAnyRunCompleted;
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

    /// <summary>Set only when the .task file itself failed to parse (see Markwardt.TaskRunner.TaskParseException) - a per-script failure shows on that script's own panel instead.</summary>
    [ObservableProperty]
    private string _parseError = "";

    public ObservableCollection<ScriptPanelViewModel> ScriptBlocks { get; } = [];

    /// <summary>Same items as ScriptBlocks, filtered to IsVisible - what the panel grid actually renders, kept in sync via each panel's own PropertyChanged, so hidden panels don't reserve empty space (see OutputTabView.axaml's Grid). Every add/remove/visibility-toggle recomputes GridRowCount/GridColumnCount and each panel's ResolvedRow/ResolvedColumn via ScriptBlockGridLayout, then raises GridLayoutChanged (see the CollectionChanged hook in the constructor).</summary>
    public ObservableCollection<ScriptPanelViewModel> VisibleScriptBlocks { get; } = [];

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
    public bool HasParseError => ParseError.Length > 0;

    partial void OnIsRunningChanged(bool value)
    {
        RaiseStateChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasResultChanged(bool value) => RaiseStateChanged();
    partial void OnLastRunFailedChanged(bool value) => RaiseStateChanged();
    partial void OnLastRunWasStoppedChanged(bool value) => RaiseStateChanged();
    partial void OnHasTaskChanged(bool value) => RaiseStateChanged();
    partial void OnParseErrorChanged(string value) => OnPropertyChanged(nameof(HasParseError));

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
        ParseError = "";
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

            foreach (var runner in _scheduler.GetLiveScripts(value.Id) ?? [])
            {
                AddScriptBlockPanel(new ScriptPanelViewModel(runner, _dispatcher));
            }

            return;
        }

        IsRunning = false;
        _ = LoadMostRecentRunAsync(value.Id);
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
        if (record.ParseError is { } parseError)
        {
            ParseError = parseError;
            return;
        }

        foreach (var script in record.Scripts)
        {
            var panel = new ScriptPanelViewModel(script.Name);
            panel.ApplyFinal(script.Status, script.Log, record.WasStopped);
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
        // right now, unconditionally, rather than waiting for its scripts to become available to infer a new
        // run began. Re-running the same task that's already selected doesn't change SelectedEntry (same
        // reference, so OnSelectedEntryChanged never re-fires), so this is the only reliable place left to
        // reset for that case - without it, a re-run's output was appearing appended after the previous run's
        // leftover text instead of replacing it.
        ParseError = "";
        ClearScriptBlocks();
        IsRunning = true;
        HasResult = true;
        LastRunFailed = false;
        LastRunWasStopped = false;
    });

    /// <summary>The task's file has parsed and its scripts (see IWorkspaceTaskScheduler.GetLiveScripts) are now known - populates this task's panels if it's the one currently being viewed and they aren't already populated (e.g. OnSelectedEntryChanged already did it, for a task selected after its own run had already started).</summary>
    private void OnTaskScriptsAvailable(TaskRef task) => _dispatcher.Post(() =>
    {
        if (SelectedEntry?.Id != task.Path || ScriptBlocks.Count > 0)
        {
            return;
        }

        foreach (var runner in _scheduler.GetLiveScripts(task.Path) ?? [])
        {
            AddScriptBlockPanel(new ScriptPanelViewModel(runner, _dispatcher));
        }
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

        if (record.ParseError is { } parseError)
        {
            // Never got as far as constructing an engine (the file itself failed to parse) - every panel was
            // already watching live progress, so this only matters when there's nothing to show per-script.
            ParseError = parseError;
            return;
        }

        // Reconciles every script's panel against the final record - belt-and-suspenders against Dispatcher
        // post-ordering between a panel's own live subscription and this handler (see ScriptPanelViewModel.
        // ApplyFinal), and the only place a Stop settles a script that was killed mid-flight before its own
        // live ScriptRunner ever got the chance to post its final Status change.
        foreach (var script in record.Scripts)
        {
            var panel = ScriptBlocks.FirstOrDefault(p => p.Name == script.Name);
            if (panel is null)
            {
                panel = new ScriptPanelViewModel(script.Name);
                AddScriptBlockPanel(panel);
            }

            panel.ApplyFinal(script.Status, script.Log, record.WasStopped);
        }
    });

    private void AddScriptBlockPanel(ScriptPanelViewModel panel)
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
        if (e.PropertyName != nameof(ScriptPanelViewModel.IsVisible) || sender is not ScriptPanelViewModel panel)
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
        foreach (var panel in ScriptBlocks)
        {
            panel.PropertyChanged -= OnScriptBlockPanelPropertyChanged;
            panel.Dispose();
        }

        ScriptBlocks.Clear();
        VisibleScriptBlocks.Clear();
    }

    public void Dispose()
    {
        _scheduler.TaskRunStarted -= OnAnyRunStarted;
        _scheduler.TaskScriptsAvailable -= OnTaskScriptsAvailable;
        _scheduler.TaskRunCompleted -= OnAnyRunCompleted;
        ClearScriptBlocks();
    }
}
