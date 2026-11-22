using System.Windows;
using System.Windows.Controls;
using YuukaOCR.App.Services;
using YuukaOCR.Core.Localization;

namespace YuukaOCR.App.Ui.Controls;

/// <summary>
/// Expand + Reset for one prompt field. Attached to a <see cref="TextBox"/> it owns both behaviours, so
/// every prompt in the app behaves identically instead of each tab re-implementing them.
///
/// * Expand opens <see cref="FullTextWindow"/> with the box's current text. Only *Done* writes back, so
///   backing out of the editor leaves the field untouched.
/// * Reset puts the shipped default back — it never just empties the field. A field with no predefined
///   default (an action the user invented) keeps the button disabled.
///
/// Both paths raise <see cref="Committed"/> because the text is set programmatically: the box never
/// loses focus, so the tab's LostFocus handler would not fire.
/// </summary>
public partial class PromptTools : UserControl
{
    private AppServices? _services;
    private TextBox? _box;
    private string _titleKey = "";
    private Func<string?>? _defaultText;

    /// <summary>Raised after the text box changed (expand→Done, or reset). The tab persists here.</summary>
    public event EventHandler? Committed;

    public PromptTools() => InitializeComponent();

    public void Initialize(AppServices services, TextBox box, string titleKey, Func<string?>? defaultText)
    {
        _services = services;
        _box = box;
        _titleKey = titleKey;
        _defaultText = defaultText;
        Refresh();
    }

    /// <summary>Re-evaluates whether a default exists — call when the target field changes (action tabs).</summary>
    public void Refresh() => ResetButton.IsEnabled = _defaultText?.Invoke() is not null;

    private void OnExpand(object sender, RoutedEventArgs e)
    {
        if (_services is null || _box is null) return;

        var edited = FullTextWindow.ShowFor(Window.GetWindow(this), _services, L.T(_titleKey),
            _box.Text ?? "", readOnly: false);

        // null = Back / Escape: the field keeps exactly what it had.
        if (edited is null) return;

        _box.Text = edited;
        Committed?.Invoke(this, EventArgs.Empty);
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        var fallback = _defaultText?.Invoke();
        if (_box is null || fallback is null) return;

        // Never silently throw away writing: only ask when there is something to lose.
        if (!string.Equals(_box.Text ?? "", fallback, StringComparison.Ordinal))
        {
            var answer = MessageBox.Show(L.T("prompt.resetConfirm"), "YuukaOCR",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }

        _box.Text = fallback;
        Committed?.Invoke(this, EventArgs.Empty);
    }
}
