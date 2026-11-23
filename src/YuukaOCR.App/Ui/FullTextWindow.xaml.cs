using System.Windows;
using System.Windows.Input;
using YuukaOCR.App.Services;
using YuukaOCR.Core.Localization;
using YuukaOCR.Core.Settings;

namespace YuukaOCR.App.Ui;

/// <summary>
/// Full Text Edit Mode: one reusable window for every "this value is too big for that text box" case.
///
/// Opened from the settings window it covers the whole settings UI (it takes the owner's bounds), from
/// anywhere else it centres itself on the work area. <b>Back</b> returns without writing anything and
/// <b>Done</b> confirms — the caller only ever sees a string on Done, so a cancelled edit can never
/// leak into the settings file. Read-only mode keeps selection and Copy working while refusing edits;
/// that is what the History tab uses.
/// </summary>
public partial class FullTextWindow : Window
{
    /// <summary>Room for the drop shadow; the visible card sits this far inside the window.</summary>
    private const double ShadowPad = 30;

    private readonly bool _readOnly;
    private readonly AppServices _services;
    private string? _result;

    internal FullTextWindow(AppServices services, string title, string text, bool readOnly)
    {
        InitializeComponent();

        _readOnly = readOnly;
        _services = services;

        var settings = services.Settings.Settings;
        Theme.Apply(this, settings.Theme);

        var scale = Math.Clamp(settings.UiScale, 50, 200) / 100.0;
        Chrome.CornerRadius = new CornerRadius(12 * scale);
        EditorBox.FontSize = settings.Theme.FontSize;
        EditorBox.Text = text ?? "";
        TitleText.Text = title;

        HintText.Text = L.T(readOnly ? "fulltext.readOnlyHint" : "fulltext.hint");

        if (readOnly)
        {
            // Read-only but still selectable: the point of opening History here is to copy from it.
            EditorBox.IsReadOnly = true;
            EditorBox.IsReadOnlyCaretVisible = false;
            ReadOnlyBadge.Visibility = Visibility.Visible;
            CopyButton.Visibility = Visibility.Visible;
        }

        PreviewKeyDown += OnWindowKeyDown;

        // Resizable from every corner and edge, and the size it was left at comes back.
        ResizeGrips.Attach(this, Overlay, Chrome,
            resized: () => WindowSizeMemory.Remember(_services, WindowSizeKeys.FullText, Width, Height));
    }

    /// <summary>
    /// Shows the editor modally and returns the edited text, or null when the user backed out.
    /// In read-only mode the return value is always null: nothing can be applied.
    /// </summary>
    public static string? ShowFor(Window? owner, AppServices services, string title, string text, bool readOnly = false)
    {
        var window = new FullTextWindow(services, title, text, readOnly);

        if (owner is not null && owner.IsVisible)
        {
            window.Owner = owner;
            window.PlaceOver(owner);
        }
        else
        {
            var area = SystemParameters.WorkArea;

            if (!WindowSizeMemory.Restore(window, services, WindowSizeKeys.FullText))
            {
                window.Width = Math.Min(area.Width * 0.8, 900) + ShadowPad * 2;
                window.Height = Math.Min(area.Height * 0.8, 700) + ShadowPad * 2;
            }

            window.Left = area.Left + (area.Width - window.Width) / 2;
            window.Top = area.Top + (area.Height - window.Height) / 2;
        }

        window.ShowDialog();
        return readOnly ? null : window._result;
    }

    /// <summary>
    /// Takes exactly the owner's rectangle — not a pixel more. The editor is a stand-in for the settings
    /// window while it is open, so it has to occupy the same space on screen, including the shadow padding
    /// (both windows use the same 30 DIP pad, so the cards line up too).
    ///
    /// A floor keeps it usable when the owner is the small result bubble: the settings window is never
    /// smaller than its own 820x580 minimum, so this never breaks the exact match.
    /// </summary>
    internal void PlaceOver(Window owner)
    {
        Width = Math.Max(owner.ActualWidth, MinWidth);
        Height = Math.Max(owner.ActualHeight, MinHeight);
        Left = owner.Left;
        Top = owner.Top;
    }

    // ------------------------------------------------------------------ actions

    private void OnDone(object sender, RoutedEventArgs e) => Confirm();

    private void OnBack(object sender, RoutedEventArgs e) => Cancel();

    private void Confirm()
    {
        // Read-only callers never receive text, but the window still closes the same way.
        _result = _readOnly ? null : EditorBox.Text;
        CloseDialog(true);
    }

    private void Cancel() => CloseDialog(false);

    private void CloseDialog(bool applied)
    {
        try
        {
            DialogResult = applied;
        }
        catch (InvalidOperationException)
        {
            // Not shown with ShowDialog (a preview harness): closing is still the right answer.
            Close();
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            if (EditorBox.SelectionLength > 0) EditorBox.Copy();
            else Clipboard.SetText(EditorBox.Text ?? "");
        }
        catch (Exception ex)
        {
            // The clipboard can be held by another process; nothing to do but say so.
            HintText.Text = L.F("common.errorPrefix", ex.Message);
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Cancel();
        }
    }

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Enter confirms; a plain Enter stays a newline, which is the whole point of the editor.
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            Confirm();
        }
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        EditorBox.Focus();
        if (_readOnly)
        {
            // Inspecting means the whole thing is selected, ready to copy.
            EditorBox.SelectAll();
            EditorBox.ScrollToHome();
        }
        else
        {
            EditorBox.CaretIndex = EditorBox.Text.Length;
            EditorBox.ScrollToEnd();
        }
    }
}
