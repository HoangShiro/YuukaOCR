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

/// <summary>
/// A window the bar can stick to, plus everything needed to keep it stuck.
///
/// <see cref="AnchorX"/> is the bar's left edge measured from the target's left edge, captured at the
/// moment the hook was made. Every later tick recomputes the position from it, which is what makes the
/// bar travel *with* the window instead of staying at a fixed screen coordinate while the window slides
/// out from under it. It is also re-captured whenever the user drags the bar to a new place.
/// </summary>
public sealed record HookCandidate(
    IntPtr Handle,
    HookEdge Edge,
    double TargetX,
    double TargetY,
    ScreenRect WindowBounds,
    string ProcessName,
    double AnchorX);

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
    ///
    /// <paramref name="barLeft"/> is the bar's current left edge. When it is given, the horizontal
    /// position is kept exactly as the user left it — hooking must not slide the bar to the middle of
    /// the target's edge. Only the axis the hook owns (vertical) is computed here; null falls back to
    /// centring, which is what a caller without a live window (a test, a probe) wants.
    /// </summary>
    public static (double X, double Y) TargetFor(
        ScreenRect window,
        double barWidth,
        double barHeight,
        HookEdge edge,
        HookMode mode,
        int offsetTop,
        int offsetBottom,
        double? barLeft = null)
    {
        var x = barLeft ?? window.Left + (window.Width - barWidth) / 2.0;
        return Compute(window, barWidth, barHeight, edge, mode, offsetTop, offsetBottom, x);
    }

    /// <summary>
    /// Where the bar belongs on every tick *while it is hooked*.
    ///
    /// <paramref name="anchorX"/> is the bar's left edge relative to the target's left edge (see
    /// <see cref="AnchorFor"/>), not an absolute screen coordinate: the bar then keeps its place inside
    /// the window and follows it when the window is moved — including horizontally, which is where a
    /// fixed absolute X used to leave the bar behind.
    /// </summary>
    public static (double X, double Y) Follow(
        ScreenRect window,
        double barWidth,
        double barHeight,
        HookEdge edge,
        HookMode mode,
        int offsetTop,
        int offsetBottom,
        double anchorX) =>
        Compute(window, barWidth, barHeight, edge, mode, offsetTop, offsetBottom, window.Left + anchorX);

    /// <summary>The bar's left edge measured from the target's left edge — the value to remember.</summary>
    public static double AnchorFor(ScreenRect window, double barLeft) => barLeft - window.Left;

    private static (double X, double Y) Compute(
        ScreenRect window,
        double barWidth,
        double barHeight,
        HookEdge edge,
        HookMode mode,
        int offsetTop,
        int offsetBottom,
        double x)
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
        HookMode mode = HookMode.Outside,
        bool keepHorizontalPosition = false)
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

            // Where the bar sits inside the window, remembered for every tick after this one. Keeping
            // the user's horizontal position means the anchor is taken from the live bar; without one
            // (a caller that has no bar yet) the anchor centres the bar on the edge.
            var anchorX = keepHorizontalPosition ? AnchorFor(window, barLeft) : (window.Width - barWidth) / 2.0;
            var (targetX, targetY) = Follow(window, barWidth, barHeight, edge, mode, offsetTop, offsetBottom, anchorX);

            bestDistance = distance;
            best = new HookCandidate(handle, edge, targetX, targetY, window, ProcessNameOf(handle), anchorX);
            return true;
        }, IntPtr.Zero);

        return best;
    }
}
