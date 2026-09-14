using Avalonia.Controls;
using AutoDev.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoDev.ViewModels.Content;

public sealed partial class WorkspaceContentViewModel(
    EditTabViewModel edit,
    GenerateTabViewModel generate,
    HistoryTabViewModel history,
    ScriptTabViewModel script,
    CommandTabViewModel command)
    : ViewModelBase, IAsyncDisposable
{
    public const int GenerateTabIndex = 0;
    public const int HistoryTabIndex = 1;
    public const int ScriptTabIndex = 2;
    public const int CommandTabIndex = 3;

    public EditTabViewModel Edit { get; } = edit;
    public GenerateTabViewModel Generate { get; } = generate;
    public HistoryTabViewModel History { get; } = history;
    public ScriptTabViewModel Script { get; } = script;
    public CommandTabViewModel Command { get; } = command;

    private GitTarget? lastTarget;
    private bool isBusy;
    private bool isAiWorking;
    private bool hasRunningScripts;

    /// <summary>History is the tab shown first when a workspace opens (fresh, cloned, or restored on launch) - a new WorkspaceContentViewModel is created exactly once per opened workspace tab, so this default alone covers every case. History's own data still populates correctly despite [ObservableProperty]'s change hooks never firing for an unchanged initial value (skipping the HistoryTabIndex case in OnSelectedTabIndexChanged below) - HistoryTabViewModel independently reloads on Version.TargetChanged, which always fires once during WorkspaceViewModel.InitializeAsync regardless of which tab is selected.</summary>
    [ObservableProperty]
    private int selectedTabIndex = HistoryTabIndex;

    /// <summary>Edit pane / right-tabs column widths, bound two-way from WorkspaceContentView.axaml's ColumnDefinitions - persisted only in-memory for this workspace tab's lifetime, so a GridSplitter drag survives switching to a different open workspace tab and back (the View, not this VM, is torn down/rebuilt on that switch). Both sides are star-sized, so both need binding - dragging shifts the ratio between them rather than one side absorbing a fixed remainder.</summary>
    [ObservableProperty]
    private GridLength editColumnWidth = new(1, GridUnitType.Star);

    [ObservableProperty]
    private GridLength tabsColumnWidth = new(1, GridUnitType.Star);

    partial void OnSelectedTabIndexChanged(int value)
    {
        switch (value)
        {
            case GenerateTabIndex:
                Generate.RequestFocus();
                break;
            case HistoryTabIndex:
                _ = History.RefreshFromRemoteAsync();
                break;
        }
    }

    /// <summary>Edit is an always-visible left pane now (see WorkspaceContentView.axaml), not a switchable tab - opening a file just loads it there and focuses it, without touching SelectedTabIndex.</summary>
    public async Task OpenFileAsync(string path, int? seekLine = null)
    {
        await Edit.LoadFileAsync(path, seekLine);
        UpdateEditReadOnly();
        Edit.RequestFocus();
    }

    /// <summary>
    /// Generate/Edit are only fully usable while targeting a branch (a tag/commit is a read-only historical
    /// snapshot) - see GenerateTabViewModel.SwitchSessionAsync and EditTabViewModel.IsReadOnly.
    /// </summary>
    public async Task ApplyTargetStateAsync(GitTarget? target)
    {
        lastTarget = target;
        UpdateEditReadOnly();

        string? sessionKey = target is { Kind: GitTargetKind.Branch, BranchName: { } branchName } ? branchName : null;
        await Generate.SwitchSessionAsync(sessionKey);
    }

    /// <summary>
    /// Edit is also forced read-only for the whole duration of a Generate turn, or any plain (non-AI) version
    /// action running its own git commands (Merge/Publish/Iterate/Update/a
    /// History switch/etc.) - regardless of target mode. See VersionSectionViewModel.IsInteractionBlocked,
    /// which is the OR of the two flags kept separate here only so ComputeReadOnlyReason can report which one
    /// actually applies.
    /// </summary>
    public void ApplyInteractionBlockedState(bool isBusy, bool isAiWorking)
    {
        this.isBusy = isBusy;
        this.isAiWorking = isAiWorking;
        UpdateEditReadOnly();
    }

    /// <summary>Edit is also forced read-only for as long as any .cs file in this workspace is running - manual editing, script running, and AI working are meant to be mutually exclusive states over the same working tree. Mirrors ApplyInteractionBlockedState, kept as its own method/flag purely so ComputeReadOnlyReason can report which one actually applies. Called from WorkspaceViewModel whenever FilesSectionViewModel.HasRunningScripts changes.</summary>
    public void ApplyHasRunningScriptsState(bool hasRunningScripts)
    {
        this.hasRunningScripts = hasRunningScripts;
        UpdateEditReadOnly();
    }

    private bool IsEditableTarget => lastTarget?.Kind == GitTargetKind.Branch;

    private void UpdateEditReadOnly()
    {
        Edit.IsReadOnly = isBusy || isAiWorking || hasRunningScripts || !IsEditableTarget;
        Edit.ReadOnlyReason = Edit.IsReadOnly ? ComputeReadOnlyReason() : "";
    }

    /// <summary>The specific, currently-true reason editing is blocked - checked in the same priority order UpdateEditReadOnly itself uses (AI-working/busy/script-running override target kind, since any of them locks a branch target too).</summary>
    private string ComputeReadOnlyReason()
    {
        if (isAiWorking)
        {
            return "Read-only — AI is currently working.";
        }

        if (isBusy)
        {
            return "Read-only — a version action is in progress.";
        }

        if (hasRunningScripts)
        {
            return "Read-only — a script is running.";
        }

        return lastTarget?.Kind switch
        {
            GitTargetKind.Tag => "Read-only — this is a tag, a historical snapshot that can't be edited.",
            GitTargetKind.Commit => "Read-only — this is a detached commit, a historical snapshot that can't be edited.",
            _ => "Read-only — no branch is targeted.",
        };
    }

    public async ValueTask DisposeAsync()
    {
        await Generate.DisposeAsync();
        Script.Dispose();
        Command.Dispose();
    }
}
