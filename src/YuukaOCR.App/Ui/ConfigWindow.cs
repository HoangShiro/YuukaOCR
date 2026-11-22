using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using YuukaOCR.App.Services;
using YuukaOCR.App.Ui.Tabs;

namespace YuukaOCR.App.Ui;

/// <summary>Settings shell. Each tab owns its own bindings; this window only wires them up.</summary>
public partial class ConfigWindow : Window, IThemedWindow
{
    private readonly AppServices _services;

    /// <summary>
    /// Raised by Layout → Test. The result window belongs to the bar, so the shell only forwards the
    /// request instead of building a second bubble of its own.
    /// </summary>
    public event Action? TestResultRequested;

    public ConfigWindow(AppServices services)
    {
        _services = services;

        InitializeComponent();

        Theme.Apply(this, _services.Settings.Settings.Theme);
        VersionText.Text = $"v{typeof(ConfigWindow).Assembly.GetName().Version}";

        SystemPanel.Initialize(_services);
        ThemePanel.Initialize(_services);
        LayoutPanel.Initialize(_services);
        LayoutPanel.TestRequested += () => TestResultRequested?.Invoke();
        HookingPanel.Initialize(_services);
        PromptPanel.Initialize(_services);
        LogPanel.Initialize(_services);
        StatusPanel.Initialize(_services);
        ConsolePanel.Initialize(_services);

        // Repainted by AppearanceSync like every other window; no per-window subscription here.
        AppearanceSync.Register(this);

        // Borderless window: draggable on both the title strip and the padding around the content,
        // but not when the click lands on a control (tab, button, text box, slider...).
        MouseLeftButtonDown += OnChromeMouseDown;

        StatusPanel.Refresh();
        Tabs.SelectionChanged += (_, _) =>
        {
            if (Tabs.SelectedIndex == 5) LogPanel.Refresh();
            if (Tabs.SelectedIndex == 6) StatusPanel.Refresh();
        };
    }

    /// <summary>
    /// Repaints the shell from the current theme. <see cref="AppearanceSync"/> calls this whenever the
    /// Appearance tab changes anything, so the settings window updates while it is being used.
    /// </summary>
    public void ApplyAppearance()
    {
        Theme.Apply(this, _services.Settings.Settings.Theme);
        var scale = Math.Clamp(_services.Settings.Settings.UiScale, 50, 200) / 100.0;
        Chrome.CornerRadius = new CornerRadius(14 * scale);
    }

    public void FocusProvider(string providerId)
    {
        Tabs.SelectedIndex = 0;
        SystemPanel.SelectProvider(providerId);
    }

    /// <summary>Startup check: opens on System and puts the "no key / key rejected" notice above the input.</summary>
    public void ShowKeyPrompt(string? reason) => SystemPanel.ShowKeyPrompt(reason);

    /// <summary>
    /// Tab the user is looking at. Strings are resolved while the XAML loads, so a language switch
    /// rebuilds this window; the shell reads this first so the user lands back where they were.
    /// </summary>
    public int SelectedTab => Tabs.SelectedIndex;

    public void OpenTab(int index) =>
        Tabs.SelectedIndex = Math.Clamp(index, 0, Tabs.Items.Count - 1);

    private void OnTitleBarDown(object sender, MouseButtonEventArgs e)
    {
        DragChrome();
        e.Handled = true;   // so Window does not run DragChrome a second time
    }

    private void OnChromeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (IsInteractive(e.OriginalSource as DependencyObject)) return;
        DragChrome();
    }

    private void DragChrome()
    {
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The mouse was released before DragMove caught it — not an error.
        }
    }

    /// <summary>True when the click landed on a real control (it must not become a window drag).</summary>
    private static bool IsInteractive(DependencyObject? source)
    {
        for (var node = source; node is not null and not Window; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase or TextBoxBase or Slider or ComboBox or TabControl or ScrollBar or ListBox)
                return true;
        }
        return false;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
