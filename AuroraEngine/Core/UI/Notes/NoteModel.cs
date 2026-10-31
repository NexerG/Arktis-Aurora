using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Security;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Text;
using System.Numerics;
using System.Xml.Linq;

namespace ArctisAurora.Core.UI.Notes
{
    // One entry of a note: a block or a table.
    public abstract class NoteNode
    {
        // the gap above, px; null takes the layout's block spacing; how far the gap may shrink on a full page, px
        public float? spaceBefore;
        public float shrinkBefore;

        // a break before the entry, and the footnote or float it is part of; null in the flow
        public PageBreak pageBreak;
        public PageInsert? insert;

        // the password-sealed run the entry is written into; null when it is plain
        public NoteSeal? seal;

        // edited in place since its segment was last written
        internal bool dirty;
    }

    // The key of a sealed run of entries while it is unlocked.
    public sealed class NoteSeal
    {
        public readonly PasswordKey key;

        public NoteSeal(PasswordKey key) => this.key = key;
    }

    // A sealed run still locked: its <Sealed> element as read, written back unchanged.
    public sealed class NoteSealed : NoteNode
    {
        public readonly XElement element;

        public NoteSealed(XElement element) => this.element = element;
    }

    // One block of a note as data: its string, spans and block properties.
    public sealed class NoteBlock : NoteNode
    {
        public readonly TextRunData run = new TextRunData();

        public TextStyleType stylingType = TextStyleType.Text;
        public TextAlignment alignment;

        // first-line indent, px
        public float firstIndent;

        // TeX's \leftskip and \rightskip, and the last line's right indent (\parfillskip under \rightskip); null is the right indent, px
        public float leftIndent;
        public float rightIndent;
        public float? rightIndentLast;

        // a rule's weight in px, standing on its line's baseline; null is a 1 px rule centred on the line
        public float? ruleWeight;

        // the page style its page takes, and the running-head marks it sets; null leaves a mark as it was
        public string? pageStyle;
        public string? markLeft;
        public string? markRight;

        // a code block's fence language; null when none was named
        public string? language;

        // a code block that wraps its lines; .axml only, so Markdown code never wraps
        public bool codeWrap;

        // list item state
        public ListKind listKind;
        public int listLevel;
        public bool isChecked;

        // the item's own marker, and its own indent per level in px; null takes the layout's
        public ListMarker? listMarker;
        public float? listIndent;

        // the block's own line height as a multiple of its font size; null takes the layout's
        public float? lineHeight;

        // a page break may not fall between this block and the next
        public bool keepWithNext;

        // a page break may not fall between this block's lines
        public bool keepTogether;

        // TeX's \strut on every line: at least 0.7 of the line box above the baseline and 0.3 below
        public bool strut;

        // the number this item restarts its list at; null counts on from the item above
        public int? listStart;

        // pre-palette block ink, dropped from runs at load
        private const string legacyInkHex = "#2C2B26";

        // Load: the run's text joins the block's string and its style becomes the next span.
        public void AppendRun(Run appended)
        {
            string slice = appended.image != null || appended.math != null || appended.sheet != null ? BlockControl.PictureChar : appended.text ?? string.Empty;

            run.spans.Add(new StyleSpan
            {
                count = slice.Length,
                style = appended.Style,
                colorHex = string.Equals(appended.colorHex, legacyInkHex, StringComparison.OrdinalIgnoreCase) ? null : appended.colorHex,
                fontName = appended.fontName,
                variant = appended.variant,
                fontSize = appended.fontSizeAuthored ? appended.fontSize : 0,
                gradient = appended.gradient,
                effect = appended.effect,
                strikethrough = appended.strikethrough,
                underline = appended.underline,
                highlightHex = appended.highlightHex,
                stylingType = appended.stylingType,
                fontSizeAuthored = appended.fontSizeAuthored,
                imageSource = appended.image,
                imageWidth = appended.width,
                imageHeight = appended.height,
                wrap = appended.image != null ? appended.wrap : PictureWrap.Inline,
                imageX = appended.image != null ? appended.x : 0f,
                imageY = appended.image != null ? appended.y : 0f,
                imageRotation = appended.image != null ? appended.rotation : 0f,
                collision = appended.image != null ? appended.collision : PictureCollision.Box,
                mathSource = appended.image == null ? appended.math : null,
                mathDisplay = appended.image == null && appended.math != null && appended.display,
                mathStrut = appended.image == null && appended.math != null ? appended.strut : 0f,
                sheetRef = appended.image == null && appended.math == null ? appended.sheet : null,
                spaceWidth = appended.image == null && appended.math == null && appended.sheet == null ? appended.space : 0f,
                fill = appended.image == null && appended.math == null && appended.sheet == null && appended.fill,
                leader = appended.fill ? appended.leader : null,
                leaderWidth = appended.fill ? appended.leaderWidth : 0f,
                leaderInset = appended.fill ? appended.leaderInset : 0f,
                note = appended.note,
                urlStretch = appended.urlStretch,
                urlWidth = appended.urlWidth,
                urlShrink = appended.urlShrink,
                urlBreakPenalty = appended.urlBreakPenalty,
                urlBigBreakPenalty = appended.urlBigBreakPenalty,
                urlNumPenalty = appended.urlNumPenalty, urlNumWidth = appended.urlNumWidth, urlNumStretch = appended.urlNumStretch, urlNumShrink = appended.urlNumShrink,
                urlUcPenalty = appended.urlUcPenalty, urlUcWidth = appended.urlUcWidth, urlUcStretch = appended.urlUcStretch, urlUcShrink = appended.urlUcShrink,
                urlLcPenalty = appended.urlLcPenalty, urlLcWidth = appended.urlLcWidth, urlLcStretch = appended.urlLcStretch, urlLcShrink = appended.urlLcShrink
            });

            run.text += slice;
        }

        // Keeps [0..offset) and returns a block of the same styling holding the rest.
        public NoteBlock SplitAt(int offset)
        {
            string whole = run.text ?? string.Empty;
            NoteBlock tail = new NoteBlock
            {
                stylingType = stylingType,
                alignment = alignment,
                firstIndent = firstIndent,
                leftIndent = leftIndent,
                rightIndent = rightIndent,
                rightIndentLast = rightIndentLast,
                ruleWeight = ruleWeight,
                spaceBefore = spaceBefore,
                shrinkBefore = shrinkBefore,
                insert = insert,
                seal = seal,
                language = language,
                codeWrap = codeWrap,
                listKind = listKind,
                listLevel = listLevel,
                listMarker = listMarker,
                listIndent = listIndent,
                lineHeight = lineHeight,
                keepWithNext = keepWithNext,
                keepTogether = keepTogether,
                strut = strut
            };

            StyleSpan carried = run.StyleAt(offset);
            carried.count = 0;

            int index = run.SplitSpanAt(offset);
            for (int i = index; i < run.spans.Count; i++)
                tail.run.spans.Add(run.spans[i]);
            run.spans.RemoveRange(index, run.spans.Count - index);

            tail.run.text = whole[offset..];
            run.text = whole[..offset];

            if (run.spans.Count == 0) run.spans.Add(carried);
            if (tail.run.spans.Count == 0) tail.run.spans.Add(carried);

            return tail;
        }

        // One block's content as data, for an edit record that has to put it back.
        public BlockSnapshot Snapshot() => SliceSnapshot(0, run.Length);

        // The part of this block a range covers, and nothing else.
        public BlockSnapshot SliceSnapshot(int from, int to)
        {
            BlockSnapshot snapshot = new BlockSnapshot
            {
                stylingType = stylingType,
                alignment = alignment,
                firstIndent = firstIndent,
                leftIndent = leftIndent,
                rightIndent = rightIndent,
                rightIndentLast = rightIndentLast,
                ruleWeight = ruleWeight,
                spaceBefore = spaceBefore,
                shrinkBefore = shrinkBefore,
                pageBreak = pageBreak,
                pageStyle = pageStyle,
                markLeft = markLeft,
                markRight = markRight,
                insert = insert,
                seal = seal,
                language = language,
                codeWrap = codeWrap,
                listKind = listKind,
                listLevel = listLevel,
                listMarker = listMarker,
                listIndent = listIndent,
                lineHeight = lineHeight,
                keepWithNext = keepWithNext,
                keepTogether = keepTogether,
                strut = strut,
                listStart = listStart,
                isChecked = isChecked,
                text = (run.text ?? string.Empty)[from..to]
            };

            int start = 0;
            foreach (StyleSpan span in run.spans)
            {
                int spanEnd = start + span.count;
                int covered = Math.Min(spanEnd, to) - Math.Max(start, from);
                if (covered > 0)
                {
                    StyleSpan cut = span;
                    cut.count = covered;
                    snapshot.spans.Add(cut);
                }
                start = spanEnd;
            }

            if (snapshot.spans.Count == 0)
                snapshot.spans.Add(new StyleSpan { count = 0, style = FontStyle.Regular });

            return snapshot;
        }

        // Replaces everything this block holds.
        public void Restore(BlockSnapshot snapshot)
        {
            TakeKind(snapshot);
            run.spans.Clear();
            run.spans.AddRange(snapshot.spans);
            run.text = snapshot.text;
        }

        // Takes another block's kind and leaves the text alone.
        public void TakeKind(BlockSnapshot kind)
        {
            stylingType = kind.stylingType;
            alignment = kind.alignment;
            firstIndent = kind.firstIndent;
            leftIndent = kind.leftIndent;
            rightIndent = kind.rightIndent;
            rightIndentLast = kind.rightIndentLast;
            ruleWeight = kind.ruleWeight;
            spaceBefore = kind.spaceBefore;
            shrinkBefore = kind.shrinkBefore;
            pageBreak = kind.pageBreak;
            pageStyle = kind.pageStyle;
            markLeft = kind.markLeft;
            markRight = kind.markRight;
            insert = kind.insert;
            seal = kind.seal;
            language = kind.language;
            codeWrap = kind.codeWrap;
            listKind = kind.listKind;
            listLevel = kind.listLevel;
            listMarker = kind.listMarker;
            listIndent = kind.listIndent;
            lineHeight = kind.lineHeight;
            keepWithNext = kind.keepWithNext;
            keepTogether = kind.keepTogether;
            strut = kind.strut;
            listStart = kind.listStart;
            isChecked = kind.isChecked;
        }

        public static NoteBlock From(BlockSnapshot snapshot)
        {
            NoteBlock block = new NoteBlock();
            block.Restore(snapshot);
            return block;
        }
    }

    // One cell of a table: the columns it spans, its rules and its blocks.
    public sealed class NoteCell
    {
        public int span = 1;
        public CellRules? rules;
        public NoteBlock[] blocks = Array.Empty<NoteBlock>();
    }

    // A table of a note as data: fixed-width columns, rows of cells.
    public sealed class NoteTable : NoteNode
    {
        // column widths in design pixels, before zoom, and the rules down each column's edges
        public readonly float[] widths;
        public readonly TableRule[] leftRules;
        public readonly TableRule[] rightRules;

        public NoteCell[][] rows = Array.Empty<NoteCell[]>();

        public bool showBorders = true;

        // where a table narrower than the note sits; Justify is Left
        public TextAlignment alignment;

        // a left-aligned table's indent, px: TeX's \parindent before a tabular that opens a paragraph
        public float indent;

        // the math axis above the baseline of the line a tabular is centred on, px; 0 = not set in a line
        public float axis;

        // the cell inset across and down, px; null = the view's own
        public Vector2? cellPadding;

        public NoteTable(float[] widths)
        {
            this.widths = widths;
            leftRules = new TableRule[widths.Length];
            rightRules = new TableRule[widths.Length];
        }
    }

    // What an edit to a note changed.
    public enum NoteChangeKind
    {
        Text,
        Spans,
        Kind,
        Inserted,
        Removed,
        Table,
        Page,
        Palette,
        Layout,
        Properties,
        ReadOnly,
        Replaced,
        Nodes
    }

    // One change the model made, raised after it is made.
    public readonly struct NoteChange
    {
        public readonly NoteChangeKind kind;

        // blocks by flat index; a note-level index for a table
        public readonly int first;
        public readonly int count;

        // where text went in or came out, and how much; negative is removed
        public readonly DocumentAddress at;
        public readonly int length;

        // where the edit leaves the caret, and the selection's other end; null leaves them
        public readonly DocumentAddress? anchor;
        public readonly DocumentAddress? caret;

        public NoteChange(NoteChangeKind kind, int first, int count, DocumentAddress at, int length,
            DocumentAddress? anchor, DocumentAddress? caret)
        {
            this.kind = kind;
            this.first = first;
            this.count = count;
            this.at = at;
            this.length = length;
            this.anchor = anchor;
            this.caret = caret;
        }

        // Where an address from before this change sits after it.
        public DocumentAddress Map(DocumentAddress a)
        {
            switch (kind)
            {
                case NoteChangeKind.Text:
                    if (a.block != at.block || a.offset <= at.offset) return a;
                    return new DocumentAddress(a.block, Math.Max(at.offset, a.offset + length));
                case NoteChangeKind.Inserted:
                    if (a.block >= first) return new DocumentAddress(a.block + count, a.offset);
                    if (a.block == at.block && a.offset > at.offset)
                        return new DocumentAddress(first + count - 1, a.offset - at.offset + length);
                    return a;
                case NoteChangeKind.Removed:
                    if (a.block >= first + count) return new DocumentAddress(a.block - count, a.offset);
                    if (a.block == first + count - 1) return new DocumentAddress(at.block, at.offset + Math.Max(0, a.offset - length));
                    if (a.block >= first || (a.block == at.block && a.offset > at.offset)) return at;
                    return a;
                case NoteChangeKind.Nodes:
                    if (a.block >= first + count) return new DocumentAddress(a.block - count + length, a.offset);
                    if (a.block < first) return a;
                    return length > 0 ? new DocumentAddress(Math.Min(a.block, first + length - 1), a.offset) : new DocumentAddress(first, 0);
                default:
                    return a;
            }
        }
    }
}
