using YuukaOCR.Core.Settings;
using YuukaOCR.Core.Localization;

namespace YuukaOCR.Core.Ocr;

public sealed record MediaLoadResult(
    byte[]? Bytes,
    string? Mime,
    string? InlineText,
    string? FileName,
    string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>
/// Turns a dropped / pasted file into something a model can eat: images and PDFs and audio go as
/// bytes + mime, plain-text formats are inlined. Size and extension checks live here so both the
/// drop path and the clipboard path share them.
/// </summary>
public static class MediaLoader
{
    private static readonly Dictionary<string, string> MimeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif",
        [".bmp"] = "image/bmp",
        [".tiff"] = "image/tiff",
        [".ico"] = "image/x-icon",
        [".pdf"] = "application/pdf",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".m4a"] = "audio/mp4",
        [".flac"] = "audio/flac",
        [".ogg"] = "audio/ogg"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".csv", ".log", ".xml", ".yaml", ".yml", ".ini", ".cs", ".py", ".js", ".ts"
    };

    public static bool IsAccepted(string path, AppConfig config)
        => config.IsAcceptedExtension(Path.GetExtension(path)) || TextExtensions.Contains(Path.GetExtension(path));

    public static MediaLoadResult Load(string path, AppConfig config, long maxBytes = 0)
    {
        try
        {
            if (!File.Exists(path)) return Fail(L.F("media.notFound", Path.GetFileName(path)));

            var extension = Path.GetExtension(path);
            if (!IsAccepted(path, config))
                return Fail(L.F("media.unsupported", extension));

            var info = new FileInfo(path);
            var limit = maxBytes > 0 ? maxBytes : (long)config.MaxFileSizeMb * 1024 * 1024;
            if (info.Length > limit)
                return Fail(L.F("media.tooLarge", info.Length / 1024 / 1024, limit / 1024 / 1024));

            if (TextExtensions.Contains(extension))
            {
                var text = File.ReadAllText(path);
                return new MediaLoadResult(null, null, text, Path.GetFileName(path), null);
            }

            if (!MimeByExtension.TryGetValue(extension, out var mime))
                return Fail(L.F("media.noMime", extension));

            return new MediaLoadResult(File.ReadAllBytes(path), mime, null, Path.GetFileName(path), null);
        }
        catch (Exception ex)
        {
            return Fail(L.F("media.readError", ex.Message));
        }
    }

    public static MediaLoadResult FromBytes(byte[] bytes, string mime, string fileName = "clipboard")
        => new(bytes, mime, null, fileName, null);

    private static MediaLoadResult Fail(string error) => new(null, null, null, null, error);
}
