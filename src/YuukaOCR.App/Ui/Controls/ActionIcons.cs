using System.Windows;
using System.Windows.Media;

namespace YuukaOCR.App.Ui.Controls;

/// <summary>
/// The action bar's icons, as path geometry in a 16×16 box drawn with a 1.4 px stroke.
///
/// Core stores only an icon *key* (<c>AgentAction.Icon</c>) because it must not know about WPF; this
/// table is where a key becomes something drawable. Unknown keys fall back to <see cref="Fallback"/>,
/// so a hand-edited settings file can never produce a button with no glyph.
/// </summary>
public static class ActionIcons
{
    public const string Fallback = "chat";

    private static readonly Dictionary<string, string> Paths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chat"] = "M2,3.5 A1.5,1.5 0 0 1 3.5,2 H12.5 A1.5,1.5 0 0 1 14,3.5 V9.5 A1.5,1.5 0 0 1 12.5,11 H7 L4,14 V11 H3.5 A1.5,1.5 0 0 1 2,9.5 Z",
        ["translate"] = "M2,3 H8 M5,3 V4.5 M2.5,5.5 C3,8.5 5.5,10 8,10.5 M8,5.5 C7.5,8 5,10 2,10.5 M9.5,9 H14.5 M12,9 V10.2 M10,13.5 L12,9 L14,13.5",
        ["summarize"] = "M2,3 H14 M2,6.5 H14 M2,10 H9 M2,13.5 H6",
        ["explain"] = "M8,2.2 A5.8,5.8 0 1 0 8,13.8 A5.8,5.8 0 1 0 8,2.2 Z M6.1,6.3 A2,2 0 0 1 10,6.6 C10,8.2 8,8.1 8,9.6 M8,11.4 V11.6",
        ["check"] = "M2.5,8.5 L6,12 L13.5,4",
        ["list"] = "M2,4 H3 M5.5,4 H14 M2,8 H3 M5.5,8 H14 M2,12 H3 M5.5,12 H14",
        ["code"] = "M5.5,4 L2,8 L5.5,12 M10.5,4 L14,8 L10.5,12",
        ["search"] = "M7,2.2 A4.8,4.8 0 1 0 7,11.8 A4.8,4.8 0 1 0 7,2.2 Z M10.6,10.6 L14.5,14.5",
        ["star"] = "M8,2 L9.8,6.2 L14.5,6.6 L11,9.6 L12.1,14.2 L8,11.7 L3.9,14.2 L5,9.6 L1.5,6.6 L6.2,6.2 Z",
        ["bulb"] = "M8,2 A4,4 0 0 1 10.5,9.4 V10.6 H5.5 V9.4 A4,4 0 0 1 8,2 Z M6,12.4 H10 M7,14 H9",
        ["wand"] = "M3,13 L10,6 M11,2 V4.5 M13.6,3.2 L12.2,4.6 M14,7.2 H11.4 M10.2,2.4 L11.6,3.8",
        ["quill"] = "M2,14 C4,8 8,4 14,2 C13,8 9,12 5,13 Z M4,12 L9,7",

        // Window chrome — not action icons, but they live here so every glyph in the app comes from
        // one table instead of being pasted into XAML.
        ["copy"] = "M5.5,5.5 H13 V13.5 H5.5 Z M3,10.5 H2.5 V2.5 H10.5 V3",
        ["send"] = "M2,8 L14,2 L10,14 L8,9.2 Z",
        ["expand"] = "M6,2 H2 V6 M10,14 H14 V10 M2,2 L7,7 M14,14 L9,9",
        ["collapse"] = "M7,7 L2,2 M2,6 V2 H6 M9,9 L14,14 M14,10 V14 H10",
        ["remove"] = "M3,4.5 H13 M6.5,4.5 V2.8 H9.5 V4.5 M4.5,4.5 L5.2,13.5 H10.8 L11.5,4.5",
        ["plus"] = "M8,3 V13 M3,8 H13"
    };

    /// <summary>Path data for a key, or the fallback glyph's data.</summary>
    public static string Data(string? key) =>
        key is not null && Paths.TryGetValue(key, out var data) ? data : Paths[Fallback];

    /// <summary>True when the key has its own glyph (the settings tab greys out anything else).</summary>
    public static bool Has(string? key) => key is not null && Paths.ContainsKey(key);

    /// <summary>Every action icon key, in the order the picker shows them.</summary>
    public static IReadOnlyList<string> Keys { get; } = new[]
    {
        "chat", "translate", "summarize", "explain", "check", "list",
        "code", "search", "star", "bulb", "wand", "quill"
    };

    /// <summary>Builds a stroked glyph for a button, sized to <paramref name="size"/>.</summary>
    public static UIElement Glyph(string? key, double size, Brush stroke, double thickness = 1.4) =>
        Build(key, size, thickness, path => path.Stroke = stroke);

    /// <summary>
    /// Builds a glyph whose colour is a *theme resource key* rather than a brush instance.
    ///
    /// <see cref="Theme.Apply"/> replaces the brush objects in a window's resources on every theme switch;
    /// a Path holding a captured Brush therefore keeps the old colour until it is rebuilt, which is why
    /// icons used to stay dark-theme coloured after switching to light. A resource reference re-resolves
    /// itself, so the icon follows the theme immediately.
    /// </summary>
    public static UIElement Glyph(string? key, double size, string strokeResourceKey, double thickness = 1.4) =>
        Build(key, size, thickness, path =>
            path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, strokeResourceKey));

    private static UIElement Build(string? key, double size, double thickness,
        Action<System.Windows.Shapes.Path> configure)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(Data(key)),
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            SnapsToDevicePixels = true
        };

        configure(path);

        // A Viewbox keeps every glyph the same size even though the path data are hand-written.
        return new System.Windows.Controls.Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Child = path
        };
    }
}
