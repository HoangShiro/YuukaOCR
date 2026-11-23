namespace YuukaOCR.Core.Ui;

/// <summary>Which side of a window a resize drag is pulling.</summary>
public enum ResizeEdge
{
    Left,
    Right,
    Top,
    Bottom,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

/// <summary>A window rectangle in DIPs, the same units as <c>Window.Left/Top/Width/Height</c>.</summary>
public readonly record struct WindowRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

/// <summary>
/// The geometry of dragging a window's edge or corner, kept free of WPF so it can be tested without a
/// window: <see cref="Drag"/> turns "the pointer moved this far" into the rectangle the window should
/// take, and <see cref="Fit"/> makes a remembered size usable on the monitors that exist right now.
///
/// A window that is resized from its left or top side has to move as well as grow, and the side the
/// user is not dragging must stay exactly where it was — that is the whole point of the anchored math
/// below, and the reason it is not four copies of "add the delta".
/// </summary>
public static class ResizeMath
{
    /// <summary>
    /// Applies a pointer delta to <paramref name="start"/>. The dragged side follows the pointer, the
    /// opposite side stays put, and neither dimension shrinks below its minimum — a drag past the
    /// minimum keeps the window at the minimum instead of flipping it inside out.
    /// </summary>
    public static WindowRect Drag(ResizeEdge edge, WindowRect start, double dx, double dy,
        double minWidth, double minHeight)
    {
        var width = start.Width;
        var height = start.Height;
        var left = start.Left;
        var top = start.Top;

        var pullsLeft = edge is ResizeEdge.Left or ResizeEdge.TopLeft or ResizeEdge.BottomLeft;
        var pullsRight = edge is ResizeEdge.Right or ResizeEdge.TopRight or ResizeEdge.BottomRight;
        var pullsTop = edge is ResizeEdge.Top or ResizeEdge.TopLeft or ResizeEdge.TopRight;
        var pullsBottom = edge is ResizeEdge.Bottom or ResizeEdge.BottomLeft or ResizeEdge.BottomRight;

        if (pullsRight) width = Math.Max(minWidth, start.Width + dx);

        if (pullsLeft)
        {
            width = Math.Max(minWidth, start.Width - dx);
            left = start.Right - width;          // the right edge does not move
        }

        if (pullsBottom) height = Math.Max(minHeight, start.Height + dy);

        if (pullsTop)
        {
            height = Math.Max(minHeight, start.Height - dy);
            top = start.Bottom - height;         // the bottom edge does not move
        }

        return new WindowRect(left, top, width, height);
    }

    /// <summary>
    /// Fits a remembered size into the work area it would be shown in. A size from a monitor that is
    /// gone (or from a 4K screen now driving a laptop panel) would otherwise put half the window
    /// off-screen, where it cannot be dragged back.
    /// </summary>
    public static WindowRect Fit(WindowRect rect, double areaLeft, double areaTop, double areaWidth, double areaHeight,
        double minWidth, double minHeight)
    {
        var width = Math.Clamp(rect.Width, minWidth, Math.Max(minWidth, areaWidth));
        var height = Math.Clamp(rect.Height, minHeight, Math.Max(minHeight, areaHeight));
        var left = Math.Clamp(rect.Left, areaLeft, Math.Max(areaLeft, areaLeft + areaWidth - width));
        var top = Math.Clamp(rect.Top, areaTop, Math.Max(areaTop, areaTop + areaHeight - height));

        return new WindowRect(left, top, width, height);
    }

    /// <summary>True when <paramref name="edge"/> touches the left side of the window.</summary>
    public static bool TouchesLeft(ResizeEdge edge) =>
        edge is ResizeEdge.Left or ResizeEdge.TopLeft or ResizeEdge.BottomLeft;

    /// <summary>True when <paramref name="edge"/> touches the top side of the window.</summary>
    public static bool TouchesTop(ResizeEdge edge) =>
        edge is ResizeEdge.Top or ResizeEdge.TopLeft or ResizeEdge.TopRight;
}
