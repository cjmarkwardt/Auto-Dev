namespace AutoDev.Core.Models;

public enum GenerateRequestStatus
{
    Working,
    Cancelled,
    Completed,
}

/// <summary>
/// One user-submitted Generate tab request, as persisted under `.autodev/local/generate-requests.json`
/// (see IWorkspaceMetadataStore.LoadGenerateRequestsAsync/SaveGenerateRequestsAsync) - the last 5 per
/// session are kept, oldest evicted first. Input grows via string append when the user sends more text
/// while this request is still Working (an interjection - see GenerateTabViewModel.SendAsync), rather than
/// creating a separate request.
/// </summary>
public sealed class GenerateRequest
{
    public required string Id { get; set; }
    public required string Input { get; set; }
    public GenerateRequestStatus Status { get; set; } = GenerateRequestStatus.Working;
    public string? Output { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
}
