using System.ComponentModel;
using System.Collections.ObjectModel;
using AutoDev.Core.Models;
using AutoDev.Core.Services;
using AutoDev.ViewModels.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoDev.ViewModels.Content;

/// <summary>One dropdown entry - a script that is currently running or has run at least once before (see ScriptTabViewModel.LoadAsync). IsRunning drives the same "●" indicator the Files sidebar row uses.</summary>
public sealed partial class ScriptEntry(string id, string name) : ViewModelBase
{
    public string Id { get; } = id;

    [ObservableProperty]
    private string name = name;

    [ObservableProperty]
    private bool isRunning;

    public void UpdateFrom(string name) => Name = name;
}

/// <summary>
/// Read-only view of a .cs script's `dotnet run --file` output, switchable via a dropdown between every
/// script that is currently running or has run at least once before (see LoadAsync/Entries) - the last run of
/// a script not currently running stays visible until that script is re-run. Subscribes to the workspace's
/// one IWorkspaceScriptRunner instance for its whole lifetime, so history for every script stays reachable
/// regardless of which one is currently selected for viewing (only one script total ever runs at a time
/// though - see IWorkspaceScriptRunner.RunNowAsync).
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
    }

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

    partial void OnIsRunningChanged(bool value)
    {
        RaiseStateChanged();
        StopCommand.NotifyCanExecuteChanged();
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

    /// <summary>Called once when a workspace tab is opened - seeds the dropdown from persisted run history (plus anything already running), so scripts run in a previous session still show up, not just ones touched this session. There's no central script registry to enumerate (scripts are just .cs files wherever the user put them) - LoadRunScriptRefsAsync derives the list from run history instead, so a script that's never been run doesn't appear until it is.</summary>
    public async Task LoadAsync()
    {
        IReadOnlyList<ScriptRef> scripts = await metadataStore.LoadRunScriptRefsAsync(workspacePath);
        foreach (ScriptRef script in scripts)
        {
            GetOrCreateEntry(script.Path, script.Name).IsRunning = scriptRunner.IsRunning(script.Path);
        }
    }

    /// <summary>Raised by the sidebar's "View" action (or a row activation) - adds the script to the dropdown if it isn't there yet (a never-run script can still be viewed, showing the empty state) and selects it.</summary>
    public void SelectScript(string id, string name)
    {
        ScriptEntry entry = GetOrCreateEntry(id, name);
        entry.IsRunning = scriptRunner.IsRunning(id);
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

    partial void OnSelectedEntryChanged(ScriptEntry? value)
    {
        DetachLiveRun();

        if (value is null)
        {
            HasScript = false;
            IsRunning = false;
            HasResult = false;
            return;
        }

        ScriptName = value.Name;
        HasScript = true;

        if (scriptRunner.GetLiveRun(value.Id) is { } liveRun)
        {
            IsRunning = true;
            HasResult = true;
            AttachLiveRun(liveRun);
            return;
        }

        IsRunning = false;
        _ = LoadMostRecentRunAsync(value.Id);
    }

    private async Task LoadMostRecentRunAsync(string scriptId)
    {
        List<ScriptRunRecord> runs = await metadataStore.LoadScriptRunsAsync(workspacePath, scriptId);

        // Selection moved on, or - the specific race this guards against - a new run of this very script
        // started while this disk read was in flight (e.g. Run re-selects the already-selected script, then
        // starts it, all before this load's await returns): without the second check, this stale historical
        // load would land after OnAnyRunStarted's reset and overwrite the fresh display with the *previous*
        // run's leftover text, which every following progress line would then get appended after.
        if (SelectedEntry?.Id != scriptId || scriptRunner.IsRunning(scriptId))
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

    private bool CanStop() => IsRunning && SelectedEntry is not null;

    /// <summary>Forcefully stops the currently-viewed script's run - see IWorkspaceScriptRunner.StopRun.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        if (SelectedEntry is { } entry)
        {
            scriptRunner.StopRun(entry.Id);
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
        ScriptEntry entry = GetOrCreateEntry(script.Path, script.Name);
        entry.IsRunning = true;
        SelectedEntry ??= entry; // nothing viewed yet this session - default to the first script that starts running

        if (SelectedEntry?.Id != script.Path)
        {
            return;
        }

        // A run of the currently-viewed script just started - clear whatever the previous run left displayed
        // right now, unconditionally, rather than waiting for output to arrive to infer a new run began.
        // Re-running the same script that's already selected doesn't change SelectedEntry (same reference, so
        // OnSelectedEntryChanged never re-fires), so this is the only reliable place left to reset for that
        // case - without it, a re-run's output was appearing appended after the previous run's leftover text
        // instead of replacing it.
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
        ScriptEntry? entry = Entries.FirstOrDefault(e => e.Id == record.FilePath);
        if (entry is not null)
        {
            entry.IsRunning = false;
        }

        if (SelectedEntry?.Id != record.FilePath)
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

    /// <summary>Unsubscribes from the live LiveScriptRun's PropertyChanged, if one is currently attached - a no-op otherwise. Called whenever the viewed script's live run is no longer relevant to this view model (selection changed, a new run started, or the run finished), so a still-running script's continued progress doesn't keep posting into a display that's since moved on.</summary>
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
        DetachLiveRun();
    }
}
