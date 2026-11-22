using System.Windows;
using YuukaOCR.Core.Settings;

namespace YuukaOCR.App.Ui;

/// <summary>
/// A window that can repaint itself from <see cref="ThemeSettings"/>.
///
/// Implementing this is the *only* thing a window has to do to stay in sync with the Appearance tab:
/// <see cref="AppearanceSync"/> calls <see cref="ApplyAppearance"/> on every registered window the
/// moment a setting changes, so no window subscribes to <c>SettingsStore.Changed</c> on its own.
/// That is the rule for every window added from now on — the result bubble used to keep its old
/// colours until it was reopened, which is exactly the bug this exists to prevent.
/// </summary>
public interface IThemedWindow
{
    void ApplyAppearance();
}

/// <summary>
/// Central appearance fan-out. One subscription to the settings store, one list of live windows.
///
/// Windows are held through weak references: a closed window must not be kept alive (and must not be
/// repainted) just because it registered once.
/// </summary>
public static class AppearanceSync
{
    private static readonly List<WeakReference<IThemedWindow>> Windows = new();
    private static SettingsStore? _settings;

    /// <summary>Subscribes once. Called from the composition root before the first window exists.</summary>
    public static void Initialize(SettingsStore settings)
    {
        if (ReferenceEquals(_settings, settings)) return;
        if (_settings is not null) _settings.Changed -= OnSettingsChanged;

        _settings = settings;
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>Adds a window to the fan-out and paints it immediately.</summary>
    public static void Register(IThemedWindow window)
    {
        Prune();
        Windows.Add(new WeakReference<IThemedWindow>(window));
        window.ApplyAppearance();
    }

    /// <summary>Drops a window that is closing (optional: dead references are pruned anyway).</summary>
    public static void Unregister(IThemedWindow window)
    {
        for (var i = Windows.Count - 1; i >= 0; i--)
        {
            if (Windows[i].TryGetTarget(out var target) && ReferenceEquals(target, window))
                Windows.RemoveAt(i);
        }
    }

    /// <summary>Repaints every live window. Exposed for the "apply now" path and for the tests.</summary>
    public static void ApplyAll()
    {
        Prune();
        foreach (var reference in Windows.ToArray())
        {
            if (reference.TryGetTarget(out var window)) window.ApplyAppearance();
        }
    }

    /// <summary>Number of live registrations (used by the self-check in the test runner).</summary>
    public static int Count
    {
        get
        {
            Prune();
            return Windows.Count;
        }
    }

    private static void OnSettingsChanged(object? sender, EventArgs e)
    {
        // The store can raise Changed from a background thread (the save timer); every WPF touch has to
        // happen on the dispatcher.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        if (dispatcher.CheckAccess()) ApplyAll();
        else dispatcher.BeginInvoke(ApplyAll);
    }

    private static void Prune()
    {
        for (var i = Windows.Count - 1; i >= 0; i--)
        {
            if (!Windows[i].TryGetTarget(out _)) Windows.RemoveAt(i);
        }
    }
}
