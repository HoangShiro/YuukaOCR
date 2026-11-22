using YuukaOCR.Core;
using YuukaOCR.Core.Agent;
using YuukaOCR.Core.Input;
using YuukaOCR.Core.Interop;
using YuukaOCR.Core.Localization;
using YuukaOCR.Core.Logging;
using YuukaOCR.Core.Ocr;
using YuukaOCR.Core.Settings;
using YuukaOCR.Core.Update;

namespace YuukaOCR.App.Services;

/// <summary>
/// One place that owns every long-lived object, so windows never construct services themselves
/// (the old version created loggers and OCR clients from inside widget constructors).
/// </summary>
public sealed class AppServices : IDisposable
{
    public AppConfig Config { get; }
    public SettingsStore Settings { get; }
    public LogService Log { get; }
    public OcrService Ocr { get; }

    /// <summary>The chat agent behind the result window's action bar (one conversation for the whole app).</summary>
    public AgentService Agent { get; }

    public ClipboardWatcher Clipboard { get; }
    public GlobalHotkey Hotkey { get; }
    public GitUpdateService Updater { get; }

    public AppServices()
    {
        Config = AppConfig.Load(AppPaths.AppConfigFile);
        Settings = new SettingsStore(AppPaths.SettingsFile);
        // Must load right here: before this version Load() was never called, so the app always started
        // with defaults and then overwrote settings.json — every user choice
        // (provider, model, theme, physics, window position) was wiped on each restart.
        Settings.Load();
        // The language has to be live before any window is built: every view resolves its strings
        // through L at load time.
        L.Use(Settings.Settings.Language);
        Log = new LogService(AppPaths.LogFile);

        Ocr = new OcrService(Settings, Log);
        Agent = new AgentService(Settings, Log);
        Clipboard = new ClipboardWatcher();
        Hotkey = new GlobalHotkey(Log.ConsoleLog);
        Updater = new GitUpdateService(Log);

        Log.ConsoleLog(L.F("app.startup", typeof(AppServices).Assembly.GetName().Version,
            Settings.Settings.ApiProvider));
    }

    public void Dispose()
    {
        Hotkey.Dispose();
        Clipboard.Dispose();
        Agent.Dispose();
        Ocr.Dispose();
        Settings.Flush();
        Settings.Dispose();
        Log.Flush();
        Log.Dispose();
    }
}
