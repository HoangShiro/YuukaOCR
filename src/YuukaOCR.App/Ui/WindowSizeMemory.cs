using System.Windows;
using YuukaOCR.App.Services;
using YuukaOCR.Core.Settings;

namespace YuukaOCR.App.Ui;

/// <summary>
/// Remembers window sizes between sessions: one line of plumbing per window, and the size the user
/// dragged out is the size it opens with next time.
///
/// Sizes live in the settings file under <see cref="WindowSizeKeys"/>, so no window needs a file of its
/// own, and a size that no longer fits the monitors is repaired instead of opening off-screen.
/// </summary>
public static class WindowSizeMemory
{
    /// <summary>
    /// Applies the remembered size to <paramref name="window"/>. Returns false when the user never
    /// resized it, so the caller keeps whatever it would do by default (content-sized, centred, …).
    /// </summary>
    public static bool Restore(Window window, AppServices services, string key)
    {
        var size = services.Settings.Settings.SizeOf(key);
        if (size is null) return false;

        // Only the size is restored, not the position: every window here places itself (centred, or
        // beside the bar), and a remembered position would fight that.
        var area = SystemParameters.WorkArea;
        var width = Math.Clamp(size.Width, Math.Max(window.MinWidth, WindowSizeKeys.MinSide),
            Math.Max(window.MinWidth, area.Width));
        var height = Math.Clamp(size.Height, Math.Max(window.MinHeight, WindowSizeKeys.MinSide),
            Math.Max(window.MinHeight, area.Height));

        window.Width = width;
        window.Height = height;
        return true;
    }

    /// <summary>Stores the size a drag ended on. Called once per finished drag, never per frame.</summary>
    public static void Remember(AppServices services, string key, double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;
        if (width < WindowSizeKeys.MinSide || height < WindowSizeKeys.MinSide) return;

        services.Settings.Update(s => s.WindowSizes[key] = new WindowSize(width, height).Clamped());
    }
}
