using System.Windows;
using YuukaOCR.App.Ui;
using YuukaOCR.App.Ui.Tabs;

// Loads every tab's XAML through the real application resources, without showing anything. This is the
// only way a duplicate resource key or a bad StaticResource shows up: those are runtime errors, the
// compiler is perfectly happy with them.

internal static class Program
{
    private static int Count(FrameworkElement root, string name) =>
        root.FindName(name) is System.Windows.Controls.Panel panel ? panel.Children.Count : -1;

    [STAThread]
    private static int Main()
    {
        // A plain Application, not YuukaOCR.App.App: the real App posts OnStartup on its dispatcher, which
        // would create the single-instance mutex, show the main bar and start the API-key check the first
        // time a probe pumps the queue. Merging Styles.xaml by hand gives the same resources, headless.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/YuukaOCR;component/Ui/Styles.xaml", UriKind.Absolute)
        });

        var failures = 0;

        void Check(string name, Func<object> create)
        {
            try
            {
                var instance = create();
                Console.WriteLine($"OK    {name}  ({instance.GetType().Name})");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"FAIL  {name}: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException is not null) Console.WriteLine($"      inner: {ex.InnerException.Message}");
            }
        }

        Check("SystemTab", () => new SystemTab());
        Check("ThemeTab", () => new ThemeTab());
        Check("LayoutTab", () => new LayoutTab());
        Check("HookingTab", () => new HookingTab());
        Check("PromptAgentTab", () => new PromptAgentTab());
        Check("LogTab", () => new LogTab());
        Check("StatusTab", () => new StatusTab());
        Check("ConsoleTab", () => new ConsoleTab());
        Check("SnipOverlay", () => new SnipOverlay());

        // The result bubble is the one window the settings smoke run never constructs (it only appears
        // after an OCR), so build it for real, with the same services the app hands it.
        YuukaOCR.App.Services.AppServices? services = null;
        try
        {
            services = new YuukaOCR.App.Services.AppServices();
            Check("ResultWindow", () => new ResultWindow(services));

            // History rows are built in code, so building them is the only way to catch a missing
            // resource or a null settings value there.
            var logTab = new LogTab();
            logTab.Initialize(services);
            logTab.Refresh();
            Console.WriteLine($"      history rows: outputs={Count(logTab, "OutputList")} " +
                              $"prompts={Count(logTab, "PromptList")} chat={Count(logTab, "ChatList")}");

            // Chat history rows: write two turns, render, then clear again so the user's log stays clean.
            services.Log.AddChatTurn("user", "summarize this", "2026-01-01 00:00:01");
            services.Log.AddChatTurn("assistant", "Short summary.", "2026-01-01 00:00:02");
            logTab.Refresh();
            Console.WriteLine($"      chat rows after two turns: {Count(logTab, "ChatList")}");
            services.Agent.ClearHistory();
            logTab.Refresh();
            Console.WriteLine($"      chat rows after clear: {Count(logTab, "ChatList")}");

            // The Prompt/Agent tab builds its action tabs from settings; run it once with real data.
            var agentTab = new PromptAgentTab();
            agentTab.Initialize(services);
            Console.WriteLine($"      agent tab action tabs: {Count(agentTab, "ActionTabs")} " +
                              $"(settings has {services.Settings.Settings.Actions.Count} actions)");
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine($"FAIL  AppServices/ResultWindow: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            services?.Dispose();
        }

        Console.WriteLine(failures == 0 ? "all XAML parsed" : $"{failures} failed");

        // Geometry probes: thumb clipping, tick rows, input clipping, tick density, action-bar sync.
        // They need live services, so they run after the parse checks and share their failure count.
        var geometry = new YuukaOCR.App.Services.AppServices();
        try
        {
            failures += XamlCheck.LayoutProbe.Run(geometry);
        }
        finally
        {
            geometry.Dispose();
        }

        Console.WriteLine(failures == 0 ? "all layout probes passed" : $"{failures} layout failures");
        return failures == 0 ? 0 : 1;
    }

}
