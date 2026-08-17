using System.Text.Json.Serialization;

namespace AutoDev.ClaudeCli.Models;

public sealed record AskUserQuestionOption(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("description")] string? Description);

public sealed record AskUserQuestionItem(
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("header")] string? Header,
    [property: JsonPropertyName("options")] IReadOnlyList<AskUserQuestionOption> Options,
    [property: JsonPropertyName("multiSelect")] bool MultiSelect);

public sealed record AskUserQuestionInput(
    [property: JsonPropertyName("questions")] IReadOnlyList<AskUserQuestionItem> Questions);

/// <summary>Raised when the Claude CLI invokes its AskUserQuestion tool mid-session.</summary>
public sealed record AskUserQuestionRequest(string ToolUseId, IReadOnlyList<AskUserQuestionItem> Questions);
