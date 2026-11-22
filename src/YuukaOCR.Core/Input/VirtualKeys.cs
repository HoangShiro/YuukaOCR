namespace YuukaOCR.Core.Input;

/// <summary>Token ↔ Win32 virtual-key translation, used by hotkey capture and parsing.</summary>
public static class VirtualKeys
{
    private static readonly Dictionary<string, int> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = 0x20, ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09,
        ["esc"] = 0x1B, ["escape"] = 0x1B, ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E,
        ["insert"] = 0x2D, ["ins"] = 0x2D, ["home"] = 0x24, ["end"] = 0x23,
        ["pageup"] = 0x21, ["pgup"] = 0x21, ["pagedown"] = 0x22, ["pgdn"] = 0x22,
        ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["printscreen"] = 0x2C, ["prtsc"] = 0x2C, ["pause"] = 0x13,
        ["capslock"] = 0x14, ["numlock"] = 0x90, ["scrolllock"] = 0x91,
        ["minus"] = 0xBD, ["equals"] = 0xBB, ["comma"] = 0xBC, ["period"] = 0xBE,
        ["slash"] = 0xBF, ["semicolon"] = 0xBA, ["quote"] = 0xDE, ["backslash"] = 0xDC,
        ["bracketleft"] = 0xDB, ["bracketright"] = 0xDD, ["grave"] = 0xC0, ["backtick"] = 0xC0,
        ["apps"] = 0x5D, ["multiply"] = 0x6A, ["add"] = 0x6B, ["subtract"] = 0x6D,
        ["decimal"] = 0x6E, ["divide"] = 0x6F
    };

    private static readonly Dictionary<int, string> Names;

    static VirtualKeys()
    {
        for (var c = 'A'; c <= 'Z'; c++) Map[c.ToString()] = c;
        for (var c = '0'; c <= '9'; c++) Map[c.ToString()] = c;
        for (var i = 1; i <= 24; i++) Map[$"f{i}"] = 0x6F + i;
        for (var i = 0; i <= 9; i++) Map[$"numpad{i}"] = 0x60 + i;

        Names = new Dictionary<int, string>();
        foreach (var (token, vk) in Map)
        {
            // Prefer the first (canonical) name for each key code.
            if (!Names.ContainsKey(vk)) Names[vk] = token;
        }

        // Pretty names for display.
        foreach (var (vk, name) in new[]
                 {
                     (0x0D, "Enter"), (0x1B, "Esc"), (0x20, "Space"), (0x2E, "Delete"), (0x08, "Backspace"),
                     (0x26, "Up"), (0x28, "Down"), (0x25, "Left"), (0x27, "Right"), (0x2C, "PrintScreen")
                 })
        {
            Names[vk] = name;
        }
    }

    public static bool TryParse(string token, out int virtualKey) => Map.TryGetValue(token, out virtualKey);

    public static string NameOf(int virtualKey)
    {
        if (Names.TryGetValue(virtualKey, out var name)) return name.ToUpperInvariant();
        if (virtualKey is >= 0x41 and <= 0x5A) return ((char)virtualKey).ToString();
        if (virtualKey is >= 0x30 and <= 0x39) return ((char)virtualKey).ToString();
        if (virtualKey is >= 0x70 and <= 0x87) return $"F{virtualKey - 0x6F}";
        return $"VK{virtualKey:X2}";
    }
}
