using System.Windows;
using System.Windows.Controls;
using YuukaOCR.App.Services;
using YuukaOCR.Core.Localization;

namespace YuukaOCR.App.Ui.Tabs;

public partial class ConsoleTab : UserControl
{
    private AppServices _services = null!;

    public ConsoleTab() => InitializeComponent();

    public void Initialize(AppServices services)
    {
        _services = services;

        _services.Log.MessageLogged += OnMessage;
        Unloaded += (_, _) => _services.Log.MessageLogged -= OnMessage;

        ConsoleBox.Text = $"[{DateTime.Now:HH:mm:ss}] {L.T("console.ready")}{Environment.NewLine}";
    }

    private void OnMessage(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnMessage(message));
            return;
        }

        ConsoleBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        if (AutoScrollBox.IsChecked == true) ConsoleBox.ScrollToEnd();
    }

    private void OnClear(object sender, RoutedEventArgs e) => ConsoleBox.Clear();
}
