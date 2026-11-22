using System.Windows.Markup;
using YuukaOCR.Core.Localization;

namespace YuukaOCR.App.Ui.Localization;

/// <summary>
/// <c>{ui:Tr tab.system}</c> — resolves a key from <see cref="L"/> while the XAML loads.
///
/// Values are deliberately not live: switching language raises <see cref="L.Changed"/> and the shell
/// rebuilds the affected windows, which keeps every view a plain, debuggable XAML file instead of a
/// tree of bindings. See <c>MainWindow.Relocalize</c>.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Key);
}
