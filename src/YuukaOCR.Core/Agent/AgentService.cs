using YuukaOCR.Core.Localization;
using YuukaOCR.Core.Logging;
using YuukaOCR.Core.Providers;
using YuukaOCR.Core.Settings;

namespace YuukaOCR.Core.Agent;

/// <summary>
/// The chat agent behind the result window's action bar.
///
/// One conversation, shared by the chat box and by every registered action — an action is a
/// pre-composed chat turn, so it must not fork the history (that is what makes "explain that" work
/// right after "summarize this"). The history is persisted through <see cref="LogService"/> so the
/// History tab can show it and it survives a restart.
/// </summary>
public sealed class AgentService : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly LogService _log;
    private readonly HttpClient _http;
    private readonly ApiKeyPool _pool = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<ChatTurn> _history = new();

    public AgentService(SettingsStore settings, LogService log, HttpClient? http = null)
    {
        _settings = settings;
        _log = log;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) };

        // The conversation is part of the activity log, so it is restored on start like the rest of it.
        foreach (var entry in _log.GetChatHistory())
            _history.Add(new ChatTurn(entry.Role, entry.Text, entry.Timestamp));
    }

    /// <summary>Raised when a request starts (true) and finishes (false), for the busy indicator.</summary>
    public event Action<bool>? ThinkingChanged;

    /// <summary>Raised after a turn is added to the conversation, so an open window can redraw.</summary>
    public event Action? HistoryChanged;

    public IReadOnlyList<ChatTurn> History
    {
        get
        {
            lock (_history) return _history.ToList();
        }
    }

    public bool IsBusy => _gate.CurrentCount == 0;

    /// <summary>
    /// Sends a chat message. <paramref name="context"/> is the OCR result or the selected text — the
    /// caller decides which, the agent only labels it.
    /// </summary>
    public Task<AgentReply> AskAsync(
        string message,
        string? context = null,
        string? contextLabel = null,
        CancellationToken cancellationToken = default) =>
        SendAsync((message ?? "").Trim(), context, contextLabel, contextInSystem: true, cancellationToken);

    /// <summary>
    /// Runs a registered action: global instruction (system) + action instruction + context, through the
    /// same conversation as <see cref="AskAsync"/>. The context travels in the user turn here, so the
    /// system prompt stays persona-only and the text is not sent twice.
    /// </summary>
    public Task<AgentReply> RunActionAsync(
        AgentAction action,
        string? context = null,
        string? contextLabel = null,
        CancellationToken cancellationToken = default)
    {
        var message = AgentPrompts.ComposeAction(action?.Instruction, context, contextLabel);
        return SendAsync(message, context, contextLabel, contextInSystem: false, cancellationToken);
    }

    private async Task<AgentReply> SendAsync(
        string message,
        string? context,
        string? contextLabel,
        bool contextInSystem,
        CancellationToken cancellationToken)
    {
        if (message.Length == 0) return AgentReply.Fail(L.T("agent.chat.empty"));

        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return AgentReply.Fail(L.T("agent.chat.busy"));

        ThinkingChanged?.Invoke(true);
        try
        {
            var settings = _settings.Settings;
            var agent = settings.Agent;

            // An empty provider/model follows the OCR configuration, so a user who never touched the
            // Agent section still gets a working chat.
            var providerId = string.IsNullOrWhiteSpace(agent.Provider) ? settings.ApiProvider : agent.Provider;
            var info = ProviderRegistry.Get(providerId);

            // No agent model of its own means "use whatever OCR uses" — but only while the agent also
            // follows the OCR provider. A model id belongs to one provider, so following the OCR model
            // across providers would send a foreign id and fail.
            var followsOcr = string.IsNullOrWhiteSpace(agent.Provider) || agent.Provider == settings.ApiProvider;
            var model = !string.IsNullOrWhiteSpace(agent.Model) ? agent.Model.Trim()
                : followsOcr && !string.IsNullOrWhiteSpace(settings.Model) ? settings.Model.Trim()
                : info.DefaultModel;

            var keys = ApiKeyPool.LoadFor(AppPaths.EnvFile, info.Id);
            if (keys.Count == 0 && info.RequiresKey)
            {
                _log.ConsoleLog(L.F("agent.noKey", info.DisplayName));
                return AgentReply.Fail(L.F("agent.noKey", info.DisplayName));
            }

            var key = keys.Count == 0 ? "" : _pool.Next(info.Id, keys);
            var baseUrl = info.Id == settings.ApiProvider ? settings.CustomBaseUrl : "";

            var system = AgentPrompts.SystemPrompt(
                agent.Instruction, contextInSystem ? context : null, contextLabel);
            var turns = AgentPrompts.TrimHistory(History, agent.ContextTokens);

            var request = new LlmRequest(
                model,
                message,
                History: turns.Select(t => new LlmTurn(t.Role, t.Text)).ToList(),
                SystemPrompt: system,
                Temperature: agent.Temperature,
                JsonMode: false);

            var provider = ProviderFactory.Create(info, key, baseUrl, _http);
            var response = await provider.GenerateAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.Success)
            {
                _log.LogApiCall(model, false, response.Error);
                return AgentReply.Fail(response.Error);
            }

            _log.LogApiCall(model, true);

            var answer = (response.Text ?? "").Trim();
            if (answer.Length == 0) return AgentReply.Fail(L.T("agent.chat.emptyAnswer"));

            // The turn is only recorded once the model answered, so a failed request cannot leave a
            // dangling question in the history.
            Append(AgentPrompts.UserRole, message);
            Append(AgentPrompts.AssistantRole, answer);
            return AgentReply.Success(answer);
        }
        catch (OperationCanceledException)
        {
            return AgentReply.Fail(L.T("agent.chat.cancelled"));
        }
        catch (Exception ex)
        {
            _log.ConsoleLog(L.F("agent.chat.failed", ex.Message));
            return AgentReply.Fail(L.F("agent.chat.failed", ex.Message));
        }
        finally
        {
            _gate.Release();
            ThinkingChanged?.Invoke(false);
        }
    }

    private void Append(string role, string text)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        lock (_history) _history.Add(new ChatTurn(role, text, stamp));

        _log.AddChatTurn(role, text, stamp);
        HistoryChanged?.Invoke();
    }

    /// <summary>Drops the conversation from memory and from `user/log.json`.</summary>
    public void ClearHistory()
    {
        lock (_history) _history.Clear();
        _log.ClearChatHistory();
        HistoryChanged?.Invoke();
    }

    /// <summary>Removes one turn by its position in <see cref="History"/>.</summary>
    public void RemoveTurn(int index)
    {
        lock (_history)
        {
            if (index < 0 || index >= _history.Count) return;
            _history.RemoveAt(index);
        }
        _log.RemoveChatTurn(index);
        HistoryChanged?.Invoke();
    }

    public void Dispose()
    {
        _gate.Dispose();
        _http.Dispose();
    }
}
