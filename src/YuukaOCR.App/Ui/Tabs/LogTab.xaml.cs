using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using YuukaOCR.App.Services;
using YuukaOCR.App.Ui.Controls;
using YuukaOCR.Core.Interop;
using YuukaOCR.Core.Localization;
using YuukaOCR.Core.Logging;

namespace YuukaOCR.App.Ui.Tabs;

/// <summary>
/// History tab: recent results and prompts side by side, the agent's conversation underneath.
///
/// Every entry is built in code rather than from a DataTemplate because each one needs real, selectable
/// text (a read-only <see cref="TextBox"/>) plus its own copy / expand / remove buttons. Click-to-copy is
/// gone: selecting text and pressing the floating copy button is the interaction now, here and in the
/// result bubble, through the same <see cref="SelectionCopy"/> helper.
/// </summary>
public partial class LogTab : UserControl
{
    /// <summary>Collapsed height of an entry, in DIP. Expanding lifts it and lets the row grow.</summary>
    private const double CollapsedHeight = 46;

    private AppServices _services = null!;

    public LogTab() => InitializeComponent();

    public void Initialize(AppServices services)
    {
        _services = services;
        Refresh();
    }

    public void Refresh()
    {
        if (_services is null) return;

        BuildList(OutputList, _services.Log.GetRecentOutputs()
            .Select(entry => Entry.From(entry.Timestamp, Full(entry.Text)))
            .ToList(), RemoveOutput);

        BuildList(PromptList, _services.Log.GetRecentPrompts()
            .Select(entry => Entry.From(entry.Timestamp, Full(entry.Text)))
            .ToList(), RemovePrompt);

        BuildList(ChatList, _services.Log.GetChatHistory()
            .Select(entry => Entry.From(
                $"{entry.Timestamp}  ·  {RoleLabel(entry.Role)}", entry.Text,
                isUser: entry.Role != "assistant"))
            .ToList(), RemoveChat);

        // The conversation is chronological, so the interesting end is the bottom.
        ChatScroll.ScrollToEnd();

        EmptyHint.Visibility = OutputList.Children.Count + PromptList.Children.Count + ChatList.Children.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void BuildList(StackPanel host, List<Entry> entries, Action<int> remove)
    {
        host.Children.Clear();

        for (var i = 0; i < entries.Count; i++)
        {
            var index = i;
            host.Children.Add(BuildRow(entries[i], () => remove(index)));
        }
    }

    /// <summary>One history entry: stamp, selectable text, and the three icon buttons at the bottom right.</summary>
    private FrameworkElement BuildRow(Entry entry, Action remove)
    {
        var text = new TextBox
        {
            Text = entry.Full,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = (Brush)FindResource("YuukaText"),
            FontFamily = (FontFamily)FindResource("YuukaFontFamily"),
            FontSize = (double)FindResource("YuukaFontSize"),
            SelectionBrush = (Brush)FindResource("YuukaAccent"),
            MaxHeight = CollapsedHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        SelectionCopy.Attach(text, () => _services.Clipboard.SuppressNext());

        var stamp = new TextBlock
        {
            Text = entry.Stamp,
            Foreground = (Brush)FindResource(entry.IsUser ? "YuukaAccent" : "YuukaSubtle"),
            FontFamily = (FontFamily)FindResource("YuukaFontFamily"),
            FontSize = 10
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 2, 0, 0)
        };

        // Expand no longer grows the row in place: it opens the Full Text Edit Mode read-only, where the
        // whole entry is visible, selectable and copyable. The stored entry is never touched.
        var expandButton = IconButton("expand", L.T("log.expand"), () =>
            FullTextWindow.ShowFor(Window.GetWindow(this), _services, entry.Stamp, entry.Full, readOnly: true));

        actions.Children.Add(IconButton("copy", L.T("result.copy"), () =>
        {
            if (!ClipboardWriter.SetText(entry.Full)) return;
            _services.Clipboard.SuppressNext();
        }));
        actions.Children.Add(expandButton);
        actions.Children.Add(IconButton("remove", L.T("log.remove"), remove));

        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(stamp);
        row.Children.Add(text);
        row.Children.Add(actions);

        // A hairline between entries: the lists are dense and the rows have no borders of their own.
        return new Border
        {
            BorderBrush = (Brush)FindResource("YuukaBorder"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 4, 0, 4),
            Child = row
        };
    }

    private Button IconButton(string icon, string tooltip, Action onClick)
    {
        var button = new Button
        {
            Width = 22,
            Height = 22,
            Margin = new Thickness(4, 0, 0, 0),
            Style = (Style)FindResource("YuukaIconButton"),
            ToolTip = tooltip,
            Content = ActionIcons.Glyph(icon, 12, (Brush)FindResource("YuukaSubtle"))
        };

        button.Click += (_, _) => onClick();
        return button;
    }

    // ---------------------------------------------------------------- actions

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    private void RemoveOutput(int index)
    {
        _services.Log.RemoveOutput(index);
        Refresh();
    }

    private void RemovePrompt(int index)
    {
        _services.Log.RemovePrompt(index);
        Refresh();
    }

    private void RemoveChat(int index)
    {
        _services.Agent.RemoveTurn(index);
        Refresh();
    }

    private void OnClearOutputs(object sender, RoutedEventArgs e)
    {
        _services.Log.ClearOutputs();
        Refresh();
    }

    private void OnClearPrompts(object sender, RoutedEventArgs e)
    {
        _services.Log.ClearPrompts();
        Refresh();
    }

    private void OnClearChat(object sender, RoutedEventArgs e)
    {
        _services.Agent.ClearHistory();
        Refresh();
    }

    // ------------------------------------------------------------------ text

    private static string RoleLabel(string role) =>
        role == "assistant" ? L.T("log.role.assistant") : L.T("log.role.user");

    private static string Full(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Object when element.TryGetProperty("extracted_text", out var text) &&
                                  text.ValueKind == JsonValueKind.String => text.GetString() ?? "",
        _ => element.ToString()
    };

    /// <summary>One row's data. The full text is what the row shows and what the editor opens.</summary>
    private sealed class Entry
    {
        public string Stamp { get; private init; } = "";
        public string Full { get; private init; } = "";
        public bool IsUser { get; private init; }

        public static Entry From(string stamp, string full, bool isUser = false) =>
            new() { Stamp = stamp, Full = full, IsUser = isUser };
    }
}
