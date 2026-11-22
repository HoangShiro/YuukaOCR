namespace YuukaOCR.Core.Providers;

public static class ProviderFactory
{
    public static ILlmProvider Create(string providerId, string apiKey, string customBaseUrl, HttpClient http)
        => Create(ProviderRegistry.Get(providerId), apiKey, customBaseUrl, http);

    public static ILlmProvider Create(ProviderInfo info, string apiKey, string customBaseUrl, HttpClient http)
    {
        var baseUrl = info.Id == "custom" ? customBaseUrl : info.BaseUrl;
        return info.Kind switch
        {
            ProviderKind.Gemini => new GeminiProvider(info, apiKey, baseUrl, http),
            ProviderKind.Anthropic => new AnthropicProvider(info, apiKey, baseUrl, http),
            _ => new OpenAiCompatibleProvider(info, apiKey, baseUrl, http)
        };
    }
}
