namespace YuukaOCR.Core.Ocr;

public enum OcrSource
{
    ImageClipboard,
    FileClipboard,
    TextClipboard,
    HookedRegion,
    FileDrop
}

/// <summary>
/// One unit of work. Media (image/pdf/audio) travels as bytes + mime; plain text files are read
/// into <see cref="InlineText"/> so providers that cannot accept uploads still work.
/// </summary>
public sealed record OcrRequest(
    OcrSource Source,
    byte[]? MediaBytes = null,
    string? MediaMime = null,
    string? InlineText = null,
    string? FileName = null);
