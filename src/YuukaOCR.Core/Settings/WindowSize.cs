using System.Text.Json.Serialization;

namespace YuukaOCR.Core.Settings;

/// <summary>
/// A window's own size, in DIPs, as the user left it. Kept as a small object rather than two loose
/// dictionary entries so a settings file stays readable:
/// <code>"window_sizes": { "settings": { "width": 980, "height": 720 } }</code>
/// </summary>
public sealed class WindowSize
{
    [JsonPropertyName("width")] public double Width { get; set; }
    [JsonPropertyName("height")] public double Height { get; set; }

    public WindowSize() { }

    public WindowSize(double width, double height)
    {
        Width = width;
        Height = height;
    }

    /// <summary>True when both sides are real, usable numbers — a hand-edited file can hold anything.</summary>
    [JsonIgnore]
    public bool IsUsable =>
        double.IsFinite(Width) && double.IsFinite(Height) &&
        Width >= WindowSizeKeys.MinSide && Height >= WindowSizeKeys.MinSide;

    /// <summary>Clamps the pair into the range a window can actually be given.</summary>
    public WindowSize Clamped() => new(
        Math.Clamp(double.IsFinite(Width) ? Width : 0, WindowSizeKeys.MinSide, WindowSizeKeys.MaxSide),
        Math.Clamp(double.IsFinite(Height) ? Height : 0, WindowSizeKeys.MinSide, WindowSizeKeys.MaxSide));

    public WindowSize Clone() => new(Width, Height);
}

/// <summary>
/// The keys <see cref="AppSettings.WindowSizes"/> uses, and the bounds every stored size is clamped to.
///
/// One key per resizable window, so a size survives a restart without any window needing its own
/// settings file. Adding a resizable window means adding a key here — nothing else.
/// </summary>
public static class WindowSizeKeys
{
    /// <summary>Settings shell (the orange button).</summary>
    public const string Settings = "settings";

    /// <summary>The result bubble, once the user has resized it.</summary>
    public const string Result = "result";

    /// <summary>Full Text Edit Mode.</summary>
    public const string FullText = "fulltext";

    /// <summary>Nothing smaller than this is ever stored or restored (DIPs).</summary>
    public const double MinSide = 120;

    /// <summary>A size above this is a corrupt number, not a window (DIPs).</summary>
    public const double MaxSide = 20000;
}
