using YuukaOCR.Core.Interop;
using YuukaOCR.Core.Localization;
using YuukaOCR.Core.Logging;
using YuukaOCR.Core.Providers;
using YuukaOCR.Core.Settings;

namespace YuukaOCR.Core.Ocr;

/// <summary>
/// The whole OCR pipeline: pick a provider + key, build the prompt, call the model off the UI
/// thread, parse the answer, copy it to the clipboard and record statistics.
/// </summary>
public sealed class OcrService : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly LogService _log;
    private readonly HttpClient _http;
    private readonly ApiKeyPool _pool = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OcrService(SettingsStore settings, LogService log, HttpClient? http = null)
    {
        _settings = settings;
        _log = log;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
    }

    /// <summary>Raised when a run starts (UI: show the "thinking" state).</summary>
    public event Action? ProcessingStarted;

    /// <summary>Raised when a run ends, success or not.</summary>
    public event Action? ProcessingFinished;

    /// <summary>message, duration in ms (0 = keep until replaced).</summary>
    public event Action<string, int>? StatusChanged;

    public event Action<string>? ResultReady;

    /// <summary>No API key configured for the current provider.</summary>
    public event Action<string>? ApiKeyMissing;

    public event Action<string, List<string>>? ModelsLoaded;

    /// <summary>Last text the app itself copied — used to ignore our own clipboard change.</summary>
    public string? LastResultText { get; private set; }

    public bool IsBusy => _gate.CurrentCount == 0;

    public string ProviderId => _settings.Settings.ApiProvider;

    /// <summary>
    /// Proves a single key works. Deliberately separate from <see cref="VerifyAsync"/>: that one
    /// fetches the model list (the *Load models* button needs it), which some providers serve without
    /// any auth — OpenRouter would happily return its catalogue for a garbage key.
    /// </summary>
    public async Task<ModelListResult> ValidateKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        var settings = _settings.Settings;
        var info = ProviderRegistry.Get(settings.ApiProvider);

        try
        {
            var provider = ProviderFactory.Create(info, apiKey ?? "", settings.CustomBaseUrl, _http);
            return await provider.ValidateKeyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ModelListResult.Fail(L.F("ocr.verify.error", ex.Message));
        }
    }

    public async Task<ModelListResult> VerifyAsync(string? apiKey = null, CancellationToken cancellationToken = default)
    {
        var settings = _settings.Settings;
        var info = ProviderRegistry.Get(settings.ApiProvider);
        var key = apiKey;

        if (string.IsNullOrWhiteSpace(key))
            key = ApiKeyPool.LoadFor(AppPaths.EnvFile, info.Id).FirstOrDefault();

        if (info.RequiresKey && string.IsNullOrWhiteSpace(key))
            return ModelListResult.Fail(L.T("ocr.verify.noKey"));

        try
        {
            var provider = ProviderFactory.Create(info, key ?? "", settings.CustomBaseUrl, _http);
            var result = await provider.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            if (result.Success) ModelsLoaded?.Invoke(info.Id, result.Models);
            return result;
        }
        catch (Exception ex)
        {
            return ModelListResult.Fail(L.F("ocr.verify.error", ex.Message));
        }
    }

    public async Task RunAsync(OcrRequest request, CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            StatusChanged?.Invoke(L.T("ocr.busy"), 2500);
            return;
        }

        var started = DateTime.UtcNow;
        ProcessingStarted?.Invoke();

        try
        {
            StatusChanged?.Invoke(L.T("ocr.wait"), 0);

            var settings = _settings.Settings;
            var info = ProviderRegistry.Get(settings.ApiProvider);
            var keys = ApiKeyPool.LoadFor(AppPaths.EnvFile, info.Id);

            string key;
            if (keys.Count == 0)
            {
                if (info.RequiresKey)
                {
                    ApiKeyMissing?.Invoke(info.Id);
                    StatusChanged?.Invoke(L.T("ocr.needKey"), 4000);
                    return;
                }

                key = ""; // local servers (Ollama, LM Studio, vLLM) run unauthenticated
            }
            else
            {
                key = _pool.Next(info.Id, keys);
            }

            var model = string.IsNullOrWhiteSpace(settings.Model) ? info.DefaultModel : settings.Model.Trim();
            var plan = PromptBuilder.Build(BasePromptFor(request), settings.PromptEnabled, settings.CustomPrompt);

            if (plan.HasCustom) _log.AddRecentPrompt(plan.CustomInstruction!);

            _log.LogSource(ToSourceKind(request.Source), request.FileName);

            var provider = ProviderFactory.Create(info, key, settings.CustomBaseUrl, _http);
            var response = await provider.GenerateAsync(
                new LlmRequest(model, plan.Prompt, request.MediaBytes, request.MediaMime,
                    request.InlineText, request.FileName),
                cancellationToken).ConfigureAwait(false);

            if (!response.Success)
            {
                _log.LogApiCall(model, false, response.Error);
                StatusChanged?.Invoke(L.F("bar.status", Shorten(response.Error, 90)), 6000);
                return;
            }

            var parsed = ResponseParser.Parse(response.Text);
            if (!parsed.Ok)
            {
                _log.LogApiCall(model, false, parsed.Error);
                _log.AddRecentOutput(new { error = parsed.Error, raw = Shorten(response.Text, 500) });
                StatusChanged?.Invoke(L.T("ocr.unreadable"), 5000);
                return;
            }

            _log.LogApiCall(model, true);
            _log.AddRecentOutput(parsed.Data ?? System.Text.Json.JsonSerializer.SerializeToElement(parsed.Text));

            LastResultText = parsed.Text;
            if (!ClipboardWriter.SetText(parsed.Text))
                _log.ConsoleLog(L.T("ocr.clipboardWrite"));

            ResultReady?.Invoke(parsed.Text);
            StatusChanged?.Invoke(L.T("ocr.paste"), 3000);
        }
        catch (OperationCanceledException)
        {
            StatusChanged?.Invoke(L.T("ocr.cancelled"), 2000);
        }
        catch (Exception ex)
        {
            _log.LogApiCall(_settings.Settings.Model, false, ex.Message);
            StatusChanged?.Invoke(L.F("ocr.failed", Shorten(ex.Message, 80)), 6000);
        }
        finally
        {
            _log.UpdateRuntime();
            ProcessingFinished?.Invoke();
            _gate.Release();
        }

        _log.ConsoleLog(L.F("ocr.done", $"{((DateTime.UtcNow - started).TotalSeconds):0.0}"));
    }

    private static string BasePromptFor(OcrRequest request) => request.Source switch
    {
        OcrSource.TextClipboard => Prompts.Text,
        _ when request.MediaMime?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true => Prompts.Image,
        _ => Prompts.File
    };

    private static SourceKind ToSourceKind(OcrSource source) => source switch
    {
        OcrSource.TextClipboard => SourceKind.TextClipboard,
        OcrSource.FileClipboard => SourceKind.FileClipboard,
        OcrSource.HookedRegion => SourceKind.HookedOcr,
        OcrSource.FileDrop => SourceKind.FileDrop,
        _ => SourceKind.ImageClipboard
    };

    private static string Shorten(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return L.T("ocr.unknownError");
        var single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length <= max ? single : single[..max] + "...";
    }

    public void Dispose()
    {
        _gate.Dispose();
        _http.Dispose();
    }
}
