using System.Runtime.InteropServices;

using System.Security.Cryptography;
using System.Text;

namespace YuukaOCR.Core.Interop;

public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public override string ToString() => $"{Left},{Top} {Width}x{Height}";
}

public readonly record struct MonitorInfo(string DeviceName, ScreenRect Bounds, ScreenRect WorkArea, uint Dpi);

/// <summary>
/// Monitor geometry and DPI. Everything here is in physical pixels; the UI layer converts to DIPs
/// (WPF units) when it places windows.
/// </summary>
public static class ScreenGeometry
{
    public static IReadOnlyList<MonitorInfo> Monitors()
    {
        var monitors = new List<MonitorInfo>();

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
        {
            var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (!NativeMethods.GetMonitorInfoW(handle, ref info)) return true;

            uint dpi = 96;
            if (NativeMethods.GetDpiForMonitor(handle, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0)
                dpi = dpiX == 0 ? 96 : dpiX;

            monitors.Add(new MonitorInfo(
                info.rcMonitor.Left + "x" + info.rcMonitor.Top,
                new ScreenRect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom),
                new ScreenRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom),
                dpi));
            return true;
        }, IntPtr.Zero);

        if (monitors.Count == 0)
        {
            var width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
            var height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
            if (width <= 0) width = 1920;
            if (height <= 0) height = 1080;
            monitors.Add(new MonitorInfo("primary", new ScreenRect(0, 0, width, height), new ScreenRect(0, 0, width, height), 96));
        }

        return monitors;
    }

    private static ScreenRect? _virtualCache;
    private static long _virtualCacheTick;

    /// <summary>
    /// Union of all monitors (physical pixels), including negative origins.
    /// Cached for ~1s: the physics loop calls this 60 times a second, and each call is a round of
    /// EnumDisplayMonitors + GetDpiForMonitor.
    /// </summary>
    public static ScreenRect VirtualScreen()
    {
        var now = Environment.TickCount64;
        if (_virtualCache is { } cached && now - _virtualCacheTick < 1000) return cached;

        var monitors = Monitors();
        var rect = new ScreenRect(
            monitors.Min(m => m.Bounds.Left),
            monitors.Min(m => m.Bounds.Top),
            monitors.Max(m => m.Bounds.Right),
            monitors.Max(m => m.Bounds.Bottom));

        _virtualCache = rect;
        _virtualCacheTick = now;
        return rect;
    }

    /// <summary>Working area (excludes taskbar) of the monitor containing the point.</summary>
    public static ScreenRect WorkAreaAt(int x, int y)
    {
        var handle = NativeMethods.MonitorFromPoint(new NativeMethods.POINT(x, y), NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (NativeMethods.GetMonitorInfoW(handle, ref info))
            return new ScreenRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);

        return VirtualScreen();
    }

    public static double DpiScaleAt(int x, int y)
    {
        var handle = NativeMethods.MonitorFromPoint(new NativeMethods.POINT(x, y), NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (NativeMethods.GetDpiForMonitor(handle, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
            return dpiX / 96.0;
        return 1.0;
    }

    public static double DpiScaleForWindow(IntPtr hwnd)
    {
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    /// <summary>
    /// Stable id for the current monitor layout (sha256 of sorted geometry), so window positions
    /// are remembered per configuration — same idea as the old app, without Python's hash()
    /// randomization.
    /// </summary>
    public static string DisplayConfigHash()
    {
        var description = string.Join(";", Monitors()
            .OrderBy(m => m.Bounds.Left)
            .ThenBy(m => m.Bounds.Top)
            .Select(m => $"{m.Bounds.Left},{m.Bounds.Top},{m.Bounds.Width},{m.Bounds.Height}@{m.Dpi}"));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(description));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
