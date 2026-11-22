using System.Windows.Controls;
using YuukaOCR.Core.Settings;

namespace YuukaOCR.App.Ui.Controls;

/// <summary>
/// Points a slider at its <see cref="SliderSpec"/>.
///
/// The XAML still carries a range so the designer preview is not nonsense, but this call is what the
/// running app uses: range, tick grid and change steps all come from <see cref="SliderSpecs"/>, so a
/// slider can never offer a value that <c>Normalize()</c> would snap away.
/// </summary>
public static class SliderSetup
{
    public static void Apply(Slider slider, string key)
    {
        var spec = SliderSpecs.Get(key);

        slider.Minimum = spec.Min;
        slider.Maximum = spec.Max;
        slider.TickFrequency = spec.Step;
        slider.SmallChange = spec.Step;
        slider.LargeChange = spec.Step;
        slider.Value = spec.Snap(slider.Value);
    }
}
