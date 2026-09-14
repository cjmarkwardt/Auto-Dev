using System.ComponentModel;
using System.Collections.ObjectModel;
using AutoDev.Core.Models;
using AutoDev.Core.Services;
using AutoDev.ViewModels.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoDev.ViewModels.Content;

/// <summary>
/// One dropdown entry - either a standalone script (IsTask false, Children empty) that is currently running or
/// has run at least once before, or a task (IsTask true) whose Children are its own one-sub-tab-per-script list
/// (see ScriptTabViewModel.LoadAsync/SelectTask). IsRunning drives the same "●" indicator the Files sidebar row
/// uses - for a task entry it covers the task's whole run, start to finish across every batch (see
/// IWorkspaceScriptRunner.TaskRunStarted/TaskRunCompleted), not just whichever child happens to be selected.
/// </summary>
public sealed partial class ScriptEntry(string id, string name, bool isTask = false) : ViewModelBase
{
    public string Id { get; } = id;

    public bool IsTask { get; } = isTask;

    [ObservableProperty]
    private string name = name;

    [ObservableProperty]
    private bool isRunning;

    /// <summary>Whether this is the currently-viewed sub-tab within its own parent task - unused for a top-level entry. Set explicitly by ScriptTabViewModel (see SetSelectedChild) rather than inferred by reference-comparison in the View, per AGENTS.md's "prefer an explicit, always-available state over relying on a control's own selection".</summary>
    [ObservableProperty]
    private bool isSelected;

    /// <summary>Populated only for a task entry - one child per script it runs, in file order. Empty for a plain script entry.</summary>
    public ObservableCollection<ScriptEntry> Children { get; } = [];

    /// <summary>Which of Children is currently being viewed - remembered per task so switching the dropdown away and back preserves the last-viewed sub-tab. Unused for a plain script entry.</summary>
    [ObservableProperty]
    private ScriptEntry? selectedChild;

    public void UpdateFrom(string name) => Name = name;
}

/// <summary>
/// Read-only view of a .cs script's (or a .task file's group of scripts') `dotnet run --file` output,
/// switchable via a dropdown between every script/task that is currently running or has run at least once
/// before (see LoadAsync/Entries) - the last run of one not currently running stays visible until it's re-run.
/// A task's own scripts are never listed as their own top-level dropdown entries (see ScriptRunRecord.TaskId) -
/// they're only reachable as sub-tabs under their task's entry (see ScriptEntry.Children). Subscribes to the
/// workspace's one IWorkspaceScriptRunner instance for its whole lifetime, so history for every script/task
/// stays reachable regardless of which one is currently selected for viewing (only one script or task total
/// ever runs at a time though - see IWorkspaceScriptRunner.RunNowAsync/RunTaskNowAsync).
/// </summary>
public sealed partial class ScriptTabViewModel : ViewModelBase, IDisposable
{
    private readonly string workspacePath;
    private readonly IWorkspaceMetadataStore metadataStore;
    private readonly IWorkspaceScriptRunner scriptRunner;
    private readonly IClipboardService clipboardService;
    private readonly IUiDispatcher dispatcher;
    private LiveScriptRun? liveRun;
    private PropertyChangedEventHandler? liveRunHandler;

    public ScriptTabViewModel(string workspacePath, IWorkspaceMetadataStore metadataStore, IWorkspaceScriptRunner scriptRunner, IClipboardService clipboardService, IUiDispatcher dispatcher)
    {
        this.workspacePath = workspacePath;
        this.metadataStore = metadataStore;
        this.scriptRunner = scriptRunner;
        this.clipboardService = clipboardService;
        this.dispatcher = dispatcher;

        Entries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasEntries));

        this.scriptRunner.ScriptRunStarted += OnAnyRunStarted;
        this.scriptRunner.ScriptRunCompleted += OnAnyRunCompleted;
        this.scriptRunner.TaskRunStarted += OnAnyTaskStarted;
        this.scriptRunner.TaskRunCompleted += OnAnyTaskCompleted;
    }

    /// <summary>Raised every time AttachOrLoad runs, regardless of whether anything it sets actually changed value - the View's own cue to forget whatever scroll-position state it was tracking for whichever script/task was previously displayed (see ScriptTabView's own OnDisplayedEntryChanged), since that's purely visual state this ViewModel has no business owning itself.</summary>
    public event Action? DisplayedEntryChanged;

    public ObservableCollection<ScriptEntry> Entries { get; } = [];

    public bool HasEntries => Entries.Count > 0;

    [ObservableProperty]
    private ScriptEntry? selectedEntry;

    [ObservableProperty]
    private bool hasScript;

    [ObservableProperty]
    private string scriptName = "";

    [ObservableProperty]
    private bool isRunning;

    [ObservableProperty]
    private bool hasResult;

    [ObservableProperty]
    private bool lastRunFailed;

    /// <summary>True only when the last run ended via an explicit user Stop - drives showing "Stopped" instead of "Failed" in the header.</summary>
    [ObservableProperty]
    private bool lastRunWasStopped;

    /// <summary>The last run's process exit code - null while a run is in flight, or if it was stopped before exiting on its own.</summary>
    [ObservableProperty]
    private int? exitCode;

    [ObservableProperty]
    private string outputText = "";

    partial void OnOutputTextChanged(string value) => CopyOutputCommand.NotifyCanExecuteChanged();

    /// <summary>Not-yet-sent text for the running script's own stdin (see SendInputAsync) - a script that calls Console.ReadLine() would otherwise hang forever, since nothing else in AutoDev ever supplies it input.</summary>
    [ObservableProperty]
    private string inputText = "";

    public bool ShowRunning => IsRunning;
    public bool ShowSucceeded => !IsRunning && HasResult && !LastRunFailed;
    public bool ShowStopped => !IsRunning && HasResult && LastRunFailed && LastRunWasStopped;
    public bool ShowFailed => !IsRunning && HasResult && LastRunFailed && !LastRunWasStopped;

    /// <summary>The entry whose own Running/Succeeded/output state the header/body below is currently displaying - the selected task's own SelectedChild if it's a task, or SelectedEntry itself otherwise.</summary>
    private ScriptEntry? EffectiveEntry => SelectedEntry is { IsTask: true } task ? task.SelectedChild : SelectedEntry;

    partial void OnIsRunningChanged(bool value)
    {
        RaiseStateChanged();
        SendInputCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasResultChanged(bool value) => RaiseStateChanged();
    partial void OnLastRunFailedChanged(bool value) => RaiseStateChanged();
    partial void OnLastRunWasStoppedChanged(bool value) => RaiseStateChanged();

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(ShowRunning));
        OnPropertyChanged(nameof(ShowSucceeded));
        OnPropertyChanged(nameof(ShowStopped));
        OnPropertyChanged(nameof(ShowFailed));
    }

    /// <summary>Called once when a workspace tab is opened - seeds the dropdown from persisted run history (plus anything already running), so scripts/tasks run in a previous session still show up, not just ones touched this session. There's no central script/task registry to enumerate (scripts and tasks are just files wherever the user put them) - LoadRunScriptRefsAsync/LoadRunTaskRefsAsync derive the list from run history instead, so one that's never been run doesn't appear until it is.</summary>
    public async Task LoadAsync()
    {
        IReadOnlyList<ScriptRef> scripts = await metadataStore.LoadRunScriptRefsAsync(workspacePath);
        foreach (ScriptRef script in scripts)
        {
            GetOrCreateEntry(script.Path, script.Name).IsRunning = scriptRunner.IsRunning(script.Path);
        }

        IReadOnlyList<TaskRunRecord> tasks = await metadataStore.LoadRunTaskRefsAsync(workspacePath);
        foreach (TaskRunRecord task in tasks)
        {
            ScriptEntry taskEntry = GetOrCreateTaskEntry(task.FilePath, task.FileName, task.Scripts);
            taskEntry.IsRunning = scriptRunner.IsTaskRunning(task.FilePath);
            foreach (ScriptEntry child in taskEntry.Children)
            {
                child.IsRunning = scriptRunner.IsRunning(child.Id);
            }
        }
    }

    /// <summary>Raised by the sidebar's "View" action (or a row activation) - adds the script to the dropdown if it isn't there yet (a never-run script can still be viewed, showing the empty state) and selects it.</summary>
    public void SelectScript(string id, string name)
    {
        ScriptEntry entry = GetOrCreateEntry(id, name);
        entry.IsRunning = scriptRunner.IsRunning(id);
        SelectedEntry = entry;
    }

    /// <summary>Raised by the sidebar's "Run"/"View" action for a .task file - adds/refreshes the task's own dropdown entry (never one per script - see ScriptEntry.Children) and selects it.</summary>
    public void SelectTask(string id, string name, IReadOnlyList<ScriptRef> scripts)
    {
        ScriptEntry entry = GetOrCreateTaskEntry(id, name, scripts);
        entry.IsRunning = scriptRunner.IsTaskRunning(id);
        foreach (ScriptEntry child in entry.Children)
        {
            child.IsRunning = scriptRunner.IsRunning(child.Id);
        }

        SelectedEntry = entry;
    }

    private ScriptEntry GetOrCreateEntry(string id, string name)
    {
        ScriptEntry? existing = Entries.FirstOrDefault(e => e.Id == id);
        if (existing is not null)
        {
            existing.UpdateFrom(name);
            return existing;
        }

        ScriptEntry entry = new ScriptEntry(id, name);
        Entries.Add(entry);
        return entry;
    }

    private ScriptEntry GetOrCreateTaskEntry(string id, string name, IReadOnlyList<ScriptRef> scripts)
    {
        ScriptEntry? existing = Entries.FirstOrDefault(e => e.Id == id);
        ScriptEntry entry;
        if (existing is not null)
        {
            existing.UpdateFrom(name);
            entry = existing;
        }
        else
        {
            entry = new ScriptEntry(id, name, isTask: true);
            Entries.Add(entry);
        }

        SyncTaskChildren(entry, scripts);
        return entry;
    }

    /// <summary>
    /// Reconciles a task entry's own Children against its current script list - a fast path when it's
    /// unchanged since last time (the common case: re-running/re-viewing the same task repeatedly), which
    /// preserves each child's own identity/selection instead of rebuilding, and a slow path (the task file's own
    /// script list actually changed) that rebuilds from scratch, carrying the previous selection over by id if
    /// it still exists.
    /// </summary>
    private static void SyncTaskChildren(ScriptEntry taskEntry, IReadOnlyList<ScriptRef> scripts)
    {
        if (taskEntry.Children.Select(c => c.Id).SequenceEqual(scripts.Select(s => s.Path)))
        {
            for (int i = 0; i < scripts.Count; i++)
            {
                taskEntry.Children[i].UpdateFrom(scripts[i].Name);
            }

            return;
        }

        string? previousSelectionId = taskEntry.SelectedChild?.Id;
        taskEntry.Children.Clear();
        foreach (ScriptRef script in scripts)
        {
            taskEntry.Children.Add(new ScriptEntry(script.Path, script.Name));
        }

        ScriptEntry? restoredSelection = taskEntry.Children.FirstOrDefault(c => c.Id == previousSelectionId) ?? taskEntry.Children.FirstOrDefault();
        SetSelectedChild(taskEntry, restoredSelection);
    }

    private static void SetSelectedChild(ScriptEntry task, ScriptEntry? child)
    {
        if (task.SelectedChild is { } previous)
        {
            previous.IsSelected = false;
        }

        task.SelectedChild = child;
        if (child is not null)
        {
            child.IsSelected = true;
        }
    }

    partial void OnSelectedEntryChanged(ScriptEntry? value)
    {
        if (value is { IsTask: true } task)
        {
            ScriptEntry? child = task.SelectedChild ?? task.Children.FirstOrDefault();
            SetSelectedChild(task, child);
            AttachOrLoad(child);
        }
        else
        {
            AttachOrLoad(value);
        }

        StopCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Raised by the sub-tab strip shown while SelectedEntry is a task - switches which of its scripts is currently displayed, without affecting the task's own run (Stop still applies to the whole task regardless of which sub-tab is showing - see CanStop).</summary>
    [RelayCommand]
    private void SelectChildTab(ScriptEntry child)
    {
        if (SelectedEntry is not { IsTask: true } task || task.SelectedChild == child)
        {
            return;
        }

        SetSelectedChild(task, child);
        AttachOrLoad(child);
    }

    /// <summary>Attaches to entry's own live run if it's currently in flight, or loads its most recent historical run otherwise - shared by both a top-level selection change and a sub-tab switch, since either one just changes which single script's own output is currently being displayed.</summary>
    private void AttachOrLoad(ScriptEntry? entry)
    {
        DisplayedEntryChanged?.Invoke();
        DetachLiveRun();

        if (entry is null)
        {
            HasScript = false;
            IsRunning = false;
            HasResult = false;
            return;
        }

        ScriptName = entry.Name;
        HasScript = true;

        if (scriptRunner.GetLiveRun(entry.Id) is { } liveRun)
        {
            IsRunning = true;
            HasResult = true;
            AttachLiveRun(liveRun);
            return;
        }

        IsRunning = false;
        _ = LoadMostRecentRunAsync(entry.Id);
    }

    private async Task LoadMostRecentRunAsync(string scriptId)
    {
        List<ScriptRunRecord> runs = await metadataStore.LoadScriptRunsAsync(workspacePath, scriptId);

        // Selection moved on, or - the specific race this guards against - a new run of this very script
        // started while this disk read was in flight (e.g. Run re-selects the already-selected script, then
        // starts it, all before this load's await returns): without the second check, this stale historical
        // load would land after OnAnyRunStarted's reset and overwrite the fresh display with the *previous*
        // run's leftover text, which every following progress line would then get appended after.
        if (EffectiveEntry?.Id != scriptId || scriptRunner.IsRunning(scriptId))
        {
            return;
        }

        ScriptRunRecord? mostRecentRun = runs.FirstOrDefault();
        if (mostRecentRun is not null)
        {
            HasResult = true;
            LastRunFailed = !mostRecentRun.Success;
            LastRunWasStopped = mostRecentRun.WasStopped;
            ExitCode = mostRecentRun.ExitCode;
            OutputText = mostRecentRun.Output;
        }
        else
        {
            HasResult = false;
        }
    }

    /// <summary>SelectedEntry.IsRunning alone (not the header's own IsRunning) - so Stop stays available for a whole task while it's still in progress even if the specific sub-tab currently being viewed has already finished (see ScriptEntry.IsRunning's own doc comment).</summary>
    private bool CanStop() => SelectedEntry?.IsRunning == true;

    /// <summary>Forcefully stops the currently-selected script's run, or every script in the currently-selected task - see IWorkspaceScriptRunner.StopRun/StopTask.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        if (SelectedEntry is not { } entry)
        {
            return;
        }

        if (entry.IsTask)
        {
            scriptRunner.StopTask(entry.Id);
        }
        else
        {
            scriptRunner.StopRun(entry.Id);
        }
    }

    private bool CanRemoveEntry(ScriptEntry? entry) => entry is { IsRunning: false };

    /// <summary>Removes a script or task from the dropdown entirely and deletes its persisted run history, so it doesn't reappear the next time this workspace tab is opened (see LoadAsync) - disabled while it's currently running (Stop it first). Clears SelectedEntry if the removed entry was the one being viewed.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveEntry))]
    private void RemoveEntry(ScriptEntry entry)
    {
        Entries.Remove(entry);
        if (SelectedEntry == entry)
        {
            SelectedEntry = null;
        }

        if (entry.IsTask)
        {
            metadataStore.DeleteTaskRuns(workspacePath, entry.Id);
        }
        else
        {
            metadataStore.DeleteScriptRuns(workspacePath, entry.Id);
        }
    }

    /// <summary>
    /// Copies the whole of OutputText to the clipboard, regardless of whatever text selection (if any) the
    /// SelectableTextBlock displaying it currently has - a dedicated, always-reliable way to copy everything
    /// shown, rather than depending on that control's own built-in select-all-then-copy, which can leave Copy
    /// disabled after enough live text updates have gone by while it held a selection (a script's own output
    /// streaming in updates OutputText - and so the control's bound Text - continuously while it runs).
    /// </summary>
    private bool CanCopyOutput() => OutputText.Length > 0;

    [RelayCommand(CanExecute = nameof(CanCopyOutput))]
    private async Task CopyOutputAsync() => await clipboardService.SetTextAsync(OutputText);

    private bool CanSendInput() => IsRunning && liveRun is not null && InputText.Length > 0;

    /// <summary>Sends InputText to the currently-viewed script's own stdin and clears the box - see LiveScriptRun.SendInputAsync.</summary>
    [RelayCommand(CanExecute = nameof(CanSendInput))]
    private async Task SendInputAsync()
    {
        if (liveRun is not { } run)
        {
            return;
        }

        string text = InputText;
        InputText = "";
        await run.SendInputAsync(text);
    }

    partial void OnInputTextChanged(string value) => SendInputCommand.NotifyCanExecuteChanged();

    private void OnAnyRunStarted(ScriptRef script) => dispatcher.Post(() =>
    {
        ScriptEntry? entry;
        if (script.TaskId is { } taskId)
        {
            entry = Entries.FirstOrDefault(e => e.Id == taskId)?.Children.FirstOrDefault(c => c.Id == script.Path);
            if (entry is null)
            {
                return; // this task's entry isn't tracked here yet - its own OnAnyTaskStarted has nothing to seed children from either
            }
        }
        else
        {
            entry = GetOrCreateEntry(script.Path, script.Name);
            SelectedEntry ??= entry; // nothing viewed yet this session - default to the first script that starts running
        }

        entry.IsRunning = true;
        StopCommand.NotifyCanExecuteChanged();

        if (EffectiveEntry?.Id != script.Path)
        {
            return;
        }

        // A run of the currently-displayed script just started - clear whatever the previous run left
        // displayed right now, unconditionally, rather than waiting for output to arrive to infer a new run
        // began. Re-running the same script that's already selected doesn't change SelectedEntry (same
        // reference, so OnSelectedEntryChanged never re-fires), so this is the only reliable place left to
        // reset for that case - without it, a re-run's output was appearing appended after the previous run's
        // leftover text instead of replacing it.
        DetachLiveRun();
        IsRunning = true;
        HasResult = true;
        LastRunFailed = false;
        LastRunWasStopped = false;
        ExitCode = null;
        OutputText = "";

        if (scriptRunner.GetLiveRun(script.Path) is { } liveRun)
        {
            AttachLiveRun(liveRun);
        }
    });

    private void OnAnyRunCompleted(ScriptRunRecord record) => dispatcher.Post(() =>
    {
        ScriptEntry? entry = record.TaskId is { } taskId
            ? Entries.FirstOrDefault(e => e.Id == taskId)?.Children.FirstOrDefault(c => c.Id == record.FilePath)
            : Entries.FirstOrDefault(e => e.Id == record.FilePath);

        if (entry is not null)
        {
            entry.IsRunning = false;
            StopCommand.NotifyCanExecuteChanged();
        }

        if (EffectiveEntry?.Id != record.FilePath)
        {
            return;
        }

        // Reconciles the display against the final record - belt-and-suspenders against Dispatcher
        // post-ordering between the live run's own OutputText subscription and this handler.
        DetachLiveRun();
        IsRunning = false;
        HasResult = true;
        LastRunFailed = !record.Success;
        LastRunWasStopped = record.WasStopped;
        ExitCode = record.ExitCode;
        OutputText = record.Output;
    });

    private void OnAnyTaskStarted(TaskRef task) => dispatcher.Post(() =>
    {
        if (Entries.FirstOrDefault(e => e.Id == task.Path) is not { IsTask: true } entry)
        {
            return; // SelectTask always runs first (see FilesSectionViewModel.RunTaskAsync) - nothing to mark here otherwise
        }

        entry.IsRunning = true;
        SelectedEntry ??= entry;
        StopCommand.NotifyCanExecuteChanged();
    });

    private void OnAnyTaskCompleted(TaskRunRecord record) => dispatcher.Post(() =>
    {
        if (Entries.FirstOrDefault(e => e.Id == record.FilePath) is { IsTask: true } entry)
        {
            entry.IsRunning = false;
            StopCommand.NotifyCanExecuteChanged();
        }
    });

    private void AttachLiveRun(LiveScriptRun liveRun)
    {
        this.liveRun = liveRun;
        OutputText = liveRun.OutputText;
        SendInputCommand.NotifyCanExecuteChanged();
        liveRunHandler = (_, e) => dispatcher.Post(() =>
        {
            if (e.PropertyName == nameof(LiveScriptRun.OutputText))
            {
                OutputText = liveRun.OutputText;
            }
        });
        liveRun.PropertyChanged += liveRunHandler;
    }

    /// <summary>Unsubscribes from the live LiveScriptRun's PropertyChanged, if one is currently attached - a no-op otherwise. Called whenever the displayed script's live run is no longer relevant to this view model (selection/sub-tab changed, a new run started, or the run finished), so a still-running script's continued progress doesn't keep posting into a display that's since moved on.</summary>
    private void DetachLiveRun()
    {
        if (liveRun is not null && liveRunHandler is not null)
        {
            liveRun.PropertyChanged -= liveRunHandler;
        }

        liveRun = null;
        liveRunHandler = null;
        SendInputCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        scriptRunner.ScriptRunStarted -= OnAnyRunStarted;
        scriptRunner.ScriptRunCompleted -= OnAnyRunCompleted;
        scriptRunner.TaskRunStarted -= OnAnyTaskStarted;
        scriptRunner.TaskRunCompleted -= OnAnyTaskCompleted;
        DetachLiveRun();
    }
}
