using YuukaOCR.Core;
using YuukaOCR.Core.Agent;
using YuukaOCR.Core.Input;
using YuukaOCR.Core.Interop;
using YuukaOCR.Core.Localization;
using YuukaOCR.Core.Logging;
using YuukaOCR.Core.Ocr;
using YuukaOCR.Core.Providers;
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

    /// <summary>GitHub Releases: the update source for a published build (see <see cref="Updater"/> for checkouts).</summary>
    public GitHubUpdateService Releases { get; }

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

        // Owner/repo come from the checkout's origin when there is one, so a fork updates itself
        // instead of the upstream project. The fallback is the repository this build ships from.
        var remote = GitUpdateService.RemoteUrl(AppPaths.Root);
        if (!GitHubUpdateService.TryParseRepository(remote, out var owner, out var repository))
        {
            owner = GitHubUpdateService.DefaultOwner;
            repository = GitHubUpdateService.DefaultRepository;
        }

        Releases = new GitHubUpdateService(Log, Version, GitHubToken(), owner, repository);

        Log.ConsoleLog(L.F("app.startup", typeof(AppServices).Assembly.GetName().Version,
            Settings.Settings.ApiProvider));
    }

    /// <summary>
    /// The running application version as text ("2.1.0"). One source for the title bar, the settings
    /// window and the update comparison, so they can never disagree.
    /// </summary>
    public static string Version =>
        typeof(AppServices).Assembly.GetName().Version is { } version
            ? AppVersion.From(version).ToString()
            : "0.0.0";

    /// <summary>
    /// Optional GitHub token. The repository is private, so a token is the only way to see its
    /// releases; the environment wins over user/.env, and neither is ever logged.
    /// </summary>
    private static string GitHubToken()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(GitHubUpdateService.TokenKey);
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment.Trim();
        return ApiKeyPool.ReadValue(AppPaths.EnvFile, GitHubUpdateService.TokenKey);
    }

    /// <summary>
    /// The startup check: asks GitHub once per launch, logs the answer, and hands the result back.
    /// Whether to *tell* the user is the caller's decision, because only the startup path announces and
    /// only once per release.
    /// </summary>
    public async Task<UpdateCheck?> CheckForUpdatesAsync()
    {
        if (!Settings.Settings.AutoUpdateEnabled) return null;

        var check = await Releases.CheckAsync();
        Log.ConsoleLog("[update] " + (check.MessageArg is null
            ? L.T(check.MessageKey)
            : L.F(check.MessageKey, check.MessageArg)));

        return check;
    }

    public void Dispose()
    {
        Releases.Dispose();
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
