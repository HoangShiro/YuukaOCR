using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
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
        // the theme instead (dark ink on the light pastel accents, light ink on the dark ones).
        var button = new Button
        {
            Width = 26,
            Height = 26,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = L.T("result.copySelection"),
            Style = box.TryFindResource("YuukaAccentIconButton") as Style,
            Content = ActionIcons.Glyph("copy", 13, GlyphBrush(box))
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

        return new Unbinder(() =>
        {
            box.SelectionChanged -= OnSelectionChanged;
            button.Click -= OnCopy;
            popup.IsOpen = false;
            popup.Child = null;
        });
    }

    /// <summary>
    /// The glyph colour: on-accent when the host paints the button with the accent (the result bubble),
    /// plain text otherwise.
    /// </summary>
    private static Brush GlyphBrush(FrameworkElement host) =>
        host.TryFindResource("YuukaOnAccent") as Brush ?? Brushes.White;

    private sealed class Unbinder(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
