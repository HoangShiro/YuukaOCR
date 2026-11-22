using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using YuukaOCR.Core.Localization;

namespace YuukaOCR.Core.Providers;

/// <summary>
/// Shared HTTP plumbing. Every request carries its own key (header or query string), so rotating
/// API keys can never affect a request that is already in flight — the bug the Python version had
/// with google.generativeai's global configure().
/// </summary>
public abstract class HttpProviderBase : ILlmProvider
{
    protected readonly HttpClient Http;
    protected readonly string ApiKey;
    protected readonly string BaseUrl;

    public ProviderInfo Info { get; }

    protected HttpProviderBase(ProviderInfo info, string apiKey, string baseUrl, HttpClient http)
    {
        Info = info;
        ApiKey = apiKey ?? "";
        BaseUrl = (baseUrl ?? info.BaseUrl).TrimEnd('/');
        Http = http;
    }

    public abstract Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken);
    public abstract Task<ModelListResult> ListModelsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Default check: the model list, which most providers only serve to an authenticated caller.
    /// <see cref="ProviderInfo.AuthCheckPath"/> covers the exception (a public model list), so
    /// "Add" in the System tab can never report a wrong key as valid.
    /// </summary>
    public virtual async Task<ModelListResult> ValidateKeyAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(ApiKey)) return ModelListResult.Fail(L.T("ocr.verify.noKey"));

        if (Info.AuthCheckPath.Length == 0)
            return await ListModelsAsync(cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + Info.AuthCheckPath);
        AddAuth(request);
        AddJsonAccept(request);

        var (status, body) = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return (int)status is >= 200 and < 300
            ? ModelListResult.Ok([], (int)status)
            : ModelListResult.Fail(ExtractError(body, (int)status), (int)status);
    }

    protected static StringContent JsonContent(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    protected async Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (response.StatusCode, body);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (HttpStatusCode.RequestTimeout, L.T("http.timeout"));
        }
        catch (HttpRequestException ex)
        {
            return (HttpStatusCode.BadGateway, L.F("http.connectError", ex.Message));
        }
    }

    /// <summary>Pulls a human-readable message out of the many error shapes providers use.</summary>
    protected static string ExtractError(string body, int status)
    {
        if (string.IsNullOrWhiteSpace(body)) return $"HTTP {status}";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) return error.GetString() ?? body;
                if (error.ValueKind == JsonValueKind.Object)
                {
                    foreach (var key in new[] { "message", "msg", "detail", "code" })
                    {
                        if (error.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                            return $"{v.GetString()}";
                    }
                }
            }
            foreach (var key in new[] { "message", "detail", "error_description" })
            {
                if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
            // Not JSON (HTML error page, plain text): fall through.
        }
        return Truncate(body, 200);
    }

    protected static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var flat = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "...";
    }

    protected static string FriendlyStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => L.T("http.401"),
        HttpStatusCode.Forbidden => L.T("http.403"),
        HttpStatusCode.NotFound => L.T("http.404"),
        HttpStatusCode.TooManyRequests => L.T("http.429"),
        HttpStatusCode.RequestEntityTooLarge => L.T("http.413"),
        _ => ""
    };

    protected void AddAuth(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(ApiKey))
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {ApiKey}");
    }

    protected static void AddJsonAccept(HttpRequestMessage request)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    protected static string Base64(byte[] data) => Convert.ToBase64String(data);
}
