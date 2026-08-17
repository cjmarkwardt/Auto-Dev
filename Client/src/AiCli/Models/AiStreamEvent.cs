using System.Text.Json;

namespace AutoDev.AiCli.Models;

/// <summary>
/// One event from an AI provider's turn-by-turn event stream, normalized to a shape every provider's
/// session client (ClaudeSessionClient, CodexSessionClient) translates its own raw wire format into -
/// GenerateTabViewModel's Handle() consumes only this common shape and never needs to know which provider
/// actually produced a given event. Each provider's own JSON parser (e.g. ClaudeStreamEventJsonConverter)
/// is supplied explicitly via JsonSerializerOptions at its own deserialize call sites rather than via a
/// [JsonConverter] attribute here, since no single converter can handle every provider's raw wire shape.
/// </summary>
public abstract record AiStreamEvent
{
    public required string Type { get; init; }
    public string? SessionId { get; init; }
}

public sealed record SystemInitEvent : AiStreamEvent
{
    public string? Subtype { get; init; }
    public string? Cwd { get; init; }
    public string? Model { get; init; }
    public string? PermissionMode { get; init; }
}

public sealed record AssistantMessageEvent : AiStreamEvent
{
    public required IReadOnlyList<ContentBlock> Content { get; init; }
    public string? StopReason { get; init; }

    public IEnumerable<TextContentBlock> TextBlocks => Content.OfType<TextContentBlock>();
    public IEnumerable<ToolUseContentBlock> ToolUses => Content.OfType<ToolUseContentBlock>();

    public string PlainText => string.Concat(TextBlocks.Select(b => b.Text));
}

/// <summary>The `{"type":"user",...}` echo events the Claude CLI emits for tool results (including our own replies being echoed back) - Codex has no equivalent, so only ClaudeStreamEventJsonConverter ever produces this.</summary>
public sealed record UserEchoEvent : AiStreamEvent
{
    public required IReadOnlyList<ContentBlock> Content { get; init; }
}

public sealed record ModelUsageEntry(
    long InputTokens,
    long OutputTokens,
    long CacheReadInputTokens,
    long CacheCreationInputTokens,
    decimal CostUsd,
    long? ContextWindow = null,
    long? MaxOutputTokens = null);

public sealed record ResultEvent : AiStreamEvent
{
    public string? Subtype { get; init; }
    public bool IsError { get; init; }
    public string? Result { get; init; }
    public decimal TotalCostUsd { get; init; }
    public IReadOnlyDictionary<string, ModelUsageEntry> ModelUsage { get; init; } = new Dictionary<string, ModelUsageEntry>();

    /// <summary>Cumulative usage for the whole session, summed across every model used - see UsageSnapshot doc comment.</summary>
    public UsageSnapshot CumulativeUsage => ModelUsage.Values.Aggregate(
        UsageSnapshot.Zero,
        (acc, m) => acc + new UsageSnapshot(m.InputTokens, m.OutputTokens, m.CacheCreationInputTokens, m.CacheReadInputTokens, m.CostUsd));
}

/// <summary>Catch-all for event types we don't act on (rate_limit_event, etc.) - keeps every provider's parser from ever throwing.</summary>
public sealed record UnknownStreamEvent : AiStreamEvent
{
    public JsonElement Raw { get; init; }
}
