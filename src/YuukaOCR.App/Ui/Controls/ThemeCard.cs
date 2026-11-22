using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using YuukaOCR.Core.Localization;
using YuukaOCR.Core.Settings;

namespace YuukaOCR.App.Ui.Controls;

/// <summary>
/// One preset card in Settings → Appearance. The preview colours are pushed in from the preset itself
/// (not from the active theme), so the cards keep showing what each preset looks like while the window
/// around them repaints.
/// </summary>
public partial class ThemeCard : UserControl
{
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(ThemeCard), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty PresetNameProperty = DependencyProperty.Register(
        nameof(PresetName), typeof(string), typeof(ThemeCard), new PropertyMetadata(""));

    public static readonly DependencyProperty BarBrushProperty = RegisterBrush(nameof(BarBrush));
    public static readonly DependencyProperty AccentBrushProperty = RegisterBrush(nameof(AccentBrush));
    public static readonly DependencyProperty PreviewBackgroundProperty = RegisterBrush(nameof(PreviewBackground));
    public static readonly DependencyProperty PreviewPanelProperty = RegisterBrush(nameof(PreviewPanel));
    public static readonly DependencyProperty PreviewTextProperty = RegisterBrush(nameof(PreviewText));
    public static readonly DependencyProperty PreviewSubtleProperty = RegisterBrush(nameof(PreviewSubtle));
    public static readonly DependencyProperty PreviewBorderProperty = RegisterBrush(nameof(PreviewBorder));

    public ThemeCard() => InitializeComponent();

    /// <summary>Raised when the user picks this card (not when the tab selects it programmatically).</summary>
    public event Action<ThemePreset>? Selected;

    public ThemePreset? Preset { get; private set; }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public string PresetName
    {
        get => (string)GetValue(PresetNameProperty);
        set => SetValue(PresetNameProperty, value);
    }

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public Brush PreviewBackground
    {
        get => (Brush)GetValue(PreviewBackgroundProperty);
        set => SetValue(PreviewBackgroundProperty, value);
    }

    public Brush PreviewPanel
    {
        get => (Brush)GetValue(PreviewPanelProperty);
        set => SetValue(PreviewPanelProperty, value);
    }

    public Brush PreviewText
    {
        get => (Brush)GetValue(PreviewTextProperty);
        set => SetValue(PreviewTextProperty, value);
    }

    public Brush PreviewSubtle
    {
        get => (Brush)GetValue(PreviewSubtleProperty);
        set => SetValue(PreviewSubtleProperty, value);
    }

    /// <summary>Hairline around the mini window — a light preset would otherwise melt into the light card.</summary>
    public Brush PreviewBorder
    {
        get => (Brush)GetValue(PreviewBorderProperty);
        set => SetValue(PreviewBorderProperty, value);
    }

    public void Bind(ThemePreset preset)
    {
        Preset = preset;
        PresetName = L.T(preset.NameKey);

        var palette = preset.Palette;
        BarBrush = Theme.Brush(palette.Bar);
        AccentBrush = Theme.Brush(palette.Accent);
        PreviewBackground = Theme.Brush(palette.Background);
        PreviewPanel = Theme.Brush(palette.Panel);
        PreviewText = Theme.Brush(palette.Text);
        PreviewSubtle = Theme.Brush(palette.Subtle);
        PreviewBorder = Theme.Brush(palette.Border);
    }

    private void OnChecked(object sender, RoutedEventArgs e)
    {
        if (Preset is not null) Selected?.Invoke(Preset);
    }

    private static DependencyProperty RegisterBrush(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(ThemeCard), new PropertyMetadata(Brushes.Transparent));
}
