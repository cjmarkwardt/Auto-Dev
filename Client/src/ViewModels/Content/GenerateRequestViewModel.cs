using AutoDev.Core.Models;
using AutoDev.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoDev.ViewModels.Content;

/// <summary>Live wrapper around a GenerateRequest for display/editing - see GenerateTabViewModel.Requests/DisplayedRequest.</summary>
public sealed partial class GenerateRequestViewModel : ViewModelBase
{
    public required string Id { get; init; }

    [ObservableProperty]
    private string _input = "";

    [ObservableProperty]
    private GenerateRequestStatus _status;

    [ObservableProperty]
    private string? _output;

    /// <summary>The most recent tool Claude has invoked while this request is Working (e.g. "Reading Foo.cs") - see GenerateTabViewModel.CaptureActiveRequestToolUse. Only ever meaningful while Status is Working; DisplayStatus falls back to StatusLabel once the turn ends regardless of this value.</summary>
    [ObservableProperty]
    private string? _currentAction;

    /// <summary>When the action currently shown in CurrentAction began - see GenerateTabViewModel.CaptureActiveRequestToolUse, which stamps this to DateTimeOffset.UtcNow on every tool-use capture, unconditionally, even if the described text happens to be identical to the previous action (e.g. two separate reads of the same file are still two separate spans of work). Driving ElapsedDisplay from this rather than from CurrentAction's own change notification matters for exactly that reason: CurrentAction only raises a change when its text actually differs, but a fresh timestamp always does.</summary>
    [ObservableProperty]
    private DateTimeOffset? _currentActionStartedAt;

    public required DateTimeOffset CreatedAt { get; init; }

    public bool IsWorking => Status == GenerateRequestStatus.Working;

    public bool IsCompleted => Status == GenerateRequestStatus.Completed;

    public bool IsPaused => Status == GenerateRequestStatus.Paused;

    /// <summary>Drives the Generate tab's Stop button visibility (GenerateTabView.axaml) - a single simple property/binding rather than a MultiBinding OR of IsWorking/IsPaused, since a MultiBinding's own per-item FallbackValue doesn't reliably apply when DisplayedRequest itself is null (the button showed, greyed out, with no request at all otherwise).</summary>
    public bool IsWorkingOrPaused => IsWorking || IsPaused;

    public string StatusLabel => Status switch
    {
        GenerateRequestStatus.Working => "Working",
        GenerateRequestStatus.Cancelled => "Cancelled",
        GenerateRequestStatus.Completed => "Completed",
        GenerateRequestStatus.Paused => "Paused",
        _ => "",
    };

    /// <summary>What the status box actually shows - the current tool action while Working (once one has arrived; "Working" until then), otherwise the same as StatusLabel.</summary>
    public string DisplayStatus => Status == GenerateRequestStatus.Working ? (CurrentAction ?? "Working") : StatusLabel;

    /// <summary>How long the AI has spent on the current action ("12s", "1m 05s"), or null once the turn is no longer Working. Not itself an ObservableProperty - it's a pure function of wall-clock time versus CurrentActionStartedAt, so it needs an explicit tick to stay live; see RefreshElapsedDisplay, called once a second by GenerateTabViewModel's elapsed-time timer for whichever request is active.</summary>
    public string? ElapsedDisplay => Status == GenerateRequestStatus.Working && CurrentActionStartedAt is { } startedAt
        ? FormatElapsed(DateTimeOffset.UtcNow - startedAt)
        : null;

    private static string FormatElapsed(TimeSpan elapsed)
    {
        var totalSeconds = (int)Math.Max(0, elapsed.TotalSeconds);
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        return minutes > 0 ? $"{minutes}m {seconds:D2}s" : $"{seconds}s";
    }

    /// <summary>Re-raises ElapsedDisplay's change notification without anything about the request's own state having changed - called on a timer since the elapsed time it reports keeps moving even between real events (see GenerateTabViewModel's elapsed-time timer).</summary>
    public void RefreshElapsedDisplay() => OnPropertyChanged(nameof(ElapsedDisplay));

    public bool HasOutput => !string.IsNullOrEmpty(Output);

    /// <summary>What GenerateTabView's output MarkdownScrollViewer actually binds to - Output with any ```mermaid fenced blocks replaced by rendered diagram images (see MermaidMarkdownProcessor). A stored (not computed) property: mermaid rendering runs off the UI thread since it can take a visible moment, so this starts out as the raw, unrendered Output the instant a turn completes and is swapped in once rendering finishes, rather than blocking the UI thread synchronously.</summary>
    [ObservableProperty]
    private string? _renderedOutput;

    /// <summary>Bumped on every call - lets a slower-finishing render from an earlier Output change detect it's stale and not overwrite a newer one's result.</summary>
    private int _renderGeneration;

    partial void OnStatusChanged(GenerateRequestStatus value)
    {
        OnPropertyChanged(nameof(IsWorking));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsWorkingOrPaused));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(DisplayStatus));
        OnPropertyChanged(nameof(ElapsedDisplay));
    }

    partial void OnCurrentActionChanged(string? value) => OnPropertyChanged(nameof(DisplayStatus));

    partial void OnCurrentActionStartedAtChanged(DateTimeOffset? value) => OnPropertyChanged(nameof(ElapsedDisplay));

    partial void OnOutputChanged(string? value)
    {
        OnPropertyChanged(nameof(HasOutput));

        var generation = ++_renderGeneration;
        RenderedOutput = value;

        if (value is null || !value.Contains("```mermaid", StringComparison.Ordinal))
        {
            return; // fast path - nothing to render, skip the background hop entirely
        }

        _ = RenderMermaidAsync(value, generation);
    }

    /// <summary>Task.Run purely gets the CPU-bound render off the UI thread; the await afterward resumes back on it automatically (OnOutputChanged is always invoked from the UI thread - see GenerateTabViewModel.Handle's _dispatcher.Post - same default-SynchronizationContext-capture reliance as every other async method in this codebase, no explicit dispatcher needed).</summary>
    private async Task RenderMermaidAsync(string output, int generation)
    {
        var rendered = await Task.Run(() => MermaidMarkdownProcessor.Process(output));
        if (generation == _renderGeneration)
        {
            RenderedOutput = rendered;
        }
    }

    public GenerateRequest ToModel() => new()
    {
        Id = Id,
        Input = Input,
        Status = Status,
        Output = Output,
        CreatedAt = CreatedAt,
    };

    public static GenerateRequestViewModel FromModel(GenerateRequest request) => new()
    {
        Id = request.Id,
        Input = request.Input,
        Status = request.Status,
        Output = request.Output,
        CreatedAt = request.CreatedAt,
    };
}
