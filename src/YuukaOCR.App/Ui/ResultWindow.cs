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

        HeaderRow.LostMouseCapture += (_, _) => EndDrag();
        _animTimer.Tick += (_, _) => OnRendering();
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
        MinWidth = Math.Max(120, settings.MinSubWindowWidth * scale) + ShadowPad * 2;
        MaxWidth = Math.Max(MinWidth + 40, 620 * scale + ShadowPad * 2);

        // The action bar scales with the UI scale, so a 150 % bar does not get a 100 % action bar.
        ChatBox.FontSize = Math.Max(10, 11 * scale);
        ScopeText.FontSize = Math.Max(9, 10 * scale);

        // Adding, removing or editing an action in Settings must show up here immediately: the settings
        // window raises Changed, AppearanceSync fans it out to this window, and this is where the bar
        // catches up — no restart, no reopening the bubble.
        if (ActionSignature() != _actionSignature) BuildActionBar();

        foreach (var button in ActionBar.Children.OfType<Button>())
            button.Width = button.Height = Math.Round(26 * scale);

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
        ResultBox.Text = text;

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

    /// <summary>
    /// Places the floating copy button under the end of the selection, or hides it when nothing is
    /// selected. The popup is positioned in the TextBox's own coordinate space, so it follows a
    /// scrolled line without extra bookkeeping.
    /// </summary>
    private void OnSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (ResultBox.SelectionLength <= 0)
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

        var end = ResultBox.GetRectFromCharacterIndex(ResultBox.SelectionStart + ResultBox.SelectionLength, true);
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
        _pinnedSelection = ResultBox.SelectedText;
        if (!_useSelection)
        {
            _useSelection = true;
            ScopeText.Text = L.T("result.context.selection");
        }
    }

    private void OnCopySelectionClick(object sender, RoutedEventArgs e)
    {
        var text = ResultBox.SelectedText;
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
        if (string.IsNullOrEmpty(ResultBox.Text)) return;
        if (ClipboardWriter.SetText(ResultBox.Text))
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
        _useSelection = !_useSelection && ResultBox.SelectionLength > 0;
        if (_useSelection) _pinnedSelection = ResultBox.SelectedText;
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
        if (message.Length == 0) return;

        var context = CurrentContext();
        ChatBox.Text = "";
        _placeholderShown = false;

        var reply = await _services.Agent.AskAsync(message, context.Text, context.Label);
        if (!reply.Ok) ShowHint(reply.Error);
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

        var whole = ResultBox.Text;
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
    /// One message body: a read-only <see cref="TextBox"/> that looks exactly like the label it replaced.
    ///
    /// A TextBox rather than a TextBlock because the user has to be able to select and copy what was said,
    /// and a TextBox is the only WPF text element that selects by mouse, keeps the selection when the
    /// focus moves on and copies on Ctrl+C without extra code. Everything that would make it look like an
    /// input field — border, caret, background, padding, scrollbars — is switched off here, so the
    /// conversation keeps the layout and rhythm it had.
    /// </summary>
    internal static TextBox CreateMessageBox(string text, bool isUser, double fontSize)
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
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            TextAlignment = isUser ? TextAlignment.Right : TextAlignment.Left,
            Margin = new Thickness(0, 0, 0, 2),

            // The selection is the point of the box: keep it painted once the focus moves to the chat box
            // (that is what "my highlight disappeared" was about) and give it the theme's accent.
            IsInactiveSelectionHighlightEnabled = true
        };

        // Theme brushes are re-created on every theme switch, so they are referenced, not copied: a
        // captured Brush instance would keep the old colour until the bubble was rebuilt.
        box.SetResourceReference(Control.ForegroundProperty, "YuukaText");
        box.SetResourceReference(Control.FontFamilyProperty, "YuukaFontFamily");
        box.SetResourceReference(TextBoxBase.SelectionBrushProperty, "YuukaAccent");
        // A disabled internal scroller: the message wraps to its own height, so there is nothing to scroll
        // and the wheel keeps reaching the conversation's ScrollViewer.
        box.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        box.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);

        return box;
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
