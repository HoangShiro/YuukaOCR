using System.Text.Json.Serialization;

namespace YuukaOCR.Core.Settings;

/// <summary>
/// The user's appearance choices: which <see cref="ThemePresets">preset</see> is active, how opaque the
/// floating frame (bar) and the window backgrounds are, and the font.
///
/// Individual colours are deliberately *not* stored any more — they come from the preset, so the
/// settings window, the bar and the result bubble always agree. Only the preset id and the two
/// opacities are persisted, which also keeps settings.json readable.
/// </summary>
public sealed class ThemeSettings
{
    /// <summary>Below this the window is still legible; under it the user could hide the app forever.</summary>
    public const int MinOpacity = 20;

    [JsonPropertyName("preset")] public string Preset { get; set; } = ThemePresets.DefaultId;

    /// <summary>Opacity of the floating bar / pill, in percent.</summary>
    [JsonPropertyName("frame_opacity")] public int FrameOpacity { get; set; } = 100;

    /// <summary>
    /// Opacity of the result bubble and the settings window background, in percent.
    ///
    /// 90 is the shipped default. The value is only ever taken from the file: a settings.json that
    /// stores its own number keeps it, so an upgrade never resets a user's chosen transparency.
    /// </summary>
    [JsonPropertyName("background_opacity")] public int BackgroundOpacity { get; set; } = 90;

    [JsonPropertyName("sub_win_font_family")] public string FontFamily { get; set; } = "Segoe UI";
    [JsonPropertyName("sub_win_font_size")] public double FontSize { get; set; } = 12;

    [JsonIgnore] public ThemePalette Palette => ThemePresets.Palette(Preset);

    /// <summary>The bar colour with the frame opacity applied — what the pill is painted with.</summary>
    [JsonIgnore] public Rgba Bar => WithAlpha(Palette.Bar, FrameOpacity);

    [JsonIgnore] public Rgba Background => WithAlpha(Palette.Background, BackgroundOpacity);
    [JsonIgnore] public Rgba Text => Palette.Text;
    [JsonIgnore] public Rgba Accent => Palette.Accent;

    [JsonIgnore] public byte FrameAlpha => Alpha(FrameOpacity);
    [JsonIgnore] public byte BackgroundAlpha => Alpha(BackgroundOpacity);

    /// <summary>Repairs values a hand-edited JSON file could break.</summary>
    public void Normalize()
    {
        Preset = ThemePresets.Normalize(Preset);
        FrameOpacity = (int)SliderSpecs.Snap(SliderSpecs.FrameOpacity, FrameOpacity);
        BackgroundOpacity = (int)SliderSpecs.Snap(SliderSpecs.BackgroundOpacity, BackgroundOpacity);
        FontSize = SliderSpecs.Snap(SliderSpecs.FontSize, FontSize);
        if (string.IsNullOrWhiteSpace(FontFamily)) FontFamily = "Segoe UI";
    }

    public ThemeSettings Clone() => new()
    {
        Preset = Preset,
        FrameOpacity = FrameOpacity,
        BackgroundOpacity = BackgroundOpacity,
        FontFamily = FontFamily,
        FontSize = FontSize
    };

    public static Rgba WithAlpha(Rgba color, int opacityPercent) => color with { A = Alpha(opacityPercent) };

    private static byte Alpha(int opacityPercent) =>
        (byte)Math.Clamp(Math.Round(Math.Clamp(opacityPercent, 0, 100) * 255.0 / 100.0), 0, 255);
}
