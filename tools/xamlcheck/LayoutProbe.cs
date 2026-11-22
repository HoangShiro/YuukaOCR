using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YuukaOCR.App.Services;
using YuukaOCR.App.Ui;
using YuukaOCR.App.Ui.Controls;
using YuukaOCR.App.Ui.Tabs;
using YuukaOCR.Core.Agent;
using YuukaOCR.Core.Interop;
using YuukaOCR.Core.Localization;
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
        SnappingTicks();
        SelectionPopupLifetime(services);
        SelectionContext(services);
        ChatSelection(services);
        ActionIconTheme(services);
        HookFollow();
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

    /// <summary>
    /// The *Window edge snapping* sliders doubled their tick count (steps 50 / 25 / 50 instead of
    /// 100 / 50 / 100). Counting the marks that are actually drawn — after `SliderSetup.Apply`, at the
    /// width the tab gives them — is the only way to know the new grid reached the screen and not just
    /// the spec table.
    /// </summary>
    private static void SnappingTicks()
    {
        var snapping = new (string Name, string Key)[]
        {
            ("ProximityTopSlider", SliderSpecs.HookProximityTop),
            ("ProximityBottomSlider", SliderSpecs.HookProximityBottom),
            ("OffsetTopSlider", SliderSpecs.HookOffsetTop),
            ("OffsetBottomSlider", SliderSpecs.HookOffsetBottom),
            ("UnhookSlider", SliderSpecs.UnhookDistance)
        };

        var tab = new HookingTab();
        var host = Host(tab);

        try
        {
            var drawn = new List<string>();

            foreach (var (name, key) in snapping)
            {
                if (tab.FindName(name) is not Slider slider)
                {
                    Fail($"snapping ticks: {name} is missing from the Hooking tab");
                    return;
                }

                SliderSetup.Apply(slider, key);
                slider.UpdateLayout();

                if (slider.Template.FindName("PART_Ticks", slider) is not TickMarks ticks)
                {
                    Fail($"snapping ticks: {name} has no tick row");
                    return;
                }

                var spec = SliderSpecs.Get(key);
                var expected = (int)Math.Round((spec.Max - spec.Min) / spec.Step) + 1;

                var width = (int)Math.Ceiling(ticks.ActualWidth);
                var height = (int)Math.Ceiling(ticks.ActualHeight);
                if (width <= 0 || height <= 0)
                {
                    Fail($"snapping ticks: {name} tick row is {width}x{height}");
                    return;
                }

                // Render through a VisualBrush: RenderTargetBitmap.Render(ticks) would include the
                // element's own layout offset and paint it outside a bitmap sized to the element.
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                    dc.DrawRectangle(new VisualBrush(ticks), null, new Rect(0, 0, width, height));

                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);

                var marks = Sample(bitmap, 0);
                if (marks.Count != expected)
                {
                    Fail($"snapping ticks: {name} draws {marks.Count} marks over {width} DIP, expected {expected}");
                    return;
                }

                drawn.Add($"{name.Replace("Slider", "")} {marks.Count}");
            }

            Pass($"window edge snapping: {string.Join(", ", drawn)} marks drawn at the tab's own width");
        }
        catch (Exception ex)
        {
            Fail($"snapping ticks: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            host.Close();
        }
    }

    // -------------------------------------------------------------- inputs
    /// <summary>
    /// The floating copy button must not outlive the selection it belongs to. It used to stay on screen
    /// after the bubble lost focus or after a click somewhere else in the window, because the popup is
    /// sticky (`StaysOpen=True` — a self-closing popup eats the drag that creates a selection).
    /// </summary>
    private static void SelectionPopupLifetime(AppServices services)
    {
        var result = new ResultWindow(services);

        try
        {
            if (result.FindName("ResultBox") is not TextBox box || result.FindName("SelectionPopup") is not Popup popup)
            {
                Fail("selection popup: ResultBox/SelectionPopup not found");
                return;
            }

            result.ShowText("Yuuka reads the text inside the selection.", null, activate: false);

            var checks = new List<string>();

            // 1. A selection opens it (the baseline the other cases start from).
            box.Select(0, 5);
            if (!popup.IsOpen)
            {
                Fail("selection popup: a selection did not open the copy button");
                return;
            }

            // 2. A click elsewhere in the bubble (the header, the action bar, the chat box).
            var elsewhere = result.FindName("HeaderRow") as DependencyObject ?? result;
            Click(result, elsewhere);
            checks.Add($"click-away:{popup.IsOpen == false}");

            // 3. A click inside the text is not "away": the selection change decides, not this handler.
            box.Select(0, 5);
            Click(result, box);
            checks.Add($"click-inside-kept:{popup.IsOpen}");

            // 4. An empty selection always closes it.
            box.Select(0, 0);
            checks.Add($"cleared:{popup.IsOpen == false}");

            // 5. Losing keyboard focus closes it, and so does hiding the window. Driven on a real
            //    (off-screen, never activated) window, because focus needs a presentation source.
            checks.AddRange(OffscreenPopupChecks());

            var failures = checks.Where(c => c.EndsWith(":False", StringComparison.Ordinal)).ToList();
            if (failures.Count > 0)
                Fail($"selection popup lifetime: {string.Join(", ", failures)}");
            else
                Pass($"selection copy button closes on {string.Join(", ", checks.Select(c => c.Split(':')[0]))}");
        }
        catch (Exception ex)
        {
            Fail($"selection popup lifetime: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            result.Close();
        }
    }

    /// <summary>
    /// Focus is the reported case: selecting text and then clicking into the chat box (or another app)
    /// leaves the selection behind, and the button used to stay floating over it. Off-screen and
    /// never activated, so the probe cannot steal focus from whatever the user is doing.
    /// </summary>
    private static List<string> OffscreenPopupChecks()
    {
        var checks = new List<string>();

        var box = new TextBox { Text = "Yuuka reads the text inside the selection.", Width = 240 };
        var host = new Window
        {
            Width = 260,
            Height = 60,
            Left = -4000,
            Top = -4000,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Content = box
        };

        var popup = new Popup
        {
            PlacementTarget = box,
            Placement = PlacementMode.Relative,
            AllowsTransparency = true,
            StaysOpen = true,
            Child = new Button { Width = 26, Height = 26, Content = "C" }
        };

        host.Show();
        var tracker = SelectionCopy.Track(box, popup);

        try
        {
            Keyboard.Focus(box);
            box.Select(0, 5);

            // `Track` owns the lifetime rules only; opening is the selection handler's job (covered by
            // the bubble probe above), so open the popup the way it would be open.
            popup.IsOpen = true;
            Keyboard.ClearFocus();
            checks.Add($"focus-lost:{!popup.IsOpen}");

            Keyboard.Focus(box);
            popup.IsOpen = true;
            host.Hide();
            checks.Add($"window-hidden:{!popup.IsOpen}");

            return checks;
        }
        finally
        {
            tracker.Dispose();
            popup.IsOpen = false;
            host.Close();
        }
    }

    /// <summary>Raises a left-button press the way the window would see it, with that element as the source.</summary>
    private static void Click(Window window, DependencyObject source) =>
        window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
            Source = source
        });

    /// <summary>
    /// The agent's context must survive the user typing the question somewhere else. Selecting text and
    /// clicking into the chat box used to lose it: the live highlight went away, `SelectedText` followed,
    /// and the request silently fell back to the whole OCR result. The selection is pinned now, and the
    /// highlight stays painted so the user can still see what will be sent.
    /// </summary>
    private static void SelectionContext(AppServices services)
    {
        var result = new ResultWindow(services);

        try
        {
            const string body = "Yuuka reads the text inside the selection.";
            result.ShowText(body, null, activate: false);

            if (result.FindName("ResultBox") is not TextBox box || result.FindName("ChatBox") is not TextBox chat)
            {
                Fail("selection context: ResultBox/ChatBox not found");
                return;
            }

            var scope = result.FindName("ScopeText") as TextBlock;
            var checks = new List<string>();

            box.Select(0, 5);
            checks.Add($"pinned:{result.CurrentContext().Text == "Yuuka"}");

            // The reported flow: pick a sentence, then click into the chat box to type the question.
            Keyboard.Focus(box);
            Pump();
            Keyboard.Focus(chat);
            Pump();

            checks.Add($"focus-moved-keeps-selection:{box.SelectionLength == 5}");
            checks.Add($"focus-moved-keeps-context:{result.CurrentContext().Text == "Yuuka"}");
            checks.Add($"focus-moved-keeps-chip:{scope?.Text == L.T("result.context.selection")}");

            // The highlight must still be painted while the chat box has the focus, or the user cannot
            // see what the request is about. Measured in pixels, not from the property.
            checks.Add($"highlight-still-painted:{HighlightInk(box) > 0}");

            // Even if the live highlight goes away entirely, the pinned copy is what gets sent.
            box.Select(0, 0);
            checks.Add($"highlight-lost-keeps-context:{result.CurrentContext().Text == "Yuuka"}");

            // Dropping the selection on purpose (box focused) is still a scope reset.
            Keyboard.Focus(box);
            Pump();
            box.Select(0, 5);
            box.Select(0, 0);
            checks.Add($"dropped-on-purpose-resets:{result.CurrentContext().Text == body}");
            checks.Add($"dropped-on-purpose-chip:{scope?.Text == L.T("result.context.all")}");

            // A new result starts clean.
            box.Select(0, 5);
            result.ShowText(body, null, activate: false);
            checks.Add($"new-result-resets:{result.CurrentContext().Text == body}");

            checks.Add($"inactive-highlight:{box.IsInactiveSelectionHighlightEnabled}");

            var failures = checks.Where(c => c.EndsWith(":False", StringComparison.Ordinal)).ToList();
            if (failures.Count > 0)
                Fail($"selection context: {string.Join(", ", failures)}");
            else
                Pass($"selection context survives focus moving to the chat box ({checks.Count} checks)");
        }
        catch (Exception ex)
        {
            Fail($"selection context: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            result.Close();
        }
    }

    /// <summary>
    /// Pixels that change when the box's selection is dropped — i.e. the highlight the user sees.
    ///
    /// Compared as two renderings instead of matching the accent colour: WPF paints a selection at
    /// <see cref="TextBoxBase.SelectionOpacity"/> (40 % by default), so the pixels are a blend of the
    /// brush and whatever is behind it, and a colour match finds nothing even when the highlight is
    /// right there. A differential cannot miss it and does not care about the theme.
    /// </summary>
    private static int HighlightInk(TextBox box)
    {
        var withSelection = Snapshot(box);
        var start = box.SelectionStart;
        var length = box.SelectionLength;

        box.Select(0, 0);
        var without = Snapshot(box);
        box.Select(start, length);

        if (withSelection.Length != without.Length) return -1;

        var changed = 0;
        for (var i = 0; i < withSelection.Length; i++)
            if (withSelection[i] != without[i]) changed++;
        return changed;
    }

    /// <summary>
    /// The conversation must be readable *and* quotable: the user's turns and the agent's replies are both
    /// selectable, copyable, and keep their highlight when the focus moves on — while the
    /// label-then-message layout stays exactly what it was when the bodies were plain TextBlocks.
    /// </summary>
    private static void ChatSelection(AppServices services)
    {
        var result = new ResultWindow(services);

        try
        {
            result.ShowText("Yuuka reads the text inside the selection.", null, activate: false);

            result.RenderConversation(
            [
                new ChatTurn(AgentPrompts.UserRole, "what does this say?", "10:00"),
                new ChatTurn(AgentPrompts.AssistantRole, "It says the text inside the selection.", "10:01")
            ]);
            Pump();

            if (result.FindName("AnswerList") is not StackPanel list)
            {
                Fail("chat selection: AnswerList not found");
                return;
            }

            var bodies = list.Children.OfType<TextBox>().ToList();
            var labels = list.Children.OfType<TextBlock>().ToList();

            if (bodies.Count != 2)
            {
                Fail($"chat selection: {bodies.Count} selectable message(s), expected 2");
                return;
            }

            var order = string.Join(",", list.Children.OfType<FrameworkElement>()
                .Select(child => child is TextBox ? "body" : "label"));

            var theme = services.Settings.Settings.Theme;

            var checks = new List<string>
            {
                $"labels:{labels.Count == 2}",
                $"order:{order == "label,body,label,body"}",
                $"read-only:{bodies.All(b => b.IsReadOnly)}",
                $"no-border:{bodies.All(b => b.BorderThickness == new Thickness(0))}",
                $"transparent:{bodies.All(b => b.Background == Brushes.Transparent)}",
                $"wrapping:{bodies.All(b => b.TextWrapping == TextWrapping.Wrap)}",
                $"user-right:{bodies[0].HorizontalAlignment == HorizontalAlignment.Right && bodies[0].TextAlignment == TextAlignment.Right}",
                $"agent-left:{bodies[1].HorizontalAlignment == HorizontalAlignment.Left && bodies[1].TextAlignment == TextAlignment.Left}",
                $"inactive-highlight:{bodies.All(b => b.IsInactiveSelectionHighlightEnabled)}",
                $"tab-stop-off:{bodies.All(b => !b.IsTabStop)}",
                $"theme-ink:{Colour(bodies[0].Foreground) == ColorOf(theme.Text)}"
            };

            // Both sides: pick a word, see it painted, and the copy command becomes available.
            var ink = new List<string>();
            var chat = result.FindName("ChatBox") as TextBox;

            foreach (var body in bodies)
            {
                // The user selects with the mouse, so the box has the focus when the highlight appears;
                // moving the focus on afterwards is what has to keep it painted.
                Keyboard.Focus(body);
                Pump();

                body.Select(0, 4);
                Pump();
                checks.Add($"selectable:{body.SelectedText.Length == 4}");
                checks.Add($"copy-command:{ApplicationCommands.Copy.CanExecute(null, body)}");
                checks.Add($"sized:{body.ActualWidth > 1 && body.ActualHeight > 1}");

                var focused = HighlightInk(body);
                checks.Add($"highlight-painted:{focused > 0}");

                // Typing the follow-up question must not make the quoted message disappear.
                if (chat is not null)
                {
                    Keyboard.Focus(chat);
                    Pump();
                }

                var inactive = HighlightInk(body);
                ink.Add($"{focused}/{inactive}");
                checks.Add($"inactive-painted:{inactive > 0}");
            }

            checks.Add($"user-text:{bodies[0].SelectedText == "what"}");
            checks.Add($"agent-text:{bodies[1].SelectedText == "It s"}");

            var failures = checks.Where(c => c.EndsWith(":False", StringComparison.Ordinal)).ToList();
            if (failures.Count > 0)
                Fail($"chat selection: {string.Join(", ", failures)} " +
                     $"(ink {string.Join("/", ink)}, sizes " +
                     $"{string.Join(" ", bodies.Select(b => $"{b.ActualWidth:0}x{b.ActualHeight:0}"))})");
            else
                Pass($"conversation messages are selectable and copyable ({checks.Count} checks, " +
                     $"highlight ink {string.Join("/", ink)} px)");
        }
        catch (Exception ex)
        {
            Fail($"chat selection: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            result.Close();
        }
    }

    /// <summary>
    /// Action icons must take their ink from the live theme, not from a brush captured when the button was
    /// built: switching between a dark and the light preset used to leave every glyph — and the selected
    /// tile's accent fill — in the old palette until the tab was rebuilt.
    /// </summary>
    private static void ActionIconTheme(AppServices services)
    {
        AppearanceSync.Initialize(services.Settings);

        var snapshot = services.Settings.Settings.Actions.Select(a => a.Clone()).ToList();
        var originalPreset = services.Settings.Settings.Theme.Preset;

        services.Settings.Update(s => s.Actions.Add(new AgentAction
        {
            Id = "probe-icons",
            Name = "Probe icons",
            Icon = "chat",
            Instruction = "Reply with OK."
        }), save: false);

        var tab = new PromptAgentTab();
        tab.Initialize(services);
        var host = Host(tab, 520, 900);

        try
        {
            if (tab.FindName("IconPicker") is not Panel picker || tab.FindName("ActionTabs") is not Panel tabs)
            {
                Fail("action icon theme: IconPicker/ActionTabs not found");
                return;
            }

            var keys = ActionIcons.Keys.ToList();

            // The picker highlights the *selected action's* icon, and the selected action is the first one
            // in the list — not necessarily the one this probe appended.
            var selectedIcon = services.Settings.Settings.Actions.FirstOrDefault()?.Icon;
            var selectedIndex = keys.FindIndex(k => string.Equals(k, selectedIcon, StringComparison.OrdinalIgnoreCase));

            List<Color> Strokes() =>
                Descendants<System.Windows.Shapes.Path>(picker).Select(p => Colour(p.Stroke)).ToList();

            List<Color> Expected(YuukaOCR.Core.Settings.ThemePalette palette)
            {
                var list = Enumerable.Repeat(ColorOf(palette.Text), keys.Count).ToList();
                if (selectedIndex >= 0) list[selectedIndex] = ColorOf(palette.OnAccent);
                return list;
            }

            // The settings window's own repaint path, applied by hand because this host is not registered.
            var dark = ThemePresets.Palette(originalPreset);
            Theme.Apply(host, services.Settings.Settings.Theme);
            host.UpdateLayout();

            var darkStrokes = Strokes();
            var darkOk = darkStrokes.SequenceEqual(Expected(dark));

            // Switch to the light preset: everything on this tab has to follow, with no rebuild and no restart.
            services.Settings.Update(s => s.Theme.Preset = "daylight", save: false);
            var light = ThemePresets.Palette("daylight");
            Theme.Apply(host, services.Settings.Settings.Theme);
            host.UpdateLayout();

            var lightStrokes = Strokes();
            var lightOk = lightStrokes.SequenceEqual(Expected(light));

            var chip = tabs.Children.OfType<Button>().FirstOrDefault();
            var chipAccent = chip is not null && Colour(chip.Background) == ColorOf(light.Accent);

            // Painted, not just set: the chip template used to ignore Background entirely, so a selected
            // tab looked exactly like an unselected one.
            var accentPixels = chip is null ? 0 : PixelsOf(Snapshot(chip), ColorOf(light.Accent));
            var area = chip is null ? 0 : Math.Max(1, (int)(chip.ActualWidth * chip.ActualHeight));
            var chipPainted = accentPixels > area * 0.15;

            var checks = new List<string>
            {
                $"glyph-count:{darkStrokes.Count == keys.Count}",
                $"dark-ink:{darkOk}",
                $"light-ink:{lightOk}",
                $"switch-changes-ink:{lightStrokes.Count > 1 && lightStrokes[1] != darkStrokes[1]}",
                $"chip-accent:{chipAccent}",
                $"chip-painted:{chipPainted}"
            };

            var failures = checks.Where(c => c.EndsWith(":False", StringComparison.Ordinal)).ToList();
            if (failures.Count > 0)
                Fail($"action icon theme: {string.Join(", ", failures)} " +
                     $"({accentPixels}/{area} accent px, dark {Describe(darkStrokes)} light {Describe(lightStrokes)})");
            else
                Pass($"action icons follow a live theme switch ({checks.Count} checks, " +
                     $"{accentPixels}/{area} accent px on the selected tab)");
        }
        catch (Exception ex)
        {
            Fail($"action icon theme: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            host.Close();
            services.Settings.Update(s =>
            {
                s.Theme.Preset = originalPreset;
                s.Actions = snapshot.Select(a => a.Clone()).ToList();
            }, save: false);
        }
    }

    private static Color Colour(Brush? brush) => brush is SolidColorBrush solid ? solid.Color : Colors.Transparent;

    private static string Describe(IEnumerable<Color> colours) =>
        string.Join(" ", colours.Select(c => c.ToString()).Distinct());

    /// <summary>
    /// The follow math against real window bounds, not a made-up rectangle: a window moved sideways has to
    /// produce a target that moves by the same amount while the bar keeps its offset inside the window.
    /// This is the coordinate-system check (DWM frame bounds, physical pixels) behind the hook.
    /// </summary>
    private static void HookFollow()
    {
        var host = new Window
        {
            Width = 360,
            Height = 240,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 200,
            Top = 200,
            Content = new Border()
        };

        host.Show();

        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(host).Handle;
            var start = WindowTracker.GetBounds(handle);

            if (start is null)
            {
                Fail("hook follow: the host window has no bounds");
                return;
            }

            const double barWidth = 200;
            const double barHeight = 50;

            // The bar sits 40 px inside the window's left edge, hooked to the bottom edge.
            var anchor = WindowTracker.AnchorFor(start.Value, start.Value.Left + 40);

            var checks = new List<string>();
            var rest = WindowTracker.Follow(start.Value, barWidth, barHeight, HookEdge.Bottom,
                HookMode.Outside, 0, 0, anchor);
            checks.Add($"at-rest:{Math.Abs(rest.X - (start.Value.Left + 40)) < 0.001}");

            foreach (var step in new[] { 40, 120, 240 })
            {
                host.Left = 200 + step;
                Pump();

                var moved = WindowTracker.GetBounds(handle);
                if (moved is null)
                {
                    checks.Add($"moved-{step}:False");
                    continue;
                }

                var (x, y) = WindowTracker.Follow(moved.Value, barWidth, barHeight, HookEdge.Bottom,
                    HookMode.Outside, 0, 0, anchor);

                // Real DWM bounds carry the frame offsets, and Left is in DIPs while the bounds are physical
                // pixels, so compare the *actual* travel of the window with the bar's.
                var travel = moved.Value.Left - start.Value.Left;
                var offset = x - moved.Value.Left;
                checks.Add($"offset-{step}:{Math.Abs(offset - 40) < 0.001}");
                checks.Add($"travels-{step}:{Math.Abs(x - rest.X - travel) < 0.001}");
                checks.Add($"edge-{step}:{Math.Abs(y - (moved.Value.Top - barHeight)) < 0.001}");

                // The old formula — the bar's own fixed left edge, fed back in as an absolute target — is
                // what left the bar behind. It must not reproduce the new travel, or this probe is blind.
                var stale = WindowTracker.TargetFor(moved.Value, barWidth, barHeight, HookEdge.Bottom,
                    HookMode.Outside, 0, 0, rest.X);
                checks.Add($"old-formula-stays-put:{Math.Abs(stale.X - rest.X) < 0.001}");
            }

            var failures = checks.Where(c => c.EndsWith(":False", StringComparison.Ordinal)).ToList();
            if (failures.Count > 0) Fail($"hook follow: {string.Join(", ", failures)}");
            else Pass($"a hooked bar follows a real window across the screen ({checks.Count} checks)");
        }
        catch (Exception ex)
        {
            Fail($"hook follow: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            host.Close();
        }
    }

    /// <summary>Opaque pixels in a BGRA snapshot that match a colour (premultiplied, so opaque is verbatim).</summary>
    private static int PixelsOf(byte[] pixels, Color colour, int tolerance = 6)
    {
        var count = 0;
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            if (pixels[i + 3] < 200) continue;
            if (Math.Abs(pixels[i] - colour.B) > tolerance) continue;
            if (Math.Abs(pixels[i + 1] - colour.G) > tolerance) continue;
            if (Math.Abs(pixels[i + 2] - colour.R) > tolerance) continue;
            count++;
        }
        return count;
    }

    /// <summary>
    /// An element's own pixels as BGRA bytes, rendered at its own size. Through a
    /// <see cref="VisualBrush"/>: a direct <c>RenderTargetBitmap.Render(element)</c> bakes in the
    /// element's layout offset and paints outside a bitmap sized to the element.
    /// </summary>
    private static byte[] Snapshot(FrameworkElement element)
    {
        element.UpdateLayout();
        var width = (int)Math.Ceiling(element.ActualWidth);
        var height = (int)Math.Ceiling(element.ActualHeight);
        if (width <= 0 || height <= 0) return Array.Empty<byte>();

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, width, height));

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var stride = width * 4;
        var buffer = new byte[stride * height];
        bitmap.CopyPixels(buffer, stride, 0);
        return buffer;
    }

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
