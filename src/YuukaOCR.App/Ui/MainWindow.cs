using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YuukaOCR.App.Services;
using YuukaOCR.Core;
using YuukaOCR.Core.Input;
using YuukaOCR.Core.Interop;
using YuukaOCR.Core.Localization;
using YuukaOCR.Core.Ocr;
using YuukaOCR.Core.Physics;
using YuukaOCR.Core.Settings;

namespace YuukaOCR.App.Ui;

/// <summary>
/// The floating bar. Owns window physics, edge hooking, the hotkey entry point and clipboard/drop
/// intake. Anything that touches the network is handed to <see cref="OcrService"/> — the UI thread
/// never waits on I/O.
/// </summary>
public partial class MainWindow : Window, IThemedWindow
{
    /// <summary>
    /// Transparent border around the pill (DIP). DropShadowEffect paints outside the element's bounds
    /// and the window clips everything outside its rect — without this padding the shadow is cut at all four corners.
    /// </summary>
    private const double ShadowPad = 20;

    private readonly AppServices _services;
    private readonly SpringMotion _spring = new();
    /// <summary>
    /// Polls the hook. 20 Hz, not 7: while the target window is being dragged the bar has to keep up
    /// with it, and the spring on top of the poll supplies the smoothing — a slow poll shows up as the
    /// bar lagging behind the window instead of trailing it.
    /// </summary>
    private readonly DispatcherTimer _hookTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };

    // Physics runs on its own timer rather than CompositionTarget.Rendering: WPF only raises
    // Rendering when a new frame is produced, and moving the window with SetWindowPos does not dirty
    // the visual tree — so the spring would never step. A 16 ms timer is ~60 FPS like the Python build (QTimer).
    // WPF/Windows rounds every timer up to the 15.6 ms tick, so the interval must be *below* 15.6 ms:
    // 16 ms yields a real tick of ~25 ms (~40 FPS), 10 ms gives ~64 FPS.
    // DispatcherPriority.Render: the default is Background, which MouseMove preempts, dropping fps.
    private readonly DispatcherTimer _physicsTimer =
        new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(10) };
    private readonly DispatcherTimer _statusTimer = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private double _lastTick;
    private IntPtr _handle;
    private HookCandidate? _hook;
    private ConfigWindow? _config;
    private ResultWindow? _result;
    private SnipOverlay? _snip;
    private bool _dragging;
    private int _lastMovedX = int.MinValue;
    private int _lastMovedY = int.MinValue;
    private bool _positioned;
    private int _dragCursorX;
    private int _dragCursorY;
    private double _dragStartX;
    private double _dragStartY;
    private string _idleStatus = L.T("bar.idle");

    public MainWindow(AppServices services)
    {
        _services = services;

        InitializeComponent();
        ApplyConfig();

        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            WindowMover.HideFromAltTab(_handle);
        };

        Loaded += OnLoaded;
        Closing += (_, _) => SavePosition();

        // Two clocks: Rendering (vsync-locked, only raised when WPF actually draws a frame) plus a 10 ms
        // timer (a safety net while the window sits still). Both call OnRendering, which computes the real
        // delta itself, so the same interval is never stepped twice.
        CompositionTarget.Rendering += (_, _) => OnRendering();
        _physicsTimer.Tick += (_, _) => OnRendering();
        _physicsTimer.Start();
        _hookTimer.Tick += (_, _) => HookTick();
        _statusTimer.Tick += (_, _) => ResetStatus();
        BarBorder.LostMouseCapture += (_, _) => EndDrag();

        var ocr = _services.Ocr;
        ocr.ProcessingStarted += () => Dispatcher.BeginInvoke(() => SetBusy(true));
        ocr.ProcessingFinished += () => Dispatcher.BeginInvoke(() => SetBusy(false));
        ocr.StatusChanged += (message, duration) => Dispatcher.BeginInvoke(() => SetStatus(message, duration));
        ocr.ApiKeyMissing += OnApiKeyMissing;
        ocr.ResultReady += text => Dispatcher.BeginInvoke(() => ShowResult(text));

        L.Changed += (_, _) => Dispatcher.BeginInvoke(new Action(Relocalize));

        _services.Hotkey.Pressed += OnHotkeyPressed;
        _services.Clipboard.Changed += () => Dispatcher.BeginInvoke(new Action(HandleClipboard));
        // Appearance and layout are fanned out by AppearanceSync (registered at the end of the ctor),
        // so this window no longer needs its own Changed subscription for them.
        AppearanceSync.Register(this);
    }

    // ---------------------------------------------------------------- setup

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestorePosition();
        _positioned = true;

        ApplyHotkey();
        UpdateClipboardWatcher();
        _hookTimer.Start();
        SetStatus(_idleStatus, 3000);
    }

    private void ApplyConfig()
    {
        var settings = _services.Settings.Settings;
        var scale = Math.Clamp(settings.UiScale, 50, 200) / 100.0;

        var barWidth = 200 * scale;
        var barHeight = 50 * scale;

        // The window is 2*ShadowPad wider than the pill so the drop shadow has room to paint.
        Width = barWidth + ShadowPad * 2;
        Height = barHeight + ShadowPad * 2;
        BarBorder.Margin = new Thickness(ShadowPad);
        BarBorder.CornerRadius = new CornerRadius(barHeight / 2);

        var theme = settings.Theme;
        Theme.Apply(this, theme);
        Resources["YuukaBarBg"] = Theme.Brush(theme.Bar);

        // Both pills come from the preset too — a hard-coded close colour would survive a theme switch
        // and leave a stray green dot on a pink bar.
        var palette = theme.Palette;
        CloseButton.Background = Theme.Brush(palette.Close);
        ConfigButton.Background = Theme.Brush(palette.Accent);

        LoadBarImage();

        ConfigButton.Width = 34 * scale;
        ConfigButton.Height = 18 * scale;
        CloseButton.Width = 34 * scale;
        CloseButton.Height = 18 * scale;

        _spring.SpringConstant = Math.Clamp(settings.SpringConstant, 0.01, 0.5);
        _spring.DampingFactor = Math.Clamp(settings.DampingFactor, 0.0, 1.0);
        _spring.BounceDamping = Math.Clamp(settings.BounceDampingFactor, 0.0, 1.0);

        ApplyHotkey();
        UpdateClipboardWatcher();
    }

    /// <summary>
    /// Repaints the bar from the current settings. Called by <see cref="AppearanceSync"/> on every
    /// settings change — the same path the result bubble and the settings window use.
    /// </summary>
    public void ApplyAppearance() => ApplyConfig();

    private void LoadBarImage()
    {
        var custom = AppPaths.UserUiImage;
        var path = File.Exists(custom) ? custom : AppPaths.BundledUiImage;
        if (!File.Exists(path))
        {
            BarImage.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();

            BarImage.Source = bitmap;
            // The bundled art already has its own pill + buttons baked in, so it is only shown as a
            // backdrop when the user supplied their own picture.
            BarImage.Visibility = File.Exists(custom) ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _services.Log.ConsoleLog(L.F("bar.imageError", ex.Message));
            BarImage.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyHotkey()
    {
        var spec = HotkeySpec.Parse(_services.Settings.Settings.HookOcrHotkey);
        if (spec == _services.Hotkey.Current) return;
        _services.Hotkey.Set(spec);
    }

    private void UpdateClipboardWatcher()
    {
        var settings = _services.Settings.Settings;
        var wanted = settings.ProcessTextClipboard || settings.ProcessImageClipboard || settings.ProcessFileClipboard;
        if (wanted) _services.Clipboard.Start();
        else _services.Clipboard.Stop();
    }

    // ------------------------------------------------------------ positioning

    private void RestorePosition()
    {
        var key = ScreenGeometry.DisplayConfigHash();
        var settings = _services.Settings.Settings;

        if (settings.Positions.TryGetValue(key, out var saved))
        {
            _spring.SnapTo(saved.X, saved.Y);
        }
        else
        {
            var screen = ScreenGeometry.WorkAreaAt(0, 0);
            _spring.SnapTo(screen.Left + (screen.Width - Width) / 2.0, screen.Top + 60);
        }

        ClampToScreen();
        MoveTo(_spring.X, _spring.Y);
    }

    private void SavePosition()
    {
        if (!_positioned) return;
        var key = ScreenGeometry.DisplayConfigHash();
        _services.Settings.Update(s => s.Positions[key] = new WindowPosition
        {
            // Save the destination, not the instantaneous position (it may be mid-flight).
            X = (int)Math.Round(_spring.TargetX),
            Y = (int)Math.Round(_spring.TargetY)
        });
    }

    /// <summary>Shadow padding converted to physical pixels at a point on screen.</summary>
    private static int PadPixels(int x, int y)
        => (int)Math.Round(ShadowPad * ScreenGeometry.DpiScaleAt(x, y));

    /// <summary>The pill's real rect (shadow padding subtracted), in physical pixels.</summary>
    private ScreenRect? VisualBar()
    {
        var bounds = WindowTracker.GetBounds(_handle);
        if (bounds is null) return null;

        var b = bounds.Value;
        var pad = PadPixels(b.Left + b.Width / 2, b.Top + b.Height / 2);
        return new ScreenRect(
            b.Left + pad, b.Top + pad,
            Math.Max(b.Left + pad + 1, b.Right - pad),
            Math.Max(b.Top + pad + 1, b.Bottom - pad));
    }

    private void ClampToScreen()
    {
        var (x, y) = ClampPoint(_spring.X, _spring.Y);
        _spring.SnapTo(x, y);
    }

    /// <summary>Clamp a window coordinate so the *pill* stays inside the monitor's work area.</summary>
    private (double X, double Y) ClampPoint(double x, double y)
    {
        var bounds = WindowTracker.GetBounds(_handle);
        var dpi = ScreenGeometry.DpiScaleAt((int)x, (int)y);
        var pad = (int)Math.Round(ShadowPad * dpi);
        var width = bounds?.Width ?? (int)(Width * dpi);
        var height = bounds?.Height ?? (int)(Height * dpi);

        var area = ScreenGeometry.WorkAreaAt((int)x + width / 2, (int)y + height / 2);

        return (
            Math.Clamp(x + pad, area.Left, Math.Max(area.Left, area.Right - (width - 2 * pad))) - pad,
            Math.Clamp(y + pad, area.Top, Math.Max(area.Top, area.Bottom - (height - 2 * pad))) - pad);
    }

    private void MoveTo(double x, double y)
    {
        if (_handle == IntPtr.Zero) return;
        _lastMovedX = (int)Math.Round(x);
        _lastMovedY = (int)Math.Round(y);
        WindowMover.MoveTo(_handle, x, y);
    }

    private void OnRendering()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var delta = now - _lastTick;
        _lastTick = now;

        // Keep stepping the spring while dragging: dragging only changes the *target* and the window
        // follows with a lag — that is the elastic feel the Python build has (set_animated_target).
        if (!_positioned || _handle == IntPtr.Zero || delta <= 0) return;

        var bounds = WindowTracker.GetBounds(_handle);
        if (bounds is null) return;

        var screen = ScreenGeometry.VirtualScreen();
        var pad = PadPixels((int)_spring.X, (int)_spring.Y);
        var moved = _spring.Step(delta,
            screen.Left - pad, screen.Top - pad,
            screen.Right - bounds.Value.Width + pad,
            screen.Bottom - bounds.Value.Height + pad);

        // Only call SetWindowPos when the *pixel* position really changed: every call on a layered
        // window is a DWM re-composite, and redundant calls just burn frames.
        if (moved && ((int)Math.Round(_spring.X) != _lastMovedX || (int)Math.Round(_spring.Y) != _lastMovedY))
            MoveTo(_spring.X, _spring.Y);
    }

    // ---------------------------------------------------------------- hooking

    private void HookTick()
    {
        if (!_positioned || _dragging) return;

        var bounds = VisualBar();
        if (bounds is null) return;

        var pad = PadPixels(bounds.Value.Left + bounds.Value.Width / 2, bounds.Value.Top + bounds.Value.Height / 2);
        var settings = _services.Settings.Settings;

        if (_hook is not null)
        {
            if (!WindowTracker.IsAlive(_hook.Handle))
            {
                Unhook();
                return;
            }

            var window = WindowTracker.GetBounds(_hook.Handle);
            if (window is null || !WindowTracker.IsTopMostAtCenter(_hook.Handle))
            {
                Unhook();
                return;
            }

            var (targetX, targetY) = WindowTracker.Follow(
                window.Value, bounds.Value.Width, bounds.Value.Height, _hook.Edge,
                WindowTracker.ParseMode(settings.HookMode),
                settings.HookOffsetTop, settings.HookOffsetBottom,
                _hook.AnchorX);

            if (Math.Abs(targetY - bounds.Value.Top) > settings.UnhookDistance)
            {
                Unhook();
                return;
            }

            // Spring coordinates are the window corner; Follow returns the pill corner.
            _spring.SetTarget(targetX - pad, targetY - pad);
            return;
        }

        var candidate = WindowTracker.Find(
            bounds.Value.Left, bounds.Value.Top, bounds.Value.Width, bounds.Value.Height,
            settings.HookProximityTop, settings.HookProximityBottom,
            settings.HookOffsetTop, settings.HookOffsetBottom,
            _handle, WindowTracker.ParseMode(settings.HookMode),
            // Attaching only moves the bar vertically: its horizontal position is the user's, and
            // hooking must not slide it to the middle of the target's edge.
            keepHorizontalPosition: true);

        if (candidate is null) return;

        _hook = candidate;
        _spring.SetTarget(candidate.TargetX - pad, candidate.TargetY - pad);
        SetStatus(L.F("bar.hooked", candidate.ProcessName), 2500);
    }

    /// <summary>
    /// Where the bar belongs for a hooked window is <see cref="WindowTracker.Follow"/>: the math (and the
    /// "stay on screen even when the target is fullscreen" rule) lives in Core so it can be tested
    /// without a window.
    /// </summary>
    private void Unhook()
    {
        _hook = null;
        _spring.SetTarget(_spring.X, _spring.Y);
        SetStatus(_idleStatus, 2000);
    }

    // ------------------------------------------------------------------- drag

    private void OnBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;

        var (cursorX, cursorY) = WindowMover.CursorPosition();
        _dragging = true;
        _dragCursorX = cursorX;
        _dragCursorY = cursorY;
        _dragStartX = _spring.X;
        _dragStartY = _spring.Y;

        // Capture must be taken on the element that owns the handler. Capturing on the Window makes WPF
        // retarget MouseMove/MouseUp to the Window, so the events no longer pass through BarBorder and
        // _dragging sticks at true — the bar freezes forever after the first click.
        BarBorder.CaptureMouse();
    }

    private void OnBarMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        // Releasing the mouse outside the window (or losing capture) must still end the drag.
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }

        // Dragging = change the target, NOT move the window directly. The spring (OnRendering) follows
        // the target, so the bar trails the cursor by a beat and bounces on direction changes — exactly
        // the feel of the Python build.
        var (cursorX, cursorY) = WindowMover.CursorPosition();
        _spring.SetTarget(_dragStartX + (cursorX - _dragCursorX), _dragStartY + (cursorY - _dragCursorY));
    }

    private void OnBarMouseUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        BarBorder.ReleaseMouseCapture();

        // Do not snap to the current position: only clamp the target inside the screen and let the
        // spring cover the remaining distance (snapping would discard the motion in flight).
        var (targetX, targetY) = ClampPoint(_spring.TargetX, _spring.TargetY);
        _spring.SetTarget(targetX, targetY);
        SavePosition();

        // The user just chose a new place for the bar: re-anchor the hook to where it was dropped. Without
        // this the next tick would pull it back to the relative position captured when it first stuck.
        if (_hook is not null && WindowTracker.GetBounds(_hook.Handle) is { } window)
        {
            var pad = PadPixels((int)targetX, (int)targetY);
            _hook = _hook with { AnchorX = WindowTracker.AnchorFor(window, targetX + pad) };
        }
    }

    private void OnBarRightClick(object sender, MouseButtonEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu();
        menu.Items.Add(MenuItem(L.T("bar.menu.snip"), () => BeginSnip(OcrSource.HookedRegion)));
        menu.Items.Add(MenuItem(L.T("bar.menu.config"), () => OpenConfig()));
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(MenuItem(L.T("bar.menu.quit"), () => Close()));
        menu.PlacementTarget = BarBorder;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static System.Windows.Controls.MenuItem MenuItem(string header, Action action)
    {
        var item = new System.Windows.Controls.MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    // -------------------------------------------------------------- hotkey/OCR

    private void OnHotkeyPressed() => Dispatcher.BeginInvoke(new Action(StartOcrFromHotkey));

    private void StartOcrFromHotkey()
    {
        if (_services.Ocr.IsBusy)
        {
            SetStatus(L.T("bar.busy"), 2000);
            return;
        }

        if (_hook is not null)
        {
            var window = WindowTracker.GetBounds(_hook.Handle);
            if (window is not null)
            {
                CaptureRegion(window.Value, OcrSource.HookedRegion);
                return;
            }
        }

        BeginSnip(OcrSource.HookedRegion);
    }

    private void BeginSnip(OcrSource source)
    {
        if (_snip is not null)
        {
            _snip.Activate();
            return;
        }

        Hide();
        _snip = new SnipOverlay();
        _snip.Selected += rect =>
        {
            CloseSnip();
            CaptureRegion(rect, source);
        };
        _snip.Cancelled += CloseSnip;
        _snip.Show();
    }

    private void CloseSnip()
    {
        _snip?.Close();
        _snip = null;
        Show();
    }

    private void CaptureRegion(ScreenRect rect, OcrSource source)
    {
        var capture = ScreenCapture.Capture(rect.Left, rect.Top, rect.Width, rect.Height);
        if (capture is null)
        {
            SetStatus(L.T("bar.captureFailed"), 4000);
            return;
        }

        _ = _services.Ocr.RunAsync(new OcrRequest(source, PngEncoder.FromCapture(capture), "image/png"));
    }

    private void HandleClipboard()
    {
        var settings = _services.Settings.Settings;

        try
        {
            if (settings.ProcessTextClipboard && Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (!string.IsNullOrWhiteSpace(text) && text != _services.Ocr.LastResultText)
                {
                    _ = _services.Ocr.RunAsync(new OcrRequest(OcrSource.TextClipboard, InlineText: text));
                    return;
                }
            }

            if (settings.ProcessImageClipboard && Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image is not null)
                {
                    var bytes = PngEncoder.Encode(image);
                    _ = _services.Ocr.RunAsync(new OcrRequest(OcrSource.ImageClipboard, bytes, "image/png"));
                    return;
                }
            }

            if (settings.ProcessFileClipboard && Clipboard.ContainsFileDropList())
            {
                foreach (var path in Clipboard.GetFileDropList())
                {
                    if (path is not null && RunFile(path)) return;
                }
            }
        }
        catch (Exception ex)
        {
            // The clipboard is owned by another process half the time; a failed read is not an error.
            _services.Log.ConsoleLog(L.F("bar.clipboardError", ex.Message));
        }
    }

    private bool RunFile(string path, OcrSource source = OcrSource.FileClipboard)
    {
        var loaded = MediaLoader.Load(path, _services.Config);
        if (!loaded.Ok)
        {
            SetStatus(L.F("bar.status", loaded.Error), 5000);
            return false;
        }

        _ = _services.Ocr.RunAsync(new OcrRequest(
            source, loaded.Bytes, loaded.Mime, loaded.InlineText, loaded.FileName));
        return true;
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;

        foreach (var file in files)
        {
            if (RunFile(file, OcrSource.FileDrop)) return;
        }
    }

    // --------------------------------------------------------------- UI state

    /// <summary>
    /// Puts a message in the bar for a while. Public because the startup update check reports through
    /// it — the bar is the app's only always-visible surface.
    /// </summary>
    public void ShowStatus(string message, int durationMs) => SetStatus(message, durationMs);

    private void SetStatus(string message, int durationMs)
    {
        StatusLabel.Text = message;
        _statusTimer.Stop();

        if (durationMs > 0)
        {
            _statusTimer.Interval = TimeSpan.FromMilliseconds(durationMs);
            _statusTimer.Start();
        }
    }

    private void ResetStatus()
    {
        _statusTimer.Stop();
        if (!_services.Ocr.IsBusy) StatusLabel.Text = _idleStatus;
    }

    private void SetBusy(bool busy)
    {
        StatusLabel.Opacity = busy ? 0.55 : 1.0;
        if (busy) SetStatus(L.T("bar.processing"), 0);
        else ResetStatus();
    }

    private void ShowResult(string text)
    {
        // Our own copy must not be re-read by the clipboard watcher.
        _services.Clipboard.SuppressNext();

        if (_result is null || !_result.IsLoaded)
            _result = new ResultWindow(_services);

        _result.ShowText(text, VisualBar());
    }

    /// <summary>
    /// Layout → Test: the real bubble, at the position the current settings put it, without taking focus
    /// from the settings window.
    /// </summary>
    private void ShowResultPreview()
    {
        if (_result is null || !_result.IsLoaded)
            _result = new ResultWindow(_services);

        _result.ShowText(L.T("layout.test.sample"), VisualBar(), activate: false);
    }

    private void OnApiKeyMissing(string providerId)
    {
        SetStatus(L.T("bar.needKey"), 4000);
        OpenConfig(providerId);
    }

    private void OnConfigClick(object sender, RoutedEventArgs e) => OpenConfig();

    public void OpenConfig(string? providerId = null, int? tab = null)
    {
        if (_config is not null && _config.IsLoaded)
        {
            _config.Activate();
            if (providerId is not null) _config.FocusProvider(providerId);
            if (tab is not null) _config.OpenTab(tab.Value);
            return;
        }

        _config = new ConfigWindow(_services);
        _config.TestResultRequested += ShowResultPreview;
        _config.Closed += (_, _) => _config = null;
        _config.Show();
        if (providerId is not null) _config.FocusProvider(providerId);
        if (tab is not null) _config.OpenTab(tab.Value);
    }

    /// <summary>
    /// Startup check entry point: open Settings → System and put the reason right above the key input.
    /// </summary>
    public void PromptForKey(string providerId, string? reason)
    {
        OpenConfig(providerId: providerId, tab: 0);
        _config?.ShowKeyPrompt(reason);
        SetStatus(L.T("bar.needKey"), 4000);
    }

    /// <summary>
    /// Language switch. Strings are resolved while XAML loads (<c>{ui:Tr}</c>), so the bar gets its
    /// few labels refreshed here and the settings window is rebuilt on the tab the user was on.
    /// </summary>
    private void Relocalize()
    {
        ApplyLocalization();
        _result?.ApplyLocalization();

        if (_config is { IsLoaded: true })
        {
            var tab = _config.SelectedTab;
            _config.Close();      // Closed → _config = null
            OpenConfig(tab: tab);
        }
    }

    private void ApplyLocalization()
    {
        _idleStatus = L.T("bar.idle");
        ConfigButton.ToolTip = L.T("bar.tooltip.config");
        CloseButton.ToolTip = L.T("bar.tooltip.close");
        if (!_services.Ocr.IsBusy) StatusLabel.Text = _idleStatus;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _hookTimer.Stop();
        _physicsTimer.Stop();
        _services.Clipboard.Changed -= HandleClipboard;
        _result?.Close();
        Application.Current.Shutdown();
        base.OnClosed(e);
    }
}
