using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace YuukaOCR.App.Ui.Controls;

/// <summary>
/// Draws a slider's discrete tick marks and their numeric labels.
///
/// A WPF <see cref="System.Windows.Controls.Slider"/> paints nothing for <c>TickPlacement</c> unless its
/// template contains a <c>TickBar</c>, and this app's slider template is hand-written — so this element
/// *is* that TickBar: one tick per <c>TickFrequency</c>, ticks up to the current value in the accent
/// colour, and labels that are skipped automatically when they would collide.
///
/// It lives in the template's second row, so it knows nothing about the track: <see cref="Inset"/>
/// matches half the thumb width, which is exactly how far the thumb centre travels.
///
/// Dense sliders (a wide range with many steps) would otherwise read as a solid comb, so when the
/// marks sit closer together than <see cref="DenseSpacing"/> every second mark is drawn fainter.
/// </summary>
public sealed class TickMarks : FrameworkElement
{
    private const double LabelSize = 8.5;
    private const double TickLength = 6.0;
    private const double LabelGap = 1.5;

    /// <summary>Below this gap (DIP) between two marks, alternate marks are drawn fainter.</summary>
    private const double DenseSpacing = 22.0;

    /// <summary>Opacity of the fainter half on a dense slider.</summary>
    private const double MinorOpacity = 0.38;

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(TickMarks),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(TickMarks),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(TickMarks),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(TickMarks),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Distance from each end where the first/last tick sits — half the thumb width.</summary>
    public static readonly DependencyProperty InsetProperty = DependencyProperty.Register(
        nameof(Inset), typeof(double), typeof(TickMarks),
        new FrameworkPropertyMetadata(7.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowLabelsProperty = DependencyProperty.Register(
        nameof(ShowLabels), typeof(bool), typeof(TickMarks),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender |
                                            FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty TickBrushProperty = DependencyProperty.Register(
        nameof(TickBrush), typeof(Brush), typeof(TickMarks),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PassedBrushProperty = DependencyProperty.Register(
        nameof(PassedBrush), typeof(Brush), typeof(TickMarks),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(TickMarks),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Stroke width of a tick. 3.0 is the doubled baseline (the original was 1.5, which vanished on a
    /// 4K screen); a slider that wants heavier marks raises it through <see cref="SliderTicks"/>.
    /// </summary>
    public static readonly DependencyProperty TickThicknessProperty = DependencyProperty.Register(
        nameof(TickThickness), typeof(double), typeof(TickMarks),
        new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelFontFamilyProperty = DependencyProperty.Register(
        nameof(LabelFontFamily), typeof(FontFamily), typeof(TickMarks),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Step
    {
        get => (double)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Inset
    {
        get => (double)GetValue(InsetProperty);
        set => SetValue(InsetProperty, value);
    }

    public bool ShowLabels
    {
        get => (bool)GetValue(ShowLabelsProperty);
        set => SetValue(ShowLabelsProperty, value);
    }

    public Brush TickBrush
    {
        get => (Brush)GetValue(TickBrushProperty);
        set => SetValue(TickBrushProperty, value);
    }

    public Brush PassedBrush
    {
        get => (Brush)GetValue(PassedBrushProperty);
        set => SetValue(PassedBrushProperty, value);
    }

    public Brush LabelBrush
    {
        get => (Brush)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public double TickThickness
    {
        get => (double)GetValue(TickThicknessProperty);
        set => SetValue(TickThicknessProperty, value);
    }

    public FontFamily LabelFontFamily
    {
        get => (FontFamily)GetValue(LabelFontFamilyProperty);
        set => SetValue(LabelFontFamilyProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Half the stroke sticks out above the line start, so the tick row has to reserve it.
        var overhang = Math.Max(0, TickThickness / 2 - 0.75);
        var height = TickLength + overhang + 1;
        return new Size(0, ShowLabels ? height + LabelGap + LabelSize * 1.5 + 1 : height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 1 || height <= 0) return;

        var min = Minimum;
        var max = Maximum;
        if (double.IsNaN(min) || double.IsNaN(max) || max <= min) return;

        // Without a tick frequency there is nothing discrete to show — fall back to ten even steps.
        var step = Step > 0 ? Step : (max - min) / 10.0;

        // Ticks at min + n*step, plus the maximum itself so the last mark always names the top of the
        // range (e.g. 20..600 by 100 ends on 600, not 520).
        var ticks = new List<double>();
        for (var value = min; value <= max + 1e-9; value += step) ticks.Add(value);
        if (ticks.Count < 2) return;
        if (max - ticks[^1] > 1e-9) ticks.Add(max);

        var inset = Math.Clamp(Inset, 0, width / 2);
        var span = width - inset * 2;
        var valueFraction = (Math.Clamp(Value, min, max) - min) / (max - min);

        // Thick marks: a 1.5 px tick disappears on a 4K screen at 100 % scaling, so the baseline is
        // 3.0 and a slider can ask for more through SliderTicks.TickThickness.
        var thickness = Math.Clamp(TickThickness, 1.0, 12.0);
        var pending = new Pen(TickBrush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var passed = new Pen(PassedBrush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pending.Freeze();
        passed.Freeze();

        // Half the stroke overhangs the start of the line; keep it inside the element.
        var top = Math.Max(thickness / 2, 0.75);
        var bottom = Math.Min(height, top + TickLength);

        // A dense slider (0..32000 by 4000, 0..600 by 50 ...) turns into a solid comb at full strength.
        // Faint alternate marks keep the steps countable without hiding them.
        var spacing = ticks.Count > 1 ? span / (ticks.Count - 1) : span;
        var dense = spacing < DenseSpacing;

        for (var i = 0; i < ticks.Count; i++)
        {
            var fraction = (ticks[i] - min) / (max - min);
            var x = Math.Round(inset + span * fraction, 1);
            var pen = fraction <= valueFraction + 1e-9 ? passed : pending;

            // Faint only the marks between the named ones, and only when they are crowded.
            var minor = dense && i % 2 == 1 && i != ticks.Count - 1;
            if (minor) dc.PushOpacity(MinorOpacity);
            dc.DrawLine(pen, new Point(x, top), new Point(x, bottom));
            if (minor) dc.Pop();
        }

        if (!ShowLabels) return;

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(LabelFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var format = Math.Abs(step - Math.Round(step)) < 1e-9 ? "0" : "0.#";
        var labelTop = Math.Min(height, top + TickLength + LabelGap);
        var lastRight = double.NegativeInfinity;

        // Greedy left to right: a label that would touch the previous one is dropped, so a narrow
        // slider (or a big UI font) thins the numbers out instead of overprinting them.
        foreach (var tick in ticks)
        {
            var text = tick.ToString(format, CultureInfo.CurrentCulture);
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, LabelSize, LabelBrush, pixelsPerDip);

            var centre = inset + span * ((tick - min) / (max - min));
            var left = Math.Clamp(centre - formatted.Width / 2, 0, Math.Max(0, width - formatted.Width));
            if (left < lastRight + 3) continue;

            dc.DrawText(formatted, new Point(left, labelTop));
            lastRight = left + formatted.Width;
        }
    }
}

/// <summary>
/// Per-slider tick weight. The slider template forwards <see cref="Thickness"/> into its
/// <see cref="TickMarks"/>, so a tab can ask for heavier marks without its own template:
/// <c>&lt;Slider controls:SliderTicks.Thickness="6" ... /&gt;</c>.
/// </summary>
public static class SliderTicks
{
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.RegisterAttached(
        "Thickness", typeof(double), typeof(SliderTicks),
        new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static double GetThickness(DependencyObject element) => (double)element.GetValue(ThicknessProperty);

    public static void SetThickness(DependencyObject element, double value) =>
        element.SetValue(ThicknessProperty, value);
}
