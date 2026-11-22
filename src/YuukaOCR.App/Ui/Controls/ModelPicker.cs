using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YuukaOCR.Core.Providers;

namespace YuukaOCR.App.Ui.Controls;

/// <summary>One suggestion row. <see cref="IsPreset"/> only drives the highlight in the list.</summary>
public sealed record ModelEntry(string Id, bool IsPreset)
{
    public override string ToString() => Id;
}

/// <summary>
/// The model box: a text field that doubles as a search field.
///
/// A plain editable ComboBox cannot do this reliably — opening its dropdown re-syncs the text from
/// SelectedItem (null) and wipes whatever was typed, which is why the old implementation had to write
/// the text back on a Dispatcher callback and still fought the control. A TextBox plus a non-focusable
/// Popup is deterministic: focus stays in the field while the list is open, typing keeps filtering,
/// and an unknown name stays usable as a custom model.
/// </summary>
public partial class ModelPicker : UserControl
{
    private List<ModelEntry> _all = new();
    private bool _suppress;

    /// <summary>Raised when the value is final: Enter, a click on a row, or the field losing focus.</summary>
    public event EventHandler? TextCommitted;

    public ModelPicker() => InitializeComponent();

    /// <summary>How many models the picker currently knows (0 = nothing to suggest).</summary>
    public int ModelCount => _all.Count;

    public string Text
    {
        get => Editor.Text ?? "";
        set
        {
            _suppress = true;
            Editor.Text = value ?? "";
            Editor.CaretIndex = Editor.Text.Length;
            _suppress = false;
        }
    }

    /// <summary>Replaces the suggestion list. An open list is re-filtered in place so it never goes stale.</summary>
    public void SetModels(IEnumerable<string> models)
    {
        _all = models
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(m => new ModelEntry(m, ModelRanking.IsPreset(m)))
            .ToList();

        if (SuggestionPopup.IsOpen) ApplyFilter(Editor.Text);
        else SuggestionList.ItemsSource = _all;
    }

    // ---------------------------------------------------------------- the list

    /// <summary>Focus or a click on the field shows the whole list (nothing to show → stays closed).</summary>
    private void OpenAll()
    {
        if (_all.Count == 0) return;

        SuggestionList.ItemsSource = _all;
        SuggestionList.SelectedIndex = -1;
        ShowPopup();
    }

    private void ShowPopup()
    {
        // The popup is its own window, so a binding to the editor's ActualWidth would cross that
        // boundary unreliably; the width is copied right before it opens instead.
        SuggestionBorder.Width = Math.Max(Editor.ActualWidth, 380);
        SuggestionPopup.IsOpen = true;
    }

    private void ClosePopup() => SuggestionPopup.IsOpen = false;

    /// <summary>Filter as you type: case-insensitive, and every word must match.</summary>
    private void ApplyFilter(string? query)
    {
        if (_all.Count == 0)
        {
            ClosePopup();
            return;
        }

        var tokens = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var filtered = tokens.Length == 0
            ? _all
            : _all.Where(m => tokens.All(t => m.Id.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();

        if (filtered.Count == 0)
        {
            // No match: keep typing — an unknown name is still a valid custom model on Enter.
            ClosePopup();
            return;
        }

        SuggestionList.ItemsSource = filtered;
        SuggestionList.SelectedIndex = -1;
        ShowPopup();
    }

    private void MoveSelection(int delta)
    {
        var count = SuggestionList.Items.Count;
        if (count == 0) return;

        var index = SuggestionList.SelectedIndex < 0
            ? (delta > 0 ? 0 : count - 1)
            : SuggestionList.SelectedIndex + delta;

        SuggestionList.SelectedIndex = Math.Clamp(index, 0, count - 1);
        SuggestionList.ScrollIntoView(SuggestionList.SelectedItem);
    }

    private void Commit(ModelEntry? picked)
    {
        if (picked is not null && !string.Equals(picked.Id, Editor.Text, StringComparison.Ordinal))
        {
            _suppress = true;
            Editor.Text = picked.Id;
            Editor.CaretIndex = Editor.Text.Length;
            _suppress = false;
        }

        ClosePopup();
        TextCommitted?.Invoke(this, EventArgs.Empty);
    }

    // ----------------------------------------------------------------- events

    private void OnEditorGotFocus(object sender, KeyboardFocusChangedEventArgs e) => OpenAll();

    /// <summary>Clicking the field again re-opens the list even when it is already focused (after Esc).</summary>
    private void OnEditorMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!SuggestionPopup.IsOpen) OpenAll();
    }

    private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        ApplyFilter(Editor.Text);
    }

    private void OnEditorLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        ClosePopup();
        TextCommitted?.Invoke(this, EventArgs.Empty);
    }

    private void OnEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
            case Key.Up:
                if (!SuggestionPopup.IsOpen)
                {
                    if (_all.Count == 0) return;
                    OpenAll();
                }
                MoveSelection(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                return;

            case Key.Enter:
                // Enter takes the highlighted row, or the first match of what was typed (like a search
                // bar). With no match the popup is closed and the typed text stays a custom model.
                Commit(SuggestionList.SelectedItem as ModelEntry
                       ?? (SuggestionPopup.IsOpen ? SuggestionList.Items.OfType<ModelEntry>().FirstOrDefault() : null));
                e.Handled = true;
                return;

            case Key.Escape:
                if (SuggestionPopup.IsOpen)
                {
                    ClosePopup();
                    e.Handled = true;
                }
                return;
        }
    }

    /// <summary>
    /// Mouse-down rather than SelectionChanged: handling it here (and marking it handled) keeps focus in
    /// the text field, so the click cannot close the popup before the row has been taken.
    /// </summary>
    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (RowOf(e.OriginalSource as DependencyObject) is not { } entry) return;
        e.Handled = true;
        Commit(entry);
    }

    private static ModelEntry? RowOf(DependencyObject? node)
    {
        while (node is not null and not ListBoxItem) node = VisualTreeHelper.GetParent(node);
        return (node as ListBoxItem)?.DataContext as ModelEntry;
    }
}
