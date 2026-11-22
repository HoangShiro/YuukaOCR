using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using YuukaOCR.App.Services;
using YuukaOCR.App.Ui.Controls;
using YuukaOCR.Core;
using YuukaOCR.Core.Localization;
using YuukaOCR.Core.Settings;

namespace YuukaOCR.App.Ui.Tabs;

public partial class ThemeTab : UserControl
{
    private readonly List<ThemeCard> _cards = new();
    private AppServices _services = null!;
    private bool _loading;

    public ThemeTab() => InitializeComponent();

    public void Initialize(AppServices services)
    {
        _services = services;
        _loading = true;   // the combo assignments below raise SelectionChanged

        var families = Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        FontCombo.ItemsSource = families;

        LanguageCombo.ItemsSource = L.Languages;
        LanguageCombo.SelectedItem = L.Languages.FirstOrDefault(o => o.Code == L.Language) ?? L.Languages[0];

        BuildPresetCards();

        var theme = _services.Settings.Settings.Theme;

        // Ranges and tick grids come from SliderSpecs; the XAML values are just the designer preview.
        SliderSetup.Apply(FrameOpacitySlider, SliderSpecs.FrameOpacity);
        SliderSetup.Apply(BackgroundOpacitySlider, SliderSpecs.BackgroundOpacity);
        SliderSetup.Apply(FontSizeSlider, SliderSpecs.FontSize);
        SliderSetup.Apply(ScaleSlider, SliderSpecs.UiScale);

        SelectPreset(theme.Preset);
        FrameOpacitySlider.Value = theme.FrameOpacity;
        BackgroundOpacitySlider.Value = theme.BackgroundOpacity;

        FontCombo.SelectedItem = families.FirstOrDefault(f =>
            string.Equals(f, theme.FontFamily, StringComparison.OrdinalIgnoreCase)) ?? families.FirstOrDefault();
        FontSizeSlider.Value = theme.FontSize;
        ScaleSlider.Value = _services.Settings.Settings.UiScale;

        UpdateLabels();
        ImageStatus.Text = L.T(File.Exists(AppPaths.UserUiImage) ? "ui.barImage.custom" : "ui.barImage.bundled");

        _loading = false;
    }

    /// <summary>One card per preset; the card carries its own preview colours, so it never needs the active theme.</summary>
    private void BuildPresetCards()
    {
        foreach (var preset in ThemePresets.All)
        {
            var card = new ThemeCard();
            card.Bind(preset);
            card.Selected += OnPresetSelected;
            _cards.Add(card);
            PresetPanel.Children.Add(card);
        }
    }

    private void SelectPreset(string id)
    {
        var preset = ThemePresets.Get(id);
        foreach (var card in _cards)
            card.IsSelected = card.Preset?.Id == preset.Id;
    }

    /// <summary>
    /// A preset changes the whole app: the settings window re-applies its own resources on
    /// <c>Settings.Changed</c>, and the bar/result window follow the same palette.
    /// </summary>
    private void OnPresetSelected(ThemePreset preset)
    {
        if (_loading) return;
        if (_services.Settings.Settings.Theme.Preset == preset.Id) return;

        _services.Settings.Update(s => s.Theme.Preset = preset.Id);
    }

    /// <summary>
    /// Language is the one setting that cannot be applied in place: every view resolved its strings
    /// while its XAML loaded. <see cref="L.Use"/> raises <c>L.Changed</c> and the shell reopens this
    /// window on the same tab (see <c>MainWindow.Relocalize</c>), so the page is never half-translated.
    /// </summary>
    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (LanguageCombo.SelectedItem is not LanguageOption option) return;
        if (_services.Settings.Settings.Language == option.Code) return;

        _services.Settings.Update(s => s.Language = option.Code);
        L.Use(option.Code);
    }

    private void SaveTheme(Action<ThemeSettings> mutate)
    {
        if (_loading) return;
        _services.Settings.Update(s => mutate(s.Theme));
    }

    private void UpdateLabels()
    {
        FontSizeValue.Text = $"{FontSizeSlider.Value:0}";
        FrameOpacityValue.Text = $"{FrameOpacitySlider.Value:0}%";
        BackgroundOpacityValue.Text = $"{BackgroundOpacitySlider.Value:0}%";
        ScaleValue.Text = $"{ScaleSlider.Value:0}";
    }

    private void OnFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FontCombo.SelectedItem is not string family) return;
        SaveTheme(t => t.FontFamily = family);
    }

    private void OnFontSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        UpdateLabels();
        SaveTheme(t => t.FontSize = Math.Round(e.NewValue));
    }

    private void OnFrameOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        UpdateLabels();
        SaveTheme(t => t.FrameOpacity = (int)Math.Round(e.NewValue));
    }

    private void OnBackgroundOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        UpdateLabels();
        SaveTheme(t => t.BackgroundOpacity = (int)Math.Round(e.NewValue));
    }

    private void OnScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        UpdateLabels();
        if (_loading) return;
        _services.Settings.Update(s => s.UiScale = (int)Math.Round(e.NewValue));
    }

    private void OnPickImage(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = L.T("ui.barImage.dialogTitle"),
            Filter = L.T("ui.barImage.filter")
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            Directory.CreateDirectory(AppPaths.UserDir);
            File.Copy(dialog.FileName, AppPaths.UserUiImage, overwrite: true);
            ImageStatus.Text = L.F("ui.barImage.changed", Path.GetFileName(dialog.FileName));
            _services.Settings.Save();
        }
        catch (Exception ex)
        {
            ImageStatus.Text = L.F("common.errorPrefix", ex.Message);
        }
    }

    private void OnResetImage(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(AppPaths.UserUiImage)) File.Delete(AppPaths.UserUiImage);
            ImageStatus.Text = L.T("ui.barImage.reset");
            _services.Settings.Save();
        }
        catch (Exception ex)
        {
            ImageStatus.Text = L.F("common.errorPrefix", ex.Message);
        }
    }

    private void OnResetTheme(object sender, RoutedEventArgs e)
    {
        _loading = true;

        var defaults = new ThemeSettings();
        _services.Settings.Update(s => s.Theme = defaults.Clone());

        SelectPreset(defaults.Preset);
        FrameOpacitySlider.Value = defaults.FrameOpacity;
        BackgroundOpacitySlider.Value = defaults.BackgroundOpacity;
        FontCombo.SelectedItem = Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .FirstOrDefault(name => string.Equals(name, defaults.FontFamily, StringComparison.OrdinalIgnoreCase));
        FontSizeSlider.Value = defaults.FontSize;

        _loading = false;
        UpdateLabels();
        ImageStatus.Text = L.T("ui.reset.done");
    }
}
