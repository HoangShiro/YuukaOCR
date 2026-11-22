using System.Text.Json;
using YuukaOCR.Core.Localization;

namespace YuukaOCR.Core.Logging;

public enum SourceKind
{
    TextClipboard,
    ImageClipboard,
    FileClipboard,
    HookedOcr,
    FileDrop
}

public sealed record LogSnapshot(
    double TotalSeconds,
    string TodayDate,
    double TodaySeconds,
    double SessionSeconds,
    int TotalApiCalls,
    IReadOnlyDictionary<string, int> ByModel,
    int TextClipboard,
    int ImageClipboard,
    int FileClipboard,
    int HookedOcr,
    IReadOnlyDictionary<string, int> FileDrop);

/// <summary>
/// Central logger: console stream for the UI, API/source statistics, recent outputs and prompts.
/// Writes are coalesced (1.5s) instead of rewriting the whole JSON file on every event.
/// </summary>
public sealed class LogService : IDisposable
{
    private readonly object _lock = new();
    private readonly ActivityLog _log;
    private readonly System.Threading.Timer _saveTimer;
    private readonly string _path;
    private DateTime _sessionStart = DateTime.Now;
    private bool _dirty;

    public event Action<string>? MessageLogged;

    public LogService(string? path = null)
    {
        _path = path ?? AppPaths.LogFile;
        _log = ActivityLog.Load(_path);
        _saveTimer = new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void ConsoleLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] Yuuka: {message}\n";
        MessageLogged?.Invoke(line);
        try
        {
            Console.Out.Write(line);
            Console.Out.Flush();
        }
        catch (Exception)
        {
            // No console attached (WinExe): the UI stream is the real sink.
        }
    }

    public void LogApiCall(string model, bool success, string? error = null)
    {
        if (!success)
        {
            ConsoleLog(L.F("logline.apiError", error));
            return;
        }

        lock (_lock)
        {
            var stats = _log.ApiCalls;
            stats.Total++;
            stats.ByModel[model] = stats.ByModel.GetValueOrDefault(model) + 1;
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            if (!stats.Daily.TryGetValue(today, out var daily))
            {
                daily = new Dictionary<string, int>();
                stats.Daily[today] = daily;
            }
            daily[model] = daily.GetValueOrDefault(model) + 1;
            MarkDirty();
        }
        ConsoleLog(L.F("logline.apiOk", model));
    }

    public void AddRecentOutput(object? data)
    {
        JsonElement element;
        try
        {
            element = data is JsonElement je ? je : JsonSerializer.SerializeToElement(data, Json.Options);
        }
        catch (Exception)
        {
            element = JsonSerializer.SerializeToElement(new { error = "unserializable output" }, Json.Options);
        }

        lock (_lock)
        {
            _log.RecentOutputs.Insert(0, new LogEntry
            {
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Text = element
            });
            Trim(_log.RecentOutputs);
            MarkDirty();
        }
    }

    public void AddRecentPrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;
        var added = false;
        lock (_lock)
        {
            var exists = _log.RecentPrompts.Any(e =>
                e.Text.ValueKind == JsonValueKind.String && e.Text.GetString() == prompt);
            if (!exists)
            {
                _log.RecentPrompts.Insert(0, new LogEntry
                {
                    Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Text = JsonSerializer.SerializeToElement(prompt, Json.Options)
                });
                Trim(_log.RecentPrompts);
                MarkDirty();
                added = true;
            }
        }
        if (added) ConsoleLog(L.T("logline.promptSaved"));
    }

    public void LogSource(SourceKind source, string? detail = null)
    {
        lock (_lock)
        {
            var stats = _log.SourceStats;
            switch (source)
            {
                case SourceKind.TextClipboard: stats.TextClipboard++; break;
                case SourceKind.ImageClipboard: stats.ImageClipboard++; break;
                case SourceKind.FileClipboard: stats.FileClipboard++; break;
                case SourceKind.HookedOcr: stats.HookedOcr++; break;
                case SourceKind.FileDrop:
                    var ext = string.IsNullOrEmpty(detail) ? "" : Path.GetExtension(detail).ToLowerInvariant();
                    if (ext.Length == 0) return;
                    stats.FileDrop[ext] = stats.FileDrop.GetValueOrDefault(ext) + 1;
                    break;
            }
            MarkDirty();
        }
    }

    /// <summary>Adds the time elapsed since the previous tick to the running totals.</summary>
    public void UpdateRuntime()
    {
        var now = DateTime.Now;
        lock (_lock)
        {
            var elapsed = Math.Max(0, (now - _sessionStart).TotalSeconds);
            _sessionStart = now;
            var runtime = _log.Runtime;
            runtime.TotalSeconds += elapsed;
            var today = now.ToString("yyyy-MM-dd");
            if (runtime.Today.Date == today)
            {
                runtime.Today.Seconds += elapsed;
            }
            else
            {
                runtime.Today = new TodaySeconds { Date = today, Seconds = elapsed };
            }
            runtime.LastSessionSeconds = elapsed;
            _log.LastSessionSeconds = elapsed;
            MarkDirty();
        }
    }

    public LogSnapshot Snapshot()
    {
        lock (_lock)
        {
            var stats = _log.ApiCalls;
            var src = _log.SourceStats;
            return new LogSnapshot(
                _log.Runtime.TotalSeconds,
                _log.Runtime.Today.Date,
                _log.Runtime.Today.Seconds,
                Math.Max(0, (DateTime.Now - _sessionStart).TotalSeconds),
                stats.Total,
                new Dictionary<string, int>(stats.ByModel),
                src.TextClipboard,
                src.ImageClipboard,
                src.FileClipboard,
                src.HookedOcr,
                new Dictionary<string, int>(src.FileDrop));
        }
    }

    public List<LogEntry> GetRecentOutputs()
    {
        lock (_lock) return _log.RecentOutputs.ToList();
    }

    public List<LogEntry> GetRecentPrompts()
    {
        lock (_lock) return _log.RecentPrompts.ToList();
    }

    // ------------------------------------------------------------------ chat

    public void AddChatTurn(string role, string text, string? stamp = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_lock)
        {
            _log.ChatHistory.Add(new ChatEntry
            {
                Timestamp = stamp ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Role = role,
                Text = text
            });

            if (_log.ChatHistory.Count > ActivityLog.MaxChatEntries)
                _log.ChatHistory.RemoveRange(0, _log.ChatHistory.Count - ActivityLog.MaxChatEntries);

            MarkDirty();
        }
    }

    public List<ChatEntry> GetChatHistory()
    {
        lock (_lock) return _log.ChatHistory.Select(e => new ChatEntry
        {
            Timestamp = e.Timestamp,
            Role = e.Role,
            Text = e.Text
        }).ToList();
    }

    public void ClearChatHistory()
    {
        lock (_lock)
        {
            _log.ChatHistory.Clear();
            MarkDirty();
        }
        ConsoleLog(L.T("logline.chatCleared"));
    }

    public void RemoveChatTurn(int index)
    {
        lock (_lock)
        {
            if (index < 0 || index >= _log.ChatHistory.Count) return;
            _log.ChatHistory.RemoveAt(index);
            MarkDirty();
        }
    }

    /// <summary>Removes one entry from a history list by position (the History tab's remove button).</summary>
    public void RemoveOutput(int index)
    {
        lock (_lock)
        {
            if (index < 0 || index >= _log.RecentOutputs.Count) return;
            _log.RecentOutputs.RemoveAt(index);
            MarkDirty();
        }
    }

    public void RemovePrompt(int index)
    {
        lock (_lock)
        {
            if (index < 0 || index >= _log.RecentPrompts.Count) return;
            _log.RecentPrompts.RemoveAt(index);
            MarkDirty();
        }
    }

    public void ClearOutputs()
    {
        lock (_lock)
        {
            _log.RecentOutputs.Clear();
            MarkDirty();
        }
        ConsoleLog(L.T("logline.outputsCleared"));
    }

    public void ClearPrompts()
    {
        lock (_lock)
        {
            _log.RecentPrompts.Clear();
            MarkDirty();
        }
        ConsoleLog(L.T("logline.promptsCleared"));
    }

    private static void Trim(List<LogEntry> entries)
    {
        if (entries.Count > ActivityLog.MaxRecentEntries)
            entries.RemoveRange(ActivityLog.MaxRecentEntries, entries.Count - ActivityLog.MaxRecentEntries);
    }

    private void MarkDirty()
    {
        _dirty = true;
        _saveTimer.Change(1500, Timeout.Infinite);
    }

    public void Flush()
    {
        lock (_lock)
        {
            if (!_dirty) return;
            _log.Save(_path);
            _dirty = false;
        }
    }

    public void Dispose()
    {
        UpdateRuntime();
        Flush();
        _saveTimer.Dispose();
    }
}
