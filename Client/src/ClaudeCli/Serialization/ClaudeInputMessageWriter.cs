using System.Text.Json.Nodes;
using AutoDev.AiCli.Models;

namespace AutoDev.ClaudeCli.Serialization;

/// <summary>
/// Builds the newline-delimited JSON envelopes written to a `claude -p --input-format stream-json` process's stdin.
/// Shapes verified empirically against the running CLI (see plan doc for the probing session).
/// </summary>
public static class ClaudeInputMessageWriter
{
    public static string UserMessage(string text)
    {
        var node = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = text,
            },
        };
        return node.ToJsonString();
    }

    /// <summary>Same envelope as UserMessage, but with a multi-block content array (Anthropic Messages API shape) - each image first, then a trailing text block if text is non-empty, so pasted images show up in the order they'd naturally be read.</summary>
    public static string UserMessageWithAttachments(string text, IReadOnlyList<ImageAttachment> images)
    {
        var content = new JsonArray();
        foreach (var image in images)
        {
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = image.MediaType,
                    ["data"] = image.Base64Data,
                },
            });
        }

        if (text.Length > 0)
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        }

        var node = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = content,
            },
        };
        return node.ToJsonString();
    }
}
