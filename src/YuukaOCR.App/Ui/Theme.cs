using System.Windows;
using System.Windows.Media;
using YuukaOCR.Core;
using YuukaOCR.Core.Settings;

namespace YuukaOCR.App.Ui;

/// <summary>
/// Applies the active theme to a window's resources. Every window calls this in its constructor and
/// again when settings change, so colours/fonts update live.
///
/// One palette feeds all of them — the bar, the result bubble *and* the settings window — which is why
/// switching a preset repaints the whole app instead of only the pill.
/// </summary>
public static class Theme
{
    public static SolidColorBrush Brush(Rgba color)
    {
        var brush = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    public static void Apply(FrameworkElement element, ThemeSettings theme)
    {
        var palette = theme.Palette;

        // The background opacity applies to every surface of a window, so a translucent window stays
        // uniformly translucent instead of showing opaque panels on a see-through frame.
        var alpha = theme.BackgroundOpacity;

        element.Resources["YuukaBg"] = Brush(ThemeSettings.WithAlpha(palette.Background, alpha));
        element.Resources["YuukaPanel"] = Brush(ThemeSettings.WithAlpha(palette.Panel, alpha));
        element.Resources["YuukaPanelHover"] = Brush(ThemeSettings.WithAlpha(palette.PanelHover, alpha));
        element.Resources["YuukaBorder"] = Brush(ThemeSettings.WithAlpha(palette.Border, alpha));
        element.Resources["YuukaText"] = Brush(palette.Text);
        element.Resources["YuukaSubtle"] = Brush(palette.Subtle);
        element.Resources["YuukaAccent"] = Brush(palette.Accent);
        element.Resources["YuukaOnAccent"] = Brush(palette.OnAccent);
        element.Resources["YuukaDanger"] = Brush(palette.Danger);
        element.Resources["YuukaBarBg"] = Brush(theme.Bar);   // frame opacity lives in ThemeSettings.Bar
        element.Resources["YuukaBarText"] = Brush(palette.BarText);
        element.Resources["YuukaFontFamily"] = new FontFamily(theme.FontFamily);
        element.Resources["YuukaFontSize"] = theme.FontSize;
    }
}
