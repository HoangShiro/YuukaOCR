using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YuukaOCR.App.Services;
using YuukaOCR.App.Ui;
using YuukaOCR.App.Ui.Controls;
using YuukaOCR.App.Ui.Tabs;
using YuukaOCR.Core.Agent;
using YuukaOCR.Core.Settings;

namespace XamlCheck;

/// <summary>
/// Layout probes for the things a compiler cannot see: a thumb clipped by its row, a fixed-height input
/// that is shorter than the text it has to show, a tick row drawn under the thumb, and an action bar that
/// forgets to react to settings.
///
/// Everything here is measured from the real templates and the real visual tree — no screenshots, no
/// pixel guesses — so it runs in the same headless pass as the XAML smoke test.
/// </summary>
internal static class LayoutProbe
{
    private static int _failures;

    public static int Run(AppServices services)
    {
        _failures = 0;

        SliderGeometry();
        InputClipping(services);
        TickInk();
        ThemeContrast();
        ActionButtons(services);
        ActionBarSync(services);

        return _failures;
    }

    // ------------------------------------------------------------- sliders

    /// <summary>
    /// The thumb must sit completely inside the slider: a 15 DIP track row clipped the top and bottom of
    /// an 18 DIP thumb, and the tick row must start below the thumb instead of behind it.
    /// </summary>
    private static void SliderGeometry()
    {
        var tabs = new (string Name, Func<FrameworkElement> Create)[]
        {
            ("ThemeTab", () => new ThemeTab()),
            ("LayoutTab", () => new LayoutTab()),
            ("HookingTab", () => new HookingTab()),
            ("PromptAgentTab", () => new PromptAgentTab())
        };

        foreach (var (name, create) in tabs)
        {
            var tab = create();
            var host = Host(tab);

            var sliders = Descendants<Slider>(tab).ToList();
            if (sliders.Count == 0)
            {
                Fail($"{name}: no sliders found");
                host.Close();
                continue;
            }

            var clipped = 0;
            var overlapped = 0;

            foreach (var slider in sliders)
            {
                if (Find<Thumb>(slider) is not { } thumb) continue;

                var bounds = thumb.TransformToAncestor(slider).TransformBounds(
                    new Rect(0, 0, thumb.ActualWidth, thumb.ActualHeight));

                if (bounds.Top < -0.5 || bounds.Bottom > slider.ActualHeight + 0.5)
                {
                    clipped++;
                    Console.WriteLine($"      {name}: {slider.Name} thumb {bounds.Top:0.0}..{bounds.Bottom:0.0} " +
                                      $"outside 0..{slider.ActualHeight:0.0}");
                }

                if (slider.Template.FindName("PART_Ticks", slider) is TickMarks ticks &&
                    ticks.TransformToAncestor(slider) is { } t)
                {
                    var tickTop = t.Transform(new Point(0, 0)).Y;
                    if (tickTop < bounds.Bottom - 0.5) overlapped++;
                }
            }

            // The Window edge snapping sliders ask for doubled marks; everything else keeps the baseline.
            var thick = sliders.Count(s => s.Template.FindName("PART_Ticks", s) is TickMarks t && t.TickThickness > 5.5);
            var expectedThick = name == "HookingTab" ? 5 : 0;

            if (clipped == 0 && overlapped == 0 && thick == expectedThick)
                Pass($"{name}: {sliders.Count} sliders, every thumb fully inside, ticks start below it" +
                     (expectedThick > 0 ? $", {thick} with doubled marks" : ""));
            else
                Fail($"{name}: {clipped} clipped thumbs, {overlapped} tick rows under a thumb");

            host.Close();
        }
    }

    // -------------------------------------------------------------- inputs

    /// <summary>
    /// An input that got less height than it asked for clips its text — that is exactly what a hard-coded
    /// Height did once the theme font or the UI scale grew. 100 %, 125 % and 150 % are the reported cases.
    /// </summary>
    private static void InputClipping(AppServices services)
    {
        var original = services.Settings.Settings.UiScale;

        var result = new ResultWindow(services)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -6000,
            Top = -6000,
            ShowActivated = false
        };

        try
        {
            result.Show();
            result.UpdateLayout();

            var chat = (TextBox)result.FindName("ChatBox");
            chat.Text = "Ask about this text — a long line that must not be clipped";

            foreach (var scale in new[] { 100, 125, 150 })
            {
                services.Settings.Update(s => s.UiScale = scale, save: false);
                result.ApplyAppearance();
                result.UpdateLayout();

                var needs = chat.FontSize * 1.42 + chat.Padding.Top + chat.Padding.Bottom;
                var ok = double.IsNaN(chat.Height) && chat.ActualHeight + 0.5 >= Math.Max(chat.DesiredSize.Height, needs);

                if (ok)
                    Pass($"ChatBox at {scale} %: {chat.ActualHeight:0.0} DIP for a {needs:0.0} DIP line");
                else
                    Fail($"ChatBox at {scale} %: height {chat.ActualHeight:0.0}, wants " +
                         $"{Math.Max(chat.DesiredSize.Height, needs):0.0} (fixed Height={chat.Height})");
            }
        }
        catch (Exception ex)
        {
            Fail($"ChatBox: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            result.Close();
            services.Settings.Update(s => s.UiScale = original, save: false);
        }

        // The action editor's Name field: the reported clipping was a 26 DIP box with a taller line box.
        try
        {
            var tab = new PromptAgentTab();
            tab.Initialize(services);
            var host = Host(tab);

            if (tab.FindName("ActionTabs") is Panel tabs && tabs.Children.OfType<Button>().FirstOrDefault() is { } first)
            {
                first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                host.UpdateLayout();
            }

            if (tab.FindName("ActionNameBox") is TextBox nameBox)
            {
                nameBox.Text = "Summarize the selection";
                host.UpdateLayout();

                var needs = nameBox.FontSize * 1.42 + nameBox.Padding.Top + nameBox.Padding.Bottom;
                if (double.IsNaN(nameBox.Height) && nameBox.ActualHeight + 0.5 >= Math.Max(nameBox.DesiredSize.Height, needs))
                    Pass($"ActionNameBox: {nameBox.ActualHeight:0.0} DIP for a {needs:0.0} DIP line");
                else
                    Fail($"ActionNameBox: height {nameBox.ActualHeight:0.0}, wants " +
                         $"{Math.Max(nameBox.DesiredSize.Height, needs):0.0} (fixed Height={nameBox.Height})");
            }
            else
            {
                Fail("ActionNameBox not found");
            }

            host.Close();
        }
        catch (Exception ex)
        {
            Fail($"ActionNameBox: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // --------------------------------------------------------------- theme

    /// <summary>
    /// Accent-filled buttons have to stay readable in every preset: the glyph is YuukaOnAccent, which is
    /// derived from the accent's own luminance, so a light theme gets dark ink and a dark theme light ink.
    /// The old transparent button kept on-accent ink and vanished on the dark presets.
    /// </summary>
    private static void ThemeContrast()
    {
        var host = Host(new Border { Width = 200, Height = 80 });

        try
        {
            var failures = 0;

            foreach (var preset in ThemePresets.All)
            {
                Theme.Apply(host, new ThemeSettings { Preset = preset.Id, BackgroundOpacity = 100, FrameOpacity = 100 });

                var accent = ((SolidColorBrush)host.FindResource("YuukaAccent")).Color;
                var ink = ((SolidColorBrush)host.FindResource("YuukaOnAccent")).Color;
                var bg = ((SolidColorBrush)host.FindResource("YuukaBg")).Color;

                var normal = Contrast(ink, accent);
                var hover = Contrast(ink, Composite(accent, 0.82, bg));
                var pressed = Contrast(ink, Composite(accent, 0.62, bg));
                var surface = Contrast(accent, bg);

                if (normal < 3.0 || hover < 3.0 || pressed < 2.0 || surface < 1.15)
                {
                    failures++;
                    Console.WriteLine($"      {preset.Id}: glyph {normal:0.00}, hover {hover:0.00}, " +
                                      $"pressed {pressed:0.00}, surface {surface:0.00}");
                }
            }

            if (failures == 0)
                Pass($"accent buttons readable in all {ThemePresets.All.Count} presets " +
                     $"(worst glyph contrast {ThemePresets.All.Min(p => Contrast(
                         ColorOf(ThemePresets.Palette(p.Id).OnAccent), ColorOf(ThemePresets.Palette(p.Id).Accent))):0.0}:1)");
            else
                Fail($"accent buttons unreadable in {failures} presets");
        }
        catch (Exception ex)
        {
            Fail($"theme contrast: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            host.Close();
        }
    }

    /// <summary>The bar's own buttons must use the accent style — that is the fix, not a coincidence.</summary>
    private static void ActionButtons(AppServices services)
    {
        var result = new ResultWindow(services);
        try
        {
            var style = result.FindResource("YuukaAccentIconButton");
            var send = result.FindName("SendButton") as Button;
            var copy = result.FindName("SelectionCopyButton") as Button;
            var bar = result.FindName("ActionBar") as Panel;

            var wrong = new List<string>();
            if (!ReferenceEquals(send?.Style, style)) wrong.Add("SendButton");
            if (!ReferenceEquals(copy?.Style, style)) wrong.Add("SelectionCopyButton");
            wrong.AddRange(bar is null
                ? ["ActionBar"]
                : bar.Children.OfType<Button>().Where(b => !ReferenceEquals(b.Style, style)).Select(b => $"action '{b.ToolTip}'"));

            if (wrong.Count == 0)
                Pass($"send, floating copy and {bar!.Children.OfType<Button>().Count()} action buttons use the accent style");
            else
                Fail($"not on the accent style: {string.Join(", ", wrong)}");
        }
        catch (Exception ex)
        {
            Fail($"action buttons: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            result.Close();
        }
    }

    private static Color ColorOf(YuukaOCR.Core.Rgba c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    /// <summary>WCAG relative-luminance contrast ratio, 1.0 (identical) to 21.0 (black on white).</summary>
    private static double Contrast(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>A translucent surface over an opaque one — what a hover/pressed opacity actually paints.</summary>
    private static Color Composite(Color top, double alpha, Color bottom) => Color.FromRgb(
        (byte)Math.Round(top.R * alpha + bottom.R * (1 - alpha)),
        (byte)Math.Round(top.G * alpha + bottom.G * (1 - alpha)),
        (byte)Math.Round(top.B * alpha + bottom.B * (1 - alpha)));

    // ------------------------------------------------------------- tick ink

    /// <summary>
    /// Dense sliders must alternate: every second mark fainter, so 33 marks read as steps instead of a
    /// comb. Rendered and sampled, because "looks fainter" is a pixel fact.
    /// </summary>
    private static void TickInk()
    {
        var dense = MakeTicks(0, 32000, 1000);
        var sparse = MakeTicks(0, 400, 100);

        var panel = new Canvas { Width = 420, Height = 80 };
        Canvas.SetTop(dense, 4);
        Canvas.SetTop(sparse, 44);
        panel.Children.Add(dense);
        panel.Children.Add(sparse);

        var host = Host(panel, width: 440, height: 100);

        try
        {
            var bitmap = new RenderTargetBitmap(420, 80, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(panel);

            var denseInk = Sample(bitmap, dense.TransformToAncestor(panel).Transform(new Point(0, 0)).Y);
            var sparseInk = Sample(bitmap, sparse.TransformToAncestor(panel).Transform(new Point(0, 0)).Y);

            if (denseInk.Count < 5 || sparseInk.Count < 3)
            {
                Fail($"tick ink: sampled only {denseInk.Count}/{sparseInk.Count} marks");
            }
            else
            {
                var denseEven = denseInk.Where((_, i) => i % 2 == 0).Average();
                var denseOdd = denseInk.Where((_, i) => i % 2 == 1).Average();
                var sparseMin = sparseInk.Min();
                var sparseMax = sparseInk.Max();

                if (denseOdd < denseEven * 0.75)
                    Pass($"dense ticks alternate: major {denseEven:0.0} ink vs minor {denseOdd:0.0}");
                else
                    Fail($"dense ticks do not alternate: major {denseEven:0.0}, minor {denseOdd:0.0}");

                if (sparseInk.Count == 5 && sparseMin > sparseMax * 0.85)
                    Pass($"sparse ticks stay uniform: {sparseInk.Count} marks, {sparseMin:0.0}..{sparseMax:0.0}");
                else
                    Fail($"sparse ticks were faded: {sparseInk.Count} marks, {sparseMin:0.0}..{sparseMax:0.0} " +
                         $"[{string.Join(", ", sparseInk.Select(v => v.ToString("0.0")))}]");
            }
        }
        catch (Exception ex)
        {
            Fail($"tick ink: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            host.Close();
        }
    }

    private static TickMarks MakeTicks(double min, double max, double step) => new()
    {
        Width = 400,
        Height = 24,
        Inset = 9,
        Minimum = min,
        Maximum = max,
        Step = step,
        Value = min,
        ShowLabels = false,
        TickBrush = Brushes.Gray,
        PassedBrush = Brushes.Gray,
        LabelBrush = Brushes.Gray
    };

    /// <summary>
    /// Ink per mark, left to right, found by scanning the tick band for vertical runs instead of assuming
    /// where each mark sits — a sub-pixel offset would otherwise silently sample the gap between marks.
    /// </summary>
    private static List<double> Sample(RenderTargetBitmap bitmap, double topOffset)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        var top = (int)Math.Round(topOffset);
        var ink = new List<double>();
        var runPeak = 0.0;
        var inRun = false;

        for (var x = 0; x < bitmap.PixelWidth; x++)
        {
            var peak = 0;
            for (var y = Math.Max(0, top); y < Math.Min(bitmap.PixelHeight, top + 10); y++)
                peak = Math.Max(peak, pixels[y * stride + x * 4 + 3]);

            if (peak > 40)
            {
                inRun = true;
                runPeak = Math.Max(runPeak, peak);
            }
            else if (inRun)
            {
                ink.Add(runPeak);
                runPeak = 0;
                inRun = false;
            }
        }

        if (inRun) ink.Add(runPeak);
        return ink;
    }

    // --------------------------------------------------------- action bar

    /// <summary>
    /// Adding, renaming or removing an action must change the bar without reopening the bubble: the
    /// settings store raises Changed, AppearanceSync fans it out, the bar rebuilds.
    /// </summary>
    private static void ActionBarSync(AppServices services)
    {
        // The real app wires this in App.OnStartup; the harness has to do it itself or no window would
        // ever hear about a settings change.
        AppearanceSync.Initialize(services.Settings);

        var snapshot = services.Settings.Settings.Actions.Select(a => a.Clone()).ToList();
        var result = new ResultWindow(services);

        try
        {
            if (result.FindName("ActionBar") is not Panel bar)
            {
                Fail("ActionBar not found");
                return;
            }

            int Buttons() => bar.Children.OfType<Button>().Count();

            var before = Buttons();

            services.Settings.Update(s => s.Actions.Add(new AgentAction
            {
                Id = "probe-sync",
                Name = "Probe action",
                Icon = "search",
                Instruction = "Reply with OK."
            }), save: false);
            Pump();

            var added = Buttons();
            var tooltip = bar.Children.OfType<Button>().LastOrDefault()?.ToolTip as string;

            services.Settings.Update(s =>
            {
                var action = s.Actions.First(a => a.Id == "probe-sync");
                action.Name = "Renamed action";
            }, save: false);
            Pump();

            var renamed = bar.Children.OfType<Button>().LastOrDefault()?.ToolTip as string;

            services.Settings.Update(s => s.Actions.RemoveAll(a => a.Id == "probe-sync"), save: false);
            Pump();
            var removed = Buttons();

            if (added == before + 1 && removed == before && tooltip == "Probe action" && renamed == "Renamed action")
                Pass($"action bar sync: {before} -> {added} -> {renamed} -> {removed} buttons");
            else
                Fail($"action bar sync: {before} -> {added} ({tooltip}) -> {renamed} -> {removed}");
        }
        catch (Exception ex)
        {
            Fail($"action bar sync: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            services.Settings.Update(s => s.Actions = snapshot.Select(a => a.Clone()).ToList(), save: false);
            result.Close();
        }
    }

    // -------------------------------------------------------------- helpers

    private static Window Host(FrameworkElement content, double width = 460, double height = 760)
    {
        var host = new Window
        {
            Width = width,
            Height = height,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -6000,
            Top = -6000,
            Content = content
        };

        host.Show();
        host.UpdateLayout();
        return host;
    }

    private static T? Find<T>(DependencyObject root) where T : DependencyObject =>
        Descendants<T>(root).FirstOrDefault();

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    /// <summary>AppearanceSync marshals through the dispatcher, so let the queue drain before asserting.</summary>
    private static void Pump() =>
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.Background);

    private static void Pass(string message) => Console.WriteLine($"OK    {message}");

    private static void Fail(string message)
    {
        _failures++;
        Console.WriteLine($"FAIL  {message}");
    }
}
