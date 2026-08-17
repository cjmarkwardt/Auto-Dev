namespace AutoDev.ViewModels.Content;

/// <summary>
/// One non-image file referenced (not embedded) in a user message - pending in the Generate tab's input
/// box or already attached to a sent message, mirroring ChatImageAttachment's pending/sent duality. Unlike
/// an image there's no content-block type for this in the CLI's wire protocol (see
/// ClaudeCli.Serialization.ClaudeInputMessageWriter - only image/text exist), so DisplayName ends up
/// composed into the outgoing text as a plain "Attached file: X" line at send time (see
/// GenerateTabViewModel.SendAsync) rather than sent as structured data - Claude's own Read tool just needs
/// the path as text to open it.
/// </summary>
public sealed record ChatFileAttachment(string DisplayName, string FullPath);
