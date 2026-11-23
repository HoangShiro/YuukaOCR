using System.Text;

namespace YuukaOCR.Core.Markdown;

/// <summary>
/// A Markdown → <see cref="MdBlock"/> parser covering the constructs a model (or a scanned document)
/// actually produces: headings, paragraphs, fenced and indented code, blockquotes, ordered and
/// unordered lists (nested, with GFM task boxes), GFM tables with alignment, thematic breaks, and the
/// inline set — emphasis, strong, strikethrough, code spans, links, autolinks, images, escapes and hard
/// breaks.
///
/// Two deliberate departures from CommonMark, both because this renders OCR results:
/// <list type="bullet">
/// <item>a single newline inside a paragraph is a <b>line break</b>, not a soft wrap — otherwise a
/// scanned page collapses into one long paragraph and loses its line structure;</item>
/// <item>raw HTML is passed through as text instead of being parsed, because the input is never
/// trusted to be a web page.</item>
/// </list>
/// WPF-free on purpose: the parser is testable from the CLI test runner, and
/// <c>Ui/MarkdownRenderer</c> is the only place that knows what a table looks like on screen.
/// </summary>
public static class MarkdownParser
{
    /// <summary>Parses <paramref name="markdown"/> into blocks. Never throws, never returns null.</summary>
    public static IReadOnlyList<MdBlock> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return Array.Empty<MdBlock>();

        var lines = SplitLines(markdown);
        return ParseBlocks(lines, 0, lines.Count);
    }

    // ------------------------------------------------------------------ blocks

    private static List<MdBlock> ParseBlocks(IReadOnlyList<string> lines, int start, int end)
    {
        var blocks = new List<MdBlock>();
        var i = start;

        while (i < end)
        {
            var line = lines[i];

            if (IsBlank(line))
            {
                i++;
                continue;
            }

            // ``` / ~~~ fenced code. The info string is kept: the renderer labels the block with it.
            if (TryFence(line, out var fenceChar, out var fenceLength, out var info))
            {
                i++;
                var code = new List<string>();
                while (i < end && !IsClosingFence(lines[i], fenceChar, fenceLength))
                {
                    code.Add(lines[i]);
                    i++;
                }
                if (i < end) i++;    // the closing fence
                blocks.Add(new MdCodeBlock(info, string.Join("\n", code)));
                continue;
            }

            // Setext heading: the *next* line underlines this one.
            if (i + 1 < end && TrySetext(lines[i + 1], out var setextLevel) && !StartsBlock(lines, i, end))
            {
                blocks.Add(new MdHeading(setextLevel, ParseInlines(line.Trim())));
                i += 2;
                continue;
            }

            if (TryAtxHeading(line, out var level, out var headingText))
            {
                blocks.Add(new MdHeading(level, ParseInlines(headingText)));
                i++;
                continue;
            }

            if (IsRule(line))
            {
                blocks.Add(new MdRule());
                i++;
                continue;
            }

            if (IsQuoteLine(line))
            {
                var inner = new List<string>();
                while (i < end)
                {
                    if (IsQuoteLine(lines[i]))
                    {
                        inner.Add(StripQuote(lines[i]));
                        i++;
                        continue;
                    }

                    // A blank line inside a quote is part of it when the quote continues after it.
                    if (IsBlank(lines[i]) && i + 1 < end && IsQuoteLine(lines[i + 1]))
                    {
                        inner.Add("");
                        i++;
                        continue;
                    }

                    break;
                }

                blocks.Add(new MdQuote(ParseBlocks(inner, 0, inner.Count)));
                continue;
            }

            // GFM table: a header row directly above a |:--|--:| delimiter row.
            if (line.Contains('|') && i + 1 < end && TryTableDelimiter(lines[i + 1], out var aligns))
            {
                var rows = new List<MdTableRow> { TableRow(line, aligns.Count, header: true) };
                i += 2;

                while (i < end && !IsBlank(lines[i]) && lines[i].Contains('|'))
                {
                    rows.Add(TableRow(lines[i], aligns.Count, header: false));
                    i++;
                }

                blocks.Add(new MdTable(aligns, rows));
                continue;
            }

            if (TryListItem(line, out _, out _, out _))
            {
                i = ParseList(lines, i, end, blocks);
                continue;
            }

            if (IndentOf(line) >= 4)
            {
                var code = new List<string>();
                while (i < end && (IsBlank(lines[i]) || IndentOf(lines[i]) >= 4))
                {
                    code.Add(IsBlank(lines[i]) ? "" : lines[i][Math.Min(4, lines[i].Length)..]);
                    i++;
                }

                TrimTrailingBlanks(code);
                blocks.Add(new MdCodeBlock("", string.Join("\n", code)));
                continue;
            }

            // Paragraph: every following line that is not blank and does not start another block.
            var paragraph = new List<string>();
            while (i < end && !IsBlank(lines[i]) && !(paragraph.Count > 0 && StartsBlock(lines, i, end)))
            {
                paragraph.Add(lines[i]);
                i++;
            }

            blocks.Add(new MdParagraph(ParagraphInlines(paragraph)));
        }

        return blocks;
    }

    /// <summary>
    /// One list, from its first item to its last. Items may contain anything — the item's own text is
    /// parsed recursively after the marker's indent is stripped, which is what makes nested lists,
    /// multi-paragraph items and a code block inside a bullet work without extra code here.
    /// </summary>
    private static int ParseList(IReadOnlyList<string> lines, int i, int end, List<MdBlock> blocks)
    {
        TryListItem(lines[i], out var ordered, out var number, out _);
        var items = new List<MdListItem>();

        while (i < end)
        {
            if (!TryListItem(lines[i], out var itemOrdered, out _, out var contentColumn) || itemOrdered != ordered)
                break;

            var content = new List<string> { lines[i][contentColumn..] };
            var itemIndent = contentColumn;
            i++;

            while (i < end)
            {
                var current = lines[i];

                if (IsBlank(current))
                {
                    // Blank lines belong to the item only when something indented follows them.
                    var next = i + 1;
                    while (next < end && IsBlank(lines[next])) next++;
                    if (next < end && IndentOf(lines[next]) >= itemIndent && !TryListItem(lines[next], out _, out _, out _))
                    {
                        content.Add("");
                        i++;
                        continue;
                    }

                    break;
                }

                if (IndentOf(current) >= itemIndent)
                {
                    content.Add(current[itemIndent..]);
                    i++;
                    continue;
                }

                // A nested marker indented less than the text column still belongs to this item. The
                // indent test is what separates "a nested bullet" from "the next item of this list":
                // every top-level item starts at column 0 and must close the current one.
                if (IndentOf(current) > 0 && TryListItem(current, out _, out _, out var nestedColumn))
                {
                    content.Add(current[nestedColumn..]);
                    i++;
                    continue;
                }

                break;
            }

            bool? task = null;
            if (content.Count > 0 && TryTask(content[0], out var isChecked, out var rest))
            {
                task = isChecked;
                content[0] = rest;
            }

            items.Add(new MdListItem(ParseBlocks(content, 0, content.Count), task));

            // The list continues over a blank line when the next non-blank line is another item of
            // the same kind at the same level.
            var nextItem = i;
            while (nextItem < end && IsBlank(lines[nextItem])) nextItem++;
            if (nextItem < end && TryListItem(lines[nextItem], out var moreOrdered, out _, out _) && moreOrdered == ordered)
            {
                i = nextItem;
                continue;
            }

            break;
        }

        blocks.Add(new MdList(ordered, ordered ? number : 1, items));
        return i;
    }

    // ------------------------------------------------------------------ inline

    /// <summary>Parses the inline content of one block (a heading, a paragraph, a table cell).</summary>
    public static IReadOnlyList<MdInline> ParseInlines(string? text)
    {
        var output = new List<MdInline>();
        if (string.IsNullOrEmpty(text)) return output;

        var buffer = new StringBuilder();

        void Flush()
        {
            if (buffer.Length == 0) return;
            output.Add(new MdText(buffer.ToString()));
            buffer.Clear();
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            // Backslash escape: \* \_ \| \\ and friends.
            if (c == '\\' && i + 1 < text.Length && IsEscapable(text[i + 1]))
            {
                buffer.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '`')
            {
                var run = CountRun(text, i, '`');
                var close = FindCodeClose(text, i + run, run);
                if (close >= 0)
                {
                    Flush();
                    var code = text[(i + run)..close];
                    // CommonMark strips one space on each side when both are present.
                    if (code.Length >= 2 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0)
                        code = code[1..^1];

                    output.Add(new MdCode(code));
                    i = close + run;
                    continue;
                }

                buffer.Append(c);
                i++;
                continue;
            }

            if (c == '!' && i + 1 < text.Length && text[i + 1] == '[' &&
                TryLink(text, i + 1, out var altEnd, out var imageUrl, out var imageEnd))
            {
                Flush();
                output.Add(new MdImage(PlainText(ParseInlines(text[(i + 2)..altEnd])), imageUrl));
                i = imageEnd;
                continue;
            }

            if (c == '[' && TryLink(text, i, out var labelEnd, out var url, out var linkEnd))
            {
                Flush();
                output.Add(new MdLink(ParseInlines(text[(i + 1)..labelEnd]), url));
                i = linkEnd;
                continue;
            }

            if (c == '<')
            {
                var close = text.IndexOf('>', i + 1);
                if (close > i)
                {
                    var inner = text[(i + 1)..close];
                    if (LooksLikeUrl(inner))
                    {
                        Flush();
                        output.Add(new MdLink(new[] { new MdText(inner) }, inner));
                        i = close + 1;
                        continue;
                    }
                }
            }

            if (c is '*' or '_' or '~' && TryEmphasis(text, i, out var emphasis, out var consumed))
            {
                Flush();
                output.Add(emphasis);
                i += consumed;
                continue;
            }

            buffer.Append(c);
            i++;
        }

        Flush();
        return Merge(output);
    }

    /// <summary>
    /// <c>**strong**</c>, <c>*emphasis*</c>, <c>***both***</c>, <c>~~strike~~</c>. The delimiter run
    /// length decides what it is, and the closer must be a run of exactly the same length — that is
    /// what keeps <c>*a **b** c*</c> from closing the italic on the first star of the bold run.
    /// </summary>
    private static bool TryEmphasis(string text, int start, out MdInline node, out int consumed)
    {
        node = null!;
        consumed = 0;

        var c = text[start];
        var run = CountRun(text, start, c);
        var length = Math.Min(run, c == '~' ? 2 : 3);

        // A delimiter must be followed by something other than a space: "2 * 3 * 4" is not emphasis.
        if (start + length >= text.Length || char.IsWhiteSpace(text[start + length])) return false;

        // Underscores inside a word are part of the word (snake_case), never emphasis.
        if (c == '_' && start > 0 && char.IsLetterOrDigit(text[start - 1])) return false;

        for (var len = length; len >= 1; len--)
        {
            if (c == '~' && len != 2) continue;

            var close = FindCloser(text, start + len, c, len);
            if (close < 0) continue;

            if (c == '_' && close + len < text.Length && char.IsLetterOrDigit(text[close + len])) continue;

            var inner = text[(start + len)..close];
            if (inner.Trim().Length == 0) continue;

            var children = ParseInlines(inner);
            node = c == '~'
                ? new MdEmphasis(children, Bold: false, Italic: false, Strike: true)
                : new MdEmphasis(children, Bold: len >= 2, Italic: len != 2, Strike: false);
            consumed = close + len - start;
            return true;
        }

        return false;
    }

    /// <summary>The next run of exactly <paramref name="length"/> delimiters, preceded by non-space.</summary>
    private static int FindCloser(string text, int from, char c, int length)
    {
        var i = from;
        while (i < text.Length)
        {
            if (text[i] != c)
            {
                i++;
                continue;
            }

            var run = CountRun(text, i, c);
            if (run == length && i > 0 && !char.IsWhiteSpace(text[i - 1]))
                return i;

            i += run;
        }

        return -1;
    }

    /// <summary><c>[label](url)</c>: returns the label's end, the url and the index after <c>)</c>.</summary>
    private static bool TryLink(string text, int open, out int labelEnd, out string url, out int end)
    {
        labelEnd = -1;
        url = "";
        end = -1;

        var depth = 0;
        var close = -1;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '[') depth++;
            else if (text[i] == ']')
            {
                depth--;
                if (depth == 0)
                {
                    close = i;
                    break;
                }
            }
        }

        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(') return false;

        var paren = 0;
        var closeParen = -1;
        for (var i = close + 1; i < text.Length; i++)
        {
            if (text[i] == '(') paren++;
            else if (text[i] == ')')
            {
                paren--;
                if (paren == 0)
                {
                    closeParen = i;
                    break;
                }
            }
        }

        if (closeParen < 0) return false;

        var target = text[(close + 2)..closeParen].Trim();
        if (target.Length == 0) return false;

        // Drop an optional "title" and unwrap <angle brackets>.
        var space = target.IndexOfAny([' ', '\t']);
        if (space > 0 && !target.StartsWith('<')) target = target[..space];
        target = target.Trim().TrimStart('<').TrimEnd('>');

        labelEnd = close;
        url = target;
        end = closeParen + 1;
        return true;
    }

    private static bool LooksLikeUrl(string inner) =>
        inner.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        inner.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        inner.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
        inner.StartsWith("www.", StringComparison.OrdinalIgnoreCase);

    private static int FindCodeClose(string text, int from, int run)
    {
        var i = from;
        while (i < text.Length)
        {
            if (text[i] != '`')
            {
                i++;
                continue;
            }

            var length = CountRun(text, i, '`');
            if (length == run) return i;
            i += length;
        }

        return -1;
    }

    private static int CountRun(string text, int start, char c)
    {
        var i = start;
        while (i < text.Length && text[i] == c) i++;
        return i - start;
    }

    private static bool IsEscapable(char c) => c is '\\' or '`' or '*' or '_' or '{' or '}' or '[' or ']'
        or '(' or ')' or '#' or '+' or '-' or '.' or '!' or '|' or '~' or '>' or '"' or '\'';

    /// <summary>Joins adjacent text runs, so a renderer never receives two neighbours it must merge.</summary>
    private static List<MdInline> Merge(List<MdInline> nodes)
    {
        var merged = new List<MdInline>(nodes.Count);
        var text = new StringBuilder();

        void Flush()
        {
            if (text.Length == 0) return;
            merged.Add(new MdText(text.ToString()));
            text.Clear();
        }

        foreach (var node in nodes)
        {
            if (node is MdText run)
            {
                text.Append(run.Text);
                continue;
            }

            Flush();
            merged.Add(node);
        }

        Flush();
        return merged;
    }

    /// <summary>One paragraph's lines: a single newline is a line break (see the type comment).</summary>
    private static List<MdInline> ParagraphInlines(List<string> lines)
    {
        var output = new List<MdInline>();

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            // A trailing backslash or two trailing spaces is an explicit hard break; both end the line
            // either way, so the flag only matters for the last line of the paragraph.
            var hard = line.EndsWith('\\') || line.EndsWith("  ", StringComparison.Ordinal);
            if (hard) line = line.TrimEnd('\\').TrimEnd();

            output.AddRange(ParseInlines(line.TrimStart()));

            if (i < lines.Count - 1 || hard) output.Add(new MdLineBreak());
        }

        return output;
    }

    private static string PlainText(IReadOnlyList<MdInline> nodes)
    {
        var text = new StringBuilder();
        foreach (var node in nodes)
        {
            switch (node)
            {
                case MdText run: text.Append(run.Text); break;
                case MdCode code: text.Append(code.Text); break;
                case MdEmphasis emphasis: text.Append(PlainText(emphasis.Children)); break;
                case MdLink link: text.Append(PlainText(link.Children)); break;
                case MdImage image: text.Append(image.Alt); break;
            }
        }

        return text.ToString();
    }

    // ----------------------------------------------------------------- helpers

    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n' && text[i] != '\r') continue;
            lines.Add(text[start..i]);
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }

        lines.Add(text[start..]);
        return lines;
    }

    private static bool IsBlank(string line) => line.Trim().Length == 0;

    private static int IndentOf(string line)
    {
        var indent = 0;
        foreach (var c in line)
        {
            if (c == ' ') indent++;
            else if (c == '\t') indent += 4;
            else break;
        }

        return indent;
    }

    private static bool TryFence(string line, out char fence, out int length, out string info)
    {
        fence = '\0';
        length = 0;
        info = "";

        var trimmed = line.TrimStart();
        if (trimmed.Length < 3) return false;
        if (trimmed[0] is not ('`' or '~')) return false;

        var c = trimmed[0];
        var run = 0;
        while (run < trimmed.Length && trimmed[run] == c) run++;
        if (run < 3) return false;

        // A backtick fence may not contain a backtick in its info string.
        var rest = trimmed[run..].Trim();
        if (c == '`' && rest.Contains('`')) return false;

        fence = c;
        length = run;
        info = rest;
        return true;
    }

    private static bool IsClosingFence(string line, char fence, int length)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < length) return false;

        var run = 0;
        while (run < trimmed.Length && trimmed[run] == fence) run++;
        return run >= length && trimmed[run..].Trim().Length == 0;
    }

    private static bool TryAtxHeading(string line, out int level, out string text)
    {
        level = 0;
        text = "";

        var trimmed = line.TrimStart();
        var run = 0;
        while (run < trimmed.Length && trimmed[run] == '#') run++;
        if (run is < 1 or > 6) return false;
        if (run < trimmed.Length && trimmed[run] != ' ') return false;

        level = run;
        text = trimmed[run..].Trim();

        // Closing hashes: "## Title ##" → "Title".
        var end = text.Length;
        while (end > 0 && text[end - 1] == '#') end--;
        if (end < text.Length && (end == 0 || text[end - 1] == ' ')) text = text[..end].TrimEnd();

        return true;
    }

    private static bool TrySetext(string line, out int level)
    {
        level = 0;

        var trimmed = line.Trim();
        if (trimmed.Length == 0) return false;
        if (trimmed.Length > 3 && trimmed.Any(c => c != trimmed[0])) return false;

        if (trimmed.All(c => c == '=')) { level = 1; return true; }
        if (trimmed.All(c => c == '-')) { level = 2; return true; }
        return false;
    }

    private static bool IsRule(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 3) return false;

        var c = trimmed[0];
        if (c is not ('-' or '*' or '_')) return false;

        var count = 0;
        foreach (var ch in trimmed)
        {
            if (ch == c) count++;
            else if (ch != ' ') return false;
        }

        return count >= 3;
    }

    private static bool IsQuoteLine(string line) => line.TrimStart().StartsWith('>');

    private static string StripQuote(string line)
    {
        var trimmed = line.TrimStart();
        trimmed = trimmed[1..];
        return trimmed.StartsWith(' ') ? trimmed[1..] : trimmed;
    }

    /// <summary>
    /// <c>- item</c>, <c>* item</c>, <c>+ item</c>, <c>1. item</c>, <c>1) item</c>.
    /// <paramref name="contentColumn"/> is where the item's own text starts.
    /// </summary>
    private static bool TryListItem(string line, out bool ordered, out int number, out int contentColumn)
    {
        ordered = false;
        number = 1;
        contentColumn = 0;

        if (IsRule(line)) return false;

        var indent = 0;
        while (indent < line.Length && line[indent] == ' ') indent++;
        if (indent > 3) return false;                     // 4+ spaces is code, not a list
        if (indent >= line.Length) return false;

        var i = indent;
        var c = line[i];

        if (c is '-' or '*' or '+')
        {
            if (i + 1 >= line.Length || line[i + 1] != ' ') return false;
            ordered = false;
            contentColumn = i + 2;
            return true;
        }

        if (!char.IsAsciiDigit(c)) return false;

        var digits = 0;
        while (i + digits < line.Length && char.IsAsciiDigit(line[i + digits])) digits++;
        if (digits is < 1 or > 9) return false;
        if (i + digits >= line.Length || line[i + digits] is not ('.' or ')')) return false;
        if (i + digits + 1 >= line.Length || line[i + digits + 1] != ' ') return false;

        ordered = true;
        number = int.Parse(line.AsSpan(i, digits), System.Globalization.CultureInfo.InvariantCulture);
        contentColumn = i + digits + 2;
        return true;
    }

    /// <summary><c>[x] text</c> / <c>[ ] text</c> at the start of a list item.</summary>
    private static bool TryTask(string line, out bool isChecked, out string rest)
    {
        isChecked = false;
        rest = line;

        var trimmed = line.TrimStart();
        if (trimmed.Length < 3 || trimmed[0] != '[' || trimmed[2] != ']') return false;

        var mark = char.ToLowerInvariant(trimmed[1]);
        if (mark is not ('x' or ' ')) return false;

        isChecked = mark == 'x';
        rest = trimmed[3..].TrimStart();
        return true;
    }

    /// <summary>True when the line at <paramref name="i"/> begins a block other than a paragraph.</summary>
    private static bool StartsBlock(IReadOnlyList<string> lines, int i, int end)
    {
        var line = lines[i];
        if (IsBlank(line)) return true;
        if (TryFence(line, out _, out _, out _)) return true;
        if (TryAtxHeading(line, out _, out _)) return true;
        if (IsRule(line)) return true;
        if (IsQuoteLine(line)) return true;
        if (TryListItem(line, out _, out _, out _)) return true;
        if (IndentOf(line) >= 4) return true;
        if (line.Contains('|') && i + 1 < end && TryTableDelimiter(lines[i + 1], out _)) return true;
        return false;
    }

    // ------------------------------------------------------------------ tables

    /// <summary><c>|:--|:-:|--:|</c> — one alignment per column, or false when the line is not one.</summary>
    private static bool TryTableDelimiter(string line, out List<MdAlign> aligns)
    {
        aligns = new List<MdAlign>();

        var trimmed = line.Trim();
        if (trimmed.Length == 0) return false;
        if (!trimmed.Contains('-')) return false;
        if (trimmed.Any(c => c is not ('|' or '-' or ':' or ' ' or '\t'))) return false;

        var cells = SplitCells(trimmed);
        if (cells.Count == 0) return false;

        foreach (var cell in cells)
        {
            var value = cell.Trim();
            if (value.Length == 0) return false;

            // A delimiter cell is dashes with an optional alignment colon on either side. Strip the
            // colons first: Trim('-') cannot see them, because in ":---:" they sit outside the dashes.
            var body = value.Trim(':');
            if (body.Length == 0 || body.Any(c => c != '-')) return false;
            if (value.Contains("::")) return false;

            if (value.StartsWith(':') && value.EndsWith(':')) aligns.Add(MdAlign.Center);
            else if (value.EndsWith(':')) aligns.Add(MdAlign.Right);
            else if (value.StartsWith(':')) aligns.Add(MdAlign.Left);
            else aligns.Add(MdAlign.None);
        }

        return aligns.Count > 0;
    }

    private static MdTableRow TableRow(string line, int columns, bool header)
    {
        var cells = SplitCells(line);
        var parsed = new List<IReadOnlyList<MdInline>>(columns);

        for (var i = 0; i < columns; i++)
            parsed.Add(ParseInlines(i < cells.Count ? cells[i].Trim() : ""));

        return new MdTableRow(header, parsed);
    }

    /// <summary>Splits a table row on unescaped pipes, dropping the outer ones.</summary>
    private static List<string> SplitCells(string line)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var trimmed = line.Trim();

        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];

            if (c == '\\' && i + 1 < trimmed.Length && trimmed[i + 1] == '|')
            {
                current.Append('|');
                i++;
                continue;
            }

            if (c == '|')
            {
                cells.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        cells.Add(current.ToString());

        // "| a | b |" produces an empty leading cell and an empty trailing one.
        if (cells.Count > 0 && cells[0].Trim().Length == 0) cells.RemoveAt(0);
        if (cells.Count > 0 && cells[^1].Trim().Length == 0) cells.RemoveAt(cells.Count - 1);

        return cells;
    }

    private static void TrimTrailingBlanks(List<string> lines)
    {
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
    }
}
