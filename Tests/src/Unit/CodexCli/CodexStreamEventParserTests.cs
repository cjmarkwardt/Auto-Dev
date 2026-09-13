namespace AutoDev.Tests.CodexCli;

/// <summary>Covers CodexStreamEventParser's mapping from a `codex exec --json` event's "type" and JSON body onto the shared AiStreamEvent shape, including the item kinds it deliberately drops.</summary>
public sealed class CodexStreamEventParserTests
{
    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>"thread.started" becomes a SystemInitEvent carrying the thread id as SessionId.</summary>
    [Fact]
    public void Parse_ThreadStarted_ReturnsSystemInitEventWithSessionId()
    {
        AiStreamEvent? result = CodexStreamEventParser.Parse("thread.started", Root("""{"thread_id":"abc-123"}"""));

        SystemInitEvent init = Assert.IsType<SystemInitEvent>(result);
        Assert.Equal("thread.started", init.Type);
        Assert.Equal("abc-123", init.SessionId);
    }

    /// <summary>An "agent_message" item is only surfaced once it's completed - Codex never streams partial text for it, so "item.started" for it produces nothing.</summary>
    [Fact]
    public void Parse_AgentMessageItemStarted_ReturnsNull()
    {
        AiStreamEvent? result = CodexStreamEventParser.Parse("item.started", Root("""{"item":{"type":"agent_message","text":"partial"}}"""));

        Assert.Null(result);
    }

    /// <summary>A completed "agent_message" item becomes an AssistantMessageEvent whose single text block carries the message.</summary>
    [Fact]
    public void Parse_AgentMessageItemCompleted_ReturnsAssistantMessageWithText()
    {
        AiStreamEvent? result = CodexStreamEventParser.Parse("item.completed", Root("""{"item":{"type":"agent_message","text":"final reply"}}"""));

        AssistantMessageEvent message = Assert.IsType<AssistantMessageEvent>(result);
        Assert.Equal("final reply", message.PlainText);
    }

    /// <summary>A started "command_execution" item becomes a single-tool-use AssistantMessageEvent named "Bash", with its command as the "command" input field.</summary>
    [Fact]
    public void Parse_CommandExecutionItemStarted_ReturnsBashToolUse()
    {
        AiStreamEvent? result = CodexStreamEventParser.Parse("item.started", Root("""{"item":{"type":"command_execution","command":"ls -la"}}"""));

        AssistantMessageEvent message = Assert.IsType<AssistantMessageEvent>(result);
        ToolUseContentBlock toolUse = Assert.Single(message.ToolUses);
        Assert.Equal("Bash", toolUse.Name);
        Assert.Equal("ls -la", toolUse.Input.GetProperty("command").GetString());
    }

    /// <summary>A started "file_change" item becomes an "Edit" tool use with no input fields.</summary>
    [Fact]
    public void Parse_FileChangeItemStarted_ReturnsEditToolUse()
    {
        AiStreamEvent? result = CodexStreamEventParser.Parse("item.started", Root("""{"item":{"type":"file_change"}}"""));

        AssistantMessageEvent message = Assert.IsType<AssistantMessageEvent>(result);
        Assert.Equal("Edit", Assert.Single(message.ToolUses).Name);
    }

    /// <summary>A started "mcp_tool_call" item includes both the server and tool name in its description when the server is present.</summary>
    [Fact]
    public void Parse_McpToolCallItemStarted_DescribesServerAndTool()
    {
        AiStreamEvent? result = CodexStreamEventParser.Parse("item.started", Root("""{"item":{"type":"mcp_tool_call","server":"my-server","tool":"my-tool"}}"""));

        AssistantMessageEvent message = Assert.IsType<AssistantMessageEvent>(result);
        ToolUseContentBlock toolUse = Assert.Single(message.ToolUses);
        Assert.Equal("McpToolCall", toolUse.Name);
        Assert.Equal("my-server: my-tool", toolUse.Input.GetProperty("description").GetString());
    }

    /// <summary>A started "mcp_tool_call" item with no server name falls back to a generic description rather than "null: null".</summary>
    [Fact]
    public void Parse_McpToolCallItemStartedWithoutServer_UsesGenericDescription()
    {
        AiStreamEvent? result = CodexStreamEventParser.Parse("item.started", Root("""{"item":{"type":"mcp_tool_call"}}"""));

        AssistantMessageEvent message = Assert.IsType<AssistantMessageEvent>(result);
        Assert.Equal("Calling an MCP tool", Assert.Single(message.ToolUses).Input.GetProperty("description").GetString());
    }

    /// <summary>"reasoning" and "error" items are dropped entirely, same as Claude's own thinking blocks are never rendered.</summary>
    [Theory]
    [InlineData("reasoning")]
    [InlineData("error")]
    public void Parse_DroppedItemKinds_ReturnNull(string itemType)
    {
        AiStreamEvent? result = CodexStreamEventParser.Parse("item.started", Root("{\"item\":{\"type\":\"" + itemType + "\"}}"));

        Assert.Null(result);
    }

    /// <summary>Tool-shaped items only produce an event on "item.started" - their completion carries no new information (see DescribeToolUse, which never reads a tool result), so "item.completed" for them is dropped.</summary>
    [Fact]
    public void Parse_ToolItemCompleted_ReturnsNull()
    {
        AiStreamEvent? result = CodexStreamEventParser.Parse("item.completed", Root("""{"item":{"type":"command_execution","command":"ls"}}"""));

        Assert.Null(result);
    }

    /// <summary>An event type this parser doesn't know about at all (e.g. Codex's own turn-terminal events, handled elsewhere by CodexSessionClient) maps to null rather than throwing.</summary>
    [Fact]
    public void Parse_UnknownEventType_ReturnsNull()
    {
        AiStreamEvent? result = CodexStreamEventParser.Parse("turn.completed", Root("{}"));

        Assert.Null(result);
    }

    /// <summary>A malformed "item.started" event missing the "item"/"type" structure entirely is dropped rather than throwing.</summary>
    [Fact]
    public void Parse_ItemEventMissingItemProperty_ReturnsNull()
    {
        AiStreamEvent? result = CodexStreamEventParser.Parse("item.started", Root("{}"));

        Assert.Null(result);
    }
}
