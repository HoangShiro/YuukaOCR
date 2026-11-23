using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using YuukaOCR.App.Services;
using YuukaOCR.App.Ui.Controls;
using YuukaOCR.Core.Agent;
using YuukaOCR.Core.Interop;
using YuukaOCR.Core.Localization;
using YuukaOCR.Core.Physics;
using YuukaOCR.Core.Settings;
using YuukaOCR.Core.Ui;

namespace YuukaOCR.App.Ui;

/// <summary>
/// Floating result bubble: the OCR text (selectable, with a floating copy button), a right-hand rail
/// with close + copy, and the action bar — registered action buttons plus a chat box that talks to the
/// configured agent.
///
/// Placement follows the configured side (auto/left/right/top/bottom) plus spacing and minimum width,
/// then springs into place from the bar's position.
/// </summary>
public partial class ResultWindow : Window, IThemedWindow
{
    /// <summary>Transparent padding around the bubble so the window does not clip the DropShadowEffect.</summary>
    private const double ShadowPad = 22;

    private readonly AppServices _services;
    private readonly SpringMotion _spring = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly DispatcherTimer _animTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _hintTimer = new() { Interval = TimeSpan.FromSeconds(1.6) };

    private double _lastTick;
    private bool _animating;
    private IntPtr _handle;
    private bool _dragging;
    private int _dragCursorX;
    private int _dragCursorY;
    private double _dragStartX;
    private double _dragStartY;

    /// <summary>Index of the first conversation turn this bubble shows (older turns belong to earlier bubbles).</summary>
    private int _chatStart;

    /// <summary>True when the next request carries the selected text instead of the whole result.</summary>
    private bool _useSelection;

    /// <summary>
    /// The text the user picked, kept independently of <see cref="TextBox.SelectedText"/>.
    ///
    /// Reading the live selection at send time was a context leak: the user highlights a sentence,
    /// clicks into the chat box to type the question, and anything that drops the highlight — a click
    /// in the result, a re-layout, the window losing focus on some Windows versions — takes the agent's
    /// context with it, silently falling back to the whole OCR result. The selection is pinned when it
    /// is made and only released when the user drops it on purpose (with the box focused) or a new
    /// result arrives.
    /// </summary>
    private string? _pinnedSelection;

    /// <summary>True while the chat box still shows the placeholder rather than typed text.</summary>
    private bool _placeholderShown = true;

    /// <summary>
    /// The result exactly as it arrived. The document on screen is a *rendering* of this (Markdown
    /// headings, tables, links), so anything that has to hand the text on — copy-all, the agent's
    /// context — reads this instead of the FlowDocument, which would give back plain text with the
    /// structure flattened away.
    /// </summary>
    private string _rawText = "";

    /// <summary>Font size the document was last built for, so a theme switch rebuilds it only if needed.</summary>
    private double _renderedFontSize;

    /// <summary>
    /// True once the user has dragged the bubble to a size of their own. Until then it sizes itself to
    /// its content (SizeToContent=Height) at the configured minimum width; afterwards that size is
    /// restored every time the bubble is shown.
    /// </summary>
    private bool _userSized;

    /// <summary>
    /// Identity of the action set currently drawn in the bar. <see cref="ApplyAppearance"/> fires on
    /// every settings change, so the bar is only rebuilt when an action was added, removed or edited —
    /// not on every pixel of a slider drag.
    /// </summary>
    private string _actionSignature = "";

    /// <summary>Unhooks the floating copy button's lifetime rules when the bubble is destroyed.</summary>
    private readonly IDisposable _selectionTracker;

    /// <summary>
    /// Copy buttons of the message boxes currently in the conversation, disposed whenever the list is
    /// rebuilt — a message that is no longer on screen must not keep a popup alive.
    /// </summary>
    private readonly List<IDisposable> _messageTrackers = new();

    public ResultWindow(AppServices services)
    {
        _services = services;

        InitializeComponent();
        SelectionPopup.PlacementTarget = ResultBox;

        // The floating copy button has to vanish when the selection stops being the thing in focus:
        // window deactivated, focus moved on, or a click elsewhere in the bubble.
        _selectionTracker = SelectionCopy.Track(ResultBox, SelectionPopup);

        _services.Agent.HistoryChanged += OnAgentHistoryChanged;
        _services.Agent.ThinkingChanged += OnAgentThinkingChanged;

        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            WindowMover.HideFromAltTab(_handle);
        };

        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer.Stop();
            HintText.Text = L.T("result.hint");
        };

        ApplyLocalization();
        AppearanceSync.Register(this);

        // Restore the size the user dragged out last time, then let them drag it again.
        if (WindowSizeMemory.Restore(this, _services, WindowSizeKeys.Result))
        {
            _userSized = true;
            SizeToContent = SizeToContent.Manual;
        }

        ResizeGrips.Attach(this, Overlay, Root, BeginResize, EndResize);

        HeaderRow.LostMouseCapture += (_, _) => EndDrag();
        _animTimer.Tick += (_, _) => OnRendering();
    }

    /// <summary>
    /// First frame of a resize drag. The bubble sizes itself to its content until this moment, and
    /// <see cref="SizeToContent"/> would fight every height the user drags — so it is switched off here,
    /// once, and the current rectangle becomes the starting point of the drag.
    /// </summary>
    private WindowRect? BeginResize()
    {
        if (!_userSized)
        {
            var rect = new WindowRect(Left, Top, ActualWidth, ActualHeight);

            SizeToContent = SizeToContent.Manual;
            Width = rect.Width;
            Height = rect.Height;
            _userSized = true;

            // A fixed window scrolls its text instead of clipping it.
            ResultBox.MaxHeight = double.PositiveInfinity;
            return rect;
        }

        return new WindowRect(Left, Top, Width, Height);
    }

    private void EndResize()
    {
        if (!_userSized) return;
        WindowSizeMemory.Remember(_services, WindowSizeKeys.Result, Width, Height);
    }

    // ------------------------------------------------------------- appearance

    /// <summary>
    /// Repaints from <see cref="ThemeSettings"/>. <see cref="AppearanceSync"/> calls this whenever any
    /// appearance setting changes, which is what keeps this bubble in step with the settings window
    /// instead of showing the previous colours until it is reopened.
    /// </summary>
    public void ApplyAppearance()
    {
        var settings = _services.Settings.Settings;
        var theme = settings.Theme;
        Theme.Apply(this, theme);

        var scale = Math.Clamp(settings.UiScale, 50, 200) / 100.0;
        Root.CornerRadius = new CornerRadius(12 * scale);
        ResultBox.FontSize = theme.FontSize;

        // +2*padding so the *visible* width still matches the configured value.
        //
        // Width is set explicitly instead of being left to the content: a borderless Window that sizes
        // itself to its height keeps the OS default width for the other axis, which is then clamped by
        // MaxWidth — so the bubble always came out 620 wide and the Minimum Width slider did nothing.
        // The natural width is the ceiling of "as wide as this content wants", the slider raises the
        // floor, and a size the user dragged out wins over both.
        var natural = 620 * scale + ShadowPad * 2;
        MinWidth = Math.Max(120, settings.MinSubWindowWidth * scale) + ShadowPad * 2;
        MaxWidth = Math.Max(natural, 1800 * scale);

        FitWidth();

        // The action bar scales with the UI scale, so a 150 % bar does not get a 100 % action bar.
        ChatBox.FontSize = Math.Max(10, 11 * scale);
        ScopeText.FontSize = Math.Max(9, 10 * scale);

        // Adding, removing or editing an action in Settings must show up here immediately: the settings
        // window raises Changed, AppearanceSync fans it out to this window, and this is where the bar
        // catches up — no restart, no reopening the bubble.
        if (ActionSignature() != _actionSignature) BuildActionBar();

        foreach (var button in ActionBar.Children.OfType<Button>())
            button.Width = button.Height = Math.Round(26 * scale);

        // Heading sizes are computed from the body size, so a font-size change means a rebuild. A theme
        // change alone does not: every brush in the document is a resource reference.
        if (Math.Abs(_renderedFontSize - theme.FontSize) > 0.01) RenderText();

        RenderConversation();
    }

    /// <summary>Re-reads every label; called when the language changes while the bubble is open.</summary>
    public void ApplyLocalization()
    {
        HintText.Text = L.T("result.hint");
        CloseButton.ToolTip = L.T("common.close");
        CopyButton.ToolTip = L.T("result.copy");
        SelectionCopyButton.ToolTip = L.T("result.copySelection");
        SendButton.ToolTip = L.T("result.send");
        ScopeButton.ToolTip = L.T("result.context.scope");
        ScopeText.Text = L.T(_useSelection ? "result.context.selection" : "result.context.all");

        if (_placeholderShown) ChatBox.Text = L.T("result.chatPlaceholder");

        BuildActionBar();
        RenderConversation();
    }

    // ------------------------------------------------------------------ show

    /// <summary>
    /// Shows the bubble. <paramref name="activate"/> is false for the Layout → Test preview: the user is
    /// working in the settings window and the bubble must not take the focus away from it.
    /// </summary>
    public void ShowText(string text, ScreenRect? barBounds, bool activate = true)
    {
        ApplyAppearance();
        SetResultText(text);

        // A new result starts a fresh scope, but the conversation itself is shared and persistent.
        _useSelection = false;
        _pinnedSelection = null;
        ScopeText.Text = L.T("result.context.all");
        _chatStart = _services.Agent.History.Count;

        BuildActionBar();
        RenderConversation();

        if (!IsVisible) Show();
        UpdateLayout();

        Position(barBounds);
        FadeIn();
        if (activate) Activate();
    }

    private void Position(ScreenRect? barBounds)
    {
        var settings = _services.Settings.Settings;
        var scale = Math.Clamp(settings.UiScale, 50, 200) / 100.0;
        var spacing = Math.Max(0, settings.SubWindowSpacing) * scale;

        var dpi = ScreenGeometry.DpiScaleAt(0, 0);
        var width = ActualWidth > 1 ? ActualWidth * dpi : MinWidth;
        var height = ActualHeight > 1 ? ActualHeight * dpi : 120;

        // Visible bubble = window minus shadow padding; every placement calculation works on the visible
        // part and only subtracts the padding at the end to get window coordinates.
        var pad = ShadowPad * dpi;
        var visibleWidth = Math.Max(1, width - pad * 2);
        var visibleHeight = Math.Max(1, height - pad * 2);

        if (barBounds is null)
        {
            var screen = ScreenGeometry.WorkAreaAt(0, 0);
            _spring.SnapTo(screen.Left + (screen.Width - visibleWidth) / 2 - pad, screen.Top + 120 - pad);
            WindowMover.MoveTo(_handle, _spring.X, _spring.Y);
            return;
        }

        var bar = barBounds.Value;
        var area = ScreenGeometry.WorkAreaAt(bar.Left + bar.Width / 2, bar.Top + bar.Height / 2);

        var side = settings.SubWindowPosition.ToLowerInvariant();
        if (side == "auto")
        {
            var spaceRight = area.Right - bar.Right;
            var spaceLeft = bar.Left - area.Left;
            var spaceBelow = area.Bottom - bar.Bottom;
            side = spaceBelow >= visibleHeight + spacing ? "bottom"
                : spaceRight >= visibleWidth + spacing ? "right"
                : spaceLeft >= visibleWidth + spacing ? "left"
                : "top";
        }

        double targetX;
        double targetY;
        switch (side)
        {
            case "left":
                targetX = bar.Left - visibleWidth - spacing;
                targetY = bar.Top;
                break;
            case "top":
                targetX = bar.Left;
                targetY = bar.Top - visibleHeight - spacing;
                break;
            case "right":
                targetX = bar.Right + spacing;
                targetY = bar.Top;
                break;
            default:
                targetX = bar.Left;
                targetY = bar.Bottom + spacing;
                break;
        }

        targetX = Math.Clamp(targetX, area.Left, Math.Max(area.Left, area.Right - visibleWidth));
        targetY = Math.Clamp(targetY, area.Top, Math.Max(area.Top, area.Bottom - visibleHeight));

        // Start from the bar so the bubble visibly flies out of it.
        _spring.SnapTo(bar.Left + (bar.Width - visibleWidth) / 2 - pad, bar.Top - pad);
        _spring.SetTarget(targetX - pad, targetY - pad);
        _spring.SpringConstant = Math.Clamp(settings.SpringConstant, 0.01, 0.5);
        _spring.DampingFactor = Math.Clamp(settings.DampingFactor, 0.0, 1.0);

        WindowMover.MoveTo(_handle, _spring.X, _spring.Y);
        StartAnimation();
    }

    private void StartAnimation()
    {
        if (_animating) return;
        _animating = true;
        _lastTick = _clock.Elapsed.TotalSeconds;
        _animTimer.Start();
    }

    private void OnRendering()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var delta = now - _lastTick;
        _lastTick = now;

        if (_dragging || _handle == IntPtr.Zero) return;

        var bounds = WindowTracker.GetBounds(_handle);
        if (bounds is null) return;

        var moved = _spring.Step(delta,
            ScreenGeometry.VirtualScreen().Left, ScreenGeometry.VirtualScreen().Top,
            ScreenGeometry.VirtualScreen().Right - bounds.Value.Width,
            ScreenGeometry.VirtualScreen().Bottom - bounds.Value.Height);

        if (moved) WindowMover.MoveTo(_handle, _spring.X, _spring.Y);
        else if (_spring.IsSettled)
        {
            _animTimer.Stop();
            _animating = false;
        }
    }

    private void FadeIn() =>
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));

    // ------------------------------------------------------------- selection

    /// <summary>The raw result text, as it arrived — what copy-all and the agent's context use.</summary>
    internal string ResultText => _rawText;

    /// <summary>Stores the result and renders it. The document is a Markdown view of the text.</summary>
    private void SetResultText(string? text)
    {
        _rawText = text ?? "";
        RenderText();
        FitWidth();
    }

    /// <summary>
    /// Chooses the bubble's width from the text: as wide as the longest line needs, never narrower than
    /// the configured minimum, never wider than the natural width.
    ///
    /// This is what makes *Layout → Minimum width* do something. Before, the window was 620 wide no
    /// matter what the setting said (a borderless window that sizes itself to its height keeps the OS
    /// default width for the other axis), so dragging the slider 120…600 changed nothing at all.
    /// </summary>
    private void FitWidth()
    {
        if (_userSized) return;

        var scale = Math.Clamp(_services.Settings.Settings.UiScale, 50, 200) / 100.0;
        var natural = 620 * scale + ShadowPad * 2;

        Width = Math.Clamp(PreferredWidth(), MinWidth, Math.Max(MinWidth, natural));
    }

    /// <summary>
    /// The width the text would like: the widest line measured in the theme's own font. Measuring the
    /// text instead of the laid-out tree is deliberate — the tree is measured with a star column, so it
    /// would always report the width it was given, which is exactly the bug being fixed.
    /// </summary>
    private double PreferredWidth()
    {
        var theme = _services.Settings.Settings.Theme;
        var typeface = new Typeface(new FontFamily(theme.FontFamily), FontStyles.Normal,
            FontWeights.Normal, FontStretches.Normal);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var widest = 0.0;
        foreach (var line in _rawText.Split('\n'))
        {
            var trimmed = line.TrimEnd();   // drops the CR of a CRLF split as well
            if (trimmed.Length == 0) continue;

            var text = new FormattedText(trimmed, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, theme.FontSize, Brushes.Black, dpi);

            widest = Math.Max(widest, text.WidthIncludingTrailingWhitespace);
        }

        // Shadow padding on both sides, plus the card's own inset and the close/copy rail.
        return widest + ShadowPad * 2 + 44;
    }

    /// <summary>
    /// Rebuilds the document from <see cref="_rawText"/>. Cheap enough to do on a font-size change, and
    /// deliberately not done per repaint: a slider drag fires <see cref="ApplyAppearance"/> constantly.
    /// </summary>
    private void RenderText()
    {
        var fontSize = _services.Settings.Settings.Theme.FontSize;
        _renderedFontSize = fontSize;

        ResultBox.Document = MarkdownView.Build(_rawText, fontSize);

        // A document swap leaves a stale selection and its popup behind.
        SelectionPopup.IsOpen = false;
    }

    /// <summary>
    /// Places the floating copy button under the end of the selection, or hides it when nothing is
    /// selected. The popup is positioned in the TextBox's own coordinate space, so it follows a
    /// scrolled line without extra bookkeeping.
    /// </summary>
    private void OnSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!SelectionCopy.HasSelection(ResultBox))
        {
            SelectionPopup.IsOpen = false;

            // Dropping the selection puts the chat back on the whole result — but only when the user
            // did it: with the box focused. A box that merely lost focus must keep what it had, or
            // clicking into the chat box would throw the agent's context away.
            if (_useSelection && ResultBox.IsKeyboardFocusWithin)
            {
                _useSelection = false;
                _pinnedSelection = null;
                ScopeText.Text = L.T("result.context.all");
            }
            return;
        }

        var end = SelectionCopy.SelectionEndRect(ResultBox);
        if (end.IsEmpty)
        {
            SelectionPopup.IsOpen = false;
            return;
        }

        SelectionPopup.HorizontalOffset = Math.Clamp(end.Right + 4, 0, Math.Max(0, ResultBox.ActualWidth - 28));
        SelectionPopup.VerticalOffset = Math.Max(0, end.Bottom + 2);
        SelectionPopup.IsOpen = true;

        // A selection is a statement of intent: the next message is about it. Pin the text now, so the
        // question can be typed in the chat box without the context depending on the highlight still
        // being there when Enter is pressed.
        _pinnedSelection = SelectionCopy.SelectedText(ResultBox);
        if (!_useSelection)
        {
            _useSelection = true;
            ScopeText.Text = L.T("result.context.selection");
        }
    }

    private void OnCopySelectionClick(object sender, RoutedEventArgs e)
    {
        var text = SelectionCopy.SelectedText(ResultBox);
        if (string.IsNullOrEmpty(text)) return;

        if (ClipboardWriter.SetText(text))
        {
            _services.Clipboard.SuppressNext();
            ShowHint(L.T("common.copied"));
            SelectionPopup.IsOpen = false;
        }
    }

    private void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_rawText)) return;
        if (ClipboardWriter.SetText(_rawText))
        {
            _services.Clipboard.SuppressNext();
            ShowHint(L.T("common.copied"));
        }
    }

    private void ShowHint(string text)
    {
        HintText.Text = text;
        _hintTimer.Stop();
        _hintTimer.Start();
    }

    private void OnScopeClick(object sender, RoutedEventArgs e)
    {
        _useSelection = !_useSelection && SelectionCopy.HasSelection(ResultBox);
        if (_useSelection) _pinnedSelection = SelectionCopy.SelectedText(ResultBox);
        ScopeText.Text = L.T(_useSelection ? "result.context.selection" : "result.context.all");
    }

    // ---------------------------------------------------------------- actions

    /// <summary>
    /// Everything that decides what the bar shows. Name is part of it because the tooltip follows it.
    /// </summary>
    private string ActionSignature() => string.Join('\u001f', _services.Settings.Settings.Actions
        .Where(a => !string.IsNullOrWhiteSpace(a.Instruction))
        .Select(a => $"{a.Id}\u001e{a.Name}\u001e{a.Icon}"));

    /// <summary>Rebuilds the action buttons from settings. Icon-only, one row, at most 10.</summary>
    private void BuildActionBar()
    {
        ActionBar.Children.Clear();
        _actionSignature = ActionSignature();

        var scale = Math.Clamp(_services.Settings.Settings.UiScale, 50, 200) / 100.0;
        var size = Math.Round(26 * scale);

        // Accent-filled button with on-accent ink: dark ink on a light theme, light ink on a dark one.
        // Referenced, not captured — a theme switch while the bubble is open has to repaint the icons.

        // A half-written action (no instruction yet) is skipped: it would be a button that does nothing.
        foreach (var action in _services.Settings.Settings.Actions.Where(a => !string.IsNullOrWhiteSpace(a.Instruction)))
        {
            var button = new Button
            {
                Width = size,
                Height = size,
                Margin = new Thickness(0, 0, 4, 0),
                Style = (Style)FindResource("YuukaAccentIconButton"),
                ToolTip = string.IsNullOrWhiteSpace(action.Name) ? L.T("agent.action.unnamed") : action.Name,
                Tag = action,
                Content = ActionIcons.Glyph(action.Icon, Math.Round(14 * scale), "YuukaOnAccent")
            };

            button.Click += OnActionClick;
            ActionBar.Children.Add(button);
        }

        if (ActionBar.Children.Count == 0)
        {
            // Nothing registered: leave a hint instead of an empty strip so the bar still explains itself.
            var hint = new TextBlock
            {
                Text = L.T("agent.actions.hint"),
                FontSize = 9.5,
                MaxWidth = 150,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };

            hint.SetResourceReference(TextBlock.ForegroundProperty, "YuukaSubtle");
            ActionBar.Children.Add(hint);
        }
    }

    private async void OnActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AgentAction action }) return;

        var context = CurrentContext();
        var reply = await _services.Agent.RunActionAsync(action, context.Text, context.Label);
        if (!reply.Ok) ShowHint(reply.Error);
    }

    // ------------------------------------------------------------------ chat

    private void OnChatFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_placeholderShown) return;
        _placeholderShown = false;
        ChatBox.Text = "";
    }

    private void OnChatBlur(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ChatBox.Text.Length > 0) return;
        _placeholderShown = true;
        ChatBox.Text = L.T("result.chatPlaceholder");
    }

    private void OnChatKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = SendChatAsync();
        }
        else if (e.Key == Key.Escape)
        {
            // Esc in the input closes the bubble, matching the rest of the app's "Esc gets out" feel.
            Hide();
        }
    }

    private void OnSendClick(object sender, RoutedEventArgs e) => _ = SendChatAsync();

    private async Task SendChatAsync()
    {
        var message = _placeholderShown ? "" : ChatBox.Text.Trim();
        var context = CurrentContext();

        // An empty box is not a silent no-op: the service answers with `agent.chat.empty` and the bubble
        // shows that as a hint. Returning early here made the press invisible — no request, no error and
        // no turn in the log — which reads exactly like "the OCR context was never sent".
        if (message.Length > 0)
        {
            ChatBox.Text = "";
            _placeholderShown = false;
        }

        var reply = await _services.Agent.AskAsync(message, context.Text, context.Label);
        if (reply.Ok) return;

        // The request did not leave: put the question back so the user can retry instead of retyping it.
        if (message.Length > 0 && ChatBox.Text.Length == 0)
        {
            ChatBox.Text = message;
            _placeholderShown = false;
        }

        ShowHint(reply.Error);
    }

    /// <summary>
    /// What the next request is about: the pinned selection when the user picked one, the whole OCR
    /// result otherwise. It reads the pinned copy, never the live highlight, so typing the question in
    /// the chat box cannot change the answer's subject.
    /// </summary>
    internal (string? Text, string Label) CurrentContext()
    {
        if (_useSelection && !string.IsNullOrWhiteSpace(_pinnedSelection))
            return (_pinnedSelection, L.T("result.context.selection"));

        var whole = _rawText;
        return string.IsNullOrWhiteSpace(whole)
            ? (null, L.T("result.context.all"))
            : (whole, L.T("result.context.all"));
    }

    // ------------------------------------------------------------ transcript

    private void OnAgentHistoryChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RenderConversation);
            return;
        }
        RenderConversation();
    }

    private void OnAgentThinkingChanged(bool thinking)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnAgentThinkingChanged(thinking));
            return;
        }

        SendButton.IsEnabled = !thinking;
        RenderConversation();

        if (!thinking) return;

        AnswerPanel.Visibility = Visibility.Visible;
        AnswerList.Children.Add(new TextBlock
        {
            Text = L.T("result.thinking"),
            Foreground = (Brush)FindResource("YuukaSubtle"),
            FontSize = 10,
            FontStyle = FontStyles.Italic,
            Margin = new Thickness(0, 4, 0, 0)
        });
    }

    /// <summary>Draws this bubble's slice of the shared conversation: user turns right, replies left.</summary>
    private void RenderConversation() => RenderConversation(_services.Agent.History, _chatStart);

    /// <summary>
    /// Renders a supplied conversation from its first turn. The layout harness uses this overload to
    /// exercise the real rendering path without inventing agent state.
    /// </summary>
    internal void RenderConversation(IReadOnlyList<ChatTurn> history) => RenderConversation(history, 0);

    private void RenderConversation(IReadOnlyList<ChatTurn> history, int start)
    {
        if (AnswerList is null) return;

        ClearConversation();

        var first = Math.Min(start, history.Count);
        var fontSize = Math.Max(10, _services.Settings.Settings.Theme.FontSize - 1);

        for (var i = first; i < history.Count; i++)
        {
            var turn = history[i];
            var isUser = turn.IsUser;

            AnswerList.Children.Add(new TextBlock
            {
                Text = isUser ? L.T("log.role.user") : L.T("log.role.assistant"),
                Foreground = (Brush)FindResource(isUser ? "YuukaAccent" : "YuukaSubtle"),
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, i == first ? 0 : 6, 0, 1),
                HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left
            });

            var body = CreateMessageBox(turn.Text, isUser, fontSize);

            // Same affordance as the OCR text: select something and a copy button appears next to it.
            _messageTrackers.Add(SelectionCopy.Attach(body, OnMessageCopied));
            AnswerList.Children.Add(body);
        }

        AnswerPanel.Visibility = AnswerList.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// One message body. Your own turns are a read-only <see cref="TextBox"/> that looks exactly like the
    /// label it replaced; the agent's replies are a read-only <see cref="RichTextBox"/> showing the same
    /// Markdown rendering as the OCR text, because a model answers in Markdown and showing the source of
    /// a table or a code block is not an answer.
    ///
    /// Both types select by mouse, keep the selection when the focus moves on and copy on Ctrl+C; a
    /// <see cref="TextBlock"/> does none of that, which is why the transcript is not made of labels.
    /// Everything that would make them look like input fields — border, caret, background, padding,
    /// scrollbars — is switched off, so the conversation keeps the layout and rhythm it had.
    /// </summary>
    internal static TextBoxBase CreateMessageBox(string text, bool isUser, double fontSize) =>
        isUser ? PlainMessage(text, fontSize) : MarkdownMessage(text, fontSize);

    /// <summary>
    /// A user turn: literal text, never parsed. Typing <c>*</c> or <c>#</c> must stay what was typed.
    /// </summary>
    private static TextBox PlainMessage(string text, double fontSize)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            IsTabStop = false,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            Cursor = Cursors.IBeam,
            FontSize = fontSize,
            HorizontalAlignment = HorizontalAlignment.Right,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0, 0, 0, 2),

            // The selection is the point of the box: keep it painted once the focus moves to the chat box
            // (that is what "my highlight disappeared" was about) and give it the theme's accent.
            IsInactiveSelectionHighlightEnabled = true
        };

        Dress(box);
        return box;
    }

    /// <summary>
    /// An agent reply: the Markdown document, left aligned, growing to its own height. The text stays real
    /// selectable text, so the floating copy button works on a table cell exactly like on a sentence.
    /// </summary>
    private static RichTextBox MarkdownMessage(string text, double fontSize)
    {
        var box = new RichTextBox
        {
            Document = MarkdownView.Build(text, fontSize),
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            IsTabStop = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            Cursor = Cursors.IBeam,
            FontSize = fontSize,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 2),

            // A link in a reply opens in the browser — MarkdownView only ever sets an http/https target.
            IsDocumentEnabled = true,
            IsInactiveSelectionHighlightEnabled = true
        };

        Dress(box);
        return box;
    }

    /// <summary>
    /// The look both message bodies share: theme ink and font by *reference* (a captured brush would keep
    /// the old palette after a theme switch), and internal scrolling disabled so the message measures to
    /// its own content instead of a default box height — which is also what makes a RichTextBox grow.
    /// </summary>
    private static void Dress(TextBoxBase box)
    {
        box.SetResourceReference(Control.ForegroundProperty, "YuukaText");
        box.SetResourceReference(Control.FontFamilyProperty, "YuukaFontFamily");
        box.SetResourceReference(TextBoxBase.SelectionBrushProperty, "YuukaAccent");

        box.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        box.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
    }

    private void ClearConversation()
    {
        foreach (var tracker in _messageTrackers) tracker.Dispose();
        _messageTrackers.Clear();
        AnswerList.Children.Clear();
    }

    /// <summary>After the floating copy button wrote a message fragment to the clipboard.</summary>
    private void OnMessageCopied()
    {
        _services.Clipboard.SuppressNext();
        ShowHint(L.T("common.copied"));
    }

    // ------------------------------------------------------------------ drag

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        var (cursorX, cursorY) = WindowMover.CursorPosition();
        _dragging = true;
        _dragCursorX = cursorX;
        _dragCursorY = cursorY;
        _dragStartX = _spring.X;
        _dragStartY = _spring.Y;
        HeaderRow.CaptureMouse();
    }

    private void OnHeaderMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }

        var (cursorX, cursorY) = WindowMover.CursorPosition();
        _spring.SnapTo(_dragStartX + (cursorX - _dragCursorX), _dragStartY + (cursorY - _dragCursorY));
        WindowMover.MoveTo(_handle, _spring.X, _spring.Y);
    }

    private void OnHeaderMouseUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        HeaderRow.ReleaseMouseCapture();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();

    protected override void OnClosed(EventArgs e)
    {
        _animTimer.Stop();
        _hintTimer.Stop();
        _selectionTracker.Dispose();
        _services.Agent.HistoryChanged -= OnAgentHistoryChanged;
        _services.Agent.ThinkingChanged -= OnAgentThinkingChanged;
        AppearanceSync.Unregister(this);
        base.OnClosed(e);
    }
}
