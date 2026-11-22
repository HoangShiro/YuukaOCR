namespace YuukaOCR.Core.Settings;

/// <summary>
/// A fully resolved look: every colour a window needs, already contrast-checked.
/// Built from a <see cref="ThemePreset"/>, which only authors five colours — the panel/hover/border
/// steps are derived from the background brightness so a light preset gets darker panels instead of
/// washed-out white ones.
/// </summary>
public sealed record ThemePalette(
    Rgba Bar,
    Rgba BarText,
    Rgba Accent,
    Rgba OnAccent,
    Rgba Close,
    Rgba Background,
    Rgba Panel,
    Rgba PanelHover,
    Rgba Border,
    Rgba Text,
    Rgba Subtle,
    Rgba Danger);

/// <summary>
/// One predefined theme offered as a card in Settings → Appearance. A preset touches the bar (the
/// floating frame), the accent (settings button, selection, sliders), the result window, and — through
/// the same palette — the settings window itself, so the whole app switches look at once.
/// </summary>
public sealed record ThemePreset(
    string Id,
    string NameKey,
    bool IsDark,
    Rgba Bar,
    Rgba Accent,
    Rgba Close,
    Rgba Background,
    Rgba Text)
{
    /// <summary>The resolved colours. Cheap to build, so it is computed on demand instead of cached.</summary>
    public ThemePalette Palette
    {
        get
        {
            // Dark backgrounds step lighter, light backgrounds step darker.
            var step = IsDark ? 1 : -1;
            return new ThemePalette(
                Bar,
                Ink(Bar),
                Accent,
                Ink(Accent),
                Close,
                Background,
                Shift(Background, step * (IsDark ? 12 : 6)),
                Shift(Background, step * (IsDark ? 22 : 12)),
                Shift(Background, step * (IsDark ? 36 : 22)),
                Text,
                // Light themes need a heavier secondary ink: 58% of near-black over a near-white panel
                // reads as washed-out grey, which is exactly where the slider tick numbers live.
                new Rgba(Text.R, Text.G, Text.B, IsDark ? (byte)150 : (byte)205),
                IsDark ? new Rgba(0xFF, 0x7A, 0x85) : new Rgba(0xC0, 0x26, 0x3B));
        }
    }

    /// <summary>
    /// Text/icon colour to paint on top of a surface. The presets are pastel on purpose, so the bar and
    /// the accent buttons both need dark ink instead of the window's white text.
    /// </summary>
    private static Rgba Ink(Rgba surface)
    {
        var luminance = (0.2126 * surface.R + 0.7152 * surface.G + 0.0722 * surface.B) / 255.0;
        return luminance > 0.5 ? new Rgba(0x14, 0x15, 0x1A) : Rgba.White;
    }

    private static Rgba Shift(Rgba color, int amount) => new(
        (byte)Math.Clamp(color.R + amount, 0, 255),
        (byte)Math.Clamp(color.G + amount, 0, 255),
        (byte)Math.Clamp(color.B + amount, 0, 255));
}

/// <summary>The preset table. Adding a look = adding one row here plus its name key in both string tables.</summary>
public static class ThemePresets
{
    public const string DefaultId = "sakura";

    public static IReadOnlyList<ThemePreset> All { get; } =
    [
        new("sakura", "theme.sakura", true,
            Bar: new Rgba(0xEA, 0xA2, 0xBC), Accent: new Rgba(0xE9, 0x89, 0x73), Close: new Rgba(0xE9, 0x73, 0x9E),
            Background: new Rgba(0x1E, 0x1E, 0x1E), Text: Rgba.White),

        new("midnight", "theme.midnight", true,
            Bar: new Rgba(0x5B, 0x5E, 0xE8), Accent: new Rgba(0x81, 0x8C, 0xF8), Close: new Rgba(0xF4, 0x72, 0xB6),
            Background: new Rgba(0x10, 0x12, 0x1A), Text: new Rgba(0xEE, 0xF0, 0xFF)),

        new("ocean", "theme.ocean", true,
            Bar: new Rgba(0x2D, 0xD4, 0xBF), Accent: new Rgba(0x38, 0xBD, 0xF8), Close: new Rgba(0x22, 0xD3, 0xEE),
            Background: new Rgba(0x0B, 0x1A, 0x22), Text: new Rgba(0xEC, 0xFE, 0xFF)),

        new("forest", "theme.forest", true,
            Bar: new Rgba(0x86, 0xC7, 0x9A), Accent: new Rgba(0x4A, 0xDE, 0x80), Close: new Rgba(0x34, 0xD3, 0x99),
            Background: new Rgba(0x0F, 0x1A, 0x13), Text: new Rgba(0xEC, 0xFD, 0xF5)),

        new("sunset", "theme.sunset", true,
            Bar: new Rgba(0xF5, 0x9E, 0x6B), Accent: new Rgba(0xFB, 0x71, 0x85), Close: new Rgba(0xF9, 0x73, 0x16),
            Background: new Rgba(0x1B, 0x11, 0x14), Text: new Rgba(0xFF, 0xF1, 0xE6)),

        new("lavender", "theme.lavender", true,
            Bar: new Rgba(0xC4, 0xB5, 0xFD), Accent: new Rgba(0xA7, 0x8B, 0xFA), Close: new Rgba(0xF0, 0xAB, 0xFC),
            Background: new Rgba(0x15, 0x12, 0x1F), Text: new Rgba(0xF5, 0xF3, 0xFF)),

        new("graphite", "theme.graphite", true,
            Bar: new Rgba(0x9C, 0xA3, 0xAF), Accent: new Rgba(0xE5, 0xE7, 0xEB), Close: new Rgba(0x6B, 0x72, 0x80),
            Background: new Rgba(0x10, 0x11, 0x14), Text: new Rgba(0xF3, 0xF4, 0xF6)),

        new("daylight", "theme.daylight", false,
            Bar: new Rgba(0xF9, 0xA8, 0xD4), Accent: new Rgba(0xC2, 0x18, 0x5B), Close: new Rgba(0xDB, 0x27, 0x77),
            Background: new Rgba(0xF7, 0xF8, 0xFC), Text: new Rgba(0x1F, 0x24, 0x30))
    ];

    /// <summary>Unknown or missing id → the default look (a hand-edited settings.json must not blank the UI).</summary>
    public static string Normalize(string? id) => Get(id).Id;

    public static ThemePreset Get(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? All.First(p => p.Id == DefaultId);

    public static ThemePalette Palette(string? id) => Get(id).Palette;
}
