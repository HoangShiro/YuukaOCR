using System.Text;
using System.Text.Json;

namespace YuukaOCR.Core.Logging;

/// <summary>
/// Opt-in request/response trace for debugging a provider call from a terminal.
///
/// It is **off unless `YUUKA_API_DEBUG=1`** is in the environment: the production build must not leave
/// API payloads (which contain the user's OCR text and can contain the key in a URL) on disk. `build.ps1`
/// clears the variable and deletes the file before publishing, and the test runner proves the default
/// is off.
///
/// Enable it like this:
/// <code>
///   set YUUKA_API_DEBUG=1
///   YuukaOCR.exe
///   type user\api-debug.log
/// </code>
/// </summary>
public static class ApiTrace
{
    public const string EnvVar = "YUUKA_API_DEBUG";
    public const string FileName = "api-debug.log";

    private static readonly object Gate = new();
    private static bool? _enabled;

    /// <summary>True when <see cref="EnvVar"/> is set to anything other than 0/false/empty.</summary>
    public static bool Enabled
    {
        get
        {
            if (_enabled is { } cached) return cached;
            var raw = Environment.GetEnvironmentVariable(EnvVar);
            var on = !string.IsNullOrWhiteSpace(raw) &&
                     raw.Trim() is not ("0" or "false" or "False" or "FALSE");
            _enabled = on;
            return on;
        }
    }

    /// <summary>Where the trace goes. Lives next to the other user data so it is easy to find and delete.</summary>
    public static string FilePath => Path.Combine(AppPaths.UserDir, FileName);

    /// <summary>Forgets the cached environment lookup (tests switch the variable at runtime).</summary>
    public static void ResetCache() => _enabled = null;

    /// <summary>Writes one tagged block. Never throws: a debug trace must not break a request.</summary>
    public static void Write(string tag, object? payload)
    {
        if (!Enabled) return;

        try
        {
            var body = payload switch
            {
                null => "(null)",
                string text => text,
                _ => JsonSerializer.Serialize(payload, Json.Options)
            };

            var builder = new StringBuilder()
                .Append('[').Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append("] ")
                .Append(tag).Append('\n')
                .Append(body).Append("\n\n");

            Directory.CreateDirectory(AppPaths.UserDir);
            lock (Gate) File.AppendAllText(FilePath, builder.ToString(), Encoding.UTF8);
        }
        catch (Exception)
        {
            // The trace is a debugging aid; a locked file or a read-only folder is not an app error.
        }
    }

    /// <summary>Deletes the trace file (called on start when tracing is off, and by the build).</summary>
    public static void Clear()
    {
        try
        {
            lock (Gate)
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
        }
        catch (Exception)
        {
            // Same reasoning as Write: nothing here is worth failing over.
        }
    }
}
