using YuukaOCR.Core.Localization;

namespace YuukaOCR.Core.Input;

public enum HotkeyKind
{
    None,
    Keyboard,
    Mouse
}

public enum MouseButton
{
    None,
    Middle,
    X1,
    X2,
    Left,
    Right
}

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
    Windows = 8
}

/// <summary>
/// A hotkey is either a keyboard key (with optional modifiers) or a mouse button. Parsed from the
/// config string ("middle", "ctrl+shift+m", "x1") so old user settings keep working.
/// </summary>
public sealed record HotkeySpec(HotkeyKind Kind, int VirtualKey, MouseButton Button, HotkeyModifiers Modifiers)
{
    public static readonly HotkeySpec None = new(HotkeyKind.None, 0, MouseButton.None, HotkeyModifiers.None);

    public bool IsValid => Kind != HotkeyKind.None;

    public static HotkeySpec Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return None;

        var tokens = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return None;

        var modifiers = HotkeyModifiers.None;
        var index = 0;
        for (; index < tokens.Length - 1; index++)
        {
            var token = tokens[index].ToLowerInvariant();
            switch (token)
            {
                case "ctrl" or "control": modifiers |= HotkeyModifiers.Control; break;
                case "shift": modifiers |= HotkeyModifiers.Shift; break;
                case "alt" or "option": modifiers |= HotkeyModifiers.Alt; break;
                case "win" or "windows" or "meta" or "super" or "cmd": modifiers |= HotkeyModifiers.Windows; break;
                default: return None; // unknown modifier in the middle: reject the whole spec
            }
        }

        var key = tokens[^1].Trim();

        if (TryParseMouse(key, out var button))
            return new HotkeySpec(HotkeyKind.Mouse, 0, button, modifiers);

        if (VirtualKeys.TryParse(key, out var vk))
            return new HotkeySpec(HotkeyKind.Keyboard, vk, MouseButton.None, modifiers);

        return None;
    }

    private static bool TryParseMouse(string token, out MouseButton button)
    {
        var normalized = token.StartsWith("mouse:", StringComparison.OrdinalIgnoreCase)
            ? token[6..]
            : token;

        switch (normalized.ToLowerInvariant())
        {
            case "middle" or "mbutton" or "wheel": button = MouseButton.Middle; return true;
            case "x1" or "mouse4" or "back": button = MouseButton.X1; return true;
            case "x2" or "mouse5" or "forward": button = MouseButton.X2; return true;
            case "lbutton" or "mouseleft": button = MouseButton.Left; return true;
            case "rbutton" or "mouseright": button = MouseButton.Right; return true;
            default: button = MouseButton.None; return false;
        }
    }

    public string Serialize()
    {
        if (!IsValid) return "";

        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("win");

        parts.Add(Kind == HotkeyKind.Mouse
            ? Button switch
            {
                MouseButton.Middle => "middle",
                MouseButton.X1 => "x1",
                MouseButton.X2 => "x2",
                MouseButton.Left => "mouse:left",
                MouseButton.Right => "mouse:right",
                _ => ""
            }
            : VirtualKeys.NameOf(VirtualKey).ToLowerInvariant());

        return string.Join("+", parts);
    }

    public string Describe()
    {
        if (!IsValid) return L.T("hotkey.none");

        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");

        parts.Add(Kind == HotkeyKind.Mouse
            ? Button switch
            {
                MouseButton.Middle => L.T("hotkey.mouse.middle"),
                MouseButton.X1 => L.T("hotkey.mouse.x1"),
                MouseButton.X2 => L.T("hotkey.mouse.x2"),
                MouseButton.Left => L.T("hotkey.mouse.left"),
                MouseButton.Right => L.T("hotkey.mouse.right"),
                _ => "?"
            }
            : VirtualKeys.NameOf(VirtualKey));

        return string.Join(" + ", parts);
    }

    public override string ToString() => Describe();
}
