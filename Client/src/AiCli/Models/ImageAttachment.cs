namespace AutoDev.AiCli.Models;

/// <summary>An image to embed in a user message's content array (see ClaudeInputMessageWriter.UserMessageWithAttachments) - Base64Data is the raw base64-encoded bytes, no data-URI prefix.</summary>
public sealed record ImageAttachment(string MediaType, string Base64Data);
