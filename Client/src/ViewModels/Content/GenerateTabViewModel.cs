using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using AutoDev.AiCli;
using AutoDev.AiCli.Models;
using AutoDev.Core.Models;
using AutoDev.Core.Services;
using AutoDev.ViewModels.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AutoDev.ViewModels.Content;

public sealed partial class GenerateTabViewModel : ViewModelBase, IAsyncDisposable
{
    private static readonly TimeSpan DraftAutoSaveDebounce = TimeSpan.FromMilliseconds(500);

    /// <summary>Only the last 10 requests are ever kept, per session - see Requests/SendAsync's eviction and IWorkspaceMetadataStore.SaveGenerateRequestsAsync.</summary>
    private const int MaxRequests = 10;

    /// <summary>
    /// How long a request can go with no event at all from the CLI subprocess (not even an intermediate tool
    /// call) before StallWatchdogElapsedAsync gives up on it and force-cancels it - see there. Every known way
    /// this pipeline has actually gotten stuck (an unparseable stdout line silently killing the read loop, a
    /// stderr pipe filling and blocking the child's own writes) has already been hardened against directly in
    /// ClaudeSessionClient, but each of those was only found after the fact, one incident at a time. This
    /// timeout is the backstop meant to make the NEXT undiscovered way a turn can wedge self-heal within a
    /// bounded time instead of sitting stuck indefinitely (one prior incident sat "Working" for over an hour)
    /// - it doesn't matter *why* nothing arrived, only that nothing did.
    /// </summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan StallCheckInterval = TimeSpan.FromSeconds(15);

    private readonly string _workspacePath;
    private readonly IAiSessionClientFactory _sessionClientFactory;
    private readonly IAiProviderSelectionService _providerSelection;
    private readonly IWorkspaceMetadataStore _metadataStore;
    private readonly IUsageAggregatorService _usageAggregator;
    private readonly ISoundService _soundService;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<GenerateTabViewModel> _logger;

    private IAiSessionClient? _client;
    private string? _resumeSessionId;
    private string? _currentSessionKey;
    private TaskCompletionSource<bool>? _pendingAutomatedTurn;
    private CancellationTokenSource? _draftDebounceCts;

    /// <summary>True during a hidden automated turn (see RunAutomatedTurnAsync's visible: false path) - its assistant text is captured into _hiddenTurnText, never into an active request.</summary>
    private bool _hiddenTurnActive;
    private readonly StringBuilder _hiddenTurnText = new();

    /// <summary>
    /// True during a visible-but-automated turn (RunAutomatedTurnAsync's visible: true path - the
    /// conflict-resolution loop shared by Rebase, Merge Into Current/Rebase Current Onto This, and the
    /// stash-pop conflict path in PullWithStashIfNeededAsync). Distinct from _hiddenTurnActive/_activeRequest
    /// both: this kind of turn is neither hidden from the user's eventual read of LastAssistantText, nor is it
    /// a user-submitted request that should ever appear as a request card - GenerateTabView shows its own
    /// separate panel instead (bound to this property), with only Pause/Resume offered - see CanPause/
    /// CanResume/PauseAsync/ResumeAsync, all of which branch on this alongside _activeRequest. Critically,
    /// ResolveConflictsAsync runs this turn nested inside OnGenerateNormalTurnCompleted - i.e. after a real
    /// request's own ResultEvent already marked it Completed - while IsSending flips true again for the
    /// exchange; without this separate flag, that turn's assistant text would land in the just-completed
    /// request and corrupt already-persisted data. LastAssistantText reads from _visibleAutomatedTurnText,
    /// never from a request. An [ObservableProperty] (unlike _hiddenTurnActive, which the UI never needs to
    /// react to) purely so the View's panel and OnVisibleAutomatedTurnActiveChanged below can bind/react to it.
    /// </summary>
    [ObservableProperty]
    private bool _visibleAutomatedTurnActive;
    private readonly StringBuilder _visibleAutomatedTurnText = new();

    partial void OnVisibleAutomatedTurnActiveChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ConflictResolutionStatusText));
    }

    /// <summary>The request currently being worked on, if any - set only by SendAsync's real (non-automated) turn path, cleared once its ResultEvent/Cancel arrives. Never touched by automated turns (hidden or visible) - see _hiddenTurnActive/_visibleAutomatedTurnActive.</summary>
    private GenerateRequestViewModel? _activeRequest;

    /// <summary>
    /// Count of messages sent for the active request (the initial send, plus one per interjection) that
    /// haven't yet had their own ResultEvent accounted for. An interjection sent mid-turn can produce its
    /// own separate ResultEvent from the CLI before the interjected follow-up content is actually
    /// addressed - without this counter, that intermediate event would mark the request Completed and
    /// play the ding while Claude is still about to keep working on the interjection, which reads as the
    /// ding firing before the workspace is actually done and ready for a new request. Only ever
    /// incremented/decremented alongside _activeRequest - irrelevant to automated turns.
    /// </summary>
    private int _pendingTurnCount;

    /// <summary>
    /// True once the user has clicked Cancel on the active request and its "please stop and revert" message
    /// has been sent, but its own ResultEvent hasn't arrived yet - see CancelAsync. Checked (and reset) only
    /// in Handle()'s ResultEvent case, to finalize the turn as Cancelled instead of Completed once Claude
    /// actually finishes responding to that request; also reset at the start of every new real turn
    /// (SendAsync) and in FinalizeActiveRequestAsync, so it can never leak into a later, unrelated turn.
    /// </summary>
    private bool _cancelRequested;

    /// <summary>
    /// Accumulates the active request's assistant text as it streams in - see CaptureActiveRequestOutput,
    /// which also live-updates _activeRequest.Output with each new segment (replacing, not appending) so the
    /// output section shows Claude's latest words as they arrive rather than staying empty until the turn
    /// fully finishes. A normal ResultEvent completion still prefers the CLI's own clean Result text over
    /// this whole buffer (see the ResultEvent case in Handle) - this is only the fallback for that
    /// (should-never-happen) case. A cancel/timeout finish instead uses _lastActiveRequestSegment, not this -
    /// see its own doc comment for why.
    /// </summary>
    private readonly StringBuilder _activeRequestOutputBuffer = new();

    /// <summary>
    /// The single most recent text block captured for the active request (overwritten, not appended - see
    /// CaptureActiveRequestOutput), separately from _activeRequestOutputBuffer's full running history of
    /// every block said so far. A cancelled or stall-watchdog-timed-out turn never got a real ResultEvent
    /// with the CLI's own clean final answer, so FinalizeActiveRequestAsync shows this instead of the whole
    /// buffer - the buffer mixes in every intermediate narration segment (e.g. "let me check that" before a
    /// tool call) alongside whatever final summary the assistant managed to produce before being cut off,
    /// which read as a wall of stale play-by-play rather than the one thing the user actually wants to see.
    /// </summary>
    private string _lastActiveRequestSegment = "";

    /// <summary>Stamped at turn-start and on every single event Handle() receives thereafter (any event proves the pipe/process is still alive, not just a ResultEvent) - see StallWatchdogElapsedAsync.</summary>
    private DateTimeOffset _lastEventReceivedAt;

    private readonly System.Timers.Timer _stallWatchdogTimer;

    /// <summary>Ticks GenerateRequestViewModel.ElapsedDisplay for whichever request is active, once a second, so the elapsed time shown next to the current action keeps counting up live rather than only refreshing whenever some other event happens to arrive.</summary>
    private static readonly TimeSpan ElapsedDisplayTickInterval = TimeSpan.FromSeconds(1);

    private readonly System.Timers.Timer _elapsedDisplayTimer;

    public GenerateTabViewModel(
        string workspacePath,
        IAiSessionClientFactory sessionClientFactory,
        IAiProviderSelectionService providerSelection,
        IWorkspaceMetadataStore metadataStore,
        IUsageAggregatorService usageAggregator,
        ISoundService soundService,
        IUiDispatcher dispatcher,
        ILogger<GenerateTabViewModel> logger)
    {
        _workspacePath = workspacePath;
        _sessionClientFactory = sessionClientFactory;
        _providerSelection = providerSelection;
        _metadataStore = metadataStore;
        _usageAggregator = usageAggregator;
        _soundService = soundService;
        _dispatcher = dispatcher;
        _logger = logger;
        Attachments.CollectionChanged += OnAttachmentsChanged;
        FileAttachments.CollectionChanged += OnAttachmentsChanged;
        Requests.CollectionChanged += OnRequestsChanged;
        _providerSelection.ProviderChanged += OnProviderChanged;

        ResetModelAndEffortForProvider();

        _stallWatchdogTimer = new System.Timers.Timer(StallCheckInterval) { AutoReset = true };
        _stallWatchdogTimer.Elapsed += (_, _) => _dispatcher.Post(() => _ = StallWatchdogElapsedAsync());
        _stallWatchdogTimer.Start();

        _elapsedDisplayTimer = new System.Timers.Timer(ElapsedDisplayTickInterval) { AutoReset = true };
        _elapsedDisplayTimer.Elapsed += (_, _) => _dispatcher.Post(() => _activeRequest?.RefreshElapsedDisplay());
        _elapsedDisplayTimer.Start();
    }

    /// <summary>The last up-to-5 requests for the current session, oldest first - see SwitchSessionAsync (loaded from disk) and SendAsync (created/evicted live).</summary>
    public ObservableCollection<GenerateRequestViewModel> Requests { get; } = [];

    [ObservableProperty]
    private int _displayedIndex = -1;

    public GenerateRequestViewModel? DisplayedRequest => DisplayedIndex >= 0 && DisplayedIndex < Requests.Count ? Requests[DisplayedIndex] : null;

    public string RequestPositionLabel => Requests.Count == 0 ? "" : $"{DisplayedIndex + 1} of {Requests.Count}";

    public bool CanGoPrevious => DisplayedIndex > 0;

    public bool CanGoNext => DisplayedIndex >= 0 && DisplayedIndex < Requests.Count - 1;

    partial void OnDisplayedIndexChanged(int value)
    {
        OnPropertyChanged(nameof(DisplayedRequest));
        OnPropertyChanged(nameof(RequestPositionLabel));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        PreviousRequestCommand.NotifyCanExecuteChanged();
        NextRequestCommand.NotifyCanExecuteChanged();
    }

    private void OnRequestsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // DisplayedIndex's numeric value can stay the same across an evict+add (e.g. 4 -> 4) while the
        // object it points to in Requests changes - that leaves OnDisplayedIndexChanged unfired, so
        // DisplayedRequest must be re-notified unconditionally here rather than relying on that handler.
        OnPropertyChanged(nameof(DisplayedRequest));
        OnPropertyChanged(nameof(RequestPositionLabel));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        PreviousRequestCommand.NotifyCanExecuteChanged();
        NextRequestCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void PreviousRequest() => DisplayedIndex--;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void NextRequest() => DisplayedIndex++;

    /// <summary>Images pasted into the input box but not yet sent - cleared on send (see SendAsync) or when switching sessions.</summary>
    public ObservableCollection<ChatImageAttachment> Attachments { get; } = [];

    /// <summary>Non-image files pasted/dropped into the input box but not yet sent - same lifecycle as Attachments (see AddFileReference, SendAsync).</summary>
    public ObservableCollection<ChatFileAttachment> FileAttachments { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    public bool HasFileAttachments => FileAttachments.Count > 0;

    public bool HasAnyAttachments => HasAttachments || HasFileAttachments;

    private void OnAttachmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(HasFileAttachments));
        OnPropertyChanged(nameof(HasAnyAttachments));
        SendCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RemoveAttachment(ChatImageAttachment attachment) => Attachments.Remove(attachment);

    [RelayCommand]
    private void RemoveFileAttachment(ChatFileAttachment attachment) => FileAttachments.Remove(attachment);

    /// <summary>Decodes a clipboard bitmap (raw pixel paste - e.g. a screenshot tool's "copy image") into a pending attachment, re-encoding it as PNG since that's the only format a Bitmap can be read back out as.</summary>
    public void AddImageAttachment(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        Attachments.Add(new ChatImageAttachment("image/png", Convert.ToBase64String(stream.ToArray()), bitmap));
    }

    /// <summary>Decodes already-encoded image bytes (e.g. read from a pasted/dropped image file) into a pending attachment, preserving the original format instead of forcing a PNG re-encode.</summary>
    public void AddImageAttachment(byte[] bytes, string mediaType)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);
            Attachments.Add(new ChatImageAttachment(mediaType, Convert.ToBase64String(bytes), bitmap));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decode a pasted/dropped image file");
        }
    }

    /// <summary>A pasted/dropped non-image file can't be embedded like an image, so it's referenced by path instead - tracked as its own removable pending attachment (see FileAttachments) rather than mutating the visible/editable InputText. Composed into the outgoing message as a plain "Attached file: X" line only at send time (see SendAsync), where Claude's own Read tool can open it.</summary>
    public void AddFileReference(string path)
    {
        string display;
        try
        {
            display = Path.GetRelativePath(_workspacePath, path);
            if (display.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(display))
            {
                display = path;
            }
        }
        catch (ArgumentException)
        {
            display = path;
        }

        FileAttachments.Add(new ChatFileAttachment(display, path));
    }

    [ObservableProperty]
    private string _inputText = "";

    [ObservableProperty]
    private bool _isSending;

    /// <summary>Generate is only usable while targeting a feature, or a version with direct mode on - see WorkspaceContentViewModel.ApplyTargetStateAsync, which drives this via SwitchSessionAsync.</summary>
    [ObservableProperty]
    private bool _isEditable;

    /// <summary>
    /// Set by WorkspaceTabViewModel from VersionSectionViewModel.IsBusy - true while a plain (non-AI) version
    /// action (Merge, Publish, Iterate, Update, a History switch, etc.) is running its own git commands. A
    /// user-submitted turn started during that window would run Claude's own tool calls against the working
    /// tree at the exact same time the versioning service is checking it out/committing/rebasing it - blocking
    /// Send here closes that race, the same way IsInteractionBlocked already disables the Version section's
    /// own menu and History's switch rows for the mirror-image case (an in-progress AI turn).
    /// </summary>
    [ObservableProperty]
    private bool _isVersionActionBusy;

    partial void OnIsVersionActionBusyChanged(bool value) => SendCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// Set by WorkspaceTabViewModel from FilesSectionViewModel.HasRunningTasks - true while any .task file in
    /// this workspace has a run in flight. AI work should only ever start while nothing else is running
    /// against the same working tree, for the same race-avoidance reason IsVersionActionBusy exists - unlike
    /// that flag, this one also disables the input box itself (see GenerateTabView.axaml's InputBox), not just
    /// Send, per this feature's own explicit "disable input" requirement.
    /// </summary>
    [ObservableProperty]
    private bool _hasRunningTasks;

    /// <summary>A request whose own ResultEvent already arrived while HasRunningTasks was still true - held back from its own final status/the ding until OnHasRunningTasksChanged sees it clear. See the ResultEvent handler in Handle().</summary>
    private GenerateRequestViewModel? _pendingTaskCompletionRequest;

    /// <summary>The status _pendingTaskCompletionRequest should actually finalize as (Completed, or Cancelled if the user had asked Claude to stop and revert - see CancelAsync) - captured alongside it rather than re-read from _cancelRequested later, since that field could otherwise already belong to a different, newer turn by the time a background task finally finishes.</summary>
    private GenerateRequestStatus _pendingTaskCompletionStatus = GenerateRequestStatus.Completed;

    partial void OnHasRunningTasksChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();

        if (!value && _pendingTaskCompletionRequest is { } request)
        {
            _pendingTaskCompletionRequest = null;
            request.Status = _pendingTaskCompletionStatus;
            _ = PersistCurrentRequestsAsync();
            _soundService.PlayDing();
        }
    }

    /// <summary>The resizable input row's height, bound two-way from GenerateTabView.axaml's RowDefinition - persisted only in-memory for this tab's lifetime (see WorkspaceContentViewModel.EditColumnWidth's identical reasoning).</summary>
    [ObservableProperty]
    private GridLength _inputRowHeight = new(140);

    /// <summary>Claude's own CLI model aliases, matching what `claude --model` accepts.</summary>
    private readonly IReadOnlyList<string> _claudeModels = ["sonnet", "opus", "haiku"];

    /// <summary>Codex's own model catalog slugs (see `codex debug models`) - restricted to the general-purpose, non-hidden entries.</summary>
    private readonly IReadOnlyList<string> _codexModels = ["gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.5", "gpt-5.4-mini"];

    /// <summary>"default" omits the effort flag entirely, leaving the CLI's own default in place; the rest map straight to `claude --effort`/`codex -c model_reasoning_effort`. "max"/"ultra" are Claude-only (not every Codex model supports them) - see AvailableEfforts.</summary>
    private readonly IReadOnlyList<string> _claudeEfforts = ["default", "low", "medium", "high", "xhigh", "max"];

    private readonly IReadOnlyList<string> _codexEfforts = ["default", "low", "medium", "high", "xhigh"];

    [ObservableProperty]
    private IReadOnlyList<string> _availableModels = [];

    [ObservableProperty]
    private IReadOnlyList<string> _availableEfforts = [];

    [ObservableProperty]
    private string _selectedModel = "";

    [ObservableProperty]
    private string _selectedEffort = "default";

    partial void OnIsSendingChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ConflictResolutionStatusText));
    }

    /// <summary>
    /// A model/effort change only takes effect on the next turn: if a session is already live, tear it down
    /// now (keeping its session id so the next turn resumes the same conversation, just under the new
    /// settings) rather than restarting mid-response, which OnIsSendingChanged's CanExecute gating on the
    /// View's selectors already prevents anyway.
    /// </summary>
    partial void OnSelectedModelChanged(string value) => RestartClientForSettingsChange();

    partial void OnSelectedEffortChanged(string value) => RestartClientForSettingsChange();

    /// <summary>
    /// Unlike a plain model/effort change, switching provider can never resume the old session - a Claude
    /// session id and a Codex thread id mean nothing to the other provider's CLI - so this clears
    /// _resumeSessionId outright instead of preserving it the way RestartClientForSettingsChange does.
    /// AvailableModels/AvailableEfforts are reassigned to the new provider's own lists and SelectedModel/
    /// SelectedEffort reset to their defaults, which harmlessly re-triggers RestartClientForSettingsChange
    /// via the partial hooks above - _client is already null below by the time that happens.
    /// </summary>
    private void OnProviderChanged(AiProvider provider) => _dispatcher.Post(() =>
    {
        if (_client is not null)
        {
            var client = _client;
            _client = null;
            _resumeSessionId = null;
            _ = client.DisposeAsync().AsTask();
        }

        ResetModelAndEffortForProvider();
    });

    private void ResetModelAndEffortForProvider()
    {
        (AvailableModels, AvailableEfforts) = _providerSelection.CurrentProvider == AiProvider.Codex
            ? (_codexModels, _codexEfforts)
            : (_claudeModels, _claudeEfforts);
        SelectedModel = AvailableModels[0];
        SelectedEffort = "default";
    }

    private void RestartClientForSettingsChange()
    {
        if (_client is null)
        {
            return;
        }

        _resumeSessionId = _client.SessionId;
        var client = _client;
        _client = null;
        _ = client.DisposeAsync().AsTask();
    }

    /// <summary>Raised when this tab becomes active - the view focuses the message input in response.</summary>
    public event Action? FocusRequested;

    public void RequestFocus() => FocusRequested?.Invoke();

    /// <summary>Raised the instant a genuine user-submitted message starts a turn (not an internal automated one like conflict-resolution) - VersionSectionViewModel uses this to lock the sidebar/Edit tab for the whole turn.</summary>
    public event Action? NormalTurnStarted;

    /// <summary>Raised when a genuine user-submitted turn finishes (bool = succeeded) - distinct from an internal automated turn's own completion, which callers await directly via RunAutomatedTurnAsync's return value instead. Whatever the turn changed is left as ordinary pending changes - nothing here commits automatically.</summary>
    public event Action<bool>? NormalTurnCompleted;

    /// <summary>Raised when the active request is paused (PauseAsync, or restored Paused from disk on SwitchSessionAsync's own load) - NormalTurnCompleted is deliberately NOT raised alongside this, so the workspace stays locked exactly as if the turn were still actively working. See VersionSectionViewModel.IsAiPaused.</summary>
    public event Action? TurnPaused;

    /// <summary>Raised when a paused request resumes (ResumeAsync) - the mirror image of TurnPaused.</summary>
    public event Action? TurnResumed;

    /// <summary>
    /// Raised around a hidden turn (RunAutomatedTurnAsync's visible: false path) - lets a caller lock the
    /// workspace down the same way NormalTurnStarted/Completed do, without the turn ever surfacing in the
    /// Generate tab's own UI (no request card, no visible reply). No current caller passes visible: false -
    /// this exists as general RunAutomatedTurnAsync infrastructure, not dead code tied to one feature.
    /// Distinct from NormalTurnStarted/Completed rather than reusing them since a hidden turn is never a
    /// "genuine user-submitted turn" in that sense - only visible ones are.
    /// </summary>
    public event Action? HiddenTurnStarted;

    /// <summary>See HiddenTurnStarted - always raised once the hidden turn ends, success or failure alike, so the workspace never stays locked down by a hidden turn that errored out.</summary>
    public event Action? HiddenTurnFinished;

    /// <summary>The most recent plain-text reply of a visible automated turn (RunAutomatedTurnAsync's visible: true path - e.g. conflict-resolution) - used to inspect what Claude actually said/did, and (see CaptureVisibleAutomatedTurnText's own OnPropertyChanged) live-bound by GenerateTabView's conflict-resolution panel.</summary>
    public string? LastAssistantText => _visibleAutomatedTurnText.Length > 0 ? _visibleAutomatedTurnText.ToString() : null;

    /// <summary>GenerateTabView's conflict-resolution panel's own status line - "Resolving…" while genuinely running, "Paused" while VisibleAutomatedTurnActive but IsSending has dropped (see PauseAsync). Notified alongside both OnVisibleAutomatedTurnActiveChanged and OnIsSendingChanged.</summary>
    public string ConflictResolutionStatusText => IsSending ? "Resolving merge conflicts…" : "Paused";

    /// <summary>The plain-text reply of the most recent hidden turn (see RunAutomatedTurnAsync's visible: false path), if any - see HiddenTurnStarted.</summary>
    public string? LastHiddenTurnText => _hiddenTurnText.Length > 0 ? _hiddenTurnText.ToString() : null;

    /// <summary>
    /// Called whenever the workspace's editable target changes (including the first time, right after the
    /// repo/target is established) - sessionKey is the checked-out branch's own name while targeting a branch
    /// (see GitTarget.BranchName), null while detached at a tag/commit. Each distinct key has its own
    /// independent Generate conversation - switching away from one ends its live subprocess (if any) without
    /// losing anything, since everything said is already persisted to Claude Code's own on-disk transcript
    /// (for conversational context) and to Requests (for display) and resumable/reloadable by key.
    /// </summary>
    public async Task SwitchSessionAsync(string? sessionKey)
    {
        if (sessionKey == _currentSessionKey)
        {
            return;
        }

        await FlushPendingDraftSaveAsync();
        await FlushPendingTaskCompletionAsync();

        // Persist (as Cancelled) whatever request is still active under the OLD session key before leaving
        // it - this used to just fall out of Requests.Clear() below with no save at all, so any request still
        // "Working" at the moment something switched the targeted branch/version out from under it (the user
        // manually switching, or even a git-state re-sync racing a live turn) vanished from
        // generate-requests.json entirely: not late, not shown as Cancelled, just gone, while the underlying
        // subprocess kept running orphaned in the background. See FinalizeActiveRequestAsync.
        var hadActiveRequest = _activeRequest is not null;
        await FinalizeActiveRequestAsync(GenerateRequestStatus.Cancelled);
        if (hadActiveRequest)
        {
            // Without this, VersionSectionViewModel.IsAiWorking (set only by this same event) would stay
            // stuck true forever - the sidebar/History tab would never unlock even though the turn that set
            // it is gone.
            NormalTurnCompleted?.Invoke(false);
        }

        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }

        _currentSessionKey = sessionKey;
        IsEditable = sessionKey is not null;
        _resumeSessionId = null;
        IsSending = false;
        Requests.Clear();
        _activeRequest = null;
        _pendingTurnCount = 0;
        DisplayedIndex = -1;
        Attachments.Clear();
        FileAttachments.Clear();
        SendCommand.NotifyCanExecuteChanged();

        // Each session (feature/direct-mode version) has its own independent not-yet-sent draft, same as its
        // own independent conversation - switching away and back restores whatever was left typed, and
        // switching to a session with nothing saved starts blank rather than carrying over the previous
        // session's in-progress text.
        InputText = sessionKey is not null ? await _metadataStore.LoadGenerateDraftAsync(_workspacePath, sessionKey) ?? "" : "";

        if (sessionKey is null)
        {
            return;
        }

        _resumeSessionId = DecodeResumeSessionId(await _metadataStore.LoadGenerateSessionIdAsync(_workspacePath, sessionKey));

        var loaded = await _metadataStore.LoadGenerateRequestsAsync(_workspacePath, sessionKey);
        foreach (var request in loaded)
        {
            if (request.Status == GenerateRequestStatus.Working)
            {
                // No live process can still be "working" on a request freshly loaded from disk - only
                // reachable if the app was killed/crashed mid-turn (a clean close already writes this back
                // as Paused itself - see DisposeAsync - so a Working status can only still be sitting on
                // disk here after an unclean process death that skipped that write-back entirely). Treated
                // exactly like an explicit Pause - restored below - rather than losing the turn to a silent
                // Cancel.
                request.Status = GenerateRequestStatus.Paused;
            }

            var requestVm = GenerateRequestViewModel.FromModel(request);
            Requests.Add(requestVm);

            if (requestVm.Status == GenerateRequestStatus.Paused)
            {
                _activeRequest = requestVm;
            }
        }

        if (Requests.Count > 0)
        {
            DisplayedIndex = Requests.Count - 1;
        }

        if (_activeRequest is not null)
        {
            // Re-locks the workspace exactly as if the turn were still actively working (see TurnPaused's own
            // doc comment) - VersionSectionViewModel's subscription to these is already in place by now
            // (WorkspaceTabFactory constructs it before WorkspaceTabViewModel.InitializeAsync ever reaches
            // this call).
            NormalTurnStarted?.Invoke();
            TurnPaused?.Invoke();

            // Resume/Stop's own CanExecute needs an explicit nudge here - nothing else notifies it for a
            // request restored straight into Paused like this (contrast PauseAsync, which flips IsSending and
            // gets this for free via OnIsSendingChanged). Relying on whatever value the View happens to read
            // the first time it binds these commands would be a timing race against this same load.
            ResumeCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Not gated on !IsSending - a message sent while a turn is already running is an interjection (see
    /// SendAsync's handling), not a new turn, so there's no reason to block it. Still gated on
    /// !_hiddenTurnActive and !VisibleAutomatedTurnActive though: both are narrow, strict-reply-format
    /// exchanges (a hidden turn the user never even sees; a merge-conflict-resolution turn - see
    /// GenerateTabViewModel.VisibleAutomatedTurnActive's own doc comment) that a stray interjection landing in
    /// the same live session (SendAsync's own isInterjection path has no request card to attach it to during
    /// either) could corrupt.
    /// </summary>
    private bool CanSend() => !_hiddenTurnActive && !VisibleAutomatedTurnActive && !IsVersionActionBusy && !HasRunningTasks && (InputText.Trim().Length > 0 || Attachments.Count > 0 || FileAttachments.Count > 0) && _currentSessionKey is not null;

    partial void OnInputTextChanged(string value)
    {
        SendCommand.NotifyCanExecuteChanged();
        ScheduleDraftAutoSave();
    }

    /// <summary>Debounced so every keystroke doesn't hit disk - mirrors EditTabViewModel's auto-save. Not started while no session is targeted (the input box itself is hidden then - see GenerateTabView's IsEditable-gated DockPanel).</summary>
    private void ScheduleDraftAutoSave()
    {
        if (_currentSessionKey is null)
        {
            return;
        }

        _draftDebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _draftDebounceCts = cts;
        _ = DebounceSaveDraftAsync(cts.Token);
    }

    private async Task DebounceSaveDraftAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DraftAutoSaveDebounce, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_currentSessionKey is { } key)
        {
            await _metadataStore.SaveGenerateDraftAsync(_workspacePath, key, InputText);
        }
    }

    /// <summary>Persists whatever's currently typed immediately, bypassing the debounce - called before switching sessions and on dispose, so a draft is never lost to a debounce window that never got to fire.</summary>
    private async Task FlushPendingDraftSaveAsync()
    {
        _draftDebounceCts?.Cancel();
        if (_currentSessionKey is { } key)
        {
            await _metadataStore.SaveGenerateDraftAsync(_workspacePath, key, InputText);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = InputText.Trim();
        if (text.Length == 0 && Attachments.Count == 0 && FileAttachments.Count == 0)
        {
            return;
        }

        var images = Attachments.Select(a => new ImageAttachment(a.MediaType, a.Base64Data)).ToList();
        var fileAttachments = FileAttachments.ToList();
        Attachments.Clear();
        FileAttachments.Clear();
        InputText = "";

        // File attachments have no content-block type of their own (see ChatFileAttachment) - composed into
        // the outgoing text here, at send time, rather than ever touching the visible/editable InputText.
        var textWithFileReferences = fileAttachments.Count > 0
            ? string.Join('\n', new[] { text }.Where(s => s.Length > 0).Concat(fileAttachments.Select(f => $"Attached file: {f.DisplayName}")))
            : text;

        // Sent while a turn is already running: an interjection, not a new turn - Claude Code picks it up
        // alongside the next tool result rather than making the user wait for the current turn to finish
        // first. Sent as plain text, NOT prefixed with "/btw" - that convention is interactive-CLI-only and
        // gets rejected outright ("/btw isn't available in this environment.", a synthetic client-side reply
        // with no real model call behind it) over the stream-json/print-mode protocol AutoDev actually drives
        // the CLI with. Empirically confirmed a plain message sent mid-turn is picked up as an interjection
        // just as well with no special prefix needed at all. IsSending/NormalTurnStarted only apply to a
        // turn's first message; the client is already live, so EnsureClientStarted is skipped.
        var isInterjection = IsSending;
        var outgoingText = textWithFileReferences;

        if (isInterjection)
        {
            if (_activeRequest is not null)
            {
                _activeRequest.Input = $"{_activeRequest.Input}\n{textWithFileReferences}";
                _pendingTurnCount++;
                _ = PersistCurrentRequestsAsync();
                DisplayedIndex = Requests.Count - 1;
            }
        }
        else
        {
            var request = new GenerateRequestViewModel
            {
                Id = Guid.NewGuid().ToString(),
                Input = textWithFileReferences,
                Status = GenerateRequestStatus.Working,
                CreatedAt = DateTimeOffset.UtcNow,
                CurrentActionStartedAt = DateTimeOffset.UtcNow,
            };

            if (Requests.Count >= MaxRequests)
            {
                Requests.RemoveAt(0);
            }

            Requests.Add(request);
            _activeRequest = request;
            _activeRequestOutputBuffer.Clear();
            _lastActiveRequestSegment = "";
            _pendingTurnCount = 1;
            _cancelRequested = false;
            DisplayedIndex = Requests.Count - 1;
            _ = PersistCurrentRequestsAsync();

            IsSending = true;
            NormalTurnStarted?.Invoke();
            EnsureClientStarted();
        }

        // Baseline for the stall watchdog (see StallWatchdogElapsedAsync) - stamped here rather than only
        // once the first event actually arrives, so a slow-to-start model response can't itself look like a
        // stall before anything has had a chance to come back yet.
        _lastEventReceivedAt = DateTimeOffset.UtcNow;

        if (images.Count > 0)
        {
            await _client!.SendUserMessageAsync(outgoingText, images);
        }
        else
        {
            await _client!.SendUserMessageAsync(outgoingText);
        }
    }

    /// <summary>
    /// Only meaningful for a genuine user-submitted turn that's actually running right now (_activeRequest
    /// not null, IsSending) - an automated/hidden turn (conflict-resolution etc.) has no request card to
    /// cancel and isn't user-facing "current work" in the sense this button means, and there's nothing left
    /// to ask a paused turn to stop. DisplayedRequest.IsWorking (which GenerateTabView.axaml's Cancel button
    /// visibility mirrors) is already false during an automated turn for the same reason, so the button
    /// naturally hides itself rather than showing disabled. !_cancelRequested guards against sending the same
    /// "stop and revert" instruction twice while the first one is still being carried out. The explicit
    /// !VisibleAutomatedTurnActive is redundant with _activeRequest being null during one (belt-and-suspenders
    /// - a merge-conflict-resolution turn must never be interruptible via Cancel/Stop, only Pause/Resume, so
    /// this stays true even if _activeRequest's own nullness here ever changed for an unrelated reason).
    /// </summary>
    private bool CanCancel() => IsSending && _activeRequest is not null && !_cancelRequested && !VisibleAutomatedTurnActive;

    /// <summary>
    /// Asks Claude to stop what it's doing and revert whatever it's changed so far this turn, rather than
    /// forcibly killing it (see StopAsync for that) - sent as an ordinary interjection alongside whatever
    /// Claude is already doing, exactly like a plain user message sent mid-turn (see SendAsync's own
    /// isInterjection path), so it can actually use its own tools (git, file edits) to clean up properly
    /// instead of being cut off mid-edit. Runs "in the background": this method returns immediately once the
    /// message is sent, the same as any interjection - the turn keeps working (still shows Working/whatever
    /// tool it's using next) until its own ResultEvent arrives, at which point Handle() sees _cancelRequested
    /// and finalizes the request as Cancelled instead of Completed.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private async Task CancelAsync()
    {
        if (_activeRequest is null || _client is null)
        {
            return;
        }

        const string instruction = "Stop what you're currently doing and revert any changes you've made so far during this turn.";

        _cancelRequested = true;
        CancelCommand.NotifyCanExecuteChanged();
        _activeRequest.Input = $"{_activeRequest.Input}\n{instruction}";
        _pendingTurnCount++;
        _ = PersistCurrentRequestsAsync();
        DisplayedIndex = Requests.Count - 1;

        _lastEventReceivedAt = DateTimeOffset.UtcNow;
        await _client.SendUserMessageAsync(instruction);
    }

    /// <summary>Same gating as CanCancel, minus !_cancelRequested (stopping outright is always fine, even mid-cancel) - also true while the active request is Paused, since Stop is exactly as meaningful there (nothing to kill, but the paused turn still needs to be abandoned and the workspace unlocked). Explicitly excludes an automated turn (see CanCancel) - a merge-conflict-resolution turn can only ever be Paused/Resumed, never Stopped, so forcibly killing it and leaving the repository mid-conflict is never offered.</summary>
    private bool CanStop() => !VisibleAutomatedTurnActive && _activeRequest is not null && (IsSending || _activeRequest.IsPaused);

    /// <summary>Forcibly stops the active request's turn by killing the underlying Claude CLI subprocess outright (see ClaudeSessionClient.DisposeAsync's Kill fallback) if one is even running, rather than waiting for it to wind down on its own - the request is marked Cancelled with whatever partial output had streamed in so far, and a fresh subprocess starts on the next Send. This is what Cancel itself used to do before it became the "ask nicely" action above.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => KillActiveTurnAsync(GenerateRequestStatus.Cancelled, success: false);

    /// <summary>A genuine user-submitted turn (_activeRequest) or a visible automated conflict-resolution turn (VisibleAutomatedTurnActive) - either way, needs to be genuinely running right now (IsSending) to pause.</summary>
    private bool CanPause() => IsSending && (_activeRequest is not null || VisibleAutomatedTurnActive);

    /// <summary>
    /// Immediately stops the AI's work (kills the subprocess, same as Stop) but keeps it resumable: captures
    /// the live session id first (same mechanism RestartClientForSettingsChange uses for a model/effort
    /// change) so Resume can pick the exact same conversation back up. For a genuine request, marks it Paused
    /// rather than Cancelled/Completed - persisted to disk immediately, so the pause survives an app restart
    /// (see SwitchSessionAsync's loader, which restores a Paused request as still-active on load). For an
    /// automated conflict-resolution turn there's no request card to mark - VisibleAutomatedTurnActive simply
    /// stays true while IsSending drops, and _pendingAutomatedTurn (RunAutomatedTurnAsync's own
    /// TaskCompletionSource) is left untouched/still pending, so whichever git action is awaiting it
    /// (ResolveConflictsAsync) just stays suspended until ResumeAsync eventually leads to a real ResultEvent;
    /// this pause is session-only, not restart-persisted, unlike a genuine request's.
    /// Deliberately does NOT raise NormalTurnCompleted - the workspace stays exactly as locked as it was
    /// while the turn was genuinely working, via TurnPaused instead (see VersionSectionViewModel.IsAiPaused).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPause))]
    private async Task PauseAsync()
    {
        if (_activeRequest is null && !VisibleAutomatedTurnActive)
        {
            return;
        }

        if (_client is not null)
        {
            _resumeSessionId = _client.SessionId;
            await PersistSessionIdAsync();
        }

        var client = _client;
        _client = null;
        if (client is not null)
        {
            await client.DisposeAsync();
        }

        // Status flips to Paused BEFORE IsSending drops, not after - IsSending's own change notification
        // immediately re-evaluates CanResume() (see OnIsSendingChanged), which reads _activeRequest.Status;
        // flipping it the other way around left ResumeCommand's CanExecute cached false (read while Status
        // was still Working) even though IsPaused-bound bindings elsewhere (e.g. the button's own IsVisible)
        // already updated fine, since those re-evaluate live off the property instead of a point-in-time notify.
        _cancelRequested = false;
        if (_activeRequest is not null)
        {
            _activeRequest.Status = GenerateRequestStatus.Paused;
            await PersistCurrentRequestsAsync();
        }

        IsSending = false;

        TurnPaused?.Invoke();
    }

    /// <summary>A genuine user-submitted turn Paused, or an automated conflict-resolution turn currently sitting paused (VisibleAutomatedTurnActive stays true across a pause - see PauseAsync - so !IsSending is what actually distinguishes "paused" from "working" for that case).</summary>
    private bool CanResume() => _activeRequest is { Status: GenerateRequestStatus.Paused } || (VisibleAutomatedTurnActive && !IsSending);

    /// <summary>
    /// Re-enters the working state and tells Claude to continue from where it left off, resuming the exact
    /// same session id PauseAsync captured - still the same request/turn (see GenerateRequestStatus.Paused's
    /// own doc comment), not a new one, no matter how many times it's been paused and resumed. "In the
    /// background" the same way a fresh Send is: this returns as soon as the message is sent, not once
    /// Claude actually replies. For an automated conflict-resolution turn, the eventual ResultEvent completes
    /// _pendingAutomatedTurn exactly as if it had never been paused (see Handle's ResultEvent case, which
    /// doesn't distinguish a resumed automated turn from one that ran straight through).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanResume))]
    private async Task ResumeAsync()
    {
        if (_activeRequest is null && !VisibleAutomatedTurnActive)
        {
            return;
        }

        if (_activeRequest is not null)
        {
            _activeRequest.Status = GenerateRequestStatus.Working;
            _activeRequest.CurrentActionStartedAt = DateTimeOffset.UtcNow;
            _pendingTurnCount = 1;
            await PersistCurrentRequestsAsync();
        }

        IsSending = true;
        TurnResumed?.Invoke();
        EnsureClientStarted();

        _lastEventReceivedAt = DateTimeOffset.UtcNow;
        await _client!.SendUserMessageAsync("Continue from where you left off.");
    }

    /// <summary>
    /// The actual work behind both a user-initiated Stop and the stall watchdog's own forced stop (see
    /// StallWatchdogElapsedAsync) - finalizes+persists the active request as `status`, kills the subprocess
    /// if one is running (a no-op if the request was Paused, which already has none), and unlocks the
    /// app-wide "AI is working"/"AI is paused" state via NormalTurnCompleted. A safe no-op if there's no
    /// active request. `success` only affects what NormalTurnCompleted's subscribers are told happened - it's
    /// passed straight through rather than derived from `status`, since the watchdog's Completed treats the
    /// turn as a genuine success even though nothing ever confirmed that from the CLI side.
    /// </summary>
    private async Task KillActiveTurnAsync(GenerateRequestStatus status, bool success)
    {
        if (_activeRequest is null)
        {
            return;
        }

        await FinalizeActiveRequestAsync(status);
        IsSending = false;

        // Detached before disposing so Handle()'s dead-client guard ignores any trailing buffered events
        // from the read loop as the killed subprocess winds down.
        var client = _client;
        _client = null;
        if (client is not null)
        {
            await client.DisposeAsync();
        }

        NormalTurnCompleted?.Invoke(success);
        if (status == GenerateRequestStatus.Completed)
        {
            _soundService.PlayDing();
        }
    }

    /// <summary>
    /// KillActiveTurnAsync's counterpart for a visible automated (conflict-resolution) turn, which has no
    /// request card for that to finalize - used only by StallWatchdogElapsedAsync, since a genuinely stuck
    /// automated turn has no Stop button of its own to recover it the way a normal turn does (see
    /// CanStop/CanCancel, both deliberately false the whole time - see VisibleAutomatedTurnActive's own doc
    /// comment). Resolves _pendingAutomatedTurn with false (not a real confirmation of success) so whichever
    /// git action is awaiting it (ResolveConflictsAsync) unblocks and re-checks HasConflictsAsync itself,
    /// which - still true - lets it retry with a fresh turn up to its own attempt budget, exactly the same
    /// recovery path a normal completed-but-still-conflicted reply already takes.
    /// </summary>
    private async Task KillAutomatedTurnAsync()
    {
        if (!VisibleAutomatedTurnActive)
        {
            return;
        }

        var client = _client;
        _client = null;
        if (client is not null)
        {
            await client.DisposeAsync();
        }

        VisibleAutomatedTurnActive = false;
        IsSending = false;

        _pendingAutomatedTurn?.TrySetResult(false);
        _pendingAutomatedTurn = null;
    }

    /// <summary>
    /// The guaranteed backstop: fires every StallCheckInterval regardless of anything else happening, and
    /// force-stops the active request the moment it's gone StallTimeout with no event at all. This is
    /// deliberately blunt rather than trying to diagnose *why* nothing arrived - every specific cause found
    /// so far (a malformed stdout line silently killing the read loop, a full stderr pipe blocking the
    /// child's own writes) got fixed at its own source in ClaudeSessionClient once discovered, but each was
    /// only found after a turn had already sat stuck for a long time in production. This timer is what makes
    /// the NEXT undiscovered way a turn can wedge self-heal within a bounded, known time instead of needing
    /// its own incident and its own fix first - "stuck" becomes "recovers within 5 minutes" for any cause,
    /// known or not.
    ///
    /// Finalizes as Completed, not Cancelled: going silent only means AutoDev stopped hearing from the CLI,
    /// not that the assistant's work itself failed - whatever text had already streamed into
    /// _lastActiveRequestSegment (see FinalizeActiveRequestAsync) is very often the assistant's real,
    /// already-finished reply, just missing its own trailing ResultEvent. Showing that as a normal completed
    /// turn (ding included) rather than an aborted one is the more honest read of what actually happened.
    /// </summary>
    private async Task StallWatchdogElapsedAsync()
    {
        if (!IsSending || (_activeRequest is null && !VisibleAutomatedTurnActive))
        {
            return;
        }

        var silentFor = DateTimeOffset.UtcNow - _lastEventReceivedAt;
        if (silentFor < StallTimeout)
        {
            return;
        }

        _logger.LogWarning(
            "Generate turn for {WorkspacePath} produced no events for {SilentFor} (>= {Timeout}) - treating as stalled and force-completing with whatever output had already streamed in.",
            _workspacePath, silentFor, StallTimeout);

        if (_activeRequest is not null)
        {
            await KillActiveTurnAsync(GenerateRequestStatus.Completed, success: true);
        }
        else
        {
            await KillAutomatedTurnAsync();
        }
    }

    /// <summary>
    /// Resolves a request still waiting on OnHasRunningTasksChanged (see the ResultEvent handler in Handle())
    /// as its own already-decided final status (see _pendingTaskCompletionStatus) right away and persists -
    /// called before leaving a session (switch/dispose) so that wait can never span a session boundary:
    /// HasRunningTasks clearing later would otherwise finalize a request that's no longer part of the current
    /// session into the NEW session's file (or never get to it at all, if the workspace tab itself is gone by
    /// then). The task itself may well still be running - this only resolves the display/ding, exactly like
    /// leaving mid-turn already does for a genuinely active request.
    /// </summary>
    private async Task FlushPendingTaskCompletionAsync()
    {
        if (_pendingTaskCompletionRequest is not { } request)
        {
            return;
        }

        _pendingTaskCompletionRequest = null;
        request.Status = _pendingTaskCompletionStatus;
        await PersistCurrentRequestsAsync();
    }

    /// <summary>
    /// Flushes only the active request's LAST streamed text segment (see _lastActiveRequestSegment - not
    /// the full _activeRequestOutputBuffer history of everything said this turn), marks it `status`, and
    /// persists immediately - the one shared tail every path that can end a turn without its own real
    /// ResultEvent ever arriving (a user Stop, a session switch, app disposal, and the stall watchdog -
    /// see StallWatchdogElapsedAsync) funnels through, so none of them can silently drop the request the way
    /// SwitchSessionAsync used to. `status` is Cancelled for all of those except the stall watchdog, which
    /// treats its forced stop as a Completed turn instead (see CompleteStalledTurnAsync) - the assistant may
    /// well have actually finished; the watchdog firing only means AutoDev stopped hearing about it, not
    /// that the work itself failed. Deliberately doesn't touch _client - each caller disposes it (or not) on
    /// its own terms afterward. Not used by PauseAsync - a paused request stays _activeRequest, not finalized
    /// away. A safe no-op if there's nothing active to finalize.
    /// </summary>
    private async Task FinalizeActiveRequestAsync(GenerateRequestStatus status)
    {
        if (_activeRequest is null)
        {
            return;
        }

        var request = _activeRequest;
        _activeRequest = null;
        _pendingTurnCount = 0;
        _cancelRequested = false;

        if (_lastActiveRequestSegment.Length > 0)
        {
            request.Output = _lastActiveRequestSegment;
        }

        request.Status = status;
        await PersistCurrentRequestsAsync();
    }

    private async Task PersistCurrentRequestsAsync()
    {
        if (_currentSessionKey is not { } key)
        {
            return;
        }

        await _metadataStore.SaveGenerateRequestsAsync(_workspacePath, key, [.. Requests.Select(r => r.ToModel())]);
    }

    /// <summary>
    /// Drives an automated exchange (e.g. "resolve these merge conflicts") through the exact same live
    /// session as a normal user turn, so it shares the conversation's context - but awaits the turn's
    /// completion instead of returning immediately, for callers (VersionSectionViewModel's conflict-resolution
    /// loop) that need to know when Claude is done before proceeding. Returns false if the turn ended in error.
    /// Never creates or touches a request card - see _hiddenTurnActive/_visibleAutomatedTurnActive, which route
    /// this turn's assistant text away from Requests entirely.
    /// </summary>
    /// <param name="visible">
    /// True (the default) matches the conflict-resolution loop's intent - recoverable afterward via
    /// LastAssistantText. False keeps the exchange entirely invisible instead, recoverable via
    /// LastHiddenTurnText - no current caller uses this, see HiddenTurnStarted's own doc comment.
    /// </param>
    public async Task<bool> RunAutomatedTurnAsync(string instruction, bool visible = true, CancellationToken cancellationToken = default)
    {
        _hiddenTurnActive = !visible;
        VisibleAutomatedTurnActive = visible;
        if (visible)
        {
            _visibleAutomatedTurnText.Clear();
            OnPropertyChanged(nameof(LastAssistantText));
        }
        else
        {
            _hiddenTurnText.Clear();
            HiddenTurnStarted?.Invoke();
        }

        IsSending = true;

        EnsureClientStarted();

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingAutomatedTurn = tcs;

        await using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

        try
        {
            await _client!.SendUserMessageAsync(instruction, cancellationToken);
            return await tcs.Task;
        }
        finally
        {
            if (!visible)
            {
                HiddenTurnFinished?.Invoke();
            }
        }
    }

    private void EnsureClientStarted()
    {
        if (_client is not null)
        {
            return;
        }

        _client = _sessionClientFactory.Create(_providerSelection.CurrentProvider, _workspacePath, SelectedModel, SelectedEffort == "default" ? null : SelectedEffort);
        _client.Start(_resumeSessionId);
        _ = Task.Run(() => ReadLoopAsync(_client));
    }

    /// <summary>
    /// Takes the client it's reading for as a parameter (rather than reading the _client field, which can
    /// be reassigned to a brand-new session by the time this loop's own tail runs) purely so the finalize
    /// call below can tell a stale loop apart from the current one - see there.
    /// </summary>
    private async Task ReadLoopAsync(IAiSessionClient client)
    {
        try
        {
            await foreach (var evt in client.ReadAllEventsAsync())
            {
                var captured = evt;
                _dispatcher.Post(() => Handle(captured));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Generate tab read loop failed for {WorkspacePath}", _workspacePath);
        }

        // The stream can end without the turn in flight (if any) ever getting its own ResultEvent to finish
        // it normally - the process exited/crashed, or (see ClaudeSessionClient) a single unparseable line
        // took the whole loop down. FinalizeAbandonedTurn is what used to only happen via a manual Cancel;
        // without calling it here too, IsSending and the active request's own Status (what the UI actually
        // shows - see GenerateRequestViewModel.IsWorking) would stay stuck on Working forever, even though
        // the assistant text that had already streamed in was sitting right there in the buffer the whole
        // time. Only runs if `client` is still the live one: SwitchSessionAsync/RestartClientForSettingsChange
        // null out _client before disposing, then a later Send can assign a brand new one before this dead
        // loop's own tail gets scheduled - finalizing against that new, legitimately-in-flight turn instead
        // of the one this loop actually belonged to would wrongly cancel it.
        _dispatcher.Post(() =>
        {
            if (ReferenceEquals(_client, client))
            {
                _ = FinalizeAbandonedTurnAsync();
            }
        });
    }

    /// <summary>See ReadLoopAsync's tail call - a safe no-op if there's nothing to finalize (the common case: the stream ended because a ResultEvent already completed the turn normally, which clears _activeRequest/_pendingAutomatedTurn/IsSending itself).</summary>
    private async Task FinalizeAbandonedTurnAsync()
    {
        var hadActiveRequest = _activeRequest is not null;
        await FinalizeActiveRequestAsync(GenerateRequestStatus.Cancelled);
        if (hadActiveRequest)
        {
            NormalTurnCompleted?.Invoke(false);
        }

        // RunAutomatedTurnAsync's own finally block raises HiddenTurnFinished (for the hidden case) once
        // this TrySetResult lets its `await tcs.Task` return - no need to raise it a second time here.
        _pendingAutomatedTurn?.TrySetResult(false);
        _pendingAutomatedTurn = null;
        _hiddenTurnActive = false;
        VisibleAutomatedTurnActive = false;

        IsSending = false;
    }

    private void Handle(AiStreamEvent evt)
    {
        // Proves the subprocess/pipe is genuinely still alive - reset regardless of event kind or which
        // branch below (if any) ends up handling it, so the stall watchdog's clock only ever measures real
        // silence, never how long a specific event type takes to show up.
        _lastEventReceivedAt = DateTimeOffset.UtcNow;

        if (_client is null)
        {
            return; // SwitchSessionAsync/RestartClientForSettingsChange already killed/detached this session - ignore any trailing buffered events from the dead process's read loop
        }

        switch (evt)
        {
            case AssistantMessageEvent assistant:
                if (_hiddenTurnActive)
                {
                    CaptureHiddenAssistantText(assistant);
                }
                else if (VisibleAutomatedTurnActive)
                {
                    CaptureVisibleAutomatedTurnText(assistant);
                }
                else if (_activeRequest is not null)
                {
                    CaptureActiveRequestOutput(assistant);
                    CaptureActiveRequestToolUse(assistant);
                }

                break;

            case ResultEvent result:
                var wasAutomatedTurn = _pendingAutomatedTurn is not null;

                _usageAggregator.ReportUsage(_client!.SessionId, result.CumulativeUsage);

                _ = PersistSessionIdAsync();
                _pendingAutomatedTurn?.TrySetResult(!result.IsError);
                _pendingAutomatedTurn = null;
                _hiddenTurnActive = false;
                VisibleAutomatedTurnActive = false;

                if (wasAutomatedTurn)
                {
                    IsSending = false;
                    break;
                }

                if (_activeRequest is null)
                {
                    // Not a genuine user-submitted request's own reply at all - e.g. a stray message sent
                    // (via the shared live session) while no request was actually active, such as during a
                    // visible automated turn's own exchange. Nothing to finalize, and deliberately no
                    // NormalTurnCompleted/ding either - both must only ever fire for a real tracked request,
                    // never for background/incidental CLI activity.
                    IsSending = false;
                    break;
                }

                // Only truly "done" once every message sent for this request (the initial send, plus any
                // interjections) has had its own ResultEvent accounted for - see _pendingTurnCount's doc
                // comment. An interjection still outstanding means Claude is about to keep working on this
                // same workspace, so IsSending stays true and neither the request nor the ding/
                // NormalTurnCompleted side effects fire yet.
                _pendingTurnCount = Math.Max(0, _pendingTurnCount - 1);
                if (_pendingTurnCount > 0)
                {
                    break;
                }

                IsSending = false;

                // Prefer the CLI's own final-answer text over the accumulated streaming buffer, which mixes
                // in intermediate narration (e.g. "let me check that" before a tool call) alongside the real
                // final reply - Result is exactly the clean final text; the buffer stays as a fallback only
                // for the (should-never-happen) case a completed turn's Result is null.
                var request = _activeRequest;
                _activeRequest = null;
                request.Output = result.Result ?? _activeRequestOutputBuffer.ToString();

                // Cancelled (not Completed) if the user had asked Claude to stop and revert (CancelAsync) and
                // this is that request's own reply finally landing - captured now, not read again later,
                // since _cancelRequested could belong to a different turn by the time a deferred
                // (HasRunningTasks) completion below actually resolves.
                var finalStatus = _cancelRequested ? GenerateRequestStatus.Cancelled : GenerateRequestStatus.Completed;
                _cancelRequested = false;

                if (HasRunningTasks)
                {
                    // Claude's own turn ended, but it left (or already had) at least one AutoDev-tracked
                    // .task run still going - e.g. a dev server or watch build it started in the background
                    // and considers "done" from its own side. Marking this SPECIFIC request Completed/
                    // playing the ding now would tell the user everything's finished while a process it
                    // started is still visibly running - see OnHasRunningTasksChanged, which finishes this
                    // off once that also clears. Deliberately doesn't delay IsSending/NormalTurnCompleted
                    // above (already fired): that governs the app-wide "AI is working" lock, and gating it on
                    // a background task with no bounded runtime would just reintroduce the "stuck in Working"
                    // bug for a different reason - only this one request's own displayed status/ding waits.
                    _pendingTaskCompletionRequest = request;
                    _pendingTaskCompletionStatus = finalStatus;
                }
                else
                {
                    request.Status = finalStatus;
                    _ = PersistCurrentRequestsAsync();
                }

                NormalTurnCompleted?.Invoke(!result.IsError);
                if (_pendingTaskCompletionRequest is null)
                {
                    _soundService.PlayDing();
                }

                break;
        }
    }

    private void CaptureHiddenAssistantText(AssistantMessageEvent assistant)
    {
        foreach (var block in assistant.Content.OfType<TextContentBlock>())
        {
            _hiddenTurnText.Append(block.Text);
        }
    }

    private void CaptureVisibleAutomatedTurnText(AssistantMessageEvent assistant)
    {
        foreach (var block in assistant.Content.OfType<TextContentBlock>())
        {
            _visibleAutomatedTurnText.Append(block.Text);
        }

        // Live-updates GenerateTabView's conflict-resolution panel as Claude's reply streams in, the same way
        // CaptureActiveRequestOutput does for a normal request's own Output.
        OnPropertyChanged(nameof(LastAssistantText));
    }

    private void CaptureActiveRequestOutput(AssistantMessageEvent assistant)
    {
        foreach (var block in assistant.Content.OfType<TextContentBlock>())
        {
            // Separate text blocks are usually separate thoughts (e.g. a note before a tool call, then a
            // distinct final reply after it) - a blank line between them reads far better than running them
            // together with no separator at all.
            if (_activeRequestOutputBuffer.Length > 0)
            {
                _activeRequestOutputBuffer.Append("\n\n");
            }

            _activeRequestOutputBuffer.Append(block.Text);

            // Overwritten (not appended) - see _lastActiveRequestSegment's own doc comment.
            _lastActiveRequestSegment = block.Text;

            // Live-updates the output section with Claude's latest words as they arrive, replacing whatever
            // was shown before - rather than leaving it empty the whole turn and only ever showing something
            // once it's fully done (and even then, often just the CLI's own short wrap-up summary rather than
            // whatever richer content Claude had already actually said).
            if (_activeRequest is not null)
            {
                _activeRequest.Output = block.Text;
            }
        }
    }

    private void CaptureActiveRequestToolUse(AssistantMessageEvent assistant)
    {
        var lastToolUse = assistant.ToolUses.LastOrDefault();
        if (lastToolUse is not null && _activeRequest is not null)
        {
            _activeRequest.CurrentAction = DescribeToolUse(lastToolUse);

            // Stamped unconditionally, even if the described text is identical to the previous action (e.g.
            // two separate reads of the same file) - each tool-use capture is a genuinely new span of work,
            // and CurrentAction's own change notification wouldn't fire for a repeated value.
            _activeRequest.CurrentActionStartedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Renders a friendly one-line description of a tool-use event for the Generate status box (e.g. "Reading Foo.cs") - falls back to "Using {Name}" for any tool not explicitly recognized here, so an unfamiliar/future tool still shows something reasonable instead of nothing.</summary>
    private static string DescribeToolUse(ToolUseContentBlock toolUse) => toolUse.Name switch
    {
        "Bash" => TryGetString(toolUse.Input, "description", out var desc) ? desc
            : TryGetString(toolUse.Input, "command", out var cmd) ? $"Running: {cmd}" : "Running a command",
        "Read" => TryGetString(toolUse.Input, "file_path", out var readPath) ? $"Reading {Path.GetFileName(readPath)}" : "Reading a file",
        "Edit" => TryGetString(toolUse.Input, "file_path", out var editPath) ? $"Editing {Path.GetFileName(editPath)}" : "Editing a file",
        "Write" => TryGetString(toolUse.Input, "file_path", out var writePath) ? $"Writing {Path.GetFileName(writePath)}" : "Writing a file",
        "Grep" => TryGetString(toolUse.Input, "pattern", out var pattern) ? $"Searching for \"{pattern}\"" : "Searching",
        "Glob" => TryGetString(toolUse.Input, "pattern", out var globPattern) ? $"Finding files matching {globPattern}" : "Finding files",
        "TodoWrite" => "Updating task list",
        "WebFetch" => TryGetString(toolUse.Input, "url", out var url) ? $"Fetching {url}" : "Fetching a URL",
        "WebSearch" => TryGetString(toolUse.Input, "query", out var query) ? $"Searching the web for \"{query}\"" : "Searching the web",
        "McpToolCall" => TryGetString(toolUse.Input, "description", out var mcpDesc) ? mcpDesc : "Calling an MCP tool",
        _ => $"Using {toolUse.Name}",
    };

    private static bool TryGetString(JsonElement input, string property, out string value)
    {
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString() ?? "";
            return value.Length > 0;
        }

        value = "";
        return false;
    }

    private async Task PersistSessionIdAsync()
    {
        if (_client is null || _currentSessionKey is null)
        {
            return;
        }

        var encoded = $"{_providerSelection.CurrentProvider}:{_client.SessionId}";
        await _metadataStore.SaveGenerateSessionIdAsync(_workspacePath, _currentSessionKey, encoded);
    }

    /// <summary>
    /// A saved session id only means anything to the provider that produced it - a Claude session id and a
    /// Codex thread id are both opaque strings to the other provider's CLI, and resuming with the wrong
    /// one would either error out or silently start over anyway. Saved values are prefixed with the
    /// provider that produced them (see PersistSessionIdAsync); this returns the bare id only if that
    /// prefix matches the CURRENT provider, discarding it (returning null - a fresh conversation) otherwise.
    /// A value saved before this prefix existed has none at all, so it's treated as a bare Claude session id
    /// for backward compatibility - Claude was the only provider AutoDev supported at the time.
    /// </summary>
    private string? DecodeResumeSessionId(string? persisted)
    {
        if (string.IsNullOrEmpty(persisted))
        {
            return null;
        }

        var separatorIndex = persisted.IndexOf(':');
        if (separatorIndex < 0)
        {
            return _providerSelection.CurrentProvider == AiProvider.Claude ? persisted : null;
        }

        var provider = persisted[..separatorIndex];
        var sessionId = persisted[(separatorIndex + 1)..];
        return string.Equals(provider, _providerSelection.CurrentProvider.ToString(), StringComparison.Ordinal) ? sessionId : null;
    }

    public async ValueTask DisposeAsync()
    {
        _providerSelection.ProviderChanged -= OnProviderChanged;

        _stallWatchdogTimer.Stop();
        _stallWatchdogTimer.Dispose();
        _elapsedDisplayTimer.Stop();
        _elapsedDisplayTimer.Dispose();

        await FlushPendingDraftSaveAsync();
        await FlushPendingTaskCompletionAsync();

        // A normal app/workspace close mid-turn (this Dispose call, not a crash) is the one clean-exit path
        // that can still write the true state back before going away - SwitchSessionAsync's load-time
        // coercion is only the safety net for an unclean process death, which can't reach here at all.
        // Treated exactly like an explicit Pause rather than a Cancel, so Resume can pick the turn back up
        // next launch no matter how the app came to close while it was still working. A request already
        // Paused is left alone - it's already correctly persisted from the moment PauseAsync ran.
        if (_activeRequest is { Status: GenerateRequestStatus.Working } workingRequest)
        {
            workingRequest.Status = GenerateRequestStatus.Paused;
            await PersistCurrentRequestsAsync();
        }

        if (_client is not null)
        {
            await _client.DisposeAsync();
        }
    }
}
