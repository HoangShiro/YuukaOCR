using System.Windows;
using System.Windows.Controls;
using YuukaOCR.App.Services;
using YuukaOCR.App.Ui.Controls;
using YuukaOCR.Core.Interop;
using YuukaOCR.Core.Localization;
using YuukaOCR.Core.Settings;

namespace YuukaOCR.App.Ui.Tabs;

public partial class HookingTab : UserControl
{
    private AppServices _services = null!;
    private bool _loading;

    public HookingTab() => InitializeComponent();

    public void Initialize(AppServices services)
    {
        _services = services;
        _loading = true;

        var s = _services.Settings.Settings;

        // Hook mode: two fixed choices, so the combo is filled here instead of from XAML.
        ModeBox.Items.Clear();
        ModeBox.Items.Add(L.T("hook.mode.outside"));
        ModeBox.Items.Add(L.T("hook.mode.inside"));
        ModeBox.SelectedIndex = WindowTracker.ParseMode(s.HookMode) == HookMode.Inside ? 1 : 0;

        // Ranges, ticks and steps come from SliderSpecs — the XAML numbers are only the designer preview.
        SliderSetup.Apply(ProximityTopSlider, SliderSpecs.HookProximityTop);
        SliderSetup.Apply(ProximityBottomSlider, SliderSpecs.HookProximityBottom);
        SliderSetup.Apply(OffsetTopSlider, SliderSpecs.HookOffsetTop);
        SliderSetup.Apply(OffsetBottomSlider, SliderSpecs.HookOffsetBottom);
        SliderSetup.Apply(UnhookSlider, SliderSpecs.UnhookDistance);
        SliderSetup.Apply(SpringSlider, SliderSpecs.Spring);
        SliderSetup.Apply(DampingSlider, SliderSpecs.Damping);
        SliderSetup.Apply(BounceSlider, SliderSpecs.Bounce);

        Bind(ProximityTopSlider, ProximityTopValue, () => s.HookProximityTop,
            v => _services.Settings.Update(x => x.HookProximityTop = (int)v));
        Bind(ProximityBottomSlider, ProximityBottomValue, () => s.HookProximityBottom,
            v => _services.Settings.Update(x => x.HookProximityBottom = (int)v));
        Bind(OffsetTopSlider, OffsetTopValue, () => s.HookOffsetTop,
            v => _services.Settings.Update(x => x.HookOffsetTop = (int)v));
        Bind(OffsetBottomSlider, OffsetBottomValue, () => s.HookOffsetBottom,
            v => _services.Settings.Update(x => x.HookOffsetBottom = (int)v));
        Bind(UnhookSlider, UnhookValue, () => s.UnhookDistance,
            v => _services.Settings.Update(x => x.UnhookDistance = (int)v));

        Bind(SpringSlider, SpringValue, () => s.SpringConstant * 100,
            v => _services.Settings.Update(x => x.SpringConstant = Math.Round(v / 100, 3)));
        Bind(DampingSlider, DampingValue, () => s.DampingFactor * 100,
            v => _services.Settings.Update(x => x.DampingFactor = Math.Round(v / 100, 3)));
        Bind(BounceSlider, BounceValue, () => s.BounceDampingFactor * 100,
            v => _services.Settings.Update(x => x.BounceDampingFactor = Math.Round(v / 100, 3)));

        _loading = false;
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ModeBox.SelectedIndex < 0) return;

        var mode = ModeBox.SelectedIndex == 1 ? HookMode.Inside : HookMode.Outside;
        _services.Settings.Update(s => s.HookMode = WindowTracker.SerializeMode(mode));
    }

    private void Bind(Slider slider, TextBlock label, Func<double> read, Action<double> write)
    {
        slider.Value = read();
        label.Text = $"{slider.Value:0}";

        slider.ValueChanged += (_, e) =>
        {
            label.Text = $"{e.NewValue:0}";
            if (_loading || !IsLoaded) return;
            write(e.NewValue);
        };
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();

        _loading = true;
        _services.Settings.Update(s =>
        {
            s.HookProximityTop = defaults.HookProximityTop;
            s.HookProximityBottom = defaults.HookProximityBottom;
            s.HookOffsetTop = defaults.HookOffsetTop;
            s.HookOffsetBottom = defaults.HookOffsetBottom;
            s.UnhookDistance = defaults.UnhookDistance;
            s.SpringConstant = defaults.SpringConstant;
            s.DampingFactor = defaults.DampingFactor;
            s.BounceDampingFactor = defaults.BounceDampingFactor;
        });

        var settings = _services.Settings.Settings;
        ProximityTopSlider.Value = settings.HookProximityTop;
        ProximityBottomSlider.Value = settings.HookProximityBottom;
        OffsetTopSlider.Value = settings.HookOffsetTop;
        OffsetBottomSlider.Value = settings.HookOffsetBottom;
        UnhookSlider.Value = settings.UnhookDistance;
        SpringSlider.Value = settings.SpringConstant * 100;
        DampingSlider.Value = settings.DampingFactor * 100;
        BounceSlider.Value = settings.BounceDampingFactor * 100;
        _loading = false;
    }
}
