using YuukaOCR.Core.Localization;

namespace YuukaOCR.Core.Providers;

public enum ProviderKind
{
    /// <summary>POST {base}/chat/completions with an image_url data URI (OpenAI wire format).</summary>
    OpenAiCompatible,

    /// <summary>POST {base}/models/{model}:generateContent with inline_data parts.</summary>
    Gemini,

    /// <summary>POST {base}/messages with image/document blocks (Anthropic Messages API).</summary>
    Anthropic
}

/// <param name="RequiresKey">False for local servers (Ollama, LM Studio) that run without auth.</param>
/// <param name="Note">
/// Translation key of the one-line hint shown under the provider combo (<c>note.gemini</c> …), not the
/// text itself: the table is built once at startup and must survive a language switch. Read
/// <see cref="NoteText"/> for display.
/// </param>
public sealed record ProviderInfo(
    string Id,
    string DisplayName,
    string BaseUrl,
    ProviderKind Kind,
    string EnvKey,
    string DocsUrl,
    bool SupportsVision,
    bool RequiresKey,
    string DefaultModel,
    string[] SuggestedModels,
    string Note = "",
    /// <summary>
    /// Path of a cheap authenticated endpoint (relative to <see cref="BaseUrl"/>) used to test a key.
    /// Only needed when the provider serves its model list without auth — OpenRouter's <c>/models</c>
    /// is public, so a bogus key would otherwise look valid.
    /// </summary>
    string AuthCheckPath = "")
{
    /// <summary>The note in the active language, or an empty string when the provider has none.</summary>
    public string NoteText => Note.Length == 0 ? "" : L.T(Note);
}
