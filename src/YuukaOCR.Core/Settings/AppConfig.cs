using System.Text.Json;
using System.Text.Json.Serialization;
using YuukaOCR.Core.Localization;

namespace YuukaOCR.Core.Settings;

/// <summary>Machine-level defaults shipped in config/app_configs.json (not user-editable from the UI).</summary>
public sealed class AppConfig
{
    /// <summary>
    /// The shipped OCR prompt, in English like every other default. It asks for a Vietnamese
    /// translation because that is what the app is used for — the *prompt* is English, not the answer.
    /// </summary>
    public const string DefaultPromptText =
        "ALWAYS translate the text into Vietnamese, keeping a tone that fits the context, the speaker's " +
        "tone and a youthful, anime-style register. Return only the translated text.";

    [JsonPropertyName("UiImage")] public string UiImage { get; set; } = "ui.png";
    [JsonPropertyName("MaxFileSizeMb")] public int MaxFileSizeMb { get; set; } = 100;
    [JsonPropertyName("HookProximityTop")] public int HookProximityTop { get; set; } = 100;
    [JsonPropertyName("HookProximityBottom")] public int HookProximityBottom { get; set; } = 100;
    [JsonPropertyName("HookOffsetTop")] public int HookOffsetTop { get; set; }
    [JsonPropertyName("HookOffsetBottom")] public int HookOffsetBottom { get; set; }
    [JsonPropertyName("UnhookDistance")] public int UnhookDistance { get; set; } = 100;
    [JsonPropertyName("SpringConstant")] public double SpringConstant { get; set; } = 0.10;
    [JsonPropertyName("DampingFactor")] public double DampingFactor { get; set; } = 0.45;
    [JsonPropertyName("BounceDampingFactor")] public double BounceDampingFactor { get; set; } = 0.30;
    [JsonPropertyName("HookOcrHotkey")] public string HookOcrHotkey { get; set; } = "middle";
    [JsonPropertyName("DefaultProvider")] public string DefaultProvider { get; set; } = "gemini";
    [JsonPropertyName("DefaultPrompt")] public string DefaultPrompt { get; set; } = DefaultPromptText;

    [JsonPropertyName("AcceptedFileExtensions")]
    public string[] AcceptedFileExtensions { get; set; } =
    [
        ".pdf", ".mp3", ".wav", ".m4a", ".flac", ".ogg",
        ".jpeg", ".jpg", ".png", ".gif", ".bmp", ".tiff", ".ico", ".webp",
        ".txt", ".md", ".json", ".csv"
    ];

    public static AppConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Json.Options);
                if (cfg is not null)
                {
                    if (cfg.AcceptedFileExtensions.Length == 0) cfg.AcceptedFileExtensions = new AppConfig().AcceptedFileExtensions;
                    return cfg;
                }
            }
        }
        catch (Exception)
        {
            // A broken config file must not stop the app: fall back to compiled defaults.
        }
        return new AppConfig();
    }

    /// <summary>Writes the config back to disk (only the colour pickers change it at runtime).</summary>
    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json.Options));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(L.F("app.configWriteError", ex.Message));
        }
    }

    public bool IsAcceptedExtension(string extension) =>
        AcceptedFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
}
