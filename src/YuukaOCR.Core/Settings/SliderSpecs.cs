using System.Globalization;

namespace YuukaOCR.Core.Settings;

/// <summary>
/// One discrete slider: the range, the tick step and the value the app ships with.
///
/// Every slider in the settings window is built from one of these, so the range on screen and the
/// range the value is clamped to can never disagree. The defaults are deliberately chosen to sit
/// *exactly* on a tick — a default that falls between two ticks leaves the thumb at a position the
/// numbers under the track do not name, and the test runner fails when one drifts.
/// </summary>
public readonly record struct SliderSpec(
    string Key,
    double Min,
    double Max,
    double Step,
    double Default,
    int Decimals = 0)
{
    /// <summary>Nearest tick for <paramref name="value"/>, clamped into the range.</summary>
    public double Snap(double value)
    {
        if (double.IsNaN(value)) return Default;
        var clamped = Math.Clamp(value, Min, Max);
        if (Step <= 0) return Round(clamped);

        var steps = Math.Round((clamped - Min) / Step, MidpointRounding.AwayFromZero);
        return Round(Math.Clamp(Min + steps * Step, Min, Max));
    }

    /// <summary>True when <paramref name="value"/> is one of the values the ticks name.</summary>
    public bool IsOnGrid(double value)
    {
        if (value < Min - 1e-9 || value > Max + 1e-9) return false;
        if (Step <= 0) return true;
        var steps = (value - Min) / Step;
        return Math.Abs(steps - Math.Round(steps)) < 1e-6;
    }

    public string Format(double value) =>
        Round(value).ToString(Decimals > 0 ? "0." + new string('#', Decimals) : "0", CultureInfo.CurrentCulture);

    private double Round(double value) => Decimals > 0 ? Math.Round(value, Decimals) : Math.Round(value);
}

/// <summary>
/// The table of every slider the settings window shows. Add a slider here first, then point its
/// control at <see cref="Get"/> — that is what keeps "the ticks" and "the value" in one place.
/// </summary>
public static class SliderSpecs
{
    // Appearance
    public const string FrameOpacity = "frame_opacity";
    public const string BackgroundOpacity = "background_opacity";
    public const string FontSize = "font_size";
    public const string UiScale = "ui_scale";

    // Layout
    public const string SubWindowSpacing = "sub_window_spacing";
    public const string MinSubWindowWidth = "min_sub_window_width";

    // Hooking
    public const string HookProximityTop = "hook_proximity_top";
    public const string HookProximityBottom = "hook_proximity_bottom";
    public const string HookOffsetTop = "hook_offset_top";
    public const string HookOffsetBottom = "hook_offset_bottom";
    public const string UnhookDistance = "unhook_distance";

    // Physics
    public const string Spring = "spring";
    public const string Damping = "damping";
    public const string Bounce = "bounce";

    // Prompt/Agent
    public const string AgentTemperature = "agent_temperature";
    public const string AgentContextTokens = "agent_context_tokens";

    public static IReadOnlyList<SliderSpec> All { get; } =
    [
        // Ticks are coarse on purpose: the request doubles their thickness, which only reads well when
        // there are few of them. Every default below sits exactly on one.
        new(FrameOpacity, 20, 100, 10, 100),
        // 90 % is the shipped background opacity: the window stays readable over whatever is behind it.
        // A settings file that already stores its own value keeps it — only a missing key gets this.
        new(BackgroundOpacity, 20, 100, 10, 90),
        new(FontSize, 8, 32, 4, 12),
        new(UiScale, 50, 200, 25, 100),

        new(SubWindowSpacing, 0, 60, 5, 5),
        new(MinSubWindowWidth, 120, 600, 20, 200),

        // Window edge snapping. These five steps are deliberately half of the old ones (100/50/100),
        // so every slider shows twice as many ticks and a value can be set twice as precisely. The
        // defaults stay on the grid, and every value an older build could store (a multiple of the old
        // step) is still a multiple of the new one, so nothing moves on upgrade.
        new(HookProximityTop, 0, 400, 50, 100),
        new(HookProximityBottom, 0, 400, 50, 100),
        new(HookOffsetTop, -100, 100, 25, 0),
        new(HookOffsetBottom, -100, 100, 25, 0),
        new(UnhookDistance, 0, 600, 50, 100),

        new(Spring, 0, 50, 10, 10),
        new(Damping, 0, 100, 10, 40),
        new(Bounce, 0, 100, 10, 30),

        new(AgentTemperature, 0, 2, 0.25, 0.75, Decimals: 2),
        new(AgentContextTokens, 0, 32000, 4000, 8000)
    ];

    private static readonly Dictionary<string, SliderSpec> ByKey =
        All.ToDictionary(spec => spec.Key, StringComparer.Ordinal);

    public static SliderSpec Get(string key) =>
        ByKey.TryGetValue(key, out var spec)
            ? spec
            : throw new KeyNotFoundException($"No slider spec registered for '{key}'.");

    public static bool TryGet(string key, out SliderSpec spec) => ByKey.TryGetValue(key, out spec);

    /// <summary>Snaps a stored value onto the slider's tick grid (unknown key → the value itself).</summary>
    public static double Snap(string key, double value) =>
        ByKey.TryGetValue(key, out var spec) ? spec.Snap(value) : value;

    public static double Default(string key) => Get(key).Default;
}
