using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YuukaOCR.Core.Ui;

namespace YuukaOCR.App.Ui;

/// <summary>
/// Resize handles for a borderless window: four corners and four edges, along the *visible* card edge.
///
/// Every window in this app is <c>WindowStyle=None</c> plus <c>AllowsTransparency=True</c>, and such a
/// window gets no working resize border from the OS — the thick frame, if any, sits in the transparent
/// shadow padding, so the user would have to grab thin air a couple of centimetres outside the card.
/// The handles are laid out inside the padding instead, on the card's own border, and drive the window
/// rectangle themselves through <see cref="ResizeMath"/>: same feel as a standard desktop window
/// (the dragged side follows the pointer, the opposite side stays put), without depending on the
/// non-client area.
/// </summary>
public sealed class ResizeGrips
{
    /// <summary>Thickness of an edge handle (DIP). Wide enough to hit, narrow enough to ignore.</summary>
    private const double Band = 6;

    /// <summary>Side of a corner handle (DIP). Larger than the band so corners are easy to catch.</summary>
    private const double Corner = 16;

    private readonly Window _window;
    private readonly Panel _overlay;
    private readonly Func<WindowRect?>? _begin;
    private readonly Action? _resized;

    private ResizeEdge _edge;
    private WindowRect _start;
    private Point _origin;
    private FrameworkElement? _captured;

    private ResizeGrips(Window window, Panel overlay, Func<WindowRect?>? begin, Action? resized)
    {
        _window = window;
        _overlay = overlay;
        _begin = begin;
        _resized = resized;
    }

    /// <summary>
    /// Adds the eight handles. <paramref name="chrome"/> is the visible card (it carries the shadow
    /// padding as its margin), <paramref name="overlay"/> is a panel covering the whole window.
    ///
    /// <paramref name="begin"/> may refuse a drag (returns null) and is where a window that sizes itself
    /// to its content switches to a fixed size; <paramref name="resized"/> runs once per finished drag,
    /// which is the moment a size is worth remembering.
    /// </summary>
    public static ResizeGrips Attach(Window window, Panel overlay, FrameworkElement chrome,
        Func<WindowRect?>? begin = null, Action? resized = null)
    {
        var grips = new ResizeGrips(window, overlay, begin, resized);
        grips.Build(chrome);
        return grips;
    }

    private void Build(FrameworkElement chrome)
    {
        var pad = chrome.Margin;

        Add(ResizeEdge.TopLeft, Cursors.SizeNWSE, Corner, Corner, HorizontalAlignment.Left, VerticalAlignment.Top,
            new Thickness(pad.Left, pad.Top, 0, 0));
        Add(ResizeEdge.TopRight, Cursors.SizeNESW, Corner, Corner, HorizontalAlignment.Right, VerticalAlignment.Top,
            new Thickness(0, pad.Top, pad.Right, 0));
        Add(ResizeEdge.BottomLeft, Cursors.SizeNESW, Corner, Corner, HorizontalAlignment.Left, VerticalAlignment.Bottom,
            new Thickness(pad.Left, 0, 0, pad.Bottom));
        Add(ResizeEdge.BottomRight, Cursors.SizeNWSE, Corner, Corner, HorizontalAlignment.Right, VerticalAlignment.Bottom,
            new Thickness(0, 0, pad.Right, pad.Bottom));

        Add(ResizeEdge.Left, Cursors.SizeWE, Band, double.NaN, HorizontalAlignment.Left, VerticalAlignment.Stretch,
            new Thickness(pad.Left, pad.Top + Corner, 0, pad.Bottom + Corner));
        Add(ResizeEdge.Right, Cursors.SizeWE, Band, double.NaN, HorizontalAlignment.Right, VerticalAlignment.Stretch,
            new Thickness(0, pad.Top + Corner, pad.Right, pad.Bottom + Corner));
        Add(ResizeEdge.Top, Cursors.SizeNS, double.NaN, Band, HorizontalAlignment.Stretch, VerticalAlignment.Top,
            new Thickness(pad.Left + Corner, pad.Top, pad.Right + Corner, 0));
        Add(ResizeEdge.Bottom, Cursors.SizeNS, double.NaN, Band, HorizontalAlignment.Stretch, VerticalAlignment.Bottom,
            new Thickness(pad.Left + Corner, 0, pad.Right + Corner, pad.Bottom));
    }

    private void Add(ResizeEdge edge, Cursor cursor, double width, double height,
        HorizontalAlignment horizontal, VerticalAlignment vertical, Thickness margin)
    {
        var grip = new Border
        {
            // Transparent rather than empty: a null background is not hit-testable, so the handle would
            // be invisible *and* unclickable.
            Background = System.Windows.Media.Brushes.Transparent,
            Width = width,
            Height = height,
            HorizontalAlignment = horizontal,
            VerticalAlignment = vertical,
            Margin = margin,
            Cursor = cursor,
            Tag = edge
        };

        grip.MouseLeftButtonDown += OnDown;
        grip.MouseMove += OnMove;
        grip.MouseLeftButtonUp += OnUp;
        grip.LostMouseCapture += OnLostCapture;

        _overlay.Children.Add(grip);
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not FrameworkElement grip) return;
        if (grip.Tag is not ResizeEdge edge) return;

        // Claim the event: the window's own "drag me by the padding" handler must not also start.
        e.Handled = true;

        var start = _begin is null ? CurrentRect() : _begin();
        if (start is null) return;

        _edge = edge;
        _start = start.Value;
        _origin = CursorInDips(e);
        _captured = grip;
        grip.CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_captured is null || e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }

        var delta = CursorInDips(e) - _origin;
        var rect = ResizeMath.Drag(_edge, _start, delta.X, delta.Y,
            Math.Max(_window.MinWidth, 1), Math.Max(_window.MinHeight, 1));

        // Left/Top before Width/Height: growing a window that is anchored on its right edge has to move
        // first, or the intermediate frame jumps and the content flickers.
        _window.Left = rect.Left;
        _window.Top = rect.Top;
        _window.Width = rect.Width;
        _window.Height = rect.Height;
    }

    private void OnUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void OnLostCapture(object sender, MouseEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (_captured is null) return;

        _captured.ReleaseMouseCapture();
        _captured = null;
        _resized?.Invoke();
    }

    private WindowRect CurrentRect() => new(
        _window.Left,
        _window.Top,
        double.IsNaN(_window.Width) ? _window.ActualWidth : _window.Width,
        double.IsNaN(_window.Height) ? _window.ActualHeight : _window.Height);

    /// <summary>
    /// The pointer in the same DIP space as <see cref="Window.Left"/>. Window-relative position plus the
    /// window's own screen origin, converted through the presentation source, so a window that is
    /// *moving* while it is resized (a left-edge drag) still reports a stable pointer.
    /// </summary>
    private Point CursorInDips(MouseEventArgs e)
    {
        var point = _window.PointToScreen(e.GetPosition(_window));
        var source = PresentationSource.FromVisual(_window);
        return source?.CompositionTarget is null ? point : source.CompositionTarget.TransformFromDevice.Transform(point);
    }
}
