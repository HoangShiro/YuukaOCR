using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YuukaOCR.Core.Interop;

namespace YuukaOCR.App.Ui;

/// <summary>
/// Full-virtual-screen region selector. Returns the selection in physical pixels so the caller can
/// hand it straight to BitBlt.
/// </summary>
public partial class SnipOverlay : Window
{
    private ScreenRect _screen;
    private Point _start;
    private bool _selecting;

    public SnipOverlay()
    {
        InitializeComponent();
        _screen = ScreenGeometry.VirtualScreen();

        SourceInitialized += (_, _) =>
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            WindowMover.SetBounds(handle, _screen.Left, _screen.Top, _screen.Width, _screen.Height);
            WindowMover.HideFromAltTab(handle);
        };

        Loaded += (_, _) =>
        {
            Shade.Width = Surface.ActualWidth;
            Shade.Height = Surface.ActualHeight;
            HintLabel.SetValue(Canvas.LeftProperty, (Surface.ActualWidth - 260) / 2);
            HintLabel.SetValue(Canvas.TopProperty, 40.0);
            Activate();
            Focus();
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Cancel();
        };
    }

    public event Action<ScreenRect>? Selected;
    public event Action? Cancelled;

    private double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private Point ToPhysical(Point dip) => new(_screen.Left + dip.X * Scale, _screen.Top + dip.Y * Scale);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _start = e.GetPosition(Surface);
        _selecting = true;
        Selection.Visibility = Visibility.Visible;
        Selection.Width = Selection.Height = 0;
        Selection.SetValue(Canvas.LeftProperty, _start.X);
        Selection.SetValue(Canvas.TopProperty, _start.Y);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_selecting) return;

        var current = e.GetPosition(Surface);
        var x = Math.Min(_start.X, current.X);
        var y = Math.Min(_start.Y, current.Y);
        var width = Math.Abs(current.X - _start.X);
        var height = Math.Abs(current.Y - _start.Y);

        Selection.Width = width;
        Selection.Height = height;
        Selection.SetValue(Canvas.LeftProperty, x);
        Selection.SetValue(Canvas.TopProperty, y);

        var physicalWidth = (int)(width * Scale);
        var physicalHeight = (int)(height * Scale);
        SizeText.Text = $"{physicalWidth} × {physicalHeight}";
        SizeBadge.Visibility = Visibility.Visible;
        SizeBadge.SetValue(Canvas.LeftProperty, Math.Min(x, Surface.ActualWidth - 90));
        SizeBadge.SetValue(Canvas.TopProperty, Math.Max(0, y - 30));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_selecting) return;
        _selecting = false;

        var start = ToPhysical(_start);
        var end = ToPhysical(e.GetPosition(Surface));

        var left = (int)Math.Min(start.X, end.X);
        var top = (int)Math.Min(start.Y, end.Y);
        var width = (int)Math.Abs(end.X - start.X);
        var height = (int)Math.Abs(end.Y - start.Y);

        if (width < 6 || height < 6)
        {
            Cancel();
            return;
        }

        Selected?.Invoke(new ScreenRect(left, top, left + width, top + height));
    }

    private void Cancel()
    {
        Cancelled?.Invoke();
    }
}
