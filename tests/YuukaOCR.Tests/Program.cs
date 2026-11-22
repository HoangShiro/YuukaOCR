using System.Text.Json;
using YuukaOCR.Core;
using YuukaOCR.Core.Agent;
using YuukaOCR.Core.Input;
using YuukaOCR.Core.Interop;
using YuukaOCR.Core.Localization;
using YuukaOCR.Core.Ocr;
using YuukaOCR.Core.Physics;
using YuukaOCR.Core.Providers;
using YuukaOCR.Core.Settings;

// Tiny dependency-free test runner: this project must build on a machine with no NuGet access.
var tests = new List<(string Name, Action Body)>();
void Test(string name, Action body) => tests.Add((name, body));

static void Equal<T>(T expected, T actual, string what = "")
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{what} expected '{expected}', got '{actual}'");
}

static void True(bool condition, string what)
{
    if (!condition) throw new Exception(what);
}

// ---------------------------------------------------------------- ResponseParser
Test("ResponseParser: plain JSON", () =>
{
    var result = ResponseParser.Parse("{\"extracted_text\": \"Xin chào\"}");
    True(result.Ok, "should parse");
    Equal("Xin chào", result.Text);
});

Test("ResponseParser: fenced JSON", () =>
{
    var result = ResponseParser.Parse("```json\n{\"extracted_text\": \"abc\"}\n```");
    True(result.Ok, "should parse");
    Equal("abc", result.Text);
});

Test("ResponseParser: truncated JSON", () =>
{
    var result = ResponseParser.Parse("{\"extracted_text\": \"dòng một\\ndòng hai");
    True(result.Ok, "should recover");
    True(result.Text.Contains("dòng một"), "should keep first line");
});

Test("ResponseParser: nested JSON string", () =>
{
    var result = ResponseParser.Parse("{\"extracted_text\": \"{\\\"translated_text\\\": \\\"bản dịch\\\"}\"}");
    True(result.Ok, "should parse");
    Equal("bản dịch", result.Text);
});

Test("ResponseParser: prose fallback", () =>
{
    var result = ResponseParser.Parse("Xin chào, đây là text.");
    True(result.Ok, "should not fail");
    Equal("Xin chào, đây là text.", result.Text);
});

Test("ResponseParser: empty fails", () =>
{
    True(!ResponseParser.Parse("   ").Ok, "empty must fail");
});

// ------------------------------------------------------------------- HotkeySpec
Test("HotkeySpec: mouse middle", () =>
{
    var spec = HotkeySpec.Parse("middle");
    Equal(HotkeyKind.Mouse, spec.Kind);
    Equal(YuukaOCR.Core.Input.MouseButton.Middle, spec.Button);
});

Test("HotkeySpec: ctrl+shift+m", () =>
{
    var spec = HotkeySpec.Parse("ctrl+shift+m");
    Equal(HotkeyKind.Keyboard, spec.Kind);
    Equal(0x4D, spec.VirtualKey);
    Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, spec.Modifiers);
});

Test("HotkeySpec: round trip", () =>
{
    foreach (var text in new[] { "middle", "x1", "x2", "ctrl+m", "alt+shift+f2", "printscreen" })
    {
        var spec = HotkeySpec.Parse(text);
        True(spec.IsValid, $"{text} should be valid");
        Equal(spec.Serialize(), HotkeySpec.Parse(spec.Serialize()).Serialize(), $"round trip {text}");
    }
});

Test("HotkeySpec: garbage is rejected", () =>
{
    True(!HotkeySpec.Parse("ctrl+notakey").IsValid, "unknown key must be rejected");
    True(!HotkeySpec.Parse("").IsValid, "empty must be rejected");
    Equal("Not set", HotkeySpec.None.Describe());
});

// ---------------------------------------------------------------- SpringMotion
Test("SpringMotion: settles on target", () =>
{
    var spring = new SpringMotion(0, 0);
    spring.SetTarget(400, 200);

    for (var i = 0; i < 600; i++) spring.Step(0.016);

    True(Math.Abs(spring.X - 400) < 1.0, $"x should settle near 400, got {spring.X:0.00}");
    True(Math.Abs(spring.Y - 200) < 1.0, $"y should settle near 200, got {spring.Y:0.00}");
    True(spring.IsSettled, "should report settled");
});

Test("SpringMotion: clamps inside bounds", () =>
{
    var spring = new SpringMotion(0, 0);
    spring.SetTarget(5000, 5000);

    for (var i = 0; i < 300; i++) spring.Step(0.016, 0, 0, 800, 600);

    True(spring.X <= 800.01 && spring.Y <= 600.01, $"clamped, got {spring.X:0.0},{spring.Y:0.0}");
});

// ------------------------------------------------------------------- PromptBuilder
Test("PromptBuilder: custom prompt only when enabled", () =>
{
    var off = PromptBuilder.Build(Prompts.Image, false, "dịch sang tiếng Việt");
    True(!off.HasCustom, "must not attach when disabled");
    Equal(Prompts.Image, off.Prompt);

    var on = PromptBuilder.Build(Prompts.Image, true, "dịch sang tiếng Việt");
    True(on.HasCustom, "must attach when enabled");
    True(on.Prompt.Contains("dịch sang tiếng Việt"), "custom text in prompt");
});

// ----------------------------------------------------------------- ProviderRegistry
Test("ProviderRegistry: curated table", () =>
{
    True(ProviderRegistry.All.Count >= 18, $"expected >= 18 providers, got {ProviderRegistry.All.Count}");
    Equal(ProviderRegistry.All.Count, ProviderRegistry.All.Select(p => p.Id).Distinct().Count(), "unique ids");

    foreach (var info in ProviderRegistry.All)
    {
        // "custom" is the only entry without a base URL: the user types it in settings.
        if (info.Id != "custom") True(!string.IsNullOrWhiteSpace(info.BaseUrl), $"{info.Id} base url");
        True(!string.IsNullOrWhiteSpace(info.EnvKey), $"{info.Id} env key");
        if (info.Id != "custom") True(!string.IsNullOrWhiteSpace(info.DefaultModel), $"{info.Id} default model");
    }

    True(ProviderRegistry.Exists("gemini"), "gemini present");
    True(ProviderRegistry.Exists("anthropic"), "anthropic present");
    True(ProviderRegistry.Exists("openai"), "openai present");
    True(!ProviderRegistry.Exists("cohere"), "legacy provider dropped");

    // OpenRouter serves /models without auth, so its key check must use the authenticated endpoint —
    // otherwise "Add" would accept any garbage string.
    Equal("/key", ProviderRegistry.Get("openrouter").AuthCheckPath, "openrouter auth check path");
    True(ProviderRegistry.Get("openai").AuthCheckPath.Length == 0, "openai checks via its model list");
});

Test("ModelListResult: only a real rejection counts as a bad key", () =>
{
    True(ModelListResult.Fail("denied", 401).IsKeyRejected, "401 is a rejected key");
    True(ModelListResult.Fail("denied", 403).IsKeyRejected, "403 is a rejected key");
    True(ModelListResult.Fail("API key not valid", 400).IsKeyRejected, "Google reports a bad key as 400");
    True(!ModelListResult.Fail("offline", 502).IsKeyRejected, "a dead connection is not a bad key");
    True(!ModelListResult.Fail("timeout", 408).IsKeyRejected, "a timeout is not a bad key");
    True(!ModelListResult.Fail("slow down", 429).IsKeyRejected, "a rate limit is not a bad key");
    True(!ModelListResult.Fail("local").IsKeyRejected, "no status is not a bad key");
    True(ModelListResult.Ok([]).Success, "an empty list still counts as success");
});

// ------------------------------------------------------------------ ModelRanking
Test("ModelRanking: OpenRouter presets come first", () =>
{
    var ranked = ModelRanking.Rank(
    [
        "google/gemini-2.5-flash",
        "@preset/tools",
        "openai/gpt-4o-mini:free",
        "@preset/code"
    ]);

    // The requirement is "presets on top", not a particular order among them.
    Equal(2, ranked.Take(2).Count(ModelRanking.IsPreset), "both presets occupy the first two slots");
    True(ranked.IndexOf("google/gemini-2.5-flash") > 1, "a vision model must not jump above a preset");
    True(ranked.IndexOf("openai/gpt-4o-mini:free") > 1, "a free vision model must not jump above a preset");
    True(ModelRanking.IsPreset("@PRESET/tools"), "preset detection is case-insensitive");
    True(!ModelRanking.IsPreset("preset/tools"), "only the @preset/ prefix counts");
});

Test("ModelRanking: vision and free models float up, duplicates collapse", () =>
{
    var ranked = ModelRanking.Rank(
    [
        "deepseek/deepseek-chat",
        "meta-llama/llama-3.1-8b",
        "openai/gpt-4o-mini:free",
        "google/gemini-2.5-flash",
        "google/gemini-2.5-flash"
    ]);

    Equal(4, ranked.Count, "duplicates collapse");
    Equal("openai/gpt-4o-mini:free", ranked[0], "vision + free first");
    True(ranked.IndexOf("meta-llama/llama-3.1-8b") > 0, "a text-only model is not first");
});

// ------------------------------------------------------------------ ApiKeyPool
Test("ApiKeyPool: env round trip", () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"yuuka-env-{Guid.NewGuid():N}.env");
    try
    {
        ApiKeyPool.AddKey(path, "openai", "sk-first");
        ApiKeyPool.AddKey(path, "openai", "sk-second");
        ApiKeyPool.AddKey(path, "gemini", "gm-key");

        var keys = ApiKeyPool.LoadFor(path, "openai");
        Equal(2, keys.Count, "two openai keys");
        True(keys.Contains("sk-first") && keys.Contains("sk-second"), "both keys stored");
        Equal(1, ApiKeyPool.LoadFor(path, "gemini").Count, "one gemini key");

        var masked = ApiKeyPool.Mask("sk-first");
        True(!masked.Contains("first"), "masked value hides the secret");
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }
});

// ------------------------------------------------------------------ MediaLoader
Test("MediaLoader: text file is inlined", () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"yuuka-{Guid.NewGuid():N}.txt");
    File.WriteAllText(path, "nội dung");
    try
    {
        var result = MediaLoader.Load(path, new AppConfig());
        True(result.Ok, result.Error ?? "should load");
        Equal("nội dung", result.InlineText);
        True(result.Bytes is null, "text has no bytes");
    }
    finally
    {
        File.Delete(path);
    }
});

Test("MediaLoader: rejects unsupported extension", () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"yuuka-{Guid.NewGuid():N}.exe");
    File.WriteAllBytes(path, [1, 2, 3]);
    try
    {
        True(!MediaLoader.Load(path, new AppConfig()).Ok, "exe must be rejected");
    }
    finally
    {
        File.Delete(path);
    }
});

Test("MediaLoader: rejects oversized file", () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"yuuka-{Guid.NewGuid():N}.png");
    File.WriteAllBytes(path, new byte[2048]);
    try
    {
        True(!MediaLoader.Load(path, new AppConfig { MaxFileSizeMb = 0 }, maxBytes: 1024).Ok, "size limit enforced");
    }
    finally
    {
        File.Delete(path);
    }
});

// ------------------------------------------------------------------- AppSettings
Test("AppSettings: normalize clamps", () =>
{
    var settings = new AppSettings { UiScale = 500, UnhookDistance = -20, HookOcrHotkey = " " };
    settings.Normalize();

    Equal(200, settings.UiScale);
    Equal(0, settings.UnhookDistance);
    Equal("middle", settings.HookOcrHotkey);
    True(settings.Theme is not null, "theme created");
});

Test("AppSettings: remembers recents without duplicates", () =>
{
    var settings = new AppSettings();
    settings.RememberProvider("openai");
    settings.RememberProvider("gemini");
    settings.RememberProvider("openai");

    Equal("openai", settings.RecentProviders[0]);
    Equal(2, settings.RecentProviders.Count, "no duplicates");
});

// --------------------------------------------------------------- ScreenGeometry
Test("ScreenGeometry: stable display hash", () =>
{
    var first = ScreenGeometry.DisplayConfigHash();
    var second = ScreenGeometry.DisplayConfigHash();

    Equal(first, second, "hash must be stable");
    Equal(16, first.Length, "hash length");
});

// --------------------------------------------------------------------- AppPaths
Test("AppPaths: user data lives under user/", () =>
{
    True(AppPaths.UserDir.EndsWith("user", StringComparison.OrdinalIgnoreCase), "user dir");
    True(AppPaths.EnvFile.StartsWith(AppPaths.UserDir, StringComparison.OrdinalIgnoreCase), "env file inside user/");
    True(AppPaths.SettingsFile.StartsWith(AppPaths.UserDir, StringComparison.OrdinalIgnoreCase), "settings inside user/");
});

// ----------------------------------------------------------------- Localization
Test("Localization: every language table holds the same keys", () =>
{
    var en = L.EnglishTable;

    foreach (var (code, table) in L.Tables)
    {
        var missing = en.Keys.Except(table.Keys).OrderBy(k => k).ToList();
        var extra = table.Keys.Except(en.Keys).OrderBy(k => k).ToList();

        True(missing.Count == 0, $"{code}: missing in table: {string.Join(", ", missing)}");
        True(extra.Count == 0, $"{code}: keys not in English: {string.Join(", ", extra)}");

        foreach (var (key, value) in table)
            True(!string.IsNullOrWhiteSpace(value), $"{code}: value for '{key}' is empty");
    }
});

Test("Localization: every offered language has a table", () =>
{
    True(L.Languages.Count >= 10, $"expected at least 10 languages, got {L.Languages.Count}");
    Equal(L.Languages.Count, L.Languages.Select(o => o.Code).Distinct().Count(), "duplicate language code");

    foreach (var option in L.Languages)
    {
        True(L.Tables.ContainsKey(option.Code), $"{option.Code} has no table");
        Equal(option.Code, L.Normalize(option.Code), $"{option.Code} must normalize to itself");
    }
});

Test("Localization: the added languages really translate", () =>
{
    try
    {
        foreach (var code in new[]
                 {
                     L.Japanese, L.Korean, L.Chinese, L.Spanish,
                     L.French, L.German, L.Russian, L.Portuguese
                 })
        {
            L.Use(code);
            Equal(code, L.Language);

            // A table copied straight from English would still pass the key test, so demand that the bulk
            // of it actually differs. Short words like "Layout" or "System" legitimately stay the same.
            var translated = L.Tables[code].Count(kv => kv.Value != L.EnglishTable[kv.Key]);
            True(translated > L.EnglishTable.Count * 8 / 10, $"{code}: only {translated} strings differ from English");

            True(L.F("ocr.done", 2).Contains('2'), $"{code} must keep placeholders");
        }
    }
    finally
    {
        L.Use(L.Default);
    }
});

Test("Localization: switching language changes lookups", () =>
{
    try
    {
        L.Use("vi");
        Equal("vi", L.Language);
        Equal("Đóng", L.T("common.close"));

        L.Use("EN");                                        // case-insensitive
        Equal("en", L.Language);
        Equal("Close", L.T("common.close"));
        Equal("Yuuka: done in 2s.", L.F("ocr.done", 2));    // placeholders still format

        L.Use("klingon");                                   // unknown code falls back to the default
        Equal("en", L.Language);
        Equal("no.such.key", L.T("no.such.key"), "unknown key falls back to the key itself");
    }
    finally
    {
        L.Use(L.Default);
    }
});

Test("AppSettings: language is normalized on load", () =>
{
    var settings = new AppSettings { Language = "EN" };
    settings.Normalize();
    Equal("en", settings.Language);

    settings.Language = "klingon";
    settings.Normalize();
    Equal("en", settings.Language);
});

// ------------------------------------------------------------------------ Theme
Test("ThemePresets: ids are unique and every one resolves", () =>
{
    var ids = ThemePresets.All.Select(p => p.Id).ToList();
    Equal(ids.Count, ids.Distinct().Count(), "duplicate preset id");
    True(ids.Contains(ThemePresets.DefaultId), "default preset must exist");

    foreach (var preset in ThemePresets.All)
    {
        Equal(preset.Id, ThemePresets.Get(preset.Id).Id, "Get must round-trip");
        Equal(preset.Id, ThemePresets.Normalize(preset.Id), "Normalize must round-trip");
        True(L.T(preset.NameKey) != preset.NameKey, $"{preset.Id} has no name in the active table");
    }
});

Test("ThemePresets: unknown id falls back to the default", () =>
{
    Equal(ThemePresets.DefaultId, ThemePresets.Normalize("klingon"));
    Equal(ThemePresets.DefaultId, ThemePresets.Normalize(null));
    Equal(ThemePresets.DefaultId, ThemePresets.Normalize(""));
    Equal(ThemePresets.DefaultId, ThemePresets.Get("klingon").Id);

    // Case-insensitive, because a hand-edited settings.json is not a code path we control.
    Equal("midnight", ThemePresets.Normalize("MIDNIGHT"));
});

Test("ThemePresets: every palette stays readable", () =>
{
    static double Channel(byte value)
    {
        var srgb = value / 255.0;
        return srgb <= 0.03928 ? srgb / 12.92 : Math.Pow((srgb + 0.055) / 1.055, 2.4);
    }

    static double Luminance(Rgba c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    static double Contrast(Rgba a, Rgba b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    foreach (var preset in ThemePresets.All)
    {
        var palette = preset.Palette;

        True(Contrast(palette.Text, palette.Background) >= 4.5,
            $"{preset.Id}: body text {Contrast(palette.Text, palette.Background):0.0}:1");
        True(Contrast(palette.BarText, palette.Bar) >= 4.5,
            $"{preset.Id}: bar text {Contrast(palette.BarText, palette.Bar):0.0}:1");
        True(Contrast(palette.OnAccent, palette.Accent) >= 4.5,
            $"{preset.Id}: on-accent {Contrast(palette.OnAccent, palette.Accent):0.0}:1");

        // Panel/hover/border must actually separate from the background, otherwise controls vanish.
        True(palette.Panel != palette.Background, $"{preset.Id}: panel equals background");
        True(palette.Border != palette.Background, $"{preset.Id}: border equals background");
    }
});

Test("ThemeSettings: normalize clamps preset and opacities", () =>
{
    var theme = new ThemeSettings
    {
        Preset = "klingon",
        FrameOpacity = 0,
        BackgroundOpacity = 400,
        FontFamily = "  ",
        FontSize = 99
    };

    theme.Normalize();

    Equal(ThemePresets.DefaultId, theme.Preset);
    Equal(ThemeSettings.MinOpacity, theme.FrameOpacity);
    Equal(100, theme.BackgroundOpacity);
    Equal("Segoe UI", theme.FontFamily);
    Equal(SliderSpecs.Get(SliderSpecs.FontSize).Max, theme.FontSize, "font size snaps to the top tick");
});

Test("ThemeSettings: opacity maps to alpha and the bar keeps its hue", () =>
{
    var theme = new ThemeSettings { Preset = "ocean", FrameOpacity = 100, BackgroundOpacity = 50 };
    var opaqueBar = theme.Bar;
    Equal((byte)255, opaqueBar.A, "100% must be fully opaque");

    theme.FrameOpacity = 40;
    Equal(theme.Bar with { A = 102 }, theme.Bar, "40% of 255 rounds to 102");
    Equal(opaqueBar.R, theme.Bar.R, "frame opacity must not shift the hue");

    Equal((byte)128, theme.BackgroundAlpha, "50% of 255 rounds to 128");
    Equal(theme.Palette.Background.R, theme.Background.R, "background keeps the preset colour");
});

Test("ThemeSettings: clone is a deep copy", () =>
{
    var theme = new ThemeSettings { Preset = "forest", FrameOpacity = 70 };
    var copy = theme.Clone();

    copy.Preset = "sunset";
    Equal("forest", theme.Preset, "clone must not alias the original");

    var normalized = new ThemeSettings { FrameOpacity = 70 };
    normalized.Normalize();
    Equal(70, normalized.FrameOpacity, "a valid value survives normalization");
});

// ------------------------------------------------------------------ SliderSpecs
Test("SliderSpecs: every default sits on its own tick", () =>
{
    foreach (var spec in SliderSpecs.All)
    {
        True(spec.Default >= spec.Min && spec.Default <= spec.Max, $"{spec.Key}: default outside the range");
        True(spec.IsOnGrid(spec.Default), $"{spec.Key}: default {spec.Default} is not on a tick");
    }
});

Test("SliderSpecs: snapping lands on the nearest tick", () =>
{
    var font = SliderSpecs.Get(SliderSpecs.FontSize);
    Equal(12.0, font.Snap(11.4), "rounds down");
    Equal(16.0, font.Snap(14.5), "rounds up");
    Equal(8.0, font.Snap(-5), "clamps below the range");
    Equal(32.0, font.Snap(400), "clamps above the range");

    var temperature = SliderSpecs.Get(SliderSpecs.AgentTemperature);
    Equal(0.75, temperature.Snap(0.68), "quarter steps");
    // The label follows the current culture (a Vietnamese or German UI writes 0,75), so compare numerically.
    Equal(0.75, double.Parse(temperature.Format(0.75), System.Globalization.CultureInfo.CurrentCulture),
        "two decimals");
});

Test("SliderSpecs: no two sliders share a key", () =>
{
    var keys = SliderSpecs.All.Select(spec => spec.Key).ToList();
    Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count(), "duplicate slider key");
});

// --------------------------------------------------------------- Agent actions
Test("AgentActions: defaults are valid and the table normalizes", () =>
{
    var settings = new AppSettings();

    // Twelve actions, two of them sharing an id: Normalize has to cap the list and repair the ids.
    for (var i = 0; i < 12; i++)
    {
        settings.Actions.Add(new AgentAction
        {
            Id = i < 2 ? "act-dup" : AgentActions.NewId(),
            Name = $"Action {i}",
            Icon = "no-such-icon",
            Instruction = "do something"
        });
    }

    settings.Normalize();

    Equal(AgentActions.Max, settings.Actions.Count, "capped at the maximum");
    Equal(settings.Actions.Count, settings.Actions.Select(a => a.Id).Distinct().Count(), "ids must be unique");
    True(settings.Actions.All(a => AgentActions.Icons.Contains(a.Icon)), "unknown icons fall back");
});

Test("AgentPrompts: an action turn carries the instruction and the context", () =>
{
    var text = AgentPrompts.ComposeAction("Summarize this", "OCR result", "hello world");

    True(text.Contains("Summarize this"), "instruction is sent");
    True(text.Contains("OCR result"), "the context label is sent");
    True(text.Contains("hello world"), "the context itself is sent");
});

Test("AgentPrompts: history trimming respects the token budget", () =>
{
    var history = new List<ChatTurn>();
    for (var i = 0; i < 40; i++)
        history.Add(new ChatTurn(i % 2 == 0 ? AgentPrompts.UserRole : AgentPrompts.AssistantRole,
            new string('x', 400), $"00:00:{i:00}"));

    var trimmed = AgentPrompts.TrimHistory(history, 600);

    True(trimmed.Count > 0, "the newest turn is always kept");
    True(trimmed.Sum(turn => AgentPrompts.TokensOf(turn.Text)) <= 700, "budget is respected");
    Equal(history[^1].Text, trimmed[^1].Text, "the newest turn survives");
});

// ---------------------------------------------------------------------- run all
var failed = 0;
foreach (var (name, body) in tests)
{
    try
    {
        body();
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL  {name} — {ex.Message}");
    }
}

Console.WriteLine();
Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed.");
return failed == 0 ? 0 : 1;
