using System.Windows;
using System.Windows.Controls;
using YuukaOCR.App.Services;
using YuukaOCR.Core.Localization;
using YuukaOCR.Core.Providers;

namespace YuukaOCR.App.Ui.Tabs;

public partial class StatusTab : UserControl
{
    private AppServices _services = null!;

    public StatusTab() => InitializeComponent();

    public void Initialize(AppServices services)
    {
        _services = services;
        Refresh();
    }

    public void Refresh()
    {
        if (_services is null) return;

        var settings = _services.Settings.Settings;
        var info = ProviderRegistry.Get(settings.ApiProvider);
        var snapshot = _services.Log.Snapshot();

        ProviderLine.Text = L.F("status.providerLine", info.DisplayName,
            string.IsNullOrWhiteSpace(settings.Model) ? info.DefaultModel : settings.Model);
        HotkeyLine.Text = L.F("status.hotkeyLine", Core.Input.HotkeySpec.Parse(settings.HookOcrHotkey).Describe());
        HookLine.Text = L.F("status.hookLine", settings.HookProximityTop, settings.HookProximityBottom,
            settings.UnhookDistance);

        ApiTotalLine.Text = L.F("status.apiTotal", snapshot.TotalApiCalls);
        ApiModelsLine.Text = snapshot.ByModel.Count == 0
            ? L.T("status.apiNone")
            : string.Join("  ·  ", snapshot.ByModel.OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key}: {kv.Value}"));

        RuntimeTotalLine.Text = L.F("status.runtimeTotal", Format(snapshot.TotalSeconds));
        RuntimeTodayLine.Text = L.F("status.runtimeToday", snapshot.TodayDate, Format(snapshot.TodaySeconds));
        RuntimeSessionLine.Text = L.F("status.runtimeSession", Format(snapshot.SessionSeconds));

        SourceLine.Text = L.F("status.sourceLine", snapshot.TextClipboard, snapshot.ImageClipboard,
            snapshot.FileClipboard, snapshot.HookedOcr);

        FileDropLine.Text = snapshot.FileDrop.Count == 0
            ? L.T("status.fileDropNone")
            : L.F("status.fileDrop", string.Join("  ·  ", snapshot.FileDrop.OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key}: {kv.Value}")));
    }

    private static string Format(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{span.Minutes}m {span.Seconds}s";
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();
}
