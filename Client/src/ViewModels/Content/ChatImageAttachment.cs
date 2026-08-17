using Avalonia.Media.Imaging;

namespace AutoDev.ViewModels.Content;

/// <summary>
/// One image pending in the Generate tab's input box (see GenerateTabViewModel.Attachments) - decoded once
/// into Bitmap for the input chip's preview, rather than re-decoding Base64Data every time. MediaType/
/// Base64Data are what actually get sent to Claude (see ClaudeInputMessageWriter.UserMessageWithAttachments).
/// </summary>
public sealed record ChatImageAttachment(string MediaType, string Base64Data, Bitmap Bitmap);
