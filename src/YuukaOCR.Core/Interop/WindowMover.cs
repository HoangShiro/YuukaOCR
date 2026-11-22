namespace YuukaOCR.Core.Interop;

/// <summary>
/// Positions windows in *physical* pixels. WPF's Left/Top are DIPs against the primary monitor's
/// scale, which silently breaks on mixed-DPI setups — the old app had the same class of bug with
/// Qt's devicePixelRatio.
/// </summary>
public static class WindowMover
{
    /// <summary>
    /// Move the window WITHOUT changing z-order. Using HWND_TOPMOST here would push the window to the
    /// front of the topmost group on every pixel the spring moves — enough to cover a ContextMenu or
    /// popup that just opened. The window already has WS_EX_TOPMOST from creation, so it stays on top.
    /// </summary>
    public static void MoveTo(IntPtr hwnd, double x, double y)
    {
        if (hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(x), (int)Math.Round(y),
            0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER);
    }

    public static void SetBounds(IntPtr hwnd, double x, double y, double width, double height)
    {
        if (hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(x), (int)Math.Round(y),
            (int)Math.Round(width), (int)Math.Round(height), NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER);
    }

    public static (int X, int Y) CursorPosition()
        => NativeMethods.GetCursorPos(out var point) ? (point.X, point.Y) : (0, 0);

    /// <summary>Keeps the window out of Alt+Tab and the taskbar without touching WPF chrome.</summary>
    public static void HideFromAltTab(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var style = NativeMethods.GetWindowLongW(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongW(hwnd, NativeMethods.GWL_EXSTYLE, style | NativeMethods.WS_EX_TOOLWINDOW);
    }
}
