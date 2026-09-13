using System.Text.Json;

namespace AutoDev.AiCli.Models;

public abstract record ContentBlock
{
    public required string Type { get; init; }
}

public sealed record TextContentBlock : ContentBlock
{
    public required string Text { get; init; }
}

public sealed record ToolUseContentBlock : ContentBlock
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required JsonElement Input { get; init; }
}

public sealed record ToolResultContentBlock : ContentBlock
{
    public required string ToolUseId { get; init; }
    public required string Content { get; init; }
    public bool IsError { get; init; }
}

/// <summary>An image attached to a user message (see ClaudeInputMessageWriter/GenerateTabViewModel) - Base64Data is the raw base64-encoded bytes, no data-URI prefix, matching the Anthropic Messages API's base64 image source shape.</summary>
public sealed record ImageContentBlock : ContentBlock
{
    public required string MediaType { get; init; }
    public required string Base64Data { get; init; }
}

/// <summary>Anything we don't explicitly model (e.g. thinking blocks) - kept so the raw JSON is never lost.</summary>
public sealed record UnknownContentBlock : ContentBlock
{
    public required JsonElement Raw { get; init; }
}
