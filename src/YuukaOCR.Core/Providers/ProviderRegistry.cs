namespace YuukaOCR.Core.Providers;

/// <summary>
/// The provider table. Curated for 2026: first-party labs (OpenAI, Anthropic, Google, xAI,
/// DeepSeek, Alibaba, Moonshot, Z.ai, Mistral), aggregators/inference hosts (OpenRouter, Groq,
/// Cerebras, Together, Fireworks, NVIDIA NIM, GitHub Models, HuggingFace) and local servers
/// (Ollama, LM Studio). Default models are only hints; the real list comes from /models.
/// The last field of each row is a translation key, not text — see <see cref="ProviderInfo.Note"/>.
/// </summary>
public static class ProviderRegistry
{
    public static readonly IReadOnlyList<ProviderInfo> All =
    [
        new("gemini", "Google Gemini", "https://generativelanguage.googleapis.com/v1beta",
            ProviderKind.Gemini, "GEMINI_API_KEY", "https://aistudio.google.com/apikey",
            true, true, "gemini-2.5-flash", ["gemini-2.5-flash", "gemini-2.5-pro"],
            "note.gemini"),

        new("openai", "OpenAI", "https://api.openai.com/v1",
            ProviderKind.OpenAiCompatible, "OPENAI_API_KEY", "https://platform.openai.com/api-keys",
            true, true, "gpt-4o-mini", ["gpt-4o-mini", "gpt-4.1-mini", "gpt-4o"]),

        new("anthropic", "Anthropic Claude", "https://api.anthropic.com/v1",
            ProviderKind.Anthropic, "ANTHROPIC_API_KEY", "https://console.anthropic.com/settings/keys",
            true, true, "claude-sonnet-5-5", ["claude-sonnet-5-5", "claude-haiku-4-5", "claude-opus-5-5"],
            "note.anthropic"),

        new("xai", "xAI Grok", "https://api.x.ai/v1",
            ProviderKind.OpenAiCompatible, "XAI_API_KEY", "https://console.x.ai/",
            true, true, "grok-4", ["grok-4", "grok-3-mini"]),

        new("deepseek", "DeepSeek", "https://api.deepseek.com/v1",
            ProviderKind.OpenAiCompatible, "DEEPSEEK_API_KEY", "https://platform.deepseek.com/api_keys",
            false, true, "deepseek-chat", ["deepseek-chat", "deepseek-reasoner"],
            "note.deepseek"),

        new("qwen", "Alibaba Qwen (DashScope)", "https://dashscope-intl.aliyuncs.com/compatible-mode/v1",
            ProviderKind.OpenAiCompatible, "DASHSCOPE_API_KEY", "https://bailian.console.alibabacloud.com/",
            true, true, "qwen-vl-max", ["qwen-vl-max", "qwen-vl-plus", "qwen3-max"],
            "note.qwen"),

        new("moonshot", "Moonshot Kimi", "https://api.moonshot.ai/v1",
            ProviderKind.OpenAiCompatible, "MOONSHOT_API_KEY", "https://platform.moonshot.ai/console/api-keys",
            true, true, "kimi-k2.5", ["kimi-k2.5", "kimi-k3"]),

        new("zhipu", "Z.ai / GLM", "https://api.z.ai/api/paas/v4",
            ProviderKind.OpenAiCompatible, "ZAI_API_KEY", "https://z.ai/manage-apikey/apikey-list",
            true, true, "glm-4.6v", ["glm-4.6v", "glm-4.7", "glm-4.5v"]),

        new("mistral", "Mistral AI", "https://api.mistral.ai/v1",
            ProviderKind.OpenAiCompatible, "MISTRAL_API_KEY", "https://console.mistral.ai/api-keys",
            true, true, "pixtral-12b-latest", ["pixtral-12b-latest", "pixtral-large-latest"]),

        new("groq", "Groq", "https://api.groq.com/openai/v1",
            ProviderKind.OpenAiCompatible, "GROQ_API_KEY", "https://console.groq.com/keys",
            true, true, "meta-llama/llama-4-scout-17b-16e-instruct",
            ["meta-llama/llama-4-scout-17b-16e-instruct", "openai/gpt-oss-120b"]),

        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1",
            ProviderKind.OpenAiCompatible, "OPENROUTER_API_KEY", "https://openrouter.ai/keys",
            true, true, "google/gemini-2.5-flash",
            ["google/gemini-2.5-flash", "anthropic/claude-sonnet-4.5", "openai/gpt-4o-mini"],
            "note.openrouter", "/key"),

        new("cerebras", "Cerebras", "https://api.cerebras.ai/v1",
            ProviderKind.OpenAiCompatible, "CEREBRAS_API_KEY", "https://cloud.cerebras.ai/",
            false, true, "llama-3.3-70b", ["llama-3.3-70b"],
            "note.cerebras"),

        new("together", "Together AI", "https://api.together.xyz/v1",
            ProviderKind.OpenAiCompatible, "TOGETHER_API_KEY", "https://api.together.ai/settings/api-keys",
            true, true, "meta-llama/Llama-4-Scout-17B-16E-Instruct",
            ["meta-llama/Llama-4-Scout-17B-16E-Instruct", "Qwen/Qwen2.5-VL-72B-Instruct"]),

        new("fireworks", "Fireworks AI", "https://api.fireworks.ai/inference/v1",
            ProviderKind.OpenAiCompatible, "FIREWORKS_API_KEY", "https://fireworks.ai/account/api-keys",
            true, true, "accounts/fireworks/models/qwen2p5-vl-32b-instruct",
            ["accounts/fireworks/models/qwen2p5-vl-32b-instruct"]),

        new("nvidia", "NVIDIA NIM", "https://integrate.api.nvidia.com/v1",
            ProviderKind.OpenAiCompatible, "NVIDIA_API_KEY", "https://build.nvidia.com/",
            true, true, "meta/llama-4-maverick-17b-128e-instruct",
            ["meta/llama-4-maverick-17b-128e-instruct", "microsoft/phi-4-multimodal-instruct"]),

        new("github", "GitHub Models", "https://models.inference.ai.azure.com",
            ProviderKind.OpenAiCompatible, "GITHUB_API_KEY", "https://github.com/settings/tokens",
            true, true, "gpt-4o-mini", ["gpt-4o-mini", "gpt-4o"],
            "note.github"),

        new("huggingface", "HuggingFace Inference", "https://api-inference.huggingface.co/v1",
            ProviderKind.OpenAiCompatible, "HUGGINGFACE_API_KEY", "https://huggingface.co/settings/tokens",
            true, true, "Qwen/Qwen2.5-VL-72B-Instruct",
            ["Qwen/Qwen2.5-VL-72B-Instruct"]),

        new("ollama", "Ollama (local)", "http://localhost:11434/v1",
            ProviderKind.OpenAiCompatible, "OLLAMA_API_KEY", "https://ollama.com/download",
            true, false, "qwen2.5vl:7b", ["qwen2.5vl:7b", "llama3.2-vision:11b", "gemma3:12b"],
            "note.ollama"),

        new("lmstudio", "LM Studio (local)", "http://localhost:1234/v1",
            ProviderKind.OpenAiCompatible, "LMSTUDIO_API_KEY", "https://lmstudio.ai/",
            true, false, "local-model", ["local-model"],
            "note.lmstudio"),

        new("custom", "Custom (OpenAI-compatible)", "",
            ProviderKind.OpenAiCompatible, "CUSTOM_API_KEY", "",
            true, true, "", [], "note.custom")
    ];

    private static readonly Dictionary<string, ProviderInfo> ById =
        All.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

    public static ProviderInfo Get(string? id) =>
        id is not null && ById.TryGetValue(id, out var info) ? info : ById["gemini"];

    public static bool Exists(string? id) => id is not null && ById.ContainsKey(id);

    public static IReadOnlyList<ProviderInfo> VisionProviders =>
        All.Where(p => p.SupportsVision).ToList();

    /// <summary>Best-effort provider guess from a key prefix; used by the key editor.</summary>
    public static string? DetectFromKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        return key switch
        {
            _ when key.StartsWith("sk-ant-", StringComparison.Ordinal) => "anthropic",
            _ when key.StartsWith("sk-or-", StringComparison.Ordinal) => "openrouter",
            _ when key.StartsWith("sk-", StringComparison.Ordinal) => "openai",
            _ when key.StartsWith("gsk_", StringComparison.Ordinal) => "groq",
            _ when key.StartsWith("xai-", StringComparison.Ordinal) => "xai",
            _ when key.StartsWith("csk-", StringComparison.Ordinal) => "cerebras",
            _ when key.StartsWith("nvapi-", StringComparison.Ordinal) => "nvidia",
            _ when key.StartsWith("hf_", StringComparison.Ordinal) => "huggingface",
            _ when key.StartsWith("ghp_", StringComparison.Ordinal) => "github",
            _ when key.StartsWith("github_pat_", StringComparison.Ordinal) => "github",
            _ when key.StartsWith("AIza", StringComparison.Ordinal) => "gemini",
            _ => null
        };
    }
}
