using YuukaOCR.Core.Localization;

namespace YuukaOCR.Core.Providers;

/// <summary>Role names used by every provider when a request carries conversation history.</summary>
public static class AgentRoles
{
    public const string User = "user";
    public const string Assistant = "assistant";
}

/// <summary>
/// One earlier turn of a conversation. <see cref="Role"/> is <c>user</c> or <c>assistant</c>; the
/// providers map it onto their own shape (<c>user</c>/<c>model</c> for Gemini, for example).
/// </summary>
public sealed record LlmTurn(string Role, string Text);

/// <summary>
/// One generation request. Media is either an image, a PDF or audio (sent as binary to providers
/// that accept it) or plain text (inlined into the prompt).
///
/// <see cref="History"/> + <see cref="SystemPrompt"/> turn the request into a multi-turn chat; the OCR
/// pipeline leaves both empty and keeps <see cref="JsonMode"/> on, which is what it has always done.
/// </summary>
public sealed record LlmRequest(
    string Model,
    string Prompt,
    byte[]? MediaBytes = null,
    string? MediaMime = null,
    string? InlineText = null,
    string? FileName = null,
    IReadOnlyList<LlmTurn>? History = null,
    string? SystemPrompt = null,
    double Temperature = 0.2,
    bool JsonMode = true)
{
    public bool HasMedia => MediaBytes is { Length: > 0 } && !string.IsNullOrEmpty(MediaMime);

    public bool HasHistory => History is { Count: > 0 };

    public bool HasSystemPrompt => !string.IsNullOrWhiteSpace(SystemPrompt);
}

public sealed record LlmResponse(bool Success, string Text, string Error = "", int StatusCode = 0)
{
    public static LlmResponse Ok(string text, int status = 200) => new(true, text, "", status);
    public static LlmResponse Fail(string error, int status = 0) => new(false, "", error, status);
}

/// <summary>
/// Outcome of a key check or model-list fetch. <see cref="StatusCode"/> carries the HTTP status when
/// there was one (0 for local failures), so callers can tell "the key was rejected" apart from "the
/// network is down" without parsing the message.
/// </summary>
public sealed record ModelListResult(bool Success, string Message, List<string> Models, int StatusCode = 0)
{
    public static ModelListResult Ok(List<string> models, int status = 200) =>
        new(true, L.T("ocr.verify.ok"), models, status);

    public static ModelListResult Fail(string message, int status = 0) =>
        new(false, message, new List<string>(), status);

    /// <summary>
    /// True when the provider answered "this key is not allowed". Google reports a bad key as 400
    /// (INVALID_ARGUMENT) rather than 401, so that one is included; a timeout or a dead connection
    /// (408/502 sentinels from <c>SendAsync</c>) is not a key problem and must not be reported as one.
    /// </summary>
    public bool IsKeyRejected => StatusCode is 400 or 401 or 403;
}
