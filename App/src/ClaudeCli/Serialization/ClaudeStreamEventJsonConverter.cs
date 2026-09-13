using System.Text.Json;
using System.Text.Json.Serialization;
using AutoDev.AiCli.Models;

namespace AutoDev.ClaudeCli.Serialization;

/// <summary>
/// Hand-written because the discriminator shape is inconsistent across event types
/// (nested subtype for "system", flat for "assistant"/"result") - attribute-based
/// polymorphism ([JsonDerivedType]) doesn't handle that cleanly.
/// </summary>
public sealed class ClaudeStreamEventJsonConverter : JsonConverter<AiStreamEvent>
{
    public override AiStreamEvent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;
        string type = root.GetProperty("type").GetString() ?? "";
        string? sessionId = root.TryGetProperty("session_id", out JsonElement sid) ? sid.GetString() : null;

        return type switch
        {
            "system" => new SystemInitEvent
            {
                Type = type,
                SessionId = sessionId,
                Subtype = GetString(root, "subtype"),
                Cwd = GetString(root, "cwd"),
                Model = GetString(root, "model"),
                PermissionMode = GetString(root, "permissionMode"),
            },
            "assistant" => ParseAssistant(root, type, sessionId, options),
            "user" => ParseUserEcho(root, type, sessionId, options),
            "result" => ParseResult(root, type, sessionId),
            _ => new UnknownStreamEvent { Type = type, SessionId = sessionId, Raw = root.Clone() },
        };
    }

    private static AssistantMessageEvent ParseAssistant(JsonElement root, string type, string? sessionId, JsonSerializerOptions options)
    {
        JsonElement message = root.GetProperty("message");
        List<ContentBlock> content = message.TryGetProperty("content", out JsonElement contentEl)
            ? ParseContentBlocks(contentEl, options)
            : [];
        return new AssistantMessageEvent
        {
            Type = type,
            SessionId = sessionId,
            Content = content,
            StopReason = message.TryGetProperty("stop_reason", out JsonElement sr) && sr.ValueKind != JsonValueKind.Null ? sr.GetString() : null,
        };
    }

    private static UserEchoEvent ParseUserEcho(JsonElement root, string type, string? sessionId, JsonSerializerOptions options)
    {
        JsonElement message = root.GetProperty("message");
        List<ContentBlock> content = message.TryGetProperty("content", out JsonElement contentEl)
            ? ParseContentBlocks(contentEl, options)
            : [];
        return new UserEchoEvent { Type = type, SessionId = sessionId, Content = content };
    }

    private static ResultEvent ParseResult(JsonElement root, string type, string? sessionId)
    {
        Dictionary<string, ModelUsageEntry> modelUsage = new Dictionary<string, ModelUsageEntry>();
        if (root.TryGetProperty("modelUsage", out JsonElement muEl) && muEl.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty prop in muEl.EnumerateObject())
            {
                JsonElement m = prop.Value;
                modelUsage[prop.Name] = new ModelUsageEntry(
                    GetLong(m, "inputTokens"),
                    GetLong(m, "outputTokens"),
                    GetLong(m, "cacheReadInputTokens"),
                    GetLong(m, "cacheCreationInputTokens"),
                    GetDecimal(m, "costUSD"),
                    GetNullableLong(m, "contextWindow"),
                    GetNullableLong(m, "maxOutputTokens"));
            }
        }

        return new ResultEvent
        {
            Type = type,
            SessionId = sessionId,
            Subtype = GetString(root, "subtype"),
            IsError = root.TryGetProperty("is_error", out JsonElement ie) && ie.ValueKind == JsonValueKind.True,
            Result = GetString(root, "result"),
            TotalCostUsd = GetDecimal(root, "total_cost_usd"),
            ModelUsage = modelUsage,
        };
    }

    private static List<ContentBlock> ParseContentBlocks(JsonElement contentEl, JsonSerializerOptions options)
    {
        List<ContentBlock> blocks = new List<ContentBlock>();
        if (contentEl.ValueKind == JsonValueKind.String)
        {
            blocks.Add(new TextContentBlock { Type = "text", Text = contentEl.GetString() ?? "" });
            return blocks;
        }

        if (contentEl.ValueKind != JsonValueKind.Array)
        {
            return blocks;
        }

        foreach (JsonElement block in contentEl.EnumerateArray())
        {
            string blockType = GetString(block, "type") ?? "";
            ContentBlock parsed = blockType switch
            {
                "text" => new TextContentBlock { Type = blockType, Text = GetString(block, "text") ?? "" },
                "tool_use" => new ToolUseContentBlock
                {
                    Type = blockType,
                    Id = GetString(block, "id") ?? "",
                    Name = GetString(block, "name") ?? "",
                    Input = block.TryGetProperty("input", out JsonElement input) ? input.Clone() : default,
                },
                "tool_result" => new ToolResultContentBlock
                {
                    Type = blockType,
                    ToolUseId = GetString(block, "tool_use_id") ?? "",
                    Content = ExtractToolResultContent(block),
                    IsError = block.TryGetProperty("is_error", out JsonElement tre) && tre.ValueKind == JsonValueKind.True,
                },
                "image" => ParseImageBlock(blockType, block),
                _ => new UnknownContentBlock { Type = blockType, Raw = block.Clone() },
            };
            blocks.Add(parsed);
        }

        return blocks;
    }

    private static ImageContentBlock ParseImageBlock(string blockType, JsonElement block)
    {
        string mediaType = "image/png";
        string data = "";
        if (block.TryGetProperty("source", out JsonElement source))
        {
            mediaType = GetString(source, "media_type") ?? mediaType;
            data = GetString(source, "data") ?? data;
        }

        return new ImageContentBlock { Type = blockType, MediaType = mediaType, Base64Data = data };
    }

    private static string ExtractToolResultContent(JsonElement block)
    {
        if (!block.TryGetProperty("content", out JsonElement content))
        {
            return "";
        }

        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? "",
            _ => content.GetRawText(),
        };
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long GetLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0L;

    private static long? GetNullableLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

    private static decimal GetDecimal(JsonElement el, string name) =>
        el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;

    public override void Write(Utf8JsonWriter writer, AiStreamEvent value, JsonSerializerOptions options) =>
        throw new NotSupportedException($"{nameof(AiStreamEvent)} is read-only from the CLI's stdout stream and is never serialized back out.");
}
