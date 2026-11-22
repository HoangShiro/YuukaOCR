namespace YuukaOCR.Core.Providers;

/// <summary>
/// Sorts a provider's model list so the useful OCR candidates float to the top:
/// OpenRouter presets first, then vision + free, then free, then vision, then everything else,
/// with popular families first.
/// </summary>
public static class ModelRanking
{
    /// <summary>
    /// Prefix of an OpenRouter preset ("@preset/tools"). A preset is a saved configuration the user
    /// names on openrouter.ai and addresses as a model id; it never appears in /models, so it is the
    /// one group that must always be pinned to the top of the list instead of being ranked by name.
    /// </summary>
    public const string PresetPrefix = "@preset/";

    private static readonly string[] VisionKeywords =
    [
        "vision", "vl", "pixtral", "llava", "qwen-vl", "qwen2-vl", "qwen2.5-vl", "qwen3-vl",
        "gpt-4o", "gpt-4.1", "gpt-5", "claude", "gemini", "gemma", "omni",
        "llama-4", "llama-3.2-11b-vision", "llama-3.2-90b-vision",
        "phi-4-multimodal", "moondream", "minicpm-v", "internvl", "aya-vision", "glm-4", "kimi"
    ];

    private static readonly string[] PriorityFamilies =
    [
        "gemini", "gpt-5", "gpt-4o", "claude", "grok", "qwen", "glm", "kimi",
        "pixtral", "llama-4", "gemma", "mistral", "deepseek", "phi"
    ];

    /// <summary>True for a user-defined OpenRouter preset ("@preset/tools").</summary>
    public static bool IsPreset(string model) =>
        model.StartsWith(PresetPrefix, StringComparison.OrdinalIgnoreCase);

    public static bool LooksLikeVision(string model)
    {
        var lower = model.ToLowerInvariant();
        return VisionKeywords.Any(k => lower.Contains(k, StringComparison.Ordinal));
    }

    public static bool LooksFree(string model) => model.Contains(":free", StringComparison.OrdinalIgnoreCase);

    public static List<string> Rank(IEnumerable<string> models)
    {
        var unique = models
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return unique
            .OrderBy(m => IsPreset(m) ? 0 : 1)     // presets first, whatever they are called
            .ThenBy(ModelTier)
            .ThenBy(FamilyPriority)
            .ThenBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int ModelTier(string model)
    {
        var vision = LooksLikeVision(model);
        var free = LooksFree(model);
        return (vision, free) switch
        {
            (true, true) => 0,
            (false, true) => 1,
            (true, false) => 2,
            _ => 3
        };
    }

    private static int FamilyPriority(string model)
    {
        var lower = model.ToLowerInvariant();
        for (var i = 0; i < PriorityFamilies.Length; i++)
        {
            if (lower.Contains(PriorityFamilies[i], StringComparison.Ordinal)) return i;
        }
        return PriorityFamilies.Length;
    }
}
