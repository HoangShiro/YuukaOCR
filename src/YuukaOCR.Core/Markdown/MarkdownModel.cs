namespace YuukaOCR.Core.Markdown;

/// <summary>
/// The document model a Markdown text turns into. Deliberately WPF-free: the parser lives in Core so
/// it can be tested without a window, and <c>Ui/MarkdownRenderer</c> turns these nodes into a
/// FlowDocument. Everything the result window shows (headings, lists, tables, quotes, code, emphasis)
/// is one of the records below — adding a construct means adding a record and a case, never a new
/// rendering path.
/// </summary>
public abstract record MdBlock;

/// <summary><c># Title</c> … <c>###### Title</c>, and the <c>===</c> / <c>---</c> underline forms.</summary>
public sealed record MdHeading(int Level, IReadOnlyList<MdInline> Inlines) : MdBlock;

public sealed record MdParagraph(IReadOnlyList<MdInline> Inlines) : MdBlock;

/// <summary>A fenced (```) or four-space-indented block. <paramref name="Language"/> is the info string.</summary>
public sealed record MdCodeBlock(string Language, string Text) : MdBlock;

/// <summary><c>&gt; quoted</c> — may contain any block, including nested quotes and lists.</summary>
public sealed record MdQuote(IReadOnlyList<MdBlock> Blocks) : MdBlock;

/// <summary><c>-</c>, <c>*</c>, <c>+</c> or <c>1.</c> lists. <paramref name="Start"/> is the first number.</summary>
public sealed record MdList(bool Ordered, int Start, IReadOnlyList<MdListItem> Items) : MdBlock;

/// <summary>
/// One list entry. <paramref name="Task"/> is <c>true</c> for <c>[x]</c>, <c>false</c> for <c>[ ]</c>
/// and <c>null</c> for an ordinary bullet, so a checklist renders with a real checkbox.
/// </summary>
public sealed record MdListItem(IReadOnlyList<MdBlock> Blocks, bool? Task = null) : MdBlock;

/// <summary>A GFM table: one header row, then body rows. Every row has one cell per column.</summary>
public sealed record MdTable(IReadOnlyList<MdAlign> Aligns, IReadOnlyList<MdTableRow> Rows) : MdBlock;

public sealed record MdTableRow(bool Header, IReadOnlyList<IReadOnlyList<MdInline>> Cells);

/// <summary><c>---</c> / <c>***</c> / <c>___</c> on its own line.</summary>
public sealed record MdRule : MdBlock;

/// <summary>Column alignment taken from the <c>|:--|:-:|--:|</c> delimiter row.</summary>
public enum MdAlign
{
    None,
    Left,
    Center,
    Right
}

// ---------------------------------------------------------------------------- inlines

public abstract record MdInline;

/// <summary>Literal text. Consecutive runs are merged by the parser, so a renderer never sees two
/// neighbours that could have been one.</summary>
public sealed record MdText(string Text) : MdInline;

/// <summary>An inline <c>`code span`</c>.</summary>
public sealed record MdCode(string Text) : MdInline;

/// <summary><c>**bold**</c>, <c>*italic*</c>, <c>~~struck~~</c> and every combination.</summary>
public sealed record MdEmphasis(IReadOnlyList<MdInline> Children, bool Bold, bool Italic, bool Strike) : MdInline;

/// <summary><c>[label](url)</c> and <c>&lt;https://…&gt;</c>.</summary>
public sealed record MdLink(IReadOnlyList<MdInline> Children, string Url) : MdInline;

/// <summary><c>![alt](url)</c> — kept as its own node so a renderer can show the alt text differently.</summary>
public sealed record MdImage(string Alt, string Url) : MdInline;

/// <summary>A hard line break (two trailing spaces or a trailing backslash).</summary>
public sealed record MdLineBreak : MdInline;
