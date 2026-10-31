using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Editing;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Sheets;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;
using System.Numerics;
using System.Text;

namespace ArctisAurora.Core.UI.Document
{
    // Where a caret sits: which block, and how many characters into it. A block is one control, so
    // there is no run level to address and no boundary slot to normalize away.
    public readonly struct CaretSlot
    {
        public readonly BlockControl block;
        public readonly int offset;

        public CaretSlot(BlockControl block, int offset)
        {
            this.block = block;
            this.offset = offset;
        }

        public bool Equals(CaretSlot other) => block == other.block && offset == other.offset;
    }

    // A style change as data, so an edit record can carry one and redo can replay it. A member left
    // null is one the change does not speak to — which is what lets bold over a multicoloured
    // selection keep every colour in it.
    public readonly struct StyleDelta
    {
        public readonly bool? bold;
        public readonly bool? italic;
        public readonly bool? strikethrough;
        public readonly bool? underline;
        public readonly string colorHex;
        public readonly string highlightHex;
        public readonly float? fontSize;
        public readonly bool? code;
        public readonly string fontName;
        public readonly string variant;

        public StyleDelta(bool? bold = null, bool? italic = null, bool? strikethrough = null,
            string colorHex = null, float? fontSize = null, bool? underline = null, string highlightHex = null,
            bool? code = null, string fontName = null, string variant = null)
        {
            this.bold = bold;
            this.italic = italic;
            this.strikethrough = strikethrough;
            this.underline = underline;
            this.colorHex = colorHex;
            this.highlightHex = highlightHex;
            this.fontSize = fontSize;
            this.code = code;
            this.fontName = fontName;
            this.variant = variant;
        }

        // Everything a span carries, as a delta that would give another span the same look.
        public static StyleDelta Of(StyleSpan span) => new StyleDelta(
            span.IsBold, span.IsItalic, span.strikethrough, span.colorHex ?? string.Empty,
            span.fontSizeAuthored ? span.fontSize : null, span.underline, span.highlightHex ?? string.Empty,
            fontName: span.fontName ?? string.Empty, variant: span.variant ?? string.Empty);

        public void Apply(ref StyleSpan span)
        {
            bool isBold = bold ?? span.IsBold;
            bool isItalic = italic ?? span.IsItalic;
            span.style = isBold ? (isItalic ? FontStyle.BoldItalic : FontStyle.Bold)
                                : isItalic ? FontStyle.Italic : FontStyle.Regular;

            if (strikethrough.HasValue) span.strikethrough = strikethrough.Value;
            if (underline.HasValue) span.underline = underline.Value;
            if (colorHex != null) span.colorHex = colorHex.Length == 0 ? null : colorHex;
            if (highlightHex != null) span.highlightHex = highlightHex.Length == 0 ? null : highlightHex;
            if (fontName != null) span.fontName = fontName.Length == 0 ? null : fontName;
            if (variant != null) span.variant = variant.Length == 0 ? null : variant;
            if (fontSize.HasValue)
            {
                span.fontSizeAuthored = true;
                span.fontSize = fontSize.Value;
            }
            if (code.HasValue && code.Value != (span.stylingType == TextStyleType.Code))
            {
                span.stylingType = code.Value ? TextStyleType.Code : TextStyleType.Inherit;
                span.fontSizeAuthored = false;
                if (!code.Value) span.fontSize = 0;
            }
        }

        // This delta with another laid over it; the newer one wins wherever it speaks.
        public StyleDelta With(StyleDelta over) => new StyleDelta(
            over.bold ?? bold, over.italic ?? italic, over.strikethrough ?? strikethrough,
            over.colorHex ?? colorHex, over.fontSize ?? fontSize, over.underline ?? underline,
            over.highlightHex ?? highlightHex, over.code ?? code, over.fontName ?? fontName, over.variant ?? variant);

        // Whether applying this would move anything on the span.
        public bool Changes(StyleSpan span) =>
            (bold.HasValue && bold != span.IsBold)
            || (italic.HasValue && italic != span.IsItalic)
            || (strikethrough.HasValue && strikethrough != span.strikethrough)
            || (underline.HasValue && underline != span.underline)
            || (colorHex != null && (colorHex.Length == 0 ? null : colorHex) != span.colorHex)
            || (highlightHex != null && (highlightHex.Length == 0 ? null : highlightHex) != span.highlightHex)
            || (fontSize.HasValue && fontSize != span.fontSize)
            || (code.HasValue && code != (span.stylingType == TextStyleType.Code))
            || (fontName != null && (fontName.Length == 0 ? null : fontName) != span.fontName)
            || (variant != null && (variant.Length == 0 ? null : variant) != span.variant);
    }

    // The style the next character will take: the span the caret sits in, with an armed change laid
    // over it. Resolved against the block, so an unset span member reads as what it draws as.
    public readonly struct CaretStyle
    {
        public readonly bool bold;
        public readonly bool italic;
        public readonly bool strikethrough;
        public readonly bool underline;
        public readonly bool code;
        public readonly string colorHex;
        public readonly string highlightHex;
        public readonly float fontSize;
        public readonly string fontName;
        public readonly string variant;

        public CaretStyle(BlockControl block, StyleSpan span, StyleDelta armed)
        {
            if (armed.fontName != null) span.fontName = armed.fontName.Length == 0 ? null : armed.fontName;
            fontName = block.FontAt(span);
            variant = armed.variant == null ? span.variant : armed.variant.Length == 0 ? null : armed.variant;
            bold = armed.bold ?? span.IsBold;
            italic = armed.italic ?? span.IsItalic;
            strikethrough = armed.strikethrough ?? span.strikethrough;
            underline = armed.underline ?? span.underline;
            code = armed.code ?? span.stylingType == TextStyleType.Code;
            colorHex = armed.colorHex == null ? span.colorHex : armed.colorHex.Length == 0 ? null : armed.colorHex;
            highlightHex = armed.highlightHex == null ? span.highlightHex : armed.highlightHex.Length == 0 ? null : armed.highlightHex;
            fontSize = armed.fontSize ?? (span.fontSize > 0 ? span.fontSize : block.fontSize);
        }
    }

    // The note's content area: blocks stacked top to bottom, with the caret over them.
    public partial class DocumentControl : ContainerControl, IGlyphPressTarget
    {
        public float blockSpacing;

        // page format, assigned by the editor before the first measure
        public PageLayout page = new PageLayout();

        // paper as wide as the viewport, no sheet and no inset; assigned by the editor before the first measure
        public bool fitWidth;

        // refuses picture drags, assigned by the editor
        public bool readOnly;

        // app-wide space between pages, and the page number's type size
        private static float PageGap => DocumentLayout.Defaults.Page.gap;
        private float Inset => fitWidth ? 0f : PageGap * zoom;
        private const float pageNumberSize = 12f;

        // document zoom, assigned by the editor; 1 is 100%
        public float zoom = 1f;

        // page panels at the head of children, and where the last paginate put each block
        private readonly List<PanelControl> pages = new List<PanelControl>();
        private readonly List<float> blockTops = new List<float>();
        private readonly List<float> blockHeights = new List<float>();
        // the shrink each block's gap gave the page its first line is on, px
        private readonly List<float> blockShrinks = new List<float>();
        private readonly List<Control> blockControls = new List<Control>();
        // footnote and float blocks, out of the flow: where the last paginate put each, each page's footnote rule,
        // each footnote's blocks by id, and whether the document has any
        private readonly List<Control> insertControls = new List<Control>();
        private readonly List<float> insertTops = new List<float>();
        private readonly List<float> insertHeights = new List<float>();
        private readonly List<(int page, float y)> footnoteRules = new List<(int, float)>();
        private readonly Dictionary<string, List<BlockControl>> footnoteGroups = new Dictionary<string, List<BlockControl>>();
        private bool inserts;
        private static readonly PageBands unbounded = new PageBands(0f, float.PositiveInfinity, float.MaxValue);
        private const float PxPerPt = 96f / 72.27f;

        private Vector2 measuredPaper;
        private PageBands paginatedBands;
        private int pageCount;
        private float pageHeight, lastPageHeight;

        // a sorted checklist reorder's slide, and the flow blocks still sliding
        private const float slideSeconds = 0.25f;
        private readonly List<BlockControl> sliding = new List<BlockControl>();

        // A control above the first block on the first page, assigned by the editor.
        public Control? header
        {
            get => field;
            set
            {
                if (field != null) field.Destroy();
                field = value;
                if (value != null) AddChild(value);
            }
        }
        private float headerHeight;

        // the widest unwrapped code line, margins included; the note is at least this wide
        private float contentWidth;

        // caret and highlight paint, assigned by the editor before either is built
        public string? caretColorHex;
        public string? selectionColorHex;

        // the model these blocks came from; the file is written from its block list
        internal RichTextDocument document
        {
            get => field;
            set
            {
                if (field != null) field.changed -= OnNoteChanged;
                field = value;
                field.changed += OnNoteChanged;
            }
        } = null!;

        // the open note's history, assigned by the editor; null until a session exists
        internal UndoStack undo;
        internal DocumentEditSession? session;

        // a .txt note, which takes no Markdown as it is typed
        internal bool plainText;

        private CaretControl caret;
        private readonly List<PanelControl> highlights = new List<PanelControl>();
        private readonly List<BlockControl> selectedBlocks = new List<BlockControl>();

        public BlockControl caretBlock { get; private set; }
        public int caretOffset { get; private set; }

        // the caret block's flat index; -1 until it is looked up
        private int caretIndex = -1;

        // Where a selection started; equal to the caret means nothing is selected.
        private CaretSlot anchor;

        // a style change with nothing to apply it to, spent on the next character typed
        private StyleDelta pending;

        // a selection picked up by a press, and where it would land
        internal bool textDragging { get; private set; }
        internal bool textDragHovered;
        private CaretSlot textDragPress;
        private bool textDragPicture;
        private CaretControl dropCaret;

        // the selected picture's frame lines and handles, and the resize in progress
        private readonly List<PanelControl> pictureFrame = new List<PanelControl>();
        private readonly List<PictureHandle> pictureHandles = new List<PictureHandle>();
        private PictureRotator? pictureRotator;
        private DocumentAddress resizeAt;
        private StyleSpan resizeStored;
        private Quaternion resizeRotation;

        // the turn in progress, measured from the press about the picture's centre
        private DocumentAddress rotateAt;
        private StyleSpan rotateStored;
        private Vector2 rotateCentre;
        private Vector2 rotateGrab;
        private bool rotating;

        // floating pictures as the last pagination placed them, in column space, and the views drawing them
        private readonly List<FloatPicture> floats = new List<FloatPicture>();
        private readonly List<FloatingPicture> behindViews = new List<FloatingPicture>();
        private readonly List<FloatingPicture> frontViews = new List<FloatingPicture>();
        private DocumentAddress moveAt;
        private StyleSpan moveStored;
        private Vector2 moveGrab;
        private bool moving;
        private LayoutRect resizeBox;
        private Vector2 resizeGrab;
        private bool resizing;
        private CaretSlot? dropSlot;

        // list numbers and markers are stale; resolved at the next measure
        private bool listsDirty = true;

        // the last copy with its formatting, for a paste of the same text
        private static string? copiedText;
        private static DocumentFragment? copiedFragment;

        // A line above the caret ends exactly where the caret's line begins; the slack is for the
        // float arithmetic that got them both there.
        private const float bandTolerance = 0.5f;

        public CaretSlot Focus => new CaretSlot(caretBlock, caretOffset);

        // The editor above takes the focus, so the caret survives anything still inside it.
        public override Control? ActiveContextTarget() => (parent as Control)?.ActiveContextTarget();

        #region ---- caret ----
        // extend keeps the anchor where it is, which is what shift does; otherwise the selection
        // collapses onto the new position.
        public void SetCaret(BlockControl block, int offset, bool extend = false)
        {
            if (block == null) return;

            if (block != caretBlock)
            {
                caretIndex = -1;
                LeftHeading();
                if (NoteHeadings.IsHeading(block.stylingType))
                {
                    headingBlock = block.note;
                    headingText = block.text ?? string.Empty;
                }
            }
            caretBlock = block;
            caretOffset = Math.Clamp(offset, 0, block.Length);
            if (!extend) anchor = Focus;

            if (caret == null)
            {
                caret = new CaretControl { hitTestable = false };
                caret.PaintOr(caretColorHex, PaletteRole.Ink);
                AddChild(caret);
            }
            caret.Focus();

            InvalidateArrange();
            LinkSuggest.CaretMoved(this);
        }

        public void CollapseSelection() => anchor = Focus;

        internal void Blur()
        {
            caret?.Blur();
            LeftHeading();
        }

        // the heading the caret is in, and its text when the caret entered it
        private NoteBlock? headingBlock;
        private string? headingText;

        // A heading the caret leaves with other text, still in the note and the only one that read the old
        // text, takes the links to it along.
        internal void LeftHeading()
        {
            NoteBlock? left = headingBlock;
            string before = (headingText ?? string.Empty).Trim();
            headingBlock = null;
            headingText = null;
            if (left == null || session?.path == null || !NoteHeadings.IsHeading(left.stylingType)) return;

            string now = (left.run.text ?? string.Empty).Trim();
            if (before.Length == 0 || now.Length == 0 || now == before) return;

            bool present = false;
            List<NoteBlock> blocks = document.Blocks();
            for (int i = 0; i < blocks.Count; i++)
            {
                NoteBlock block = blocks[i];
                if (block == left) present = true;
                else if (NoteHeadings.IsHeading(block.stylingType)
                         && string.Equals((block.run.text ?? string.Empty).Trim(), before, StringComparison.OrdinalIgnoreCase)) return;
            }
            if (!present) return;

            document.RenameNoteLinks(target => target.StartsWith('#') && target[1..].Trim().Equals(before, StringComparison.OrdinalIgnoreCase) ? "#" + now : null);
            NoteLinks.HeadingRenamed(session.path, before, now);
        }

        internal void FocusCaret() => caret?.Focus();

        // The run tells the document which character was pressed; the document owns the caret.
        public void GlyphPressed(TextRunControl run, int index)
        {
            if (run is not BlockControl block) return;
            if (Following && block.LinkAt(index) is string link && FollowLink(link)) return;

            PressAt(block, index);
        }

        // Opens a link through the host: a note as [[target]], a web address as it is.
        public bool FollowLink(string link) =>
            NotePropertiesControl.openLink?.Invoke(Editor?.path, MarkdownFormat.IsWebAddress(link) ? link : "[[" + link + "]]") == true;

        // A press on a picture selects it; on the selected inline one it picks it up. Any other button
        // only selects it.
        public void PicturePressed(TextRunControl run, int index, int button)
        {
            if (run is not BlockControl block) return;
            if (button == PointerEvent.leftButton && Extending)
            {
                PressAt(block, index);
                return;
            }

            DisarmStyle();
            if (button == PointerEvent.leftButton && !StoredPicture(block, index).IsFloating
                && SelectedPicture(out BlockControl selected, out int at) && selected == block && at == index)
            {
                textDragPicture = true;
                if (BeginTextDrag(new CaretSlot(block, index))) return;
                textDragPicture = false;
            }

            SetCaret(block, index);
            SetCaret(block, index + 1, true);
        }

        // A press that landed on the document itself — the gap between blocks, or past the last one.
        public override bool OnPointerPress(PointerEvent e)
        {
            if (CaretOffText(e.point, out BlockControl block, out int offset))
                PressAt(block, offset);

            return true;
        }

        // A press inside the selection picks it up; anywhere else places the caret.
        private void PressAt(BlockControl block, int offset)
        {
            DisarmStyle();
            if (!Extending && InSelection(block, offset) && BeginTextDrag(new CaretSlot(block, offset))) return;

            SetCaret(block, offset, Extending);
            Editor?.BeginSelectionDrag();
        }

        // Two clicks take the word, three the visual line.
        public override bool OnPointerTap(PointerEvent e)
        {
            if (e.tapCount == 2 && SelectedMath(out _, out _) && Editor?.EditFormula() == true) return true;
            if (e.tapCount >= 2) DisarmStyle();
            if (e.tapCount == 2) SelectWord();
            else if (e.tapCount >= 3) Editor?.SelectLine();
            else return false;

            return true;
        }

        private DocumentEditorControl Editor => parent as DocumentEditorControl;

        internal static bool Extending => InputHandler.instance.IsModifierDown(InputModifier.Extend);

        internal static bool Following => InputHandler.instance.IsModifierDown(InputModifier.Follow);
        #endregion

        #region ---- caret navigation ----
        // The caret's rect in the space the blocks are arranged in.
        internal bool CaretPoint(out float x, out float y, out float height)
        {
            x = y = height = 0f;
            if (caretBlock == null) return false;

            CaretGeometry geometry = caretBlock.CaretAt(caretOffset);
            Vector2 origin = caretBlock.TextOrigin;

            x = origin.X + geometry.x;
            y = origin.Y + geometry.top;
            height = geometry.height;
            return true;
        }

        // Nearest caret slot to a point. The candidate is a visual line rather than a block — a line
        // closer in y always wins and x only breaks ties within a band, which is what makes one
        // primitive answer up/down, line start/end and page moves.
        // bandMin/bandMax restrict which lines may answer: up and down need the line the caret is
        // already on excluded, or the gap between blocks makes it the nearest one to a point just
        // outside it and the caret never leaves.
        internal bool CaretAtPoint(float x, float y, out BlockControl block, out int offset,
            float bandMin = float.NegativeInfinity, float bandMax = float.PositiveInfinity)
        {
            block = null;
            offset = 0;

            BlockControl best = null;
            float bestLineTop = 0f;
            float bestY = float.MaxValue;
            float bestX = float.MaxValue;

            foreach (BlockControl candidate in Blocks())
            {
                IReadOnlyList<TextLine> lines = candidate.Lines;
                if (lines == null) continue;

                Vector2 origin = candidate.TextOrigin;

                foreach (TextLine line in lines)
                {
                    float top = origin.Y + line.top;

                    if (top < bandMin - bandTolerance || top + line.height > bandMax + bandTolerance)
                        continue;

                    float dy = Distance(y, top, top + line.height);
                    float dx = Distance(x, origin.X + line.left, origin.X + line.left + line.width);
                    if (dy > bestY || (dy == bestY && dx >= bestX)) continue;

                    bestY = dy;
                    bestX = dx;
                    best = candidate;
                    bestLineTop = line.top;
                }
            }

            if (best == null) return false;

            block = best;
            offset = best.IndexAt(new Vector2(x, best.TextOrigin.Y + bestLineTop + 1f));
            return true;
        }

        // A point that landed on no line at all. Below every block there is no line worth resolving
        // against, so the document's end stands in; anywhere else the nearest slot does.
        internal bool CaretOffText(Vector2 point, out BlockControl block, out int offset)
        {
            BlockControl last = LastBlock();
            if (last != null && point.Y > last.arrangedRect.Bottom)
            {
                block = last;
                offset = last.Length;
                return true;
            }

            return CaretAtPoint(point.X, point.Y, out block, out offset);
        }

        // Blocks in document order, so a caret can step out of one and into its neighbour.
        internal BlockControl AdjacentBlock(BlockControl from, int direction)
        {
            List<BlockControl> blocks = Blocks();

            int index = blocks.IndexOf(from);
            if (index < 0) return null;

            int adjacent = index + direction;
            return adjacent >= 0 && adjacent < blocks.Count ? blocks[adjacent] : null;
        }

        private BlockControl LastBlock()
        {
            List<BlockControl> blocks = Blocks();
            return blocks.Count > 0 ? blocks[^1] : null;
        }

        private static float Distance(float value, float low, float high) =>
            value < low ? low - value : (value > high ? value - high : 0f);
        #endregion

        #region ---- selection ----
        public bool HasSelection => caretBlock != null && !anchor.Equals(Focus);

        // The run of one character class around the caret, inside the block.
        public void SelectWord()
        {
            if (caretBlock == null) return;

            string s = caretBlock.text ?? string.Empty;
            TextInputActions.CharClass? right = caretOffset < s.Length ? TextInputActions.ClassOf(s[caretOffset]) : null;
            TextInputActions.CharClass? left = caretOffset > 0 ? TextInputActions.ClassOf(s[caretOffset - 1]) : null;

            // either edge of a word takes the word, not the space beside it
            TextInputActions.CharClass? picked = right == TextInputActions.CharClass.Word || left == TextInputActions.CharClass.Word
                ? TextInputActions.CharClass.Word : right ?? left;
            if (picked == null) return;

            int start = caretOffset;
            while (start > 0 && TextInputActions.ClassOf(s[start - 1]) == picked) start--;

            int end = caretOffset;
            while (end < s.Length && TextInputActions.ClassOf(s[end]) == picked) end++;

            anchor = new CaretSlot(caretBlock, start);
            SetCaret(caretBlock, end, true);
        }

        // The first block's start to the last block's end, whatever the caret was doing.
        public void SelectAll()
        {
            List<BlockControl> blocks = Blocks();
            if (blocks.Count == 0) return;

            DisarmStyle();
            anchor = new CaretSlot(blocks[0], 0);
            SetCaret(blocks[^1], blocks[^1].Length, true);
        }

        // The two ends in reading order, since a drag can run backwards.
        internal bool OrderedSelection(out DocumentAddress from, out DocumentAddress to)
        {
            from = to = default;
            if (!HasSelection) return false;

            DocumentAddress a = AddressOf(anchor.block, anchor.offset);
            DocumentAddress b = AddressOf(caretBlock, caretOffset);
            if (a.block < 0 || b.block < 0) return false;

            bool forward = a.block < b.block || (a.block == b.block && a.offset <= b.offset);
            from = forward ? a : b;
            to = forward ? b : a;
            return true;
        }

        internal DocumentAddress AnchorAddress => AddressOf(anchor.block, anchor.offset);

        // Puts a selection back by address; equal ends leave only a caret.
        internal void Select(DocumentAddress from, DocumentAddress to)
        {
            if (!Resolve(from, out BlockControl anchorBlock, out int anchorOffset)) return;
            if (!Resolve(to, out BlockControl block, out int offset)) return;

            anchor = new CaretSlot(anchorBlock, anchorOffset);
            SetCaret(block, offset, true);
            caretIndex = to.block;
        }

        internal bool InSelection(BlockControl block, int offset)
        {
            if (!OrderedSelection(out DocumentAddress from, out DocumentAddress to)) return false;

            DocumentAddress at = AddressOf(block, offset);
            return !Before(at, from) && !Before(to, at);
        }

        private static bool Before(DocumentAddress a, DocumentAddress b) =>
            a.block < b.block || (a.block == b.block && a.offset < b.offset);

        // The selection as data, when it sits in one container and so can be lifted out.
        internal DocumentFragment? SelectedFragment()
        {
            if (!OrderedSelection(out DocumentAddress from, out DocumentAddress to)) return null;
            return OneContainer(from.block, to.block) ? document.CaptureFragment(from, to) : null;
        }

        // Puts the selection on the clipboard as plain text, keeping its formatting for our own paste.
        internal bool CopySelection()
        {
            if (!OrderedSelection(out DocumentAddress from, out DocumentAddress to)) return false;

            DocumentFragment fragment = document.CaptureFragment(from, to);
            string text =string.Join(Environment.NewLine, fragment.blocks.Select(block => PlainText(block, session?.path))).Replace(TextMeasurer.SoftHyphen.ToString(), string.Empty)
                .Replace(TextMeasurer.ZeroWidthSpace.ToString(), string.Empty).Replace(TextMeasurer.UrlBreak.ToString(), string.Empty)
                .Replace(TextMeasurer.UrlBigBreak.ToString(), string.Empty).Replace(TextMeasurer.UrlNumBreak.ToString(), string.Empty)
                .Replace(TextMeasurer.UrlUcBreak.ToString(), string.Empty).Replace(TextMeasurer.UrlLcBreak.ToString(), string.Empty);

            copiedText = text;
            copiedFragment = fragment;
            ClipboardText.Set(text);
            return true;
        }

        // A snapshot's text with each formula written as its TeX source.
        private static string PlainText(BlockSnapshot block, string? home)
        {
            StringBuilder plain = new StringBuilder(block.text.Length);
            int start = 0;
            foreach (StyleSpan span in block.spans)
            {
                int count = Math.Clamp(span.count, 0, block.text.Length - start);
                if (span.IsMath && count > 0)
                {
                    string fence = span.mathDisplay ? "$$" : "$";
                    plain.Append(fence).Append(span.mathSource).Append(fence);
                }
                else if (span.IsSheet && count > 0)
                    plain.Append(SheetLinks.Plain(home, span.sheetRef));
                else if (span.spaceWidth != 0f)
                    plain.Append(' ', count);
                else
                    plain.Append(block.text, start, count);
                start += count;
            }
            if (start < block.text.Length) plain.Append(block.text, start, block.text.Length - start);
            return plain.ToString();
        }

        // Boxes for the selected range, one per visual line it covers. Everything unused is arranged
        // to nothing rather than destroyed — a drag would otherwise create and free controls every
        // tick, each one a pool allocation and a full paint-order permute.
        private void ArrangeSelection()
        {
            int used = 0;

            foreach (BlockControl marked in selectedBlocks)
                marked.selectedFrom = marked.selectedTo = -1;
            selectedBlocks.Clear();

            if (OrderedSelection(out DocumentAddress from, out DocumentAddress to))
            {
                List<BlockControl> blocks = Blocks();

                for (int i = from.block; i <= to.block && i < blocks.Count; i++)
                {
                    BlockControl block = blocks[i];
                    block.selectedFrom = i == from.block ? from.offset : 0;
                    block.selectedTo = i == to.block ? to.offset : block.Length;
                    selectedBlocks.Add(block);

                    used = HighlightBlock(block, block.selectedFrom, block.selectedTo, used);
                }
            }

            for (int i = used; i < highlights.Count; i++)
                highlights[i].Arrange(new LayoutRect(arrangedRect.x, arrangedRect.y, 0f, 0f));
        }

        // The x span comes from CaretAt, the same function that places the caret — so the highlight
        // cannot drift from the caret drawn inside it.
        private int HighlightBlock(BlockControl block, int from, int to, int used)
        {
            IReadOnlyList<TextLine> lines = block.Lines;
            if (lines == null || to <= from) return used;

            Vector2 origin = block.TextOrigin;
            ScrollableControl? viewport = TableViewport(block);

            foreach (TextLine line in lines)
            {
                if (line.segments.Count == 0) continue;

                LineSegment last = line.segments[line.segments.Count - 1];
                int lineStart = line.segments[0].charStart;
                int lineEnd = last.charStart + last.charCount;

                int start = Math.Max(from, lineStart);
                int end = Math.Min(to, lineEnd);
                if (end <= start) continue;

                // A wrapped line's end slot belongs to the line below, so CaretAt would answer for
                // the wrong line — the line's own width is the right edge in that case.
                float left = start == lineStart ? line.left : block.CaretAt(start).x;
                float right = end == lineEnd ? line.left + line.width : block.CaretAt(end).x;

                LayoutRect box = new LayoutRect(origin.X + left, origin.Y + line.top, right - left, line.height);
                if (viewport != null) box = LayoutRect.Intersect(box, viewport.arrangedRect);
                if (box.width <= 0f) continue;

                Highlight(used++).Arrange(box);
            }

            return used;
        }

        // Highlights live at the head of the child list so they draw behind the blocks — paint order
        // is the tree's DFS order, which is why the caret, added last, draws over the text.
        private PanelControl Highlight(int index)
        {
            while (highlights.Count <= index)
            {
                PanelControl box = new PanelControl { hitTestable = false };
                box.PaintOr(selectionColorHex, PaletteRole.Line);
                box.parent = this;
                children.Insert(pages.Count + highlights.Count, box);
                highlights.Add(box);
                MarkTreeOrderDirty();
                box.Measure(arrange.measuredOffer);
            }
            return highlights[index];
        }
        #endregion

        #region ---- layout ----
        protected override Vector2 MeasureCore(Vector2 availableSize)
        {
            if (listsDirty) RenumberLists();

            Vector2 paper = fitWidth && float.IsFinite(availableSize.X) ? new Vector2(availableSize.X, page.SizePx().Y * zoom) : page.SizePx() * zoom;
            if (isMeasureDirty || paper != measuredPaper || LayoutEngine.NoSkip)
            {
                measuredPaper = paper;
                float textWidth = MathF.Max(0f, paper.X - Mm(page.marginLeft + page.marginRight));

                HighlightCode();

                Profiling.Zone.Start("Document.MeasureBlocks");
                int from = -1;
                int to = -1;
                int count = 0;
                inserts = false;
                contentWidth = paper.X;
                foreach (Entity child in children)
                {
                    Control item;
                    if (child is BlockControl block)
                    {
                        block.SetZoom(zoom);
                        item = block;
                    }
                    else if (TableIn(child) is TableControl table)
                    {
                        table.SetZoom(zoom);
                        item = (Control)child;
                    }
                    else if (child is SealedControl locked) item = locked;
                    else continue;

                    item.Measure(new Vector2(textWidth, float.MaxValue));
                    if (item is BlockControl { stylingType: TextStyleType.Code, codeWrap: false } code)
                        contentWidth = MathF.Max(contentWidth, CodeWidth(code, textWidth) + Mm(page.marginLeft + page.marginRight));
                    if (IsInsert(child))
                    {
                        inserts = true;
                        continue;
                    }
                    if (item is BlockControl { shrinkBefore: > 0f } || TableIn(child) is { shrinkBefore: > 0f }) inserts = true;
                    if (Remeasured(item) || count >= blockControls.Count || blockControls[count] != item)
                    {
                        if (from < 0) from = count;
                        to = count;
                    }
                    count++;
                }
                if (from < 0) from = count;
                Profiling.Zone.End("Document.MeasureBlocks");

                float headerBefore = headerHeight;
                headerHeight = header?.Measure(new Vector2(textWidth, float.MaxValue)).Y ?? 0f;
                if (headerHeight != headerBefore) from = 0;

                Profiling.Zone.Start("Document.Paginate");
                float height = Paginate(paper, from, to);
                Profiling.Zone.End("Document.Paginate");
                SyncFloatViews(availableSize);

                EnsurePages();
                NumberPages();
                float inset = Inset;
                arrange.desired = new Vector2(contentWidth + 2f * inset, height + 2f * inset);
            }

            caret?.Measure(availableSize);
            foreach (PanelControl box in highlights)
                box.Measure(availableSize);
            foreach (PanelControl sheet in pages)
                sheet.Measure(availableSize);

            SetFlag(ArrangeFlags.MeasureDirty, false);
            return arrange.desired;
        }

        protected override void ArrangeCore(LayoutRect finalRect)
        {
            WriteArranged(finalRect);

            LayoutRect inner = finalRect.Shrink(arrange.padding);
            Vector2 paper = fitWidth ? measuredPaper : page.SizePx() * zoom;
            float inset = Inset;
            float x = inner.x + MathF.Max(inset, (inner.width - contentWidth) * 0.5f);
            float top = inner.y + inset;
            float textX = x + Mm(page.marginLeft);
            float textWidth = MathF.Max(0f, paper.X - Mm(page.marginLeft + page.marginRight));

            ArrangePages(x, top, paper.X);
            header?.Arrange(new LayoutRect(textX, top + Mm(page.marginTop), textWidth, headerHeight));

            Profiling.Zone.Start("Document.ArrangeBlocks");
            int index = 0;
            foreach (Entity child in children)
            {
                if ((child is not BlockControl && child is not SealedControl && TableIn(child) == null) || IsInsert(child) || index >= blockTops.Count) continue;

                float width = child is BlockControl { stylingType: TextStyleType.Code, codeWrap: false } code ? CodeWidth(code, textWidth)
                    : child is SealedControl locked ? MathF.Min(textWidth, locked.DesiredSize.X) : textWidth;
                ((Control)child).Arrange(new LayoutRect(textX, top + blockTops[index], width, blockHeights[index]));
                index++;
            }
            for (int i = 0; i < insertControls.Count; i++)
                insertControls[i].Arrange(new LayoutRect(textX, top + insertTops[i], textWidth, insertHeights[i]));
            Profiling.Zone.End("Document.ArrangeBlocks");

            // after the blocks, so every line's geometry is this frame's
            Profiling.Zone.Start("Document.ArrangeOverlays");
            ArrangeFloats(textX, top);
            ArrangeSelection();
            ArrangePictureFrame();
            ArrangeCaret();
            Profiling.Zone.End("Document.ArrangeOverlays");

            SetFlag(ArrangeFlags.ArrangeDirty, false);
        }

        // After the blocks, so the caret resolves against this frame's line geometry.
        private void ArrangeCaret()
        {
            if (caret != null) ArrangeCaretAt(caret, caretBlock, caretOffset);
            if (dropCaret != null) ArrangeCaretAt(dropCaret, dropSlot?.block, dropSlot?.offset ?? 0);
        }

        private static void ArrangeCaretAt(CaretControl marker, BlockControl block, int offset)
        {
            if (block == null)
            {
                marker.Arrange(LayoutRect.Empty);
                return;
            }

            CaretGeometry geometry = block.CaretAt(offset);
            Vector2 origin = block.TextOrigin;

            LayoutRect rect = new LayoutRect(origin.X + geometry.x, origin.Y + geometry.top,
                CaretControl.Width, geometry.height);
            if (TableViewport(block) is ScrollableControl viewport && !viewport.arrangedRect.Overlaps(rect))
                rect = LayoutRect.Empty;

            marker.Arrange(rect);
        }

        // Walks only the pages and blocks the clip touches, in child order.
        internal override int CollectChildren(float z)
        {
            LayoutRect clip = arrange.clip;
            float top = arrange.arranged.Shrink(arrange.padding).y + Inset;
            float from = clip.y - top;
            float to = clip.Bottom - top;
            int walked = 0;

            float stride = pageHeight + PageGap * zoom;
            int sheets = Math.Min(pageCount, pages.Count);
            for (int i = Math.Max(0, (int)(from / stride)); i < sheets && i * stride < to; i++)
                walked += UIEngine.Collect(pages[i], z);

            foreach (PanelControl box in highlights)
                walked += UIEngine.Collect(box, z);
            foreach (FloatingPicture view in behindViews)
                walked += UIEngine.Collect(view, z);
            if (header != null) walked += UIEngine.Collect(header, z);

            int low = 0;
            int high = blockControls.Count;
            while (low < high)
            {
                int mid = (low + high) / 2;
                if (blockTops[mid] + blockHeights[mid] <= from) low = mid + 1;
                else high = mid;
            }
            int end = low;
            for (; end < blockControls.Count && blockTops[end] < to; end++)
                walked += UIEngine.Collect(blockControls[end], z);
            foreach (BlockControl block in sliding)
            {
                int i = blockControls.IndexOf(block);
                if (i >= 0 && (i < low || i >= end)) walked += UIEngine.Collect(block, z);
            }
            for (int i = 0; i < insertControls.Count; i++)
                if (insertTops[i] < to && insertTops[i] + insertHeights[i] > from) walked += UIEngine.Collect(insertControls[i], z);

            foreach (FloatingPicture view in frontViews)
                walked += UIEngine.Collect(view, z);

            if (caret != null) walked += UIEngine.Collect(caret, z);
            if (dropCaret != null) walked += UIEngine.Collect(dropCaret, z);
            foreach (PanelControl line in pictureFrame)
                walked += UIEngine.Collect(line, z);
            if (pictureRotator != null) walked += UIEngine.Collect(pictureRotator, z);
            foreach (PictureHandle handle in pictureHandles)
                walked += UIEngine.Collect(handle, z);
            return walked;
        }
        #endregion
    }
}
