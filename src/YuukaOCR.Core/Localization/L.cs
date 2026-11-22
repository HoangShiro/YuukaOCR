namespace YuukaOCR.Core.Localization;

/// <summary>
/// A selectable UI language: the code stored in settings.json plus its native name.
/// <see cref="ToString"/> returns the native name so a plain ComboBox renders it without a template —
/// the app's custom ComboBox style binds <c>SelectionBoxItem</c> and ignores DisplayMemberPath.
/// </summary>
public sealed record LanguageOption(string Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The one and only string table. Every user-visible string — bar status, settings labels, provider
/// errors, console lines — is looked up by key here, so the UI can be rebuilt in another language
/// without touching the views and without a satellite-assembly round trip.
///
/// Adding a language: add a <c>StringsXX.cs</c> dictionary, one entry in <see cref="Tables"/> and one
/// in <see cref="Languages"/>. Every table must hold exactly the English key set; the test runner fails
/// when they drift apart. Lookup falls back to English, then Vietnamese, then the key itself, so a
/// forgotten key can never render as a blank label.
/// </summary>
public static class L
{
    public const string Vietnamese = "vi";
    public const string English = "en";
    public const string Japanese = "ja";
    public const string Korean = "ko";
    public const string Chinese = "zh";
    public const string Spanish = "es";
    public const string French = "fr";
    public const string German = "de";
    public const string Russian = "ru";
    public const string Portuguese = "pt";

    /// <summary>Default language: English. Settings files that predate the switch keep their stored code.</summary>
    public const string Default = English;

    public static string Language { get; private set; } = Default;

    public static bool IsEnglish => Language == English;

    /// <summary>Every table, keyed by language code. English is the reference set.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Tables { get; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            [English] = StringsEn.Map,
            [Vietnamese] = StringsVi.Map,
            [Japanese] = StringsJa.Map,
            [Korean] = StringsKo.Map,
            [Chinese] = StringsZh.Map,
            [Spanish] = StringsEs.Map,
            [French] = StringsFr.Map,
            [German] = StringsDe.Map,
            [Russian] = StringsRu.Map,
            [Portuguese] = StringsPt.Map
        };

    /// <summary>Languages offered in Settings → Appearance, in menu order.</summary>
    public static IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new(English, "English"),
        new(Vietnamese, "Tiếng Việt"),
        new(Japanese, "日本語"),
        new(Korean, "한국어"),
        new(Chinese, "简体中文"),
        new(Spanish, "Español"),
        new(French, "Français"),
        new(German, "Deutsch"),
        new(Russian, "Русский"),
        new(Portuguese, "Português")
    ];

    /// <summary>Raised after <see cref="Language"/> actually changes. The UI rebuilds itself here.</summary>
    public static event EventHandler? Changed;

    /// <summary>Maps any stored value onto a known language code (unknown → <see cref="Default"/>).</summary>
    public static string Normalize(string? code)
    {
        foreach (var option in Languages)
            if (string.Equals(option.Code, code, StringComparison.OrdinalIgnoreCase))
                return option.Code;
        return Default;
    }

    public static void Use(string? code)
    {
        var next = Normalize(code);
        if (next == Language) return;
        Language = next;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Looks a key up in the active language, falling back to English, then Vietnamese, then the key
    /// itself. The fallback matters for a hand-edited settings.json that names a language whose table
    /// is not registered.
    /// </summary>
    public static string T(string key)
    {
        if (Tables.TryGetValue(Language, out var table) && table.TryGetValue(key, out var text)) return text;
        if (StringsEn.Map.TryGetValue(key, out var english)) return english;
        return StringsVi.Map.TryGetValue(key, out var vietnamese) ? vietnamese : key;
    }

    /// <summary>Same as <see cref="T"/> for strings with <c>{0}</c> placeholders.</summary>
    public static string F(string key, params object?[] args) => string.Format(T(key), args);

    /// <summary>Raw tables — used by the test runner to prove every language stays in sync.</summary>
    public static IReadOnlyDictionary<string, string> VietnameseTable => StringsVi.Map;
    public static IReadOnlyDictionary<string, string> EnglishTable => StringsEn.Map;
}
