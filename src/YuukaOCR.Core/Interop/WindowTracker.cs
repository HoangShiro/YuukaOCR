using System.Runtime.InteropServices;
using System.Text;

namespace YuukaOCR.Core.Interop;

public enum HookEdge
{
    None,
    Top,
    Bottom
}

/// <summary>
/// Where the bar attaches relative to the target window's edge. <see cref="Outside"/> puts it past the
/// outer edge (below a window's bottom, above its top — the historical behaviour).
/// <see cref="Inside"/> puts it along the inner edge, so the bar lives inside the window it belongs to
/// and keeps its place when that window fills the monitor.
/// </summary>
public enum HookMode
{
    Outside,
    Inside
}

public sealed record HookCandidate(
    IntPtr Handle,
    HookEdge Edge,
    double TargetX,
    double TargetY,
    ScreenRect WindowBounds,
    string ProcessName);

/// <summary>
/// Finds the window the main bar should "stick" to. Everything the old version did inline inside the
/// main window now lives here, so it can be tested without a UI: which windows are eligible, which
/// edge is nearest, and exactly where the bar should end up.
/// </summary>
public static class WindowTracker
{
    public const string Outside = "outside";
    public const string Inside = "inside";

    private const uint GA_ROOT = 2;

    private static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "shellexperiencehost", "searchhost", "searchapp",
        "startmenuexperiencehost", "applicationframehost", "textinputhost",
        "lockapp", "dwm", "yuukaocr"
    };

    public static bool IsAlive(IntPtr handle) => handle != IntPtr.Zero && NativeMethods.IsWindow(handle);

    /// <summary>Stored value → mode. Anything unknown falls back to <see cref="HookMode.Outside"/>.</summary>
    public static HookMode ParseMode(string? value) =>
        string.Equals(value?.Trim(), Inside, StringComparison.OrdinalIgnoreCase) ? HookMode.Inside : HookMode.Outside;

    /// <summary>Mode → the string stored in settings.json.</summary>
    public static string SerializeMode(HookMode mode) => mode == HookMode.Inside ? Inside : Outside;

    /// <summary>Repairs a hand-edited `hook_mode` value.</summary>
    public static string NormalizeMode(string? value) => SerializeMode(ParseMode(value));

    /// <summary>Visible bounds, preferring the DWM frame (GetWindowRect includes the invisible resize border).</summary>
    public static ScreenRect? GetBounds(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return null;

        if (NativeMethods.DwmGetWindowAttribute(
                handle, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out var frame, Marshal.SizeOf<NativeMethods.RECT>()) == 0 &&
            frame.Right > frame.Left && frame.Bottom > frame.Top)
        {
            return new ScreenRect(frame.Left, frame.Top, frame.Right, frame.Bottom);
        }

        if (!NativeMethods.GetWindowRect(handle, out var rect)) return null;
        if (rect.Right <= rect.Left || rect.Bottom <= rect.Top) return null;
        return new ScreenRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    /// <summary>True when the window is actually on top at its own centre (not covered by another window).</summary>
    public static bool IsTopMostAtCenter(IntPtr handle)
    {
        var bounds = GetBounds(handle);
        if (bounds is null) return false;

        var centerX = bounds.Value.Left + bounds.Value.Width / 2;
        var centerY = bounds.Value.Top + bounds.Value.Height / 2;
        var hit = NativeMethods.WindowFromPoint(new NativeMethods.POINT(centerX, centerY));
        if (hit == IntPtr.Zero) return false;

        var root = NativeMethods.GetAncestor(hit, GA_ROOT);
        return root == handle || hit == handle;
    }

    public static string ProcessNameOf(IntPtr handle)
    {
        NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0) return "";

        var process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero) return "";

        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            if (!NativeMethods.QueryFullProcessImageNameW(process, 0, buffer, ref size)) return "";
            return Path.GetFileNameWithoutExtension(buffer.ToString());
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    public static bool IsIgnoredProcess(IntPtr handle) => IgnoredProcesses.Contains(ProcessNameOf(handle));

    /// <summary>
    /// Where the bar goes for a given window and edge. The result is always inside the monitor's work
    /// area: a fullscreen or maximised window has no room past its outer edge, so an outside target
    /// that would land off-screen flips to the inner edge instead of being pushed under the taskbar.
    /// </summary>
    public static (double X, double Y) TargetFor(
        ScreenRect window,
        double barWidth,
        double barHeight,
        HookEdge edge,
        HookMode mode,
        int offsetTop,
        int offsetBottom)
    {
        var area = ScreenGeometry.WorkAreaAt(window.Left + window.Width / 2, window.Top + window.Height / 2);

        var inside = mode == HookMode.Inside;
        double y;

        if (edge == HookEdge.Top)
        {
            // The bar sits below the window's bottom edge; "inside" means along the bottom inner edge.
            y = inside ? window.Bottom - barHeight - offsetTop : window.Bottom + offsetTop;

            if (y + barHeight > area.Bottom) y = window.Bottom - barHeight - offsetTop;
        }
        else
        {
            // The bar sits above the window's top edge; "inside" means along the top inner edge.
            y = inside ? window.Top + offsetBottom : window.Top - barHeight - offsetBottom;

            if (y < area.Top) y = window.Top + offsetBottom;
        }

        var x = window.Left + (window.Width - barWidth) / 2.0;

        x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - barWidth));
        y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - barHeight));

        return (x, y);
    }

    /// <summary>
    /// Finds the best window to hook given the bar's current rectangle.
    /// A window qualifies when it overlaps the bar horizontally and its bottom edge (top hook) or
    /// top edge (bottom hook) is within the configured proximity.
    /// </summary>
    public static HookCandidate? Find(
        double barLeft,
        double barTop,
        double barWidth,
        double barHeight,
        int proximityTop,
        int proximityBottom,
        int offsetTop,
        int offsetBottom,
        IntPtr selfHandle,
        HookMode mode = HookMode.Outside)
    {
        var barRight = barLeft + barWidth;
        var barBottom = barTop + barHeight;
        var virtualScreen = ScreenGeometry.VirtualScreen();

        HookCandidate? best = null;
        var bestDistance = double.MaxValue;

        NativeMethods.EnumWindows((handle, _) =>
        {
            if (handle == selfHandle || !NativeMethods.IsWindowVisible(handle)) return true;
            if (NativeMethods.GetAncestor(handle, GA_ROOT) != handle) return true;

            var titleLength = NativeMethods.GetWindowTextLengthW(handle);
            if (titleLength == 0) return true;

            var bounds = GetBounds(handle);
            if (bounds is null) return true;

            var window = bounds.Value;
            if (window.Width < 120 || window.Height < 80) return true;
            if (window.Left >= virtualScreen.Right || window.Right <= virtualScreen.Left) return true;

            // Horizontal overlap with the bar.
            if (window.Right <= barLeft || window.Left >= barRight) return true;

            if (IsIgnoredProcess(handle)) return true;
            if (!IsTopMostAtCenter(handle)) return true;

            var distanceTop = Math.Abs(window.Bottom - barTop);
            var distanceBottom = Math.Abs(window.Top - barBottom);

            HookEdge edge;
            double distance;
            if (distanceTop <= proximityTop && distanceTop <= distanceBottom)
            {
                edge = HookEdge.Top;
                distance = distanceTop;
            }
            else if (distanceBottom <= proximityBottom)
            {
                edge = HookEdge.Bottom;
                distance = distanceBottom;
            }
            else
            {
                return true;
            }

            if (distance >= bestDistance) return true;

            var (targetX, targetY) = TargetFor(window, barWidth, barHeight, edge, mode, offsetTop, offsetBottom);

            bestDistance = distance;
            best = new HookCandidate(handle, edge, targetX, targetY, window, ProcessNameOf(handle));
            return true;
        }, IntPtr.Zero);

        return best;
    }
}
