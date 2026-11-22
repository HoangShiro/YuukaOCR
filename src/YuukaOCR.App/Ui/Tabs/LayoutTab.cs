using System.Windows;
using System.Windows.Controls;
using YuukaOCR.App.Services;
using YuukaOCR.App.Ui.Controls;
using YuukaOCR.Core.Settings;

namespace YuukaOCR.App.Ui.Tabs;

public partial class LayoutTab : UserControl
{
    private AppServices _services = null!;
    private bool _loading;

    /// <summary>Raised by the Test button; the bar owns the result window and shows it.</summary>
    public event Action? TestRequested;

    public LayoutTab() => InitializeComponent();

    public void Initialize(AppServices services)
    {
        _services = services;
        _loading = true;

        var settings = _services.Settings.Settings;

        var button = settings.SubWindowPosition.ToLowerInvariant() switch
        {
            "left" => LeftButton,
            "right" => RightButton,
            "top" => TopButton,
            "bottom" => BottomButton,
            _ => AutoButton
        };
        button.IsChecked = true;

        // Ranges and tick grids come from SliderSpecs; the XAML values are just the designer preview.
        SliderSetup.Apply(SpacingSlider, SliderSpecs.SubWindowSpacing);
        SliderSetup.Apply(MinWidthSlider, SliderSpecs.MinSubWindowWidth);

        SpacingSlider.Value = settings.SubWindowSpacing;
        MinWidthSlider.Value = settings.MinSubWindowWidth;
        UpdateLabels();

        _loading = false;
    }

    private void UpdateLabels()
    {
        SpacingValue.Text = $"{SpacingSlider.Value:0}";
        MinWidthValue.Text = $"{MinWidthSlider.Value:0}";
    }

    private void OnPositionChecked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var value = sender switch
        {
            _ when ReferenceEquals(sender, LeftButton) => "left",
            _ when ReferenceEquals(sender, RightButton) => "right",
            _ when ReferenceEquals(sender, TopButton) => "top",
            _ when ReferenceEquals(sender, BottomButton) => "bottom",
            _ => "auto"
        };

        _services.Settings.Update(s => s.SubWindowPosition = value);
    }

    private void OnSpacingChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        UpdateLabels();
        if (_loading) return;
        _services.Settings.Update(s => s.SubWindowSpacing = (int)Math.Round(e.NewValue));
    }

    private void OnMinWidthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        UpdateLabels();
        if (_loading) return;
        _services.Settings.Update(s => s.MinSubWindowWidth = (int)Math.Round(e.NewValue));
    }

    private void OnResetPositions(object sender, RoutedEventArgs e)
        => _services.Settings.Update(s => s.Positions.Clear());

    /// <summary>
    /// Preview: the settings are already saved (every control writes on change), so showing the bubble
    /// now is exactly what the next OCR result will look like.
    /// </summary>
    private void OnTestPosition(object sender, RoutedEventArgs e) => TestRequested?.Invoke();
}
