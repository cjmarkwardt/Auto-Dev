using System.Text.Json;
using AutoDev.AiCli.Models;

namespace AutoDev.CodexCli;

/// <summary>
/// Maps one `codex exec --json` JSONL line's "thread.started"/"item.started"/"item.completed" events onto
/// the shared AiStreamEvent shape - see CodexSessionClient, which handles "turn.started"/"turn.completed"/
/// "turn.failed" itself instead (those are turn-terminal signals that assemble a ResultEvent from state
/// accumulated across the whole turn, not a one-line-in one-event-out mapping like everything here).
/// </summary>
internal static class CodexStreamEventParser
{
    public static AiStreamEvent? Parse(string type, JsonElement root) => type switch
    {
        "thread.started" => new SystemInitEvent
        {
            Type = type,
            SessionId = GetString(root, "thread_id"),
        },
        "item.started" or "item.completed" => ParseItemEvent(type, root),
        _ => null,
    };

    /// <summary>
    /// Only a subset of item kinds are surfaced at all: "agent_message" (only once completed - Codex never
    /// streams partial text for it) becomes the turn's visible reply text; the various tool-shaped kinds
    /// become a single-tool-use AssistantMessageEvent the moment they start (matching Claude's own
    /// tool_use-block timing, which is what drives GenerateTabViewModel's CurrentAction status text) - their
    /// own completion carries no new information CaptureActiveRequestToolUse would use, so it's ignored
    /// (see DescribeToolUse, which only ever reads the latest tool use, never a result). "reasoning" and
    /// "error" items (e.g. a "model metadata not found, falling back" warning) are dropped entirely - Claude's
    /// own "thinking" content blocks are equally never rendered today.
    /// </summary>
    private static AiStreamEvent? ParseItemEvent(string type, JsonElement root)
    {
        if (!root.TryGetProperty("item", out var item) || !item.TryGetProperty("type", out var itemTypeEl))
        {
            return null;
        }

        var itemType = itemTypeEl.GetString();
        if (itemType == "agent_message")
        {
            return type == "item.completed" ? TextEvent(type, GetString(item, "text") ?? "") : null;
        }

        if (type != "item.started")
        {
            return null;
        }

        return itemType switch
        {
            "command_execution" => ToolUseEvent(type, "Bash", "command", GetString(item, "command") ?? ""),
            "file_change" => ToolUseEvent(type, "Edit"),
            "web_search" => ToolUseEvent(type, "WebSearch", "query", GetString(item, "query") ?? ""),
            "todo_list" => ToolUseEvent(type, "TodoWrite"),
            "mcp_tool_call" => ToolUseEvent(type, "McpToolCall", "description", GetString(item, "server") is { Length: > 0 } server ? $"{server}: {GetString(item, "tool")}" : "Calling an MCP tool"),
            _ => null,
        };
    }

    private static AssistantMessageEvent TextEvent(string type, string text) => new()
    {
        Type = type,
        Content = [new TextContentBlock { Type = "text", Text = text }],
    };

    /// <summary>No input at all - for a tool name whose DescribeToolUse case (see GenerateTabViewModel) already shows a fixed status line regardless of input (e.g. "TodoWrite"), or gracefully falls back to a generic line when a field it looks for (e.g. "Edit"'s "file_path") is absent.</summary>
    private static AssistantMessageEvent ToolUseEvent(string type, string toolName) => new()
    {
        Type = type,
        Content = [new ToolUseContentBlock { Type = "tool_use", Id = "", Name = toolName, Input = default }],
    };

    /// <summary>inputProperty/inputValue become the tool_use block's single Input field - matches just enough of DescribeToolUse's per-tool-name switch (e.g. "Bash" reads "command") to produce a sensible status line without needing a whole second switch statement there.</summary>
    private static AssistantMessageEvent ToolUseEvent(string type, string toolName, string inputProperty, string inputValue)
    {
        using var inputDoc = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string> { [inputProperty] = inputValue }));
        return new AssistantMessageEvent
        {
            Type = type,
            Content = [new ToolUseContentBlock { Type = "tool_use", Id = "", Name = toolName, Input = inputDoc.RootElement.Clone() }],
        };
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
