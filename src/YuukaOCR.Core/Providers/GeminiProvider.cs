using System.Net;
using System.Text.Json;
using YuukaOCR.Core.Localization;
using YuukaOCR.Core.Logging;

namespace YuukaOCR.Core.Providers;

/// <summary>
/// Native Gemini generateContent. Key travels in the query string, so parallel requests with
/// different keys from the pool cannot interfere with each other.
/// </summary>
public sealed class GeminiProvider : HttpProviderBase
{
    public GeminiProvider(ProviderInfo info, string apiKey, string baseUrl, HttpClient http)
        : base(info, apiKey, baseUrl, http)
    {
    }

    private static string NormalizeModel(string model) =>
        model.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? model["models/".Length..] : model;

    public override async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ApiKey)) return LlmResponse.Fail(L.F("provider.noKey", "Gemini"));
        if (string.IsNullOrWhiteSpace(request.Model)) return LlmResponse.Fail(L.T("provider.noModel"));

        var model = NormalizeModel(request.Model);
        var prompt = request.Prompt;
        if (!string.IsNullOrWhiteSpace(request.InlineText))
            prompt += $"\n\nFile content ({request.FileName ?? "file"}):\n{request.InlineText}";

        var parts = new List<object> { new Dictionary<string, object?> { ["text"] = prompt } };
        if (request.HasMedia)
        {
            parts.Add(new Dictionary<string, object?>
            {
                ["inline_data"] = new Dictionary<string, object?>
                {
                    ["mime_type"] = request.MediaMime,
                    ["data"] = Base64(request.MediaBytes!)
                }
            });
        }

        // A chat request sends the earlier turns ahead of the new one; Gemini calls the assistant role
        // "model" and keeps the system prompt in systemInstruction instead of a turn.
        var contents = new List<object>();
        if (request.History is { Count: > 0 })
        {
            foreach (var turn in request.History)
            {
                if (string.IsNullOrWhiteSpace(turn.Text)) continue;
                contents.Add(new Dictionary<string, object?>
                {
                    ["role"] = turn.Role == AgentRoles.Assistant ? "model" : "user",
                    ["parts"] = new object[] { new Dictionary<string, object?> { ["text"] = turn.Text } }
                });
            }
        }
        contents.Add(new Dictionary<string, object?> { ["role"] = "user", ["parts"] = parts });

        var generationConfig = new Dictionary<string, object?>
        {
            ["maxOutputTokens"] = 4096,
            ["temperature"] = Math.Round(request.Temperature, 3)
        };

        // The OCR pipeline asks for strict JSON; chat answers are prose and must not be wrapped in it.
        if (request.JsonMode) generationConfig["responseMimeType"] = "application/json";

        var payload = new Dictionary<string, object?>
        {
            ["contents"] = contents,
            ["generationConfig"] = generationConfig
        };

        if (request.HasSystemPrompt)
        {
            payload["systemInstruction"] = new Dictionary<string, object?>
            {
                ["parts"] = new object[] { new Dictionary<string, object?> { ["text"] = request.SystemPrompt } }
            };
        }

        var url = $"{BaseUrl}/models/{Uri.EscapeDataString(model)}:generateContent?key={Uri.EscapeDataString(ApiKey)}";
        ApiTrace.Write("gemini.request", new { url = $"…/{model}:generateContent", payload });
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent(payload) };
        AddJsonAccept(httpRequest);

        var (status, body) = await SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        ApiTrace.Write($"gemini.response {(int)status}", body);
        if (status != HttpStatusCode.OK)
        {
            var friendly = FriendlyStatus(status);
            var detail = ExtractError(body, (int)status);
            return LlmResponse.Fail(friendly.Length > 0 ? $"{friendly} {detail}" : $"API Error {(int)status}: {detail}", (int)status);
        }

        return ParseGenerate(body, (int)status);
    }

    private static LlmResponse ParseGenerate(string body, int status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("candidates", out var candidates) &&
                candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0)
            {
                var candidate = candidates[0];
                if (candidate.TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts) &&
                    parts.ValueKind == JsonValueKind.Array)
                {
                    var text = string.Concat(parts.EnumerateArray()
                        .Where(p => p.TryGetProperty("text", out _))
                        .Select(p => p.GetProperty("text").GetString()));
                    if (text.Length > 0) return LlmResponse.Ok(text, status);
                }

                if (candidate.TryGetProperty("finishReason", out var finish) &&
                    finish.ValueKind == JsonValueKind.String)
                {
                    return LlmResponse.Fail(L.F("provider.gemini.stopped", finish.GetString()), status);
                }
            }

            if (root.TryGetProperty("promptFeedback", out var feedback) &&
                feedback.TryGetProperty("blockReason", out var block))
            {
                return LlmResponse.Fail(L.F("provider.gemini.blocked", block.GetString()), status);
            }

            return LlmResponse.Fail(L.T("provider.noText"), status);
        }
        catch (JsonException ex)
        {
            return LlmResponse.Fail(L.F("provider.badJson", ex.Message), status);
        }
    }

    public override async Task<ModelListResult> ListModelsAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ApiKey)) return ModelListResult.Fail(L.F("provider.noKey", "Gemini"));

        var url = $"{BaseUrl}/models?pageSize=200&key={Uri.EscapeDataString(ApiKey)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddJsonAccept(request);

        var (status, body) = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (status != HttpStatusCode.OK)
        {
            var friendly = FriendlyStatus(status);
            return ModelListResult.Fail(friendly.Length > 0 ? friendly : $"HTTP {(int)status}: {ExtractError(body, (int)status)}", (int)status);
        }

        var models = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("models", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (!item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) continue;
                    var supportsGenerate = false;
                    if (item.TryGetProperty("supportedGenerationMethods", out var methods) &&
                        methods.ValueKind == JsonValueKind.Array)
                    {
                        supportsGenerate = methods.EnumerateArray()
                            .Any(m => m.ValueKind == JsonValueKind.String && m.GetString() == "generateContent");
                    }
                    if (!supportsGenerate) continue;
                    models.Add(NormalizeModel(name.GetString()!));
                }
            }
        }
        catch (JsonException)
        {
            return ModelListResult.Fail(L.T("provider.noModels"));
        }

        if (models.Count == 0) return ModelListResult.Fail(L.F("provider.noModelsFound", "Gemini"));
        return ModelListResult.Ok(ModelRanking.Rank(models));
    }
}
