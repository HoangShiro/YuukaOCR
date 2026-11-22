using System.Text.Json;
using System.Text.Json.Serialization;

namespace YuukaOCR.Core.Logging;

public sealed class ApiCallStats
{
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("by_model")] public Dictionary<string, int> ByModel { get; set; } = new();
    [JsonPropertyName("daily")] public Dictionary<string, Dictionary<string, int>> Daily { get; set; } = new();
}

public sealed class RuntimeStats
{
    [JsonPropertyName("total_seconds")] public double TotalSeconds { get; set; }
    [JsonPropertyName("today_seconds")] public TodaySeconds Today { get; set; } = new();
    [JsonPropertyName("last_session_seconds")] public double LastSessionSeconds { get; set; }
}

public sealed class TodaySeconds
{
    [JsonPropertyName("date")] public string Date { get; set; } = "1970-01-01";
    [JsonPropertyName("seconds")] public double Seconds { get; set; }
}

public sealed class SourceStats
{
    [JsonPropertyName("text_clipboard")] public int TextClipboard { get; set; }
    [JsonPropertyName("image_clipboard")] public int ImageClipboard { get; set; }
    [JsonPropertyName("file_clipboard")] public int FileClipboard { get; set; }
    [JsonPropertyName("hooked_ocr")] public int HookedOcr { get; set; }
    [JsonPropertyName("file_drop")] public Dictionary<string, int> FileDrop { get; set; } = new();
}

public sealed class LogEntry
{
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = "";
    [JsonPropertyName("text")] public JsonElement Text { get; set; }
}

/// <summary>
/// One turn of the action bar's conversation. <see cref="Role"/> is <c>user</c> or <c>assistant</c>;
/// the History tab prints it as a label, so it is stored as the plain role name.
/// </summary>
public sealed class ChatEntry
{
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = "";
    [JsonPropertyName("role")] public string Role { get; set; } = "user";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
}

/// <summary>
/// Persistent activity record. Field names match the old Python log.json so an existing
/// user folder keeps its history.
/// </summary>
public sealed class ActivityLog
{
    [JsonPropertyName("api_calls")] public ApiCallStats ApiCalls { get; set; } = new();
    [JsonPropertyName("recent_outputs")] public List<LogEntry> RecentOutputs { get; set; } = new();
    [JsonPropertyName("recent_prompts")] public List<LogEntry> RecentPrompts { get; set; } = new();

    /// <summary>Conversation of the result window's action bar (chat box + every action, in order).</summary>
    [JsonPropertyName("chat_history")] public List<ChatEntry> ChatHistory { get; set; } = new();
    [JsonPropertyName("app_runtime")] public RuntimeStats Runtime { get; set; } = new();
    [JsonPropertyName("last_session_seconds")] public double LastSessionSeconds { get; set; }
    [JsonPropertyName("source_stats")] public SourceStats SourceStats { get; set; } = new();

    public const int MaxRecentEntries = 100;

    /// <summary>Chat turns are cheap but not free; the agent trims by token budget on top of this cap.</summary>
    public const int MaxChatEntries = 200;

    public static ActivityLog Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var log = JsonSerializer.Deserialize<ActivityLog>(File.ReadAllText(path), Json.Options);
                if (log is not null) return log;
            }
        }
        catch (Exception)
        {
            // Unreadable history is not fatal.
        }
        return new ActivityLog();
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json.Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Log save failed: {ex.Message}");
        }
    }
}
