using System.Windows;
using System.Windows.Threading;
using YuukaOCR.App.Services;
using YuukaOCR.App.Ui;
using YuukaOCR.Core;
using YuukaOCR.Core.Localization;
using YuukaOCR.Core.Providers;

namespace YuukaOCR.App;

public partial class App : Application
{
    private Mutex? _singleInstance;

    public static AppServices Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.EnsureDirs();

        _singleInstance = new Mutex(true, @"Local\YuukaOCR.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(L.T("app.alreadyRunning"), "YuukaOCR", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Services?.Log.ConsoleLog(L.F("app.fatal", (args.ExceptionObject as Exception)?.Message));
            Services?.Log.Flush();
        };

        Services = new AppServices();

        // Every window repaints through this one subscription, so a change in Appearance reaches the
        // bar, the result bubble and the settings window at the same moment.
        AppearanceSync.Initialize(Services.Settings);

        var main = new MainWindow(Services);
        MainWindow = main;
        main.Show();

        // `YuukaOCR.exe --config` opens settings straight away (handy for screenshots/support).
        if (e.Args.Any(arg => arg is "--config" or "-c"))
        {
            main.OpenConfig();
            return;
        }

        // Runs after the bar is up, so a slow provider never delays the window: a missing or rejected key
        // opens Settings → System with the reason above the key input.
        _ = CheckApiKeyAsync(main);

        // Update check runs beside it, in the background: an unreachable GitHub must never delay or
        // interrupt startup, and an up-to-date install says nothing at all.
        _ = CheckUpdatesAsync(main);
    }

    /// <summary>
    /// Startup update check. Deliberately fire-and-forget: no dialog, no blocking, and the only visible
    /// effect is a line in the bar status plus the settings section it already fills in.
    /// </summary>
    private static async Task CheckUpdatesAsync(MainWindow main)
    {
        var services = Services;

        try
        {
            var check = await services.CheckForUpdatesAsync();
            if (check?.CanInstall != true) return;

            // One announcement per release: a restart must not nag about the same version.
            var notified = services.Settings.Settings.UpdateNotifiedVersion;
            if (string.Equals(notified, check.LatestVersion, StringComparison.Ordinal)) return;

            services.Settings.Update(s => s.UpdateNotifiedVersion = check.LatestVersion ?? "");
            main.ShowStatus(L.F("upd.available", check.LatestVersion), 8000);
        }
        catch (Exception ex)
        {
            // Never let an update check take the app down; the reason is in the console stream.
            services.Log.ConsoleLog(L.F("app.uiError", ex.Message));
        }
    }

    /// <summary>
    /// Startup guard. The app is useless without a working key, so instead of failing silently on the
    /// first OCR attempt it opens Settings → System when the selected provider has no key at all, or when
    /// none of the stored keys answers. Local providers (Ollama, LM Studio) need no key and are skipped.
    /// </summary>
    private static async Task CheckApiKeyAsync(MainWindow main)
    {
        var services = Services;
        var info = ProviderRegistry.Get(services.Settings.Settings.ApiProvider);
        if (!info.RequiresKey) return;

        try
        {
            var keys = ApiKeyPool.LoadFor(AppPaths.EnvFile, info.Id);
            if (keys.Count == 0)
            {
                services.Log.ConsoleLog(L.F("app.noKeyStartup", info.DisplayName));
                main.PromptForKey(info.Id, null);
                return;
            }

            // Every key in the rotation gets a chance; the prompt only appears when none of them works.
            var reason = "";
            foreach (var key in keys)
            {
                var result = await services.Ocr.ValidateKeyAsync(key);
                if (result.Success) return;

                // Offline, rate limited, provider down: the key may well be fine, so stay quiet instead of
                // accusing it. Only a rejection from the provider itself opens the prompt.
                if (!result.IsKeyRejected) return;

                reason = result.Message;
            }

            services.Log.ConsoleLog(L.F("app.badKeyStartup", info.DisplayName, reason));
            main.PromptForKey(info.Id, reason);
        }
        catch (Exception ex)
        {
            // Quitting during the check disposes the services mid-flight; that is not worth a dialog.
            services.Log.ConsoleLog(L.F("app.uiError", ex.Message));
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Services?.Log.ConsoleLog(L.F("app.uiError", e.Exception.Message));
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
