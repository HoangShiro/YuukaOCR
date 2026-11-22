using System.Globalization;

namespace YuukaOCR.Core;

/// <summary>UI-free colour value so Core never has to reference WPF.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public static readonly Rgba Transparent = new(0, 0, 0, 0);
    public static readonly Rgba White = new(255, 255, 255);
    public static readonly Rgba Accent = new(0xE9, 0x89, 0x73);

    public string ToHex() => A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{A:X2}{R:X2}{G:X2}{B:X2}";

    public string ToCss() => A == 255
        ? $"#{R:X2}{G:X2}{B:X2}"
        : $"rgba({R},{G},{B},{(A / 255.0).ToString("0.###", CultureInfo.InvariantCulture)})";

    public static Rgba Parse(string? value, Rgba fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var s = value.Trim();

        if (s.StartsWith("rgba", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            var open = s.IndexOf('(');
            var close = s.LastIndexOf(')');
            if (open < 0 || close < open) return fallback;
            var parts = s[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 3) return fallback;
            if (!byte.TryParse(parts[0], out var r) ||
                !byte.TryParse(parts[1], out var g) ||
                !byte.TryParse(parts[2], out var b)) return fallback;
            byte a = 255;
            if (parts.Length >= 4)
            {
                var alpha = parts[3];
                if (alpha.Contains('.'))
                {
                    if (!double.TryParse(alpha, NumberStyles.Float, CultureInfo.InvariantCulture, out var af))
                        return fallback;
                    a = (byte)Math.Clamp(Math.Round(af * 255), 0, 255);
                }
                else if (!byte.TryParse(alpha, out a)) return fallback;
            }
            return new Rgba(r, g, b, a);
        }

        if (!s.StartsWith('#')) return fallback;
        var hex = s[1..];
        try
        {
            return hex.Length switch
            {
                3 => new Rgba(Expand(hex[0]), Expand(hex[1]), Expand(hex[2])),
                4 => new Rgba(Expand(hex[1]), Expand(hex[2]), Expand(hex[3]), Expand(hex[0])),
                6 => new Rgba(Hex(hex, 0), Hex(hex, 2), Hex(hex, 4)),
                8 => new Rgba(Hex(hex, 2), Hex(hex, 4), Hex(hex, 6), Hex(hex, 0)),
                _ => fallback
            };
        }
        catch (FormatException)
        {
            return fallback;
        }

        static byte Expand(char c) => (byte)(Convert.ToByte(c.ToString(), 16) * 17);
        static byte Hex(string s, int i) => Convert.ToByte(s.Substring(i, 2), 16);
    }
}
