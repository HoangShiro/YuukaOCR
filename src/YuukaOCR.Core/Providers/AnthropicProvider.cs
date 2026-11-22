using System.Net;
using System.Text.Json;
using YuukaOCR.Core.Localization;
using YuukaOCR.Core.Logging;

namespace YuukaOCR.Core.Providers;

/// <summary>Anthropic Messages API (x-api-key + anthropic-version).</summary>
public sealed class AnthropicProvider : HttpProviderBase
{
    private const string ApiVersion = "2026-10-21";
    private const int MaxTokens = 4096;

    public AnthropicProvider(ProviderInfo info, string apiKey, string baseUrl, HttpClient http)
        : base(info, apiKey, baseUrl, http)
    {
    }

    private void AddAnthropicHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("x-api-key", ApiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        AddJsonAccept(request);
    }

    public override async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ApiKey)) return LlmResponse.Fail(L.F("provider.noKey", "Anthropic"));
        if (string.IsNullOrWhiteSpace(request.Model)) return LlmResponse.Fail(L.T("provider.noModel"));

        var content = new List<object>();

        if (request.HasMedia)
        {
            var mime = request.MediaMime!;
            if (mime.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
            {
                content.Add(new Dictionary<string, object?>
                {
                    ["type"] = "document",
                    ["source"] = new Dictionary<string, object?>
                    {
                        ["type"] = "base64",
                        ["media_type"] = "application/pdf",
                        ["data"] = Base64(request.MediaBytes!)
                    }
                });
            }
            else if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                content.Add(new Dictionary<string, object?>
                {
                    ["type"] = "image",
                    ["source"] = new Dictionary<string, object?>
                    {
                        ["type"] = "base64",
                        ["media_type"] = mime,
                        ["data"] = Base64(request.MediaBytes!)
                    }
                });
            }
            else
            {
                return LlmResponse.Fail(L.F("provider.anthropic.media", mime));
            }
        }

        var prompt = request.Prompt;
        if (!string.IsNullOrWhiteSpace(request.InlineText))
            prompt += $"\n\nFile content ({request.FileName ?? "file"}):\n{request.InlineText}";
        content.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = prompt });

        var messages = new List<object>();

        // Chat: the earlier turns, then the new user turn. Anthropic takes the system prompt as a
        // top-level field, not as a message.
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
            ["max_tokens"] = MaxTokens,
            ["messages"] = messages
        };

        if (request.HasSystemPrompt) payload["system"] = request.SystemPrompt;
        if (!request.JsonMode) payload["temperature"] = Math.Round(request.Temperature, 3);

        ApiTrace.Write("anthropic.request", new { model = request.Model, url = $"{BaseUrl}/messages", payload });
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/messages")
        {
            Content = JsonContent(payload)
        };
        AddAnthropicHeaders(httpRequest);

        var (status, body) = await SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        ApiTrace.Write($"anthropic.response {(int)status}", body);
        if (status != HttpStatusCode.OK)
        {
            var friendly = FriendlyStatus(status);
            var detail = ExtractError(body, (int)status);
            return LlmResponse.Fail(friendly.Length > 0 ? $"{friendly} {detail}" : $"API Error {(int)status}: {detail}", (int)status);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("content", out var blocks) ||
                blocks.ValueKind != JsonValueKind.Array)
            {
                return LlmResponse.Fail(L.T("provider.noText"), (int)status);
            }

            var text = string.Concat(blocks.EnumerateArray()
                .Where(block => block.TryGetProperty("type", out var type) &&
                                type.ValueKind == JsonValueKind.String && type.GetString() == "text")
                .Select(block => block.TryGetProperty("text", out var t) ? t.GetString() : null));

            return text.Length > 0
                ? LlmResponse.Ok(text, (int)status)
                : LlmResponse.Fail(L.T("provider.noText"), (int)status);
        }
        catch (JsonException ex)
        {
            return LlmResponse.Fail(L.F("provider.badJson", ex.Message), (int)status);
        }
    }

    public override async Task<ModelListResult> ListModelsAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ApiKey)) return ModelListResult.Fail(L.F("provider.noKey", "Anthropic"));

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/models?limit=1000");
        AddAnthropicHeaders(request);

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
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        models.Add(id.GetString()!);
                }
            }
        }
        catch (JsonException)
        {
            return ModelListResult.Fail(L.T("provider.noModels"));
        }

        if (models.Count == 0) return ModelListResult.Fail(L.F("provider.noModelsFound", "Claude"));
        return ModelListResult.Ok(ModelRanking.Rank(models));
    }
}
