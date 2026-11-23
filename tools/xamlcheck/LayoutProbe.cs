using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YuukaOCR.App.Services;
using YuukaOCR.App.Ui;
using YuukaOCR.App.Ui.Controls;
using YuukaOCR.App.Ui.Tabs;
using YuukaOCR.Core.Agent;
using YuukaOCR.Core.Interop;
using YuukaOCR.Core;
using YuukaOCR.Core.Localization;
using YuukaOCR.Core.Providers;
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
        ChatMarkdownRendering(services);
        ActionIconTheme(services);
        HookFollow();
        InputClipping(services);
        TickInk();
        ThemeContrast();
        ActionButtons(services);
        ActionBarSync(services);
        ChatContextReachesTheModel(services);
        MarkdownRendering(services);
        MinimumWidthDrivesTheBubble(services);
        WindowSizeMemoryRoundTrip(services);
        ResizeHandles();
        SettingsWindowGeometry(services);
        PromptEditsSurviveIconClicks(services);

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
            if (result.FindName("ResultBox") is not TextBoxBase box || result.FindName("SelectionPopup") is not Popup popup)
            {
                Fail("selection popup: ResultBox/SelectionPopup not found");
                return;
            }

            result.ShowText("Yuuka reads the text inside the selection.", null, activate: false);

            var checks = new List<string>();

            // 1. A selection opens it (the baseline the other cases start from).
            SelectText(box, "Yuuka");
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
            SelectText(box, "Yuuka");
            Click(result, box);
            checks.Add($"click-inside-kept:{popup.IsOpen}");

            // 4. An empty selection always closes it.
            ClearSelection(box);
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
            SelectText(box, "Yuuka");

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

            if (result.FindName("ResultBox") is not TextBoxBase box || result.FindName("ChatBox") is not TextBox chat)
            {
                Fail("selection context: ResultBox/ChatBox not found");
                return;
            }

            var scope = result.FindName("ScopeText") as TextBlock;
            var checks = new List<string>();

            SelectText(box, "Yuuka");
            checks.Add($"pinned:{result.CurrentContext().Text == "Yuuka"}");

            // The reported flow: pick a sentence, then click into the chat box to type the question.
            Keyboard.Focus(box);
            Pump();
            Keyboard.Focus(chat);
            Pump();

            checks.Add($"focus-moved-keeps-selection:{SelectionText(box).Length == 5}");
            checks.Add($"focus-moved-keeps-context:{result.CurrentContext().Text == "Yuuka"}");
            checks.Add($"focus-moved-keeps-chip:{scope?.Text == L.T("result.context.selection")}");

            // The highlight must still be painted while the chat box has the focus, or the user cannot
            // see what the request is about. Measured in pixels, not from the property.
            checks.Add($"highlight-still-painted:{HighlightInk(box) > 0}");

            // Even if the live highlight goes away entirely, the pinned copy is what gets sent.
            ClearSelection(box);
            checks.Add($"highlight-lost-keeps-context:{result.CurrentContext().Text == "Yuuka"}");

            // Dropping the selection on purpose (box focused) is still a scope reset.
            Keyboard.Focus(box);
            Pump();
            SelectText(box, "Yuuka");
            ClearSelection(box);
            checks.Add($"dropped-on-purpose-resets:{result.CurrentContext().Text == body}");
            checks.Add($"dropped-on-purpose-chip:{scope?.Text == L.T("result.context.all")}");

            // A new result starts clean.
            SelectText(box, "Yuuka");
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
    private static int HighlightInk(TextBoxBase box)
    {
        var withSelection = Snapshot(box);
        var restore = CaptureSelection(box);

        ClearSelection(box);
        var without = Snapshot(box);
        restore();

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

            // A user turn is a plain TextBox, an agent reply is a Markdown RichTextBox — both are
            // selectable text, which is what this probe is about.
            var bodies = list.Children.OfType<TextBoxBase>().ToList();
            var labels = list.Children.OfType<TextBlock>().ToList();

            if (bodies.Count != 2)
            {
                Fail($"chat selection: {bodies.Count} selectable message(s), expected 2");
                return;
            }

            var order = string.Join(",", list.Children.OfType<FrameworkElement>()
                .Select(child => child is TextBoxBase ? "body" : "label"));

            var theme = services.Settings.Settings.Theme;

            var checks = new List<string>
            {
                $"labels:{labels.Count == 2}",
                $"order:{order == "label,body,label,body"}",
                $"kinds:{bodies[0] is TextBox && bodies[1] is RichTextBox}",
                $"read-only:{bodies.All(b => b.IsReadOnly)}",
                $"no-border:{bodies.All(b => b.BorderThickness == new Thickness(0))}",
                $"transparent:{bodies.All(b => b.Background == Brushes.Transparent)}",
                // The user's box wraps its own text; the reply wraps because its document is one column.
                $"wrapping:{bodies[0] is TextBox { TextWrapping: TextWrapping.Wrap } && bodies[1] is RichTextBox rich && rich.Document.ColumnWidth == double.PositiveInfinity}",
                $"user-right:{bodies[0].HorizontalAlignment == HorizontalAlignment.Right && ((TextBox)bodies[0]).TextAlignment == TextAlignment.Right}",
                $"agent-left:{bodies[1].HorizontalAlignment == HorizontalAlignment.Left}",
                $"inactive-highlight:{bodies.All(b => b.IsInactiveSelectionHighlightEnabled)}",
                $"tab-stop-off:{bodies.All(b => !b.IsTabStop)}",
                $"theme-ink:{bodies.All(b => Colour(b.Foreground) == ColorOf(theme.Text))}"
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

                SelectFirst(body, 4);
                Pump();
                checks.Add($"selectable:{SelectionText(body).Length == 4}");
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

            checks.Add($"user-text:{SelectionText(bodies[0]) == "what"}");
            checks.Add($"agent-text:{SelectionText(bodies[1]) == "It s"}");

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
    /// The agent's replies are Markdown, and the bubble has to *render* them: a model answers with tables,
    /// lists and fenced code, and showing the source of those is not an answer. The user's own turns stay
    /// literal — typing <c>**this**</c> must not turn into bold text.
    /// </summary>
    private static void ChatMarkdownRendering(AppServices services)
    {
        const string reply = """
        ## Summary

        The invoice lists **four** items:

        | Item | Qty |
        |:-----|----:|
        | bolt | 4   |
        | nut  | 8   |

        - first
        - second

        ```text
        total: 12
        ```
        """;

        var result = new ResultWindow(services);

        try
        {
            result.ShowText("Yuuka", null, activate: false);

            result.RenderConversation(
            [
                new ChatTurn(AgentPrompts.UserRole, "**raw** markdown stays literal", "10:00"),
                new ChatTurn(AgentPrompts.AssistantRole, reply, "10:01")
            ]);
            Pump();

            if (result.FindName("AnswerList") is not StackPanel list)
            {
                Fail("chat markdown: AnswerList not found");
                return;
            }

            var bodies = list.Children.OfType<TextBoxBase>().ToList();
            var user = bodies.ElementAtOrDefault(0) as TextBox;
            var agent = bodies.ElementAtOrDefault(1) as RichTextBox;

            if (user is null || agent is null)
            {
                Fail($"chat markdown: bodies are {string.Join(",", bodies.Select(b => b.GetType().Name))}, " +
                     "expected a TextBox then a RichTextBox");
                return;
            }

            var document = agent.Document;
            var visible = DocumentText(document);
            var tables = InlineDescendants<Table>(document);
            var heading = document.Blocks.OfType<Paragraph>().FirstOrDefault(p => Text(p).Trim() == "Summary");
            var code = document.Blocks.OfType<Paragraph>()
                .FirstOrDefault(p => p.FontFamily.Source.Contains("Consolas", StringComparison.OrdinalIgnoreCase));

            var checks = new List<string>
            {
                $"agent-markdown:{agent.IsReadOnly && agent.BorderThickness == new Thickness(0)}",
                // The user's turn is not parsed: what was typed is what is shown.
                $"user-literal:{user.Text == "**raw** markdown stays literal"}",
                $"heading:{heading is not null && heading.FontWeight == FontWeights.Bold}",
                $"bold:{InlineDescendants<Span>(document).Any(s => s.FontWeight == FontWeights.Bold && Text(s) == "four")}",
                $"tables:{tables.Count == 1}",
                $"lists:{InlineDescendants<List>(document).Count == 1}",
                $"code:{code is not null && Text(code).Contains("total: 12", StringComparison.Ordinal)}",
                // The rendered document must not leak the source syntax.
                $"no-pipes:{!visible.Contains("| Item |", StringComparison.Ordinal) && !visible.Contains("|---", StringComparison.Ordinal)}",
                $"no-hashes:{!visible.Contains("## ", StringComparison.Ordinal) && !visible.Contains("```", StringComparison.Ordinal)}",
                // A reply grows to its own height instead of being clipped into a default box.
                $"sized:{agent.ActualHeight > 40}",
                $"not-scrolled:{agent.ExtentHeight <= agent.ViewportHeight + 0.5}",
                // A reply with a table needs the whole column: a content-sized box would squeeze it.
                $"full-width:{agent.ActualWidth >= list.ActualWidth - 2}"
            };

            if (tables.Count == 1)
            {
                var rows = tables[0].RowGroups[0].Rows;
                checks.Add($"table-columns:{tables[0].Columns.Count == 2}");
                checks.Add($"table-rows:{rows.Count == 3}");
                checks.Add($"table-right:{rows[1].Cells[1].TextAlignment == TextAlignment.Right}");
                checks.Add($"table-cells:{Text(rows[2].Cells[1]).Trim() == "8"}");
            }

            // Still selectable text: a table cell can be picked and copied like a sentence.
            SelectText(agent, "bolt");
            checks.Add($"cell-selectable:{SelectionText(agent).Contains("bolt", StringComparison.Ordinal)}");
            checks.Add($"copy-command:{ApplicationCommands.Copy.CanExecute(null, agent)}");
            ClearSelection(agent);

            var failures = checks.Where(c => c.EndsWith(":False", StringComparison.Ordinal)).ToList();
            if (failures.Count > 0)
                Fail($"chat markdown: {string.Join(", ", failures)} " +
                     $"({document.Blocks.Count} blocks, height {agent.ActualHeight:0}, " +
                     $"extent {agent.ExtentHeight:0}/{agent.ViewportHeight:0}, " +
                     $"width {agent.ActualWidth:0}/{list.ActualWidth:0})");
            else
                Pass($"the agent's replies render Markdown in the chat ({checks.Count} checks, " +
                     $"{document.Blocks.Count} blocks, {agent.ActualHeight:0} DIP tall)");
        }
        catch (Exception ex)
        {
            Fail($"chat markdown: {ex.GetType().Name}: {ex.Message}");
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

    private static string Describe(string? text) =>
        text is null ? "<no request>" : $"\"{text.Replace("\r", "").Replace("\n", "⏎")}\"";

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

    /// <summary>
    /// The OCR text must reach the model in both scopes. This presses the bubble's own send button and
    /// reads the request that actually leaves the app (a loopback HTTP server stands in for the
    /// provider), so it asserts the payload, not the intent: the whole result when the chip reads
    /// "OCR result", only the selection when it reads "Selected text". A scope that quietly sends
    /// nothing is indistinguishable, from the user's side, from a model that "cannot see the text".
    /// </summary>
    private static void ChatContextReachesTheModel(AppServices services)
    {
        const string firstLine = "First line of the OCR result.";
        const string secondLine = "Second line, the one the user asks about.";
        const string ocr = firstLine + "\n" + secondLine;

        var settings = services.Settings.Settings;
        var saved = (settings.ApiProvider, settings.CustomBaseUrl,
                     settings.Agent.Provider, settings.Agent.Model, settings.Agent.Instruction);

        // Provider "custom" is the only one that honours CustomBaseUrl, so it is the one that can
        // be pointed at the loopback server.
        var hadKey = ApiKeyPool.LoadFor(AppPaths.EnvFile, "custom").Count > 0;
        using var server = new LoopbackServer();
        var result = new ResultWindow(services);

        try
        {
            ApiKeyPool.AddKey(AppPaths.EnvFile, "custom", "probe-key");
            settings.ApiProvider = "custom";
            settings.CustomBaseUrl = $"http://127.0.0.1:{server.Port}/v1";
            settings.Agent.Provider = "custom";
            settings.Agent.Model = "probe-model";
            settings.Agent.Instruction = "You are a probe.";

            result.ShowText(ocr, null, activate: false);

            if (result.FindName("ResultBox") is not TextBoxBase box ||
                result.FindName("ChatBox") is not TextBox chat ||
                result.FindName("SendButton") is not Button send ||
                result.FindName("ScopeText") is not TextBlock scope)
            {
                Fail("chat context: ResultBox/ChatBox/SendButton/ScopeText not found");
                return;
            }

            var checks = new List<string>();

            // ---- scope 1: the whole OCR result (the default, and what "OCR result" advertises)
            Focus(chat);
            chat.Text = "What does it say?";
            send.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            var whole = SystemOf(server.Wait(0));
            checks.Add($"whole-request-sent:{whole is not null}");
            checks.Add($"whole-has-system:{whole is not null}");
            checks.Add($"whole-has-first-line:{whole?.Contains(firstLine, StringComparison.Ordinal) == true}");
            checks.Add($"whole-has-second-line:{whole?.Contains(secondLine, StringComparison.Ordinal) == true}");
            checks.Add($"whole-named:{whole?.Contains(L.T("result.context.all"), StringComparison.Ordinal) == true}");

            // ---- scope 2: a selection, which must not drag the rest of the result along
            var start = ocr.IndexOf(secondLine, StringComparison.Ordinal);
            SelectText(box, secondLine);
            Pump();
            checks.Add($"chip-switched:{scope.Text == L.T("result.context.selection")}");

            Focus(chat);
            chat.Text = "And this part?";
            send.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            var picked = SystemOf(server.Wait(1));
            checks.Add($"selection-request-sent:{picked is not null}");
            checks.Add($"selection-has-picked-line:{picked?.Contains(secondLine, StringComparison.Ordinal) == true}");
            checks.Add($"selection-drops-rest:{picked?.Contains(firstLine, StringComparison.Ordinal) == false}");
            checks.Add($"selection-named:{picked?.Contains(L.T("result.context.selection"), StringComparison.Ordinal) == true}");

            // ---- scope 3: the chip clicked back to the whole result, which must send the full text again
            // (a stale pin here would keep answering about the fragment while the chip advertises "OCR result")
            if (result.FindName("ScopeButton") is Button chip) chip.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            checks.Add($"chip-back:{scope.Text == L.T("result.context.all")}");

            Focus(chat);
            chat.Text = "And the whole thing?";
            send.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            var again = SystemOf(server.Wait(2));
            checks.Add($"back-request-sent:{again is not null}");
            checks.Add($"back-has-first-line:{again?.Contains(firstLine, StringComparison.Ordinal) == true}");
            checks.Add($"back-has-second-line:{again?.Contains(secondLine, StringComparison.Ordinal) == true}");

            // ---- an empty box must not be a silent no-op: it has to say so, and send nothing
            var requests = server.Count;
            if (result.FindName("HintText") is TextBlock hint) hint.Text = "";
            Focus(chat);
            chat.Text = "";
            send.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();

            checks.Add($"empty-says-something:{(result.FindName("HintText") as TextBlock)?.Text == L.T("agent.chat.empty")}");
            checks.Add($"empty-sends-nothing:{server.Count == requests}");

            var failures = checks.Where(c => c.EndsWith(":False", StringComparison.Ordinal)).ToList();
            if (failures.Count > 0)
                Fail($"chat context: {string.Join(", ", failures)} " +
                     $"(requests={server.Count}, whole={Describe(whole)}, selection={Describe(picked)})");
            else
                Pass($"the OCR text reaches the model in both scopes ({checks.Count} checks, {server.Count} requests)");
        }
        catch (Exception ex)
        {
            Fail($"chat context: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            settings.ApiProvider = saved.ApiProvider;
            settings.CustomBaseUrl = saved.CustomBaseUrl;
            settings.Agent.Provider = saved.Provider;
            settings.Agent.Model = saved.Model;
            settings.Agent.Instruction = saved.Instruction;

            if (!hadKey) ApiKeyPool.RemoveProvider(AppPaths.EnvFile, "custom");
            services.Agent.ClearHistory();
            result.Close();
        }
    }

    /// <summary>The decoded system message of a captured request, or null when there is no request.</summary>
    private static string? SystemOf(string? body)
    {
        if (body is null) return null;

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("messages", out var messages) ||
            messages.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return null;
        }

        foreach (var message in messages.EnumerateArray())
        {
            if (message.TryGetProperty("role", out var role) && role.GetString() == "system" &&
                message.TryGetProperty("content", out var content))
            {
                return content.GetString();
            }
        }

        return null;
    }

    /// <summary>Makes the chat box believe it was clicked, so the placeholder stops being a message.</summary>
    private static void Focus(TextBox box)
    {
        box.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, null, box)
        {
            RoutedEvent = Keyboard.GotKeyboardFocusEvent
        });
        Pump();
    }

    /// <summary>
    /// A loopback HTTP server that answers every request with one canned chat completion and keeps the
    /// request bodies. Raw sockets, not `HttpListener`: a listener prefix needs a URL ACL, a
    /// `TcpListener` on port 0 does not, and the probe has to run unattended.
    /// </summary>
    private sealed class LoopbackServer : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener;
        private readonly List<string> _bodies = new();
        private readonly System.Threading.CancellationTokenSource _stop = new();

        public LoopbackServer()
        {
            _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptAsync);
        }

        public int Port { get; }

        public int Count
        {
            get { lock (_bodies) return _bodies.Count; }
        }

        /// <summary>Waits for request <paramref name="index"/> while pumping the dispatcher.</summary>
        public string? Wait(int index)
        {
            var deadline = Environment.TickCount64 + 15000;
            while (Environment.TickCount64 < deadline)
            {
                Pump();
                lock (_bodies)
                {
                    if (_bodies.Count > index) return _bodies[index];
                }
                Thread.Sleep(10);
            }

            return null;
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(System.Net.Sockets.TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new List<byte>(8192);
                    var chunk = new byte[4096];
                    var headerEnd = -1;
                    var length = 0;

                    while (true)
                    {
                        var read = await stream.ReadAsync(chunk, 0, chunk.Length).ConfigureAwait(false);
                        if (read <= 0) return;
                        buffer.AddRange(chunk.Take(read));

                        if (headerEnd < 0)
                        {
                            var head = System.Text.Encoding.ASCII.GetString(buffer.ToArray());
                            var index = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                            if (index >= 0)
                            {
                                headerEnd = index + 4;
                                foreach (var line in head[..index].Split("\r\n"))
                                {
                                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                        int.TryParse(line["Content-Length:".Length..].Trim(), out length);
                                }
                            }
                        }

                        if (headerEnd >= 0 && buffer.Count >= headerEnd + length) break;
                    }

                    lock (_bodies)
                    {
                        _bodies.Add(System.Text.Encoding.UTF8.GetString(buffer.ToArray(), headerEnd, length));
                    }

                    const string json = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Probe answer.\"}}]}";
                    var payload = System.Text.Encoding.UTF8.GetBytes(json);
                    var reply = System.Text.Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + payload.Length +
                        "\r\nConnection: close\r\n\r\n");

                    await stream.WriteAsync(reply, 0, reply.Length).ConfigureAwait(false);
                    await stream.WriteAsync(payload, 0, payload.Length).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
                catch
                {
                    // A probe request that dies is reported as a missing body by the caller.
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch { /* already stopped */ }
        }
    }

    /// <summary>The selected text of either kind of box (the result is a RichTextBox, chat rows are TextBoxes).</summary>
    private static string SelectionText(TextBoxBase box) => box switch
    {
        TextBox text => text.SelectedText,
        RichTextBox rich => rich.Selection.Text,
        _ => ""
    };

    /// <summary>
    /// Selects the first occurrence of <paramref name="needle"/>, whatever kind of box this is.
    ///
    /// Selecting by character offset does not survive the move to a RichTextBox: the document counts
    /// element boundaries as symbols, so an index into the plain string lands somewhere else. Searching
    /// the text itself is what the user does with the mouse, and it means the same thing in both boxes.
    /// </summary>
    private static void SelectText(TextBoxBase box, string needle)
    {
        if (box is TextBox text)
        {
            var at = text.Text.IndexOf(needle, StringComparison.Ordinal);
            if (at >= 0) text.Select(at, needle.Length);
            return;
        }

        if (box is not RichTextBox rich) return;

        // Search run by run: an offset inside one Run's own text is the same as the symbol offset the
        // content model uses, whereas a longer range counts paragraph breaks as symbols and spells them
        // with two characters, so the selection landed two characters late ("sted item").
        foreach (var run in InlineDescendants<Run>(rich.Document))
        {
            var index = run.Text.IndexOf(needle, StringComparison.Ordinal);
            if (index < 0) continue;

            var from = run.ContentStart.GetPositionAtOffset(index, LogicalDirection.Forward);
            var to = from?.GetPositionAtOffset(needle.Length, LogicalDirection.Forward);
            if (from is not null && to is not null) rich.Selection.Select(from, to);
            return;
        }

        // A needle that spans formatting (bold in the middle of a phrase) has no single Run to sit in.
        var pointer = rich.Document.ContentStart;
        while (pointer is not null)
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text)
            {
                pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
                continue;
            }

            var run = pointer.GetTextRunLength(LogicalDirection.Forward);
            var end = pointer.GetPositionAtOffset(run, LogicalDirection.Forward);
            if (end is null) return;

            var found = new TextRange(pointer, end).Text.IndexOf(needle, StringComparison.Ordinal);
            if (found >= 0)
            {
                var from = pointer.GetPositionAtOffset(found, LogicalDirection.Forward);
                var to = from?.GetPositionAtOffset(needle.Length, LogicalDirection.Forward);
                if (from is not null && to is not null) rich.Selection.Select(from, to);
                return;
            }

            pointer = end;
        }
    }

    /// <summary>The first <paramref name="length"/> characters of a message body, of either type.</summary>
    private static void SelectFirst(TextBoxBase box, int length)
    {
        if (box is TextBox text)
        {
            text.Select(0, Math.Min(length, text.Text.Length));
            return;
        }

        if (box is not RichTextBox rich) return;

        // A RichTextBox counts element boundaries as symbols, so "the first 4 characters" has to be
        // measured from the first run's own text rather than from Document.ContentStart.
        var run = InlineDescendants<Run>(rich.Document).FirstOrDefault(r => r.Text.Length > 0);
        if (run is null) return;

        var from = run.ContentStart;
        var to = from.GetPositionAtOffset(Math.Min(length, run.Text.Length));
        if (to is not null) rich.Selection.Select(from, to);
    }

    /// <summary>Empties the selection without touching the document.</summary>
    private static void ClearSelection(TextBoxBase box)
    {
        if (box is TextBox text) text.Select(0, 0);
        else if (box is RichTextBox rich) rich.Selection.Select(rich.Document.ContentStart, rich.Document.ContentStart);
    }

    /// <summary>Returns an action that puts the selection back exactly as it was.</summary>
    private static Action CaptureSelection(TextBoxBase box)
    {
        if (box is TextBox text)
        {
            var start = text.SelectionStart;
            var length = text.SelectionLength;
            return () => text.Select(start, length);
        }

        if (box is RichTextBox rich)
        {
            var from = rich.Selection.Start;
            var to = rich.Selection.End;
            return () => rich.Selection.Select(from, to);
        }

        return () => { };
    }

    // ------------------------------------------------------ result window: Markdown

    /// <summary>
    /// The result bubble renders Markdown: a table has to become a real <see cref="Table"/> with the
    /// alignment the delimiter row asked for, lists have to nest, task boxes have to be visible, and the
    /// whole thing has to stay selectable text — a picture of a table would look right and be useless.
    /// </summary>
    private static void MarkdownRendering(AppServices services)
    {
        const string markdown = """
        # Heading one

        A paragraph with **bold**, *italic*, ~~struck~~, `code` and a [link](https://example.com).

        - first item
        - second item
          - nested item
        - [x] done
        - [ ] not done

        1. one
        2. two

        > quoted text

        | Name | Count |
        |:-----|------:|
        | a    | 1     |
        | b    | 2     |

        ```csharp
        var x = 1;
        ```

        ---
        """;

        var result = new ResultWindow(services);

        try
        {
            result.ShowText(markdown, null, activate: false);
            Pump();

            if (result.FindName("ResultBox") is not RichTextBox box)
            {
                Fail("markdown: ResultBox is not a RichTextBox");
                return;
            }

            var blocks = box.Document.Blocks.ToList();
            var paragraphs = blocks.OfType<Paragraph>().ToList();
            var lists = blocks.OfType<List>().ToList();
            var tables = blocks.OfType<Table>().ToList();
            var sections = blocks.OfType<Section>().ToList();

            var heading = paragraphs.FirstOrDefault(p => Text(p).Trim() == "Heading one");
            var body = services.Settings.Settings.Theme.FontSize;

            var code = paragraphs.FirstOrDefault(p =>
                p.FontFamily.Source.Contains("Consolas", StringComparison.OrdinalIgnoreCase));

            var rule = paragraphs.FirstOrDefault(p => p.BorderThickness.Top > 0 && Text(p).Trim().Length == 0);

            var spans = InlineDescendants<Span>(box.Document);
            var links = InlineDescendants<Hyperlink>(box.Document).ToList();
            var struck = spans.Count(s => s.TextDecorations.Count > 0 && Text(s) == "struck");

            var all = DocumentText(box.Document);

            var checks = new List<string>
            {
                $"heading:{heading is not null && heading.FontSize > body && heading.FontWeight == FontWeights.Bold}",
                $"paragraph:{all.Contains("A paragraph with", StringComparison.Ordinal)}",
                $"bold:{spans.Any(s => s.FontWeight == FontWeights.Bold && Text(s) == "bold")}",
                $"italic:{spans.Any(s => s.FontStyle == FontStyles.Italic && Text(s) == "italic")}",
                $"struck:{struck == 1}",
                $"inline-code:{InlineDescendants<Run>(box.Document).Any(r => r.Text == "code" && r.FontFamily.Source.Contains("Consolas", StringComparison.OrdinalIgnoreCase))}",
                $"link:{links.Count == 1 && links[0].NavigateUri?.Host == "example.com"}",
                $"lists:{lists.Count == 2}",
                $"bullets:{lists.Count == 2 && lists[0].MarkerStyle == TextMarkerStyle.Disc}",
                $"numbers:{lists.Count == 2 && lists[1].MarkerStyle == TextMarkerStyle.Decimal && lists[1].StartIndex == 1}",
                $"items:{lists.Count == 2 && lists[0].ListItems.Count == 4}",
                $"nested:{lists.Count == 2 && lists[0].ListItems.ElementAt(1).Blocks.OfType<List>().Count() == 1}",
                $"task-boxes:{all.Contains('☑') && all.Contains('☐')}",
                $"quote:{sections.Count == 1 && sections[0].BorderThickness.Left > 0 && Text(sections[0]).Contains("quoted text", StringComparison.Ordinal)}",
                $"code-block:{code is not null && Text(code).Contains("var x = 1;", StringComparison.Ordinal) && code.Background is not null}",
                $"rule:{rule is not null}",
                $"tables:{tables.Count == 1}"
            };

            if (tables.Count == 1)
            {
                var table = tables[0];
                var rows = table.RowGroups[0].Rows;

                checks.Add($"table-columns:{table.Columns.Count == 2}");
                checks.Add($"table-rows:{rows.Count == 3}");
                checks.Add($"table-header:{rows[0].Cells[0].Background is not null}");
                checks.Add($"table-right:{rows[1].Cells[1].TextAlignment == TextAlignment.Right}");
                checks.Add($"table-left:{rows[1].Cells[0].TextAlignment == TextAlignment.Left}");
                checks.Add($"table-cells:{Text(rows[2].Cells[1]).Trim() == "2"}");
            }

            // Every piece has to be reachable by the mouse: selecting inside the table and the quote is
            // what the user does before pressing the floating copy button.
            SelectText(box, "quoted text");
            checks.Add($"quote-selectable:{SelectionText(box).Contains("quoted text", StringComparison.Ordinal)}");

            SelectText(box, "nested item");
            var nestedSelection = SelectionText(box);
            checks.Add($"list-selectable:{nestedSelection.Contains("nested item", StringComparison.Ordinal)}");

            ClearSelection(box);

            var failures = checks.Where(c => c.EndsWith(":False", StringComparison.Ordinal)).ToList();
            if (failures.Count > 0)
                Fail($"markdown: {string.Join(", ", failures)} ({blocks.Count} blocks, selection \"{nestedSelection}\")");
            else
                Pass($"the result bubble renders Markdown ({checks.Count} checks, {blocks.Count} blocks)");
        }
        catch (Exception ex)
        {
            Fail($"markdown: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            result.Close();
        }
    }

    /// <summary>Plain text of a text element, so a test can assert on what the user would read.</summary>
    private static string Text(TextElement element) =>
        new TextRange(element.ContentStart, element.ContentEnd).Text;

    private static string DocumentText(FlowDocument document) =>
        new TextRange(document.ContentStart, document.ContentEnd).Text;

    /// <summary>
    /// Every descendant of a given inline/block type. A <see cref="FlowDocument"/> is a content tree, not
    /// a visual one — <see cref="VisualTreeHelper"/> sees nothing under it — so it is walked by hand.
    /// </summary>
    private static List<T> InlineDescendants<T>(FlowDocument document) where T : TextElement
    {
        var found = new List<T>();
        foreach (var block in document.Blocks) Walk(block, found);
        return found;
    }

    private static void Walk<T>(TextElement element, List<T> found) where T : TextElement
    {
            if (element is T match) found.Add(match);

            switch (element)
            {
                case Section section:
                    foreach (var block in section.Blocks) Walk(block, found);
                    break;
                case Paragraph paragraph:
                    foreach (var inline in paragraph.Inlines) Walk(inline, found);
                    break;
                case List list:
                    foreach (var item in list.ListItems) Walk(item, found);
                    break;
                case ListItem listItem:
                    foreach (var block in listItem.Blocks) Walk(block, found);
                    break;
                case Table table:
                    foreach (var group in table.RowGroups) Walk(group, found);
                    break;
                case TableRowGroup group:
                    foreach (var row in group.Rows) Walk(row, found);
                    break;
                case TableRow row:
                    foreach (var cell in row.Cells) Walk(cell, found);
                    break;
                case TableCell cell:
                    foreach (var block in cell.Blocks) Walk(block, found);
                    break;
                case Span span:
                    foreach (var inline in span.Inlines) Walk(inline, found);
                    break;
            }
    }

    // --------------------------------------------------- result window: minimum width

    /// <summary>
    /// *Layout → Spacing &amp; Size → Minimum width* has to change the bubble. It used to do nothing at all:
    /// the window was pinned to 620 + padding whatever the setting said, so the whole 120…600 slider was
    /// dead. Now the text decides the natural width, the setting raises the floor, and the ceiling still
    /// stops one very long OCR line from producing a window wider than the screen.
    /// </summary>
    private static void MinimumWidthDrivesTheBubble(AppServices services)
    {
        var settings = services.Settings;
        var originalMinimum = settings.Settings.MinSubWindowWidth;
        var originalScale = settings.Settings.UiScale;
        var savedSizes = new Dictionary<string, WindowSize>(settings.Settings.WindowSizes);

        // A remembered size would win over the minimum by design, so start from "never resized" — and do
        // it before the window is built, because the constructor is where the size is restored.
        settings.Update(s =>
        {
            s.WindowSizes.Remove(WindowSizeKeys.Result);
            s.UiScale = 100;
            s.MinSubWindowWidth = 200;
        });

        var result = new ResultWindow(services);

        try
        {
            result.ShowText("Yuuka", null, activate: false);
            Pump();
            var atDefault = result.Width;

            settings.Update(s => s.MinSubWindowWidth = 600);
            Pump();
            var atWide = result.Width;

            settings.Update(s => s.MinSubWindowWidth = 120);
            Pump();
            var atNarrow = result.Width;

            // A long line wants more room than the natural width allows.
            result.ShowText(new string('W', 300), null, activate: false);
            Pump();
            var longLine = result.Width;

            // The floor still holds when the user drags smaller than the setting.
            settings.Update(s => s.MinSubWindowWidth = 400);
            Pump();
            var floor = result.MinWidth;

            var checks = new List<string>
            {
                $"default:{Math.Abs(atDefault - 244) < 1.5}",
                $"wider:{Math.Abs(atWide - 644) < 1.5}",
                $"narrower:{Math.Abs(atNarrow - 164) < 1.5}",
                $"grows:{atWide > atDefault && atDefault > atNarrow}",
                $"capped:{Math.Abs(longLine - 664) < 1.5}",
                $"floor:{Math.Abs(floor - 444) < 1.5}"
            };

            var failures = checks.Where(c => c.EndsWith(":False", StringComparison.Ordinal)).ToList();
            if (failures.Count > 0)
                Fail($"minimum width: {string.Join(", ", failures)} " +
                     $"(120={atNarrow:0}, 200={atDefault:0}, 600={atWide:0}, long={longLine:0}, floor={floor:0})");
            else
                Pass($"the minimum-width slider sets the bubble width ({atNarrow:0}/{atDefault:0}/{atWide:0}, " +
                     $"long line capped at {longLine:0})");
        }
        catch (Exception ex)
        {
            Fail($"minimum width: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            result.Close();

            settings.Update(s =>
            {
                s.MinSubWindowWidth = originalMinimum;
                s.UiScale = originalScale;
            });

            settings.Settings.WindowSizes.Clear();
            foreach (var pair in savedSizes) settings.Settings.WindowSizes[pair.Key] = pair.Value;
        }
    }

    // ----------------------------------------------------------- window sizes remembered

    /// <summary>
    /// Every window remembers the size the user dragged out. Size only — never the position, because each
    /// window places itself and a remembered position would fight that.
    /// </summary>
    private static void WindowSizeMemoryRoundTrip(AppServices services)
    {
        var settings = services.Settings;
        var savedSizes = new Dictionary<string, WindowSize>(settings.Settings.WindowSizes);
        const string key = "probe-window";

        var first = new Window
        {
            Width = 500,
            Height = 300,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -6000,
            Top = -6000
        };

        var second = new Window
        {
            Width = 200,
            Height = 200,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -6000,
            Top = -6000
        };

        try
        {
            settings.Settings.WindowSizes.Remove(key);
            first.Show();
            Pump();

            // Nothing remembered yet: a caller must be able to tell, so it can pick its own default.
            var restoredNothing = WindowSizeMemory.Restore(second, services, key);

            WindowSizeMemory.Remember(services, key, first.Width, first.Height);

            var stored = settings.Settings.SizeOf(key);
            var restored = WindowSizeMemory.Restore(second, services, key);
            Pump();

            var checks = new List<string>
            {
                $"empty-first:{!restoredNothing}",
                $"stored:{stored is not null && Math.Abs(stored.Width - 500) < 0.5 && Math.Abs(stored.Height - 300) < 0.5}",
                $"restored:{restored && Math.Abs(second.Width - 500) < 0.5 && Math.Abs(second.Height - 300) < 0.5}",
                $"position-kept:{Math.Abs(second.Left - (-6000)) < 0.5}",
                // A size from a monitor that is no longer there must not open a window off screen.
                $"fitted:{second.Width <= SystemParameters.WorkArea.Width && second.Height <= SystemParameters.WorkArea.Height}"
            };

            var failures = checks.Where(c => c.EndsWith(":False", StringComparison.Ordinal)).ToList();
            if (failures.Count > 0)
                Fail($"window sizes: {string.Join(", ", failures)} " +
                     $"(stored {stored?.Width:0}x{stored?.Height:0}, restored {second.Width:0}x{second.Height:0})");
            else
                Pass($"a dragged size is stored and restored ({second.Width:0}x{second.Height:0}, position untouched)");
        }
        catch (Exception ex)
        {
            Fail($"window sizes: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            first.Close();
            second.Close();

            settings.Settings.WindowSizes.Clear();
            foreach (var pair in savedSizes) settings.Settings.WindowSizes[pair.Key] = pair.Value;
        }
    }

    // ------------------------------------------------------------------ resize handles

    /// <summary>
    /// A borderless window has no OS resize border, so the handles are ours: eight of them, sitting on the
    /// visible card edge rather than out in the transparent shadow padding, each with the cursor a desktop
    /// window would show.
    /// </summary>
    private static void ResizeHandles()
    {
        const double pad = 30;

        var overlay = new Grid();
        var card = new Border
        {
            Margin = new Thickness(pad),
            Background = Brushes.Black,
            CornerRadius = new CornerRadius(12)
        };

        overlay.Children.Add(card);

        var window = new Window
        {
            Width = 700,
            Height = 500,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -6000,
            Top = -6000,
            Content = overlay
        };

        try
        {
            window.Show();
            Pump();

            ResizeGrips.Attach(window, overlay, card);
            Pump();

            var grips = overlay.Children.OfType<Border>().Where(b => b != card).ToList();
            var expected = new Dictionary<YuukaOCR.Core.Ui.ResizeEdge, Cursor>
            {
                [YuukaOCR.Core.Ui.ResizeEdge.TopLeft] = Cursors.SizeNWSE,
                [YuukaOCR.Core.Ui.ResizeEdge.TopRight] = Cursors.SizeNESW,
                [YuukaOCR.Core.Ui.ResizeEdge.BottomLeft] = Cursors.SizeNESW,
                [YuukaOCR.Core.Ui.ResizeEdge.BottomRight] = Cursors.SizeNWSE,
                [YuukaOCR.Core.Ui.ResizeEdge.Left] = Cursors.SizeWE,
                [YuukaOCR.Core.Ui.ResizeEdge.Right] = Cursors.SizeWE,
                [YuukaOCR.Core.Ui.ResizeEdge.Top] = Cursors.SizeNS,
                [YuukaOCR.Core.Ui.ResizeEdge.Bottom] = Cursors.SizeNS
            };

            var checks = new List<string> { $"count:{grips.Count == 8}" };

            foreach (var pair in expected)
            {
                var grip = grips.FirstOrDefault(g => g.Tag is YuukaOCR.Core.Ui.ResizeEdge edge && edge == pair.Key);
                checks.Add($"{pair.Key}-present:{grip is not null}");
                if (grip is null) continue;

                checks.Add($"{pair.Key}-cursor:{grip.Cursor == pair.Value}");
                // A null background is not hit-testable, so the handle would be invisible and dead.
                checks.Add($"{pair.Key}-hittable:{grip.Background is not null && grip.IsHitTestVisible}");

                var box = Bounds(grip, window);
                var cardBox = Bounds(card, window);

                // Corners must sit on the card's own corner; edges on the card's own edge.
                var onLeft = pair.Key is YuukaOCR.Core.Ui.ResizeEdge.TopLeft
                    or YuukaOCR.Core.Ui.ResizeEdge.BottomLeft
                    or YuukaOCR.Core.Ui.ResizeEdge.Left;

                var aligned = onLeft
                    ? Math.Abs(box.Left - cardBox.Left) < 0.6
                    : Math.Abs(box.Right - cardBox.Right) < 0.6;

                if (pair.Key is YuukaOCR.Core.Ui.ResizeEdge.Left or YuukaOCR.Core.Ui.ResizeEdge.Right)
                    aligned &= Math.Abs(box.Top - (cardBox.Top + 16)) < 0.6;
                else if (pair.Key is YuukaOCR.Core.Ui.ResizeEdge.Top or YuukaOCR.Core.Ui.ResizeEdge.Bottom)
                    aligned &= Math.Abs(box.Left - (cardBox.Left + 16)) < 0.6;

                checks.Add($"{pair.Key}-on-edge:{aligned}{(aligned ? "" : $" [{box.Left:0.#},{box.Top:0.#} {box.Width:0.#}x{box.Height:0.#} vs card {cardBox.Left:0.#},{cardBox.Top:0.#} {cardBox.Width:0.#}x{cardBox.Height:0.#}]")}");
                checks.Add($"{pair.Key}-grab:{box.Width >= 5.5 && box.Height >= 5.5}");
            }

            var failures = checks.Where(c => c.EndsWith(":False", StringComparison.Ordinal)).ToList();
            if (failures.Count > 0)
                Fail($"resize handles: {string.Join(", ", failures)} ({grips.Count} handles)");
            else
                Pass($"eight resize handles sit on the card edge with the right cursors ({checks.Count} checks)");
        }
        catch (Exception ex)
        {
            Fail($"resize handles: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>An element's rectangle in window coordinates.</summary>
    private static Rect Bounds(FrameworkElement element, Visual root)
    {
        var origin = element.TransformToAncestor(root).Transform(new Point(0, 0));
        return new Rect(origin, new Size(element.ActualWidth, element.ActualHeight));
    }

    // ------------------------------------------------------- settings window geometry

    /// <summary>
    /// The settings window resizes like a desktop window and its content follows: the tab strip takes the
    /// new width, and the full-text editor is exactly the same rectangle — the editor stands in for the
    /// window while it is open, so it may not be a pixel larger or smaller.
    /// </summary>
    private static void SettingsWindowGeometry(AppServices services)
    {
        var config = new ConfigWindow(services);
        FullTextWindow? editor = null;

        try
        {
            config.WindowStartupLocation = WindowStartupLocation.Manual;
            config.Left = -6000;
            config.Top = -6000;
            config.Show();
            Pump();

            var chrome = (FrameworkElement)config.FindName("Chrome");
            var tabs = (TabControl)config.FindName("Tabs");
            var narrow = tabs.ActualWidth;

            config.Width = 1500;
            Pump();
            var wide = tabs.ActualWidth;

            editor = new FullTextWindow(services, "Full text", "line one\nline two", readOnly: false);
            editor.Show();
            Pump();
            editor.PlaceOver(config);
            Pump();

            var editorChrome = (FrameworkElement)editor.FindName("Chrome");

            var checks = new List<string>
            {
                $"topmost:{config.Topmost && editor.Topmost}",
                $"resizable:{config.ResizeMode == ResizeMode.CanResize && editor.ResizeMode == ResizeMode.CanResize}",
                $"handles:{config.FindName("Overlay") is Grid overlay && overlay.Children.OfType<Border>().Any(b => b.Tag is YuukaOCR.Core.Ui.ResizeEdge)}",
                // The content follows the window instead of staying at its old width.
                $"content-follows:{wide - narrow > 400}",
                $"no-scroll:{wide > narrow && tabs.ActualWidth < config.ActualWidth}",
                // Same rectangle, same shadow padding, so the cards line up too.
                $"same-width:{Math.Abs(editor.Width - config.ActualWidth) < 0.5}",
                $"same-height:{Math.Abs(editor.Height - config.ActualHeight) < 0.5}",
                $"same-left:{Math.Abs(editor.Left - config.Left) < 0.5}",
                $"same-top:{Math.Abs(editor.Top - config.Top) < 0.5}",
                $"same-card:{editorChrome.Margin == chrome.Margin && editorChrome.Margin.Left == 30}"
            };

            var failures = checks.Where(c => c.EndsWith(":False", StringComparison.Ordinal)).ToList();
            if (failures.Count > 0)
                Fail($"settings window: {string.Join(", ", failures)} " +
                     $"(tabs {narrow:0} -> {wide:0}, editor {editor.Width:0}x{editor.Height:0}, " +
                     $"settings {config.ActualWidth:0}x{config.ActualHeight:0})");
            else
                Pass($"the settings window resizes with its content and the editor matches it " +
                     $"({config.ActualWidth:0}x{config.ActualHeight:0}, tabs {narrow:0} -> {wide:0})");
        }
        catch (Exception ex)
        {
            Fail($"settings window: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            editor?.Close();
            config.Close();
        }
    }

    // ------------------------------------------------- prompt/agent editing survives

    /// <summary>
    /// The reported bug: type into *Actions → Action Instruction*, click any icon button, and the field
    /// snaps back to its default. The icon buttons are <c>Focusable=False</c> by design, so the box never
    /// loses focus and the old save-on-lost-focus path never ran; the rebuild then read the *stored* value
    /// back into the box. Both halves are asserted here: what the box shows, and what got saved.
    /// </summary>
    private static void PromptEditsSurviveIconClicks(AppServices services)
    {
        var settings = services.Settings;
        var savedActions = settings.Settings.Actions.Select(a => a.Clone()).ToList();

        var config = new ConfigWindow(services);

        try
        {
            config.WindowStartupLocation = WindowStartupLocation.Manual;
            config.Left = -6000;
            config.Top = -6000;
            config.Show();
            Pump();

            if (config.FindName("PromptPanel") is not PromptAgentTab panel)
            {
                Fail("prompt edits: PromptPanel not found");
                return;
            }

            var instruction = (TextBox)panel.FindName("ActionInstructionBox");
            var name = (TextBox)panel.FindName("ActionNameBox");
            var icons = (WrapPanel)panel.FindName("IconPicker");

            var action = settings.Settings.Actions[0];
            var beforeIcon = action.Icon;
            var icon = icons.Children.OfType<Button>().FirstOrDefault(b => b.Tag is string tag && tag != beforeIcon)
                       ?? icons.Children.OfType<Button>().FirstOrDefault();

            if (icon is null)
            {
                Fail("prompt edits: no icon button to click");
                return;
            }

            const string custom = "Answer in one short line, in Japanese.";

            // The user types — no focus events, exactly like a paste into the box.
            instruction.Text = custom;
            name.Text = "Summarise";

            // …then clicks an icon. This rebuilds the tab strip and the editors from settings.
            icon.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, icon));
            Pump();

            var storedInstruction = settings.Settings.Actions.First(a => a.Id == action.Id).Instruction;
            var storedName = settings.Settings.Actions.First(a => a.Id == action.Id).Name;

            var checks = new List<string>
            {
                $"box-kept:{instruction.Text == custom}",
                $"saved:{storedInstruction == custom}",
                $"name-kept:{name.Text == "Summarise"}",
                $"name-saved:{storedName == "Summarise"}",
                $"icon-applied:{settings.Settings.Actions.First(a => a.Id == action.Id).Icon != beforeIcon}"
            };

            // Closing the window is the other way out: it must commit too, not drop the edit.
            instruction.Text = custom + " (edited)";
            config.Close();
            Pump();

            var afterClose = settings.Settings.Actions.First(a => a.Id == action.Id).Instruction;
            checks.Add($"saved-on-close:{afterClose == custom + " (edited)"}");

            var failures = checks.Where(c => c.EndsWith(":False", StringComparison.Ordinal)).ToList();
            if (failures.Count > 0)
                Fail($"prompt edits: {string.Join(", ", failures)} (stored \"{storedInstruction}\")");
            else
                Pass($"typing survives an icon click and a window close ({checks.Count} checks)");
        }
        catch (Exception ex)
        {
            Fail($"prompt edits: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            config.Close();

            settings.Update(s =>
            {
                s.Actions.Clear();
                s.Actions.AddRange(savedActions);
            });
        }
    }

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
