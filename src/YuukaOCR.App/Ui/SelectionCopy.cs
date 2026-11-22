using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using YuukaOCR.App.Ui.Controls;
using YuukaOCR.Core.Interop;
using YuukaOCR.Core.Localization;

namespace YuukaOCR.App.Ui;

/// <summary>
/// Adds the "select text → a small copy button appears next to it" behaviour to any <see cref="TextBox"/>.
///
/// One implementation, used by the result bubble and by every History row: the button is a <see cref="Popup"/>
/// anchored to the selection's last character, so it follows the text when the box scrolls instead of
/// floating somewhere near the pointer.
/// </summary>
public static class SelectionCopy
{
    /// <summary>
    /// Attaches the floating copy button. <paramref name="onCopied"/> runs after the clipboard was
    /// written, so the caller can suppress its own clipboard watcher and show a hint.
    /// </summary>
    public static IDisposable Attach(TextBox box, Action? onCopied = null)
    {
        // Accent-filled, not a transparent icon: a popup has no surface of its own, so a transparent
        // button plus on-accent ink disappeared on a dark theme. The fill makes the contrast flip with
        // the theme instead (dark ink on the light pastel accents, light ink on the dark ones), and the
        // glyph names the resource so it flips with it.
        var button = new Button
        {
            Width = 26,
            Height = 26,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = L.T("result.copySelection"),
            Style = box.TryFindResource("YuukaAccentIconButton") as Style,
            Content = ActionIcons.Glyph("copy", 13, "YuukaOnAccent")
        };

        var popup = new Popup
        {
            PlacementTarget = box,
            Placement = PlacementMode.Relative,
            AllowsTransparency = true,
            StaysOpen = true,
            PopupAnimation = PopupAnimation.Fade,
            Focusable = false,
            Child = button
        };

        void OnSelectionChanged(object sender, RoutedEventArgs e)
        {
            if (box.SelectionLength <= 0)
            {
                popup.IsOpen = false;
                return;
            }

            var end = box.GetRectFromCharacterIndex(box.SelectionStart + box.SelectionLength, true);
            if (end.IsEmpty)
            {
                popup.IsOpen = false;
                return;
            }

            popup.HorizontalOffset = Math.Clamp(end.Right + 4, 0, Math.Max(0, box.ActualWidth - 28));
            popup.VerticalOffset = Math.Max(0, end.Bottom + 2);
            popup.IsOpen = true;
        }

        void OnCopy(object sender, RoutedEventArgs e)
        {
            var text = box.SelectedText;
            if (string.IsNullOrEmpty(text)) return;
            if (!ClipboardWriter.SetText(text)) return;

            popup.IsOpen = false;
            onCopied?.Invoke();
        }

        box.SelectionChanged += OnSelectionChanged;
        button.Click += OnCopy;
        var tracker = Track(box, popup);

        return new Unbinder(() =>
        {
            box.SelectionChanged -= OnSelectionChanged;
            button.Click -= OnCopy;
            tracker.Dispose();
            popup.IsOpen = false;
            popup.Child = null;
        });
    }

    /// <summary>
    /// Keeps a floating copy button honest: it disappears as soon as the selection is no longer what the
    /// user is working on — the text box loses keyboard focus, the window is deactivated, or the pointer
    /// clicks somewhere else in the window.
    ///
    /// <see cref="Popup.StaysOpen"/> has to stay <c>true</c> here: a popup that closes itself on any
    /// outside click takes the mouse capture, and with it the very drag that creates the selection. So
    /// the "the user moved on" conditions are spelled out instead of being inherited from the popup.
    /// </summary>
    public static IDisposable Track(TextBox box, Popup popup)
    {
        void Hide()
        {
            if (popup.IsOpen) popup.IsOpen = false;
        }

        void OnFocusWithinChanged(object? sender, DependencyPropertyChangedEventArgs e)
        {
            if (box.IsKeyboardFocusWithin) return;

            // Clicking the button moves keyboard focus into the popup. That is not leaving the selection.
            if (popup.Child is UIElement child && child.IsKeyboardFocusWithin) return;

            Hide();
        }

        void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Inside the box a click clears the selection or starts a new one, and SelectionChanged
            // deals with both. Any other click in this window means the user moved on.
            if (IsInside((e.OriginalSource ?? e.Source) as DependencyObject, box)) return;
            Hide();
        }

        void OnDeactivated(object? sender, EventArgs e) => Hide();

        void OnHostVisibilityChanged(object? sender, DependencyPropertyChangedEventArgs e)
        {
            // Closing or hiding the window (the bubble's close pill) must not leave the button floating.
            if (e.NewValue is false) Hide();
        }

        Window? host = null;

        void Attach()
        {
            if (host is not null) return;
            host = Window.GetWindow(box);
            if (host is null) return;

            host.PreviewMouseLeftButtonDown += OnMouseDown;
            host.Deactivated += OnDeactivated;
            host.IsVisibleChanged += OnHostVisibilityChanged;
        }

        void Detach()
        {
            if (host is null) return;
            host.PreviewMouseLeftButtonDown -= OnMouseDown;
            host.Deactivated -= OnDeactivated;
            host.IsVisibleChanged -= OnHostVisibilityChanged;
            host = null;
        }

        // The window is only resolved once the button is actually shown: a History row is built before
        // it has a window, and a bubble that is never opened has nothing to attach to.
        void OnOpened(object? sender, EventArgs e) => Attach();
        void OnClosed(object? sender, EventArgs e) => Detach();
        void OnUnloaded(object? sender, RoutedEventArgs e) => Detach();

        box.IsKeyboardFocusWithinChanged += OnFocusWithinChanged;
        box.Unloaded += OnUnloaded;
        popup.Opened += OnOpened;
        popup.Closed += OnClosed;
        if (popup.IsOpen) Attach();

        return new Unbinder(() =>
        {
            box.IsKeyboardFocusWithinChanged -= OnFocusWithinChanged;
            box.Unloaded -= OnUnloaded;
            popup.Opened -= OnOpened;
            popup.Closed -= OnClosed;
            Detach();
        });
    }

    /// <summary>True when <paramref name="node"/> is <paramref name="ancestor"/> or lives inside it.</summary>
    private static bool IsInside(DependencyObject? node, DependencyObject ancestor)
    {
        // Bounded: a broken parent chain must not turn a click into a hang.
        for (var i = 0; i < 128 && node is not null; i++)
        {
            if (ReferenceEquals(node, ancestor)) return true;

            node = node is Visual or Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    /// <summary>
    /// The glyph colour is the theme's on-accent ink: the copy button is accent-filled, so its ink has to
    /// flip with the theme (dark on the light pastels, light on the dark ones).
    /// </summary>
    private sealed class Unbinder(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
