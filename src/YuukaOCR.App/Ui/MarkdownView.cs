using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using YuukaOCR.Core.Markdown;

namespace YuukaOCR.App.Ui;

/// <summary>
/// Turns parsed Markdown into a <see cref="FlowDocument"/> for the result bubble.
///
/// The parser (<see cref="MarkdownParser"/>) knows nothing about WPF and is tested on its own; this file
/// is the only place that knows what a heading looks like. Every node maps to exactly one FlowDocument
/// construct — a heading is a bold <see cref="Paragraph"/>, a table is a real <see cref="Table"/> — so
/// "support Markdown" means "support the model", and adding a construct is one case in each of the two
/// switches below.
///
/// Colours and the font family are *referenced* from the theme (<c>SetResourceReference</c>), never
/// captured: the bubble is repainted by <c>AppearanceSync</c> on a theme switch, and a captured brush
/// would keep the old colours until the window was reopened.
/// </summary>
public static class MarkdownView
{
    /// <summary>Monospace stack for code, with fallbacks for machines without a programming font.</summary>
    private static readonly FontFamily MonoFont = new("Consolas, Cascadia Mono, Courier New");

    /// <summary>Builds the document. <paramref name="fontSize"/> is the body size from the theme.</summary>
    public static FlowDocument Build(string? markdown, double fontSize)
    {
        var body = Math.Max(9, fontSize);

        var document = new FlowDocument
        {
            FontSize = body,
            PagePadding = new Thickness(0),
            // A single column that follows the window: the default 20 em column width would fold a wide
            // bubble into two columns of text, which is not what a document reader is supposed to do.
            ColumnWidth = double.PositiveInfinity
        };

        document.SetResourceReference(TextElement.ForegroundProperty, "YuukaText");
        document.SetResourceReference(TextElement.FontFamilyProperty, "YuukaFontFamily");

        AddBlocks(document.Blocks, MarkdownParser.Parse(markdown), body, 0);
        return document;
    }

    // ---------------------------------------------------------------- blocks

    private static void AddBlocks(BlockCollection target, IReadOnlyList<MdBlock> blocks, double size, int depth)
    {
        foreach (var block in blocks)
        {
            var element = Create(block, size, depth);
            if (element is not null) target.Add(element);
        }
    }

    private static Block? Create(MdBlock block, double size, int depth) => block switch
    {
        MdHeading heading => Heading(heading, size),
        MdParagraph paragraph => ParagraphOf(paragraph.Inlines, size),
        MdCodeBlock code => CodeBlock(code, size),
        MdQuote quote => Quote(quote, size, depth),
        MdList list => ListOf(list, size, depth),
        MdTable table => TableOf(table, size),
        MdRule => Rule(),
        _ => null
    };

    /// <summary>
    /// A heading, scaled from the body size. Six levels collapse into four steps on purpose: an OCR result
    /// is not a book, and h4/h5/h6 at 1.0/0.9/0.8 would be unreadable at 10 DIP.
    /// </summary>
    private static Paragraph Heading(MdHeading heading, double size)
    {
        var scale = heading.Level switch
        {
            1 => 1.5,
            2 => 1.3,
            3 => 1.15,
            _ => 1.0
        };

        var paragraph = new Paragraph
        {
            FontSize = Math.Round(size * scale, 1),
            FontWeight = heading.Level <= 3 ? FontWeights.Bold : FontWeights.SemiBold,
            Margin = new Thickness(0, heading.Level <= 2 ? 10 : 8, 0, 4),
            // Headings that are the first block must not push the text down inside the bubble.
            KeepTogether = true
        };

        AddInlines(paragraph.Inlines, heading.Inlines, paragraph.FontSize, false);
        return paragraph;
    }

    private static Paragraph ParagraphOf(IReadOnlyList<MdInline> inlines, double size)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
        AddInlines(paragraph.Inlines, inlines, size, false);
        return paragraph;
    }

    /// <summary>
    /// A fenced block: monospace, on the panel colour, with a border so it reads as a block instead of
    /// "text in a strange font". <see cref="Paragraph"/> is used rather than a UI container so the code
    /// stays selectable and copyable like the rest of the result.
    /// </summary>
    private static Paragraph CodeBlock(MdCodeBlock code, double size)
    {
        var paragraph = new Paragraph
        {
            FontFamily = MonoFont,
            FontSize = Math.Max(9, size - 0.5),
            Margin = new Thickness(0, 2, 0, 8),
            Padding = new Thickness(9, 7, 9, 7),
            BorderThickness = new Thickness(1),
            // A fixed line height keeps wrapped code readable instead of letting it collapse.
            LineHeight = Math.Max(12, size * 1.4),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };

        paragraph.SetResourceReference(TextElement.BackgroundProperty, "YuukaPanel");
        paragraph.SetResourceReference(Block.BorderBrushProperty, "YuukaBorder");

        var text = code.Text.Replace("\r\n", "\n").TrimEnd('\n');
        paragraph.Inlines.Add(new Run(text));
        return paragraph;
    }

    /// <summary>A quote: an accent rule down the left, indented content, and the whole thing dimmed.</summary>
    private static Section Quote(MdQuote quote, double size, int depth)
    {
        var section = new Section
        {
            Margin = new Thickness(0, 2, 0, 8),
            Padding = new Thickness(10, 2, 0, 2),
            BorderThickness = new Thickness(3, 0, 0, 0)
        };

        section.SetResourceReference(Block.BorderBrushProperty, "YuukaAccent");
        section.SetResourceReference(TextElement.ForegroundProperty, "YuukaSubtle");

        // Nested quotes are depth-limited: a pathological document must not recurse forever.
        AddBlocks(section.Blocks, quote.Blocks, size, Math.Min(depth + 1, 8));
        return section;
    }

    private static List ListOf(MdList list, double size, int depth)
    {
        var element = new List
        {
            MarkerStyle = list.Ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            StartIndex = Math.Max(1, list.Start),
            Margin = new Thickness(depth == 0 ? 0 : 14, 0, 0, 8),
            Padding = new Thickness(18, 0, 0, 0),
            // Markers sit outside the text column; without this the first character is clipped when the
            // list starts a line (the bullet is drawn in the padding).
            MarkerOffset = 0
        };

        foreach (var item in list.Items)
        {
            var listItem = new ListItem { Margin = new Thickness(0, 0, 0, 2) };

            if (item.Task is { } task)
            {
                // A real checkbox cannot live in a FlowDocument, so the box is drawn as a glyph — but it
                // is the same glyph a checkbox would use, and it is part of the selectable text.
                var box = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
                box.Inlines.Add(new Run(task ? "☑  " : "☐  ")
                {
                    Foreground = task ? Accent() : Subtle()
                });

                AddInlines(box.Inlines, TaskText(item.Blocks), size, false);
                listItem.Blocks.Add(box);
            }
            else
            {
                AddBlocks(listItem.Blocks, item.Blocks, size, depth + 1);
            }

            element.ListItems.Add(listItem);
        }

        return element;
    }

    /// <summary>The text of a task item's first paragraph, so the box and the text share one line.</summary>
    private static IReadOnlyList<MdInline> TaskText(IReadOnlyList<MdBlock> blocks) =>
        blocks.Count > 0 && blocks[0] is MdParagraph paragraph ? paragraph.Inlines : [];

    /// <summary>
    /// A GFM table. Columns are star-sized so the table follows the window width (it is resizable), the
    /// header row is bold on the panel colour, and every cell carries the hairline that draws the grid.
    /// </summary>
    private static Table TableOf(MdTable table, double size)
    {
        var columns = table.Aligns.Count;

        var element = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 2, 0, 8),
            FontSize = Math.Max(9, size - 0.5)
        };

        element.SetResourceReference(Block.BorderBrushProperty, "YuukaBorder");

        for (var i = 0; i < columns; i++)
            element.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });

        var group = new TableRowGroup();
        element.RowGroups.Add(group);

        foreach (var row in table.Rows)
        {
            var tableRow = new TableRow();

            for (var i = 0; i < columns; i++)
            {
                var cell = new TableCell
                {
                    Padding = new Thickness(7, 4, 7, 4),
                    BorderThickness = new Thickness(i == 0 ? 1 : 0, row.Header ? 1 : 0, 1, 1),
                    TextAlignment = row.Header ? TextAlignment.Left : Align(table.Aligns[i])
                };

                if (row.Header) cell.SetResourceReference(TextElement.BackgroundProperty, "YuukaPanel");

                var paragraph = new Paragraph
                {
                    Margin = new Thickness(0),
                    FontWeight = row.Header ? FontWeights.SemiBold : FontWeights.Normal
                };

                var inlines = i < row.Cells.Count ? row.Cells[i] : [];
                AddInlines(paragraph.Inlines, inlines, element.FontSize, false);

                cell.Blocks.Add(paragraph);
                tableRow.Cells.Add(cell);
            }

            group.Rows.Add(tableRow);
        }

        return element;
    }

    private static TextAlignment Align(MdAlign align) => align switch
    {
        MdAlign.Center => TextAlignment.Center,
        MdAlign.Right => TextAlignment.Right,
        _ => TextAlignment.Left
    };

    /// <summary>A thematic break: a paragraph with nothing but its top border.</summary>
    private static Paragraph Rule()
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 8, 0, 8),
            BorderThickness = new Thickness(0, 1, 0, 0),
            // Zero-height content: the border is the whole element.
            FontSize = 0.1,
            LineHeight = 1
        };

        paragraph.SetResourceReference(Block.BorderBrushProperty, "YuukaBorder");
        return paragraph;
    }

    // --------------------------------------------------------------- inlines

    private static void AddInlines(InlineCollection target, IReadOnlyList<MdInline> inlines, double size, bool bold)
    {
        foreach (var inline in inlines)
        {
            var element = CreateInline(inline, size, bold);
            if (element is not null) target.Add(element);
        }
    }

    private static Inline? CreateInline(MdInline inline, double size, bool bold) => inline switch
    {
        MdText text => new Run(text.Text),
        MdCode code => CodeSpan(code, size),
        MdEmphasis emphasis => Emphasis(emphasis, size, bold),
        MdLink link => LinkOf(link.Children, link.Url, size, bold),
        MdImage image => ImageOf(image, size),
        MdLineBreak => new LineBreak(),
        _ => null
    };

    /// <summary>An inline code span: monospace on the panel colour, no border (it sits inside a line).</summary>
    private static Run CodeSpan(MdCode code, double size)
    {
        var run = new Run(code.Text)
        {
            FontFamily = MonoFont,
            FontSize = Math.Max(8.5, size - 0.5)
        };

        run.SetResourceReference(TextElement.BackgroundProperty, "YuukaPanel");
        return run;
    }

    private static Span Emphasis(MdEmphasis emphasis, double size, bool bold)
    {
        var span = new Span();

        if (emphasis.Bold) span.FontWeight = FontWeights.Bold;
        if (emphasis.Italic) span.FontStyle = FontStyles.Italic;
        if (emphasis.Strike) span.TextDecorations = TextDecorations.Strikethrough;

        AddInlines(span.Inlines, emphasis.Children, size, bold || emphasis.Bold);
        return span;
    }

    /// <summary>
    /// A link. Only http/https ever reach the shell — a <c>file:</c> or <c>javascript:</c> target from a
    /// model's answer must not be able to launch anything on this machine.
    /// </summary>
    private static Hyperlink LinkOf(IReadOnlyList<MdInline> children, string url, double size, bool bold)
    {
        var link = new Hyperlink
        {
            TextDecorations = TextDecorations.Underline,
            ToolTip = url
        };

        link.SetResourceReference(TextElement.ForegroundProperty, "YuukaAccent");

        AddInlines(link.Inlines, children.Count > 0 ? children : [new MdText(url)], size, bold);
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsWeb(uri))
        {
            link.NavigateUri = uri;
            link.RequestNavigate += OnRequestNavigate;
        }

        return link;
    }

    /// <summary>An image is shown as a labelled link: the OCR pipeline never fetches remote pictures.</summary>
    private static Hyperlink ImageOf(MdImage image, double size)
    {
        var label = string.IsNullOrWhiteSpace(image.Alt) ? image.Url : image.Alt;
        var link = LinkOf([new MdText($"🖼 {label}")], image.Url, size, false);
        link.FontStyle = FontStyles.Italic;
        return link;
    }

    private static void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;

        if (!Uri.TryCreate(e.Uri?.ToString(), UriKind.Absolute, out var uri) || !IsWeb(uri)) return;

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No default browser (or the shell refused): the link simply does nothing.
        }
    }

    private static bool IsWeb(Uri uri) => uri.Scheme is "http" or "https";

    private static Brush Accent() => Brush("YuukaAccent");

    private static Brush Subtle() => Brush("YuukaSubtle");

    private static Brush Brush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}
