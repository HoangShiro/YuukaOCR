using System.Net;
using System.Text.Json;
using YuukaOCR.Core.Localization;
using YuukaOCR.Core.Logging;

namespace YuukaOCR.Core.Providers;

/// <summary>OpenAI chat/completions wire format. Used by 16 of the 20 providers.</summary>
public sealed class OpenAiCompatibleProvider : HttpProviderBase
{
    public OpenAiCompatibleProvider(ProviderInfo info, string apiKey, string baseUrl, HttpClient http)
        : base(info, apiKey, baseUrl, http)
    {
    }

    public override async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
            return LlmResponse.Fail(L.T("provider.noBaseUrl"));
        if (string.IsNullOrWhiteSpace(request.Model))
            return LlmResponse.Fail(L.T("provider.noModel"));

        var prompt = request.Prompt;
        if (!string.IsNullOrWhiteSpace(request.InlineText))
            prompt += $"\n\nFile content ({request.FileName ?? "file"}):\n{request.InlineText}";

        var content = new List<object>
        {
            new Dictionary<string, object?> { ["type"] = "text", ["text"] = prompt }
        };

        if (request.HasMedia)
        {
            var mime = request.MediaMime!;
            if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return LlmResponse.Fail(L.F("provider.openai.media", Info.DisplayName));
            }

            content.Add(new Dictionary<string, object?>
            {
                ["type"] = "image_url",
                ["image_url"] = new Dictionary<string, object?>
                {
                    ["url"] = $"data:{mime};base64,{Base64(request.MediaBytes!)}"
                }
            });
        }

        var messages = new List<object>();

        // Chat: system prompt first, then the earlier turns, then the new user turn. The OCR pipeline
        // sends no history and no system prompt, so its payload is unchanged.
        if (request.HasSystemPrompt)
            messages.Add(new Dictionary<string, object?> { ["role"] = "system", ["content"] = request.SystemPrompt });

        if (request.History is { Count: > 0 })
        {
            foreach (var turn in request.History)
            {
                if (string.IsNullOrWhiteSpace(turn.Text)) continue;
                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = turn.Role == AgentRoles.Assistant ? "assistant" : "user",
                    ["content"] = turn.Text
                });
            }
        }

        messages.Add(new Dictionary<string, object?> { ["role"] = "user", ["content"] = content });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["max_tokens"] = 4096,
            ["stream"] = false
        };

        if (request.JsonMode)
        {
            // JSON mode is only requested for text-only calls: several hosts reject it alongside images.
            if (!request.HasMedia)
                payload["response_format"] = new Dictionary<string, object?> { ["type"] = "json_object" };
        }
        else
        {
            payload["temperature"] = Math.Round(request.Temperature, 3);
        }

        ApiTrace.Write("openai.request", new { model = request.Model, url = $"{BaseUrl}/chat/completions", payload });
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/chat/completions")
        {
            Content = JsonContent(payload)
        };
        AddAuth(httpRequest);
        AddJsonAccept(httpRequest);

        var (status, body) = await SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        ApiTrace.Write($"openai.response {(int)status}", body);
        if (status != HttpStatusCode.OK)
        {
            var friendly = FriendlyStatus(status);
            var detail = ExtractError(body, (int)status);
            return LlmResponse.Fail(friendly.Length > 0 ? $"{friendly} {detail}" : $"API Error {(int)status}: {detail}", (int)status);
        }

        return ParseCompletion(body, (int)status);
    }

    private static LlmResponse ParseCompletion(string body, int status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return LlmResponse.Fail(L.T("provider.openai.noResponse"), status);
            }

            var choice = choices[0];
            if (choice.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String)
                    return LlmResponse.Ok(content.GetString() ?? "", status);

                if (content.ValueKind == JsonValueKind.Array)
                {
                    var text = string.Concat(content.EnumerateArray()
                        .Where(part => part.TryGetProperty("text", out _))
                        .Select(part => part.GetProperty("text").GetString()));
                    if (text.Length > 0) return LlmResponse.Ok(text, status);
                }
            }

            if (choice.TryGetProperty("text", out var legacy) && legacy.ValueKind == JsonValueKind.String)
                return LlmResponse.Ok(legacy.GetString() ?? "", status);

            return LlmResponse.Fail(L.T("provider.noText"), status);
        }
        catch (JsonException ex)
        {
            return LlmResponse.Fail(L.F("provider.badJson", ex.Message), status);
        }
    }

    public override async Task<ModelListResult> ListModelsAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
            return ModelListResult.Fail(L.T("provider.noBaseUrl"));

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/models");
        AddAuth(request);
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
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    var id = item.ValueKind == JsonValueKind.String
                        ? item.GetString()
                        : item.TryGetProperty("id", out var idProp) ? idProp.GetString()
                        : item.TryGetProperty("name", out var nameProp) ? nameProp.GetString()
                        : null;
                    if (!string.IsNullOrWhiteSpace(id)) models.Add(id);
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    var id = item.ValueKind == JsonValueKind.String ? item.GetString()
                        : item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(id)) models.Add(id);
                }
            }
        }
        catch (JsonException)
        {
            return ModelListResult.Fail(L.T("provider.openai.noJson"));
        }

        if (models.Count == 0) return ModelListResult.Fail(L.T("provider.noModelsAny"));
        return ModelListResult.Ok(ModelRanking.Rank(models));
    }
}
