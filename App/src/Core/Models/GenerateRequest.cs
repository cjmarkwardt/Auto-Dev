namespace AutoDev.Core.Models;

public enum GenerateRequestStatus
{
    Working,
    Cancelled,
    Completed,

    /// <summary>Immediately stopped (no subprocess running) but still the same turn - persisted as-is across an app restart so Resume can pick the conversation back up later, whether it got here via an explicit Pause or was still Working when the app/workspace closed (a clean close coerces it to this in GenerateTabViewModel.DisposeAsync; an unclean crash/kill leaves it persisted as Working, which SwitchSessionAsync's loader coerces to this instead on the next load - either way, closing mid-turn is always recoverable as a pause, never a silent Cancel). Added after the other three - JsonStringEnumConverter (see AppJson.Options) serializes by name, but keeping new values at the end avoids ever reordering the existing ones regardless.</summary>
    Paused,
}

/// <summary>
/// One user-submitted Generate tab request, as persisted under `.autodev/local/generate-requests.json`
/// (see IWorkspaceMetadataStore.LoadGenerateRequestsAsync/SaveGenerateRequestsAsync) - the last 10 per
/// session are kept, oldest evicted first. Input grows via string append when the user sends more text
/// while this request is still Working (an interjection - see GenerateTabViewModel.SendAsync), rather than
/// creating a separate request. Pausing and resuming (see GenerateRequestStatus.Paused) don't create a new
/// request either - it's still considered the same turn no matter how many times it's paused/resumed.
/// </summary>
public sealed class GenerateRequest
{
    public required string Id { get; set; }
    public required string Input { get; set; }
    public GenerateRequestStatus Status { get; set; } = GenerateRequestStatus.Working;
    public string? Output { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
}
