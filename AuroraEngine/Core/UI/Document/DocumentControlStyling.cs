using ArctisAurora.Core.UI.Sheets;
using ArctisAurora.Core.UI.Text;

namespace ArctisAurora.Core.UI.Document
{
    public partial class DocumentControl
    {
        #region ---- formulas ----
        // The selection is exactly one formula character.
        internal bool SelectedMath(out BlockControl block, out int index)
        {
            block = null!;
            index = 0;
            if (!OrderedSelection(out DocumentAddress from, out DocumentAddress to)) return false;
            if (from.block != to.block || to.offset != from.offset + 1) return false;
            if (!Resolve(from, out block, out index)) return false;
            return StoredMath(block, index).IsMath;
        }

        // The formula span at an offset; default when there is none.
        internal static StyleSpan StoredMath(BlockControl block, int index)
        {
            int start = 0;
            foreach (StyleSpan span in block.spans)
            {
                if (start == index && span.IsMath && span.count > 0) return span;
                start += span.count;
            }
            return default;
        }

        // An empty formula at the caret, unrecorded; null where a formula cannot go.
        internal DocumentAddress? PlaceMath(bool display)
        {
            if (caretBlock == null || readOnly || plainText
                || caretBlock.stylingType is TextStyleType.Code or TextStyleType.Rule) return null;

            StyleSpan span = caretBlock.StyleAt(caretOffset).AsText();
            span.count = 1;
            span.mathSource = string.Empty;
            span.mathDisplay = display;

            BlockSnapshot block = new BlockSnapshot { text = BlockControl.PictureChar };
            block.spans.Add(span);
            DocumentFragment fragment = new DocumentFragment();
            fragment.blocks.Add(block);

            DocumentAddress at = AddressOf(caretBlock, caretOffset);
            document.InsertBetween(at, new DocumentAddress(at.block, at.offset + 1), fragment);
            Select(at, new DocumentAddress(at.block, at.offset + 1));
            DisarmStyle();
            return at;
        }

        // A placed formula as an insert record, its source final.
        internal void RecordPlaced(DocumentAddress at)
        {
            if (!Resolve(at, out BlockControl block, out int index)) return;

            BlockSnapshot snapshot = new BlockSnapshot { text = BlockControl.PictureChar };
            StyleSpan span = StoredMath(block, index);
            span.count = 1;
            snapshot.spans.Add(span);
            DocumentFragment fragment = new DocumentFragment();
            fragment.blocks.Add(snapshot);

            undo?.Push(new InsertRangeEdit(document, at, new DocumentAddress(at.block, at.offset + 1), fragment));
        }

        internal void RecordMath(DocumentAddress at, string before, string after) =>
            undo?.Push(new MathEdit(document, at, before, after));

        internal void RemovePlaced(DocumentAddress at) =>
            document.DeleteBetween(at, new DocumentAddress(at.block, at.offset + 1));

        // A formula's drawn box; its caret slot while it waits to be laid out.
        internal LayoutRect MathAnchor(DocumentAddress at)
        {
            if (!Resolve(at, out BlockControl block, out int index)) return LayoutRect.Empty;
            if (block.MathBox(index, out LayoutRect box)) return box;

            CaretGeometry slot = block.CaretAt(index);
            return new LayoutRect(block.TextOrigin.X + slot.x, block.TextOrigin.Y + slot.top, 0f, slot.height);
        }
        #endregion

        #region ---- sheet links ----
        // Replaces the selection with a live link to the copied cells; false where a plain paste should run.
        internal bool PasteLink(string text)
        {
            string? reference = SheetEditorControl.CopiedReference(text, session?.path);
            if (reference == null || caretBlock == null || readOnly || plainText
                || caretBlock.stylingType is TextStyleType.Code or TextStyleType.Rule) return false;

            if (HasSelection && !DeleteSelection()) return true;
            if (caretBlock == null) return true;

            StyleSpan span = caretBlock.StyleAt(caretOffset).AsText();
            span.count = 1;
            span.sheetRef = reference;

            BlockSnapshot block = new BlockSnapshot { text = BlockControl.PictureChar };
            block.spans.Add(span);
            DocumentFragment fragment = new DocumentFragment();
            fragment.blocks.Add(block);

            Insert(AddressOf(caretBlock, caretOffset), fragment);
            DisarmStyle();
            return true;
        }

        // Re-measures every block showing a sheet link.
        internal void RefreshSheetLinks()
        {
            foreach (BlockControl block in Blocks())
                foreach (StyleSpan span in block.spans)
                    if (span.IsSheet || span.IsMath && SheetLinks.HasMathLinks(span.mathSource))
                    {
                        block.InvalidateLayout();
                        break;
                    }
        }

        // Rewrites links the rename answers for; true when any changed.
        internal bool RenameSheetLinks(Func<string, string?> rename) => document.RenameSheetLinks(rename);

        internal bool RenameNoteLinks(Func<string, string?> rename) => document.RenameNoteLinks(rename);
        #endregion

        #region ---- styling ----
        // What a toggle reads its current state from, and what the format bar reflects. The
        // selection's start rather than the caret's own end, so a backwards drag reads the same
        // style a forwards one does.
        public CaretStyle? StyleSource
        {
            get
            {
                BlockControl block = caretBlock;
                int offset = caretOffset;

                if (OrderedSelection(out DocumentAddress from, out _)
                    && Resolve(from, out BlockControl start, out int startOffset))
                {
                    block = start;
                    offset = startOffset;
                }

                return block == null ? null : new CaretStyle(block, block.StyleAt(offset), pending);
            }
        }

        // Whether every selected character passes a test; null with nothing selected.
        public bool? SelectionAll(Func<StyleSpan, bool> test)
        {
            if (!OrderedSelection(out DocumentAddress from, out DocumentAddress to)) return null;

            List<BlockControl> blocks = Blocks();
            for (int b = from.block; b <= to.block && b < blocks.Count; b++)
            {
                int start = b == from.block ? Math.Clamp(from.offset, 0, blocks[b].Length) : 0;
                int end = b == to.block ? Math.Clamp(to.offset, 0, blocks[b].Length) : blocks[b].Length;
                if (!blocks[b].AllSpans(start, end, test)) return false;
            }
            return true;
        }

        public TextStyleType CaretBlockStyling =>caretBlock?.stylingType ?? TextStyleType.Text;

        public TextAlignment CaretBlockAlignment => caretBlock?.alignment ?? TextAlignment.Left;

        // The alignment of every block the range touches; with nothing selected, the caret's own.
        public bool SetBlockAlignment(TextAlignment alignment)
        {
            if (caretBlock == null) return false;

            int first, last;
            if (OrderedSelection(out DocumentAddress from, out DocumentAddress to))
            {
                first = from.block;
                last = to.block;
            }
            else first = last = Blocks().IndexOf(caretBlock);

            if (first < 0 || last < 0) return false;

            List<BlockSnapshot> before = SnapshotBlocks(first, last);
            document.ChangeBlocks(first, last, b => b.alignment = alignment);
            undo?.Push(new BlockStateEdit(document, first, before, SnapshotBlocks(first, last)));
            return true;
        }

        // Makes the selected paragraphs a bulleted list, or text again when the caret's paragraph is one.
        public bool ToggleBullets() => ToggleList(ListKind.Bullet, null);

        // Makes the selected paragraphs a list of the kind, numbered when the marker is, or text again when the
        // caret's paragraph already is that list.
        public bool ToggleList(ListKind kind, ListMarker? marker)
        {
            if (caretBlock == null) return false;

            int first, last;
            if (OrderedSelection(out DocumentAddress from, out DocumentAddress to))
            {
                first = from.block;
                last = to.block;
            }
            else first = last = Blocks().IndexOf(caretBlock);

            if (first < 0 || last < 0) return false;

            bool already = caretBlock.listKind == kind && (marker == null
                || ListMarkers.IsNumbered(caretBlock.listMarker ?? ListMarker.Disc) == ListMarkers.IsNumbered(marker.Value));
            List<BlockSnapshot> before = SnapshotBlocks(first, last);
            document.ChangeBlocks(first, last, b =>
            {
                b.listKind = already ? ListKind.None : kind;
                b.listMarker = already ? null : marker;
            });
            undo?.Push(new BlockStateEdit(document, first, before, SnapshotBlocks(first, last)));
            if (!already && kind == ListKind.SortedTask) SortChecklists(first, last);
            return true;
        }

        // Restyles the selected range; with nothing selected the style is armed for the next
        // character instead. False either way when nothing was written.
        public bool ApplyStyle(StyleDelta delta)
        {
            if (OrderedSelection(out DocumentAddress from, out DocumentAddress to))
                return ApplyStyleTo(from, to, delta);

            ArmStyle(delta);
            return false;
        }

        // Holds a style for the next character typed. Merged into what is already armed, so bold
        // then italic types both; dropped once it agrees with the span the caret is in, which is
        // what makes a second toggle disarm rather than pin the span's own style onto it.
        public void ArmStyle(StyleDelta delta)
        {
            if (caretBlock == null) return;

            pending = pending.With(delta);
            if (!pending.Changes(caretBlock.StyleAt(caretOffset))) pending = default;
        }

        // Restyles an addressed range; false when an end names a block the document does not have.
        public bool ApplyStyleTo(DocumentAddress from, DocumentAddress to, StyleDelta delta)
        {
            int count = Blocks().Count;
            if (from.block < 0 || to.block < 0 || from.block >= count || to.block >= count) return false;

            List<BlockSnapshot> before = SnapshotBlocks(from.block, to.block);

            document.ApplyStyleBetween(from, to, delta);
            undo?.Push(new StyleRangeEdit(document, from.block, before, from, to, delta));
            return true;
        }

        // The styling type of every block the range touches; with nothing selected, the caret's own.
        public bool SetBlockStyling(TextStyleType type)
        {
            if (caretBlock == null) return false;

            int first, last;
            if (OrderedSelection(out DocumentAddress from, out DocumentAddress to))
            {
                first = from.block;
                last = to.block;
            }
            else first = last = Blocks().IndexOf(caretBlock);

            if (first < 0 || last < 0) return false;

            List<BlockSnapshot> before = SnapshotBlocks(first, last);
            document.SetBlockStylingBetween(first, last, type);
            undo?.Push(new StyleRangeEdit(document, first, before, type));
            return true;
        }

        // A style change leaves the text alone, so a span of blocks as data is the whole inverse.
        internal List<BlockSnapshot> SnapshotBlocks(int first, int last)
        {
            List<BlockControl> blocks = Blocks();
            List<BlockSnapshot> snapshots = new List<BlockSnapshot>();

            for (int b = first; b <= last && b < blocks.Count; b++)
                snapshots.Add(blocks[b].Snapshot());

            return snapshots;
        }
        #endregion
    }
}
