using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YuukaOCR.Core.Input;
using YuukaOCR.Core.Localization;
using CoreMouse = YuukaOCR.Core.Input.MouseButton;

namespace YuukaOCR.App.Ui.Controls;

/// <summary>
/// Captures a hotkey: click it, then press a key combo or a mouse button (middle / X1 / X2).
/// Mouse buttons are captured globally for the duration of the capture, because they never reach
/// a WPF control.
/// </summary>
public partial class HotkeyBox : UserControl
{
    private HotkeySpec _spec = HotkeySpec.None;
    private bool _capturing;

    public HotkeyBox()
    {
        InitializeComponent();

        MouseLeftButtonDown += (_, _) => BeginCapture();
        LostKeyboardFocus += (_, _) => EndCapture();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public event Action<HotkeySpec>? SpecChanged;

    public HotkeySpec Spec
    {
        get => _spec;
        set
        {
            _spec = value;
            ValueText.Text = value.Describe();
        }
    }

    private void BeginCapture()
    {
        if (_capturing) return;
        _capturing = true;
        Focus();
        Keyboard.Focus(this);
        HintText.Text = L.T("hotkey.capturing");
        Root.BorderBrush = (Brush)FindResource("YuukaAccent");

        // Mouse buttons need the Win32 route while capturing.
        Mouse.Capture(this);
        Mouse.AddPreviewMouseDownOutsideCapturedElementHandler(this, OnGlobalMouseDown);
    }

    private void EndCapture()
    {
        if (!_capturing) return;
        _capturing = false;
        HintText.Text = L.T("hotkey.hint");
        Root.BorderBrush = (Brush)FindResource("YuukaBorder");
        Mouse.RemovePreviewMouseDownOutsideCapturedElementHandler(this, OnGlobalMouseDown);
        if (Mouse.Captured == this) Mouse.Capture(null);
    }

    private void OnGlobalMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_capturing) return;

        CoreMouse? captured = e.ChangedButton switch
        {
            System.Windows.Input.MouseButton.Middle => CoreMouse.Middle,
            System.Windows.Input.MouseButton.XButton1 => CoreMouse.X1,
            System.Windows.Input.MouseButton.XButton2 => CoreMouse.X2,
            _ => null
        };

        if (captured is null) return;

        var spec = new HotkeySpec(HotkeyKind.Mouse, 0, captured.Value, ToModifiers(Keyboard.Modifiers));

        Apply(spec);
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;

        if (key == Key.Escape)
        {
            EndCapture();
            e.Handled = true;
            return;
        }

        var token = KeyToken(key);
        if (token is null) return;

        if (!VirtualKeys.TryParse(token, out var virtualKey)) return;

        Apply(new HotkeySpec(HotkeyKind.Keyboard, virtualKey, Core.Input.MouseButton.None,
            ToModifiers(Keyboard.Modifiers)));
        e.Handled = true;
    }

    private void Apply(HotkeySpec spec)
    {
        Spec = spec;
        EndCapture();
        SpecChanged?.Invoke(spec);
    }

    private static HotkeyModifiers ToModifiers(ModifierKeys modifiers)
    {
        var result = HotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control)) result |= HotkeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= HotkeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Windows)) result |= HotkeyModifiers.Windows;
        return result;
    }

    private static string? KeyToken(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString().ToLowerInvariant(),
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "numpad" + (int)(key - Key.NumPad0),
        >= Key.F1 and <= Key.F24 => "f" + (int)(key - Key.F1 + 1),
        Key.Space => "space",
        Key.Enter => "enter",
        Key.Tab => "tab",
        Key.Back => "backspace",
        Key.Delete => "delete",
        Key.Insert => "insert",
        Key.Home => "home",
        Key.End => "end",
        Key.PageUp => "pageup",
        Key.PageDown => "pagedown",
        Key.Up => "up",
        Key.Down => "down",
        Key.Left => "left",
        Key.Right => "right",
        Key.PrintScreen => "printscreen",
        Key.Pause => "pause",
        Key.OemMinus => "minus",
        Key.OemPlus => "equals",
        Key.OemComma => "comma",
        Key.OemPeriod => "period",
        Key.OemQuestion => "slash",
        Key.OemSemicolon => "semicolon",
        Key.OemQuotes => "quote",
        Key.OemBackslash => "backslash",
        Key.OemOpenBrackets => "bracketleft",
        Key.OemCloseBrackets => "bracketright",
        Key.OemTilde => "grave",
        Key.Apps => "apps",
        Key.Multiply => "multiply",
        Key.Add => "add",
        Key.Subtract => "subtract",
        Key.Decimal => "decimal",
        Key.Divide => "divide",
        _ => null
    };
}
