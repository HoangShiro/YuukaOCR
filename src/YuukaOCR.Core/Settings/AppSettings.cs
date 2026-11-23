using System.Text.Json;
using System.Text.Json.Serialization;
using YuukaOCR.Core.Agent;

namespace YuukaOCR.Core.Settings;

/// <summary>
/// Every user-tunable value. JSON names intentionally match the old Python app
/// (user_config.json) so an existing user folder keeps working after migration.
/// Unknown keys survive a rewrite via <see cref="Extra"/>.
/// </summary>
public sealed class AppSettings
{
    // --- layout / persistence -------------------------------------------------
    [JsonPropertyName("positions")] public Dictionary<string, WindowPosition> Positions { get; set; } = new();
    [JsonPropertyName("ui_scale")] public int UiScale { get; set; } = 100;
    [JsonPropertyName("language")] public string Language { get; set; } = Localization.L.Default;
    [JsonPropertyName("sub_window_position")] public string SubWindowPosition { get; set; } = "auto";
    [JsonPropertyName("sub_window_spacing")] public int SubWindowSpacing { get; set; } = 5;
    [JsonPropertyName("min_sub_win_width")] public int MinSubWindowWidth { get; set; } = 200;

    /// <summary>
    /// Last size of every resizable window, keyed by <see cref="WindowSizeKeys"/>. Written when the
    /// user finishes resizing, so the next open restores what they chose.
    /// </summary>
    [JsonPropertyName("window_sizes")] public Dictionary<string, WindowSize> WindowSizes { get; set; } = new();

    // --- behaviour ------------------------------------------------------------
    [JsonPropertyName("auto_update_enabled")] public bool AutoUpdateEnabled { get; set; } = true;
    [JsonPropertyName("start_with_system")] public bool StartWithSystem { get; set; }
    /// <summary>
    /// The release version the user was last told about, e.g. "2.2.0". Empty means "never told".
    /// The startup check writes this after reporting an update, so the same release is not announced
    /// on every launch. It is *not* a "checked" marker: the check itself still runs on every start —
    /// only the repeated notification is suppressed.
    /// </summary>
    [JsonPropertyName("update_notified_version")] public string UpdateNotifiedVersion { get; set; } = "";
    [JsonPropertyName("process_text_clipboard")] public bool ProcessTextClipboard { get; set; }
    [JsonPropertyName("process_file_clipboard")] public bool ProcessFileClipboard { get; set; } = true;
    [JsonPropertyName("process_image_clipboard")] public bool ProcessImageClipboard { get; set; } = true;

    // --- prompt ---------------------------------------------------------------
    // The OCR prompt. The JSON name stays `custom_prompt` so an existing user/settings.json keeps its
    // value; only the label moved (System → Prompt/Agent → OCR, renamed "OCR prompt").
    [JsonPropertyName("prompt_enabled")] public bool PromptEnabled { get; set; }
    [JsonPropertyName("custom_prompt")] public string CustomPrompt { get; set; } = AppConfig.DefaultPromptText;

    // --- agent / actions ------------------------------------------------------
    [JsonPropertyName("agent")] public AgentSettings Agent { get; set; } = new();

    /// <summary>
    /// Actions registered in the result window's action bar, in button order. Seeded with three useful
    /// ones for a brand-new settings file; <see cref="Normalize"/> never re-seeds, so deleting them all sticks.
    /// </summary>
    [JsonPropertyName("actions")] public List<AgentAction> Actions { get; set; } = AgentActions.Defaults();

    // --- theme ----------------------------------------------------------------
    [JsonPropertyName("theme")] public ThemeSettings Theme { get; set; } = new();

    // --- physics --------------------------------------------------------------
    [JsonPropertyName("PHYSICS_SPRING_CONSTANT")] public double SpringConstant { get; set; } = 0.10;
    [JsonPropertyName("PHYSICS_DAMPING_FACTOR")] public double DampingFactor { get; set; } = 0.45;
    [JsonPropertyName("PHYSICS_BOUNCE_DAMPING_FACTOR")] public double BounceDampingFactor { get; set; } = 0.30;

    // --- hooking --------------------------------------------------------------
    [JsonPropertyName("hook_ocr_hotkey")] public string HookOcrHotkey { get; set; } = "middle";
    [JsonPropertyName("HOOK_PROXIMITY_Y")] public int HookProximityTop { get; set; } =
        (int)SliderSpecs.Default(SliderSpecs.HookProximityTop);
    [JsonPropertyName("HOOK_PROXIMITY_Y_BOTTOM")] public int HookProximityBottom { get; set; } =
        (int)SliderSpecs.Default(SliderSpecs.HookProximityBottom);
    [JsonPropertyName("HOOK_OFFSET_Y_TOP")] public int HookOffsetTop { get; set; }
    [JsonPropertyName("HOOK_OFFSET_Y_BOTTOM")] public int HookOffsetBottom { get; set; }
    [JsonPropertyName("UNHOOK_DISTANCE")] public int UnhookDistance { get; set; } =
        (int)SliderSpecs.Default(SliderSpecs.UnhookDistance);

    /// <summary>
    /// Where the bar attaches to the target window: <c>outside</c> (just past the outer edge, the
    /// historical behaviour) or <c>inside</c> (along the inner edge, so the bar stays within the
    /// window it belongs to).
    /// </summary>
    [JsonPropertyName("hook_mode")] public string HookMode { get; set; } = Interop.WindowTracker.Outside;

    // --- provider -------------------------------------------------------------
    [JsonPropertyName("api_provider")] public string ApiProvider { get; set; } = "gemini";
    [JsonPropertyName("gemini_model")] public string Model { get; set; } = "";
    [JsonPropertyName("custom_base_url")] public string CustomBaseUrl { get; set; } = "";
    [JsonPropertyName("recent_providers")] public List<string> RecentProviders { get; set; } = new();
    [JsonPropertyName("recent_models")] public Dictionary<string, List<string>> RecentModels { get; set; } = new();

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Clamps and repairs values that a hand-edited JSON file could break.</summary>
    public void Normalize()
    {
        // Slider-backed values are snapped onto the tick grid, not merely clamped: a value that lands
        // between two ticks has no label under the track, and the thumb would sit at a nameless spot.
        UiScale = (int)SliderSpecs.Snap(SliderSpecs.UiScale, UiScale);
        SubWindowSpacing = (int)SliderSpecs.Snap(SliderSpecs.SubWindowSpacing, SubWindowSpacing);
        MinSubWindowWidth = (int)SliderSpecs.Snap(SliderSpecs.MinSubWindowWidth, MinSubWindowWidth);

        SpringConstant = Math.Round(SliderSpecs.Snap(SliderSpecs.Spring, SpringConstant * 100) / 100, 3);
        DampingFactor = Math.Round(SliderSpecs.Snap(SliderSpecs.Damping, DampingFactor * 100) / 100, 3);
        BounceDampingFactor = Math.Round(SliderSpecs.Snap(SliderSpecs.Bounce, BounceDampingFactor * 100) / 100, 3);

        HookProximityTop = (int)SliderSpecs.Snap(SliderSpecs.HookProximityTop, HookProximityTop);
        HookProximityBottom = (int)SliderSpecs.Snap(SliderSpecs.HookProximityBottom, HookProximityBottom);
        HookOffsetTop = (int)SliderSpecs.Snap(SliderSpecs.HookOffsetTop, HookOffsetTop);
        HookOffsetBottom = (int)SliderSpecs.Snap(SliderSpecs.HookOffsetBottom, HookOffsetBottom);
        UnhookDistance = (int)SliderSpecs.Snap(SliderSpecs.UnhookDistance, UnhookDistance);
        HookMode = Interop.WindowTracker.NormalizeMode(HookMode);

        if (string.IsNullOrWhiteSpace(HookOcrHotkey)) HookOcrHotkey = "middle";
        if (string.IsNullOrWhiteSpace(ApiProvider)) ApiProvider = "gemini";
        Theme ??= new ThemeSettings();
        Agent ??= new AgentSettings();
        Positions ??= new();
        RecentProviders ??= new();
        RecentModels ??= new();
        NormalizeWindowSizes();
        Language = Localization.L.Normalize(Language);
        UpdateNotifiedVersion = (UpdateNotifiedVersion ?? "").Trim();
        Theme.Normalize();
        Agent.Normalize();
        NormalizeActions();
    }

    /// <summary>
    /// Repairs the action table: at most <see cref="AgentActions.Max"/> entries and no duplicate ids.
    /// A new action added in the UI is created with a fresh id already, so this only has to defend
    /// against a hand-edited file.
    /// </summary>
    private void NormalizeActions()
    {
        Actions ??= new();

        foreach (var action in Actions) action.Normalize();

        // An action with no instruction is kept: it is a tab the user is still writing. The result window
        // skips it, so a half-written action never becomes a button that does nothing.
        if (Actions.Count > AgentActions.Max) Actions.RemoveRange(AgentActions.Max, Actions.Count - AgentActions.Max);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in Actions)
        {
            while (!seen.Add(action.Id)) action.Id = AgentActions.NewId();
        }
    }

    /// <summary>
    /// Repairs the stored window sizes: a hand-edited file can hold a negative, a zero, a NaN (JSON has
    /// no NaN, but a "null" deserializes to 0) or a size no monitor can show. An entry that cannot be
    /// used is dropped rather than clamped to something absurd, so the window falls back to its default.
    /// </summary>
    private void NormalizeWindowSizes()
    {
        WindowSizes ??= new();

        foreach (var key in WindowSizes.Keys.ToList())
        {
            var size = WindowSizes[key];
            if (size is null || !size.IsUsable)
            {
                WindowSizes.Remove(key);
                continue;
            }

            WindowSizes[key] = size.Clamped();
        }
    }

    /// <summary>The stored size for <paramref name="key"/>, or null when the user never resized it.</summary>
    public WindowSize? SizeOf(string key) =>
        WindowSizes.TryGetValue(key, out var size) && size.IsUsable ? size : null;

    public void RememberProvider(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return;
        RecentProviders.Remove(providerId);
        RecentProviders.Insert(0, providerId);
        if (RecentProviders.Count > 5) RecentProviders.RemoveRange(5, RecentProviders.Count - 5);
    }

    public void RememberModel(string providerId, string model)
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(model)) return;
        if (!RecentModels.TryGetValue(providerId, out var list))
        {
            list = new List<string>();
            RecentModels[providerId] = list;
        }
        list.Remove(model);
        list.Insert(0, model);
        if (list.Count > 5) list.RemoveRange(5, list.Count - 5);
    }
}
