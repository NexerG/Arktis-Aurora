using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Text;
using System.Numerics;
using System.Text;

namespace ArctisAurora.Core.UI.Document
{
    public partial class DocumentControl
    {
        #region ---- editing ----
        // Removes the selected range; false when nothing was selected.
        public bool DeleteSelection(bool restoreSelection = true)
        {
            if (!OrderedSelection(out DocumentAddress from, out DocumentAddress to)) return false;
            if (!OneContainer(from.block, to.block)) return false;

            DocumentAddress anchorAt = AddressOf(anchor.block, anchor.offset);
            DocumentAddress caretAt = restoreSelection ? AddressOf(caretBlock, caretOffset) : anchorAt;

            DocumentFragment fragment = document.CaptureFragment(from, to);
            undo?.Push(new DeleteRangeEdit(document, from, to, fragment, anchorAt, caretAt));
            document.DeleteBetween(from, to);
            KeepDeletedStyle(fragment);
            return true;
        }

        // What is typed next looks like the first character deleted; a style chosen outright still wins.
        private void KeepDeletedStyle(DocumentFragment fragment)
        {
            foreach (BlockSnapshot block in fragment.blocks)
                foreach (StyleSpan span in block.spans)
                {
                    if (span.count == 0) continue;

                    StyleDelta kept = StyleDelta.Of(span);
                    if (kept.Changes(caretBlock.StyleAt(caretOffset))) pending = kept.With(pending);
                    return;
                }
        }

        internal void DisarmStyle() => pending = default;

        // Replaces the selection with text; the caret ends after it.
        internal bool PasteText(string text)
        {
            if (HasSelection && !DeleteSelection()) return false;
            if (caretBlock == null) return false;
            LeaveRule();

            DocumentFragment fragment = text == copiedText && copiedFragment != null
                ? copiedFragment
                : FragmentFromText(text, caretBlock, caretOffset);

            Insert(AddressOf(caretBlock, caretOffset), ForDestination(fragment, caretBlock));
            DisarmStyle();
            return true;
        }

        // Replaces the selection with a picture in the caret's style; the caret ends after it.
        internal bool PasteImage(string path)
        {
            if (HasSelection && !DeleteSelection()) return false;
            if (caretBlock == null) return false;
            LeaveRule();

            StyleSpan span = caretBlock.StyleAt(caretOffset);
            span.count = 1;
            span.imageSource = path;

            BlockSnapshot block = new BlockSnapshot { text = BlockControl.PictureChar };
            block.spans.Add(span);
            DocumentFragment fragment = new DocumentFragment();
            fragment.blocks.Add(block);

            Insert(AddressOf(caretBlock, caretOffset), fragment);
            DisarmStyle();
            return true;
        }

        // Puts a fragment in at a slot and leaves it selected — a drop from another note.
        internal void InsertAt(CaretSlot slot, DocumentFragment fragment)
        {
            DocumentAddress from = AddressOf(slot.block, slot.offset);
            if (slot.block.stylingType == TextStyleType.Rule) from = OffRule(from);
            if (!Resolve(from, out BlockControl block, out _)) return;

            DocumentAddress to = Insert(from, ForDestination(fragment, block));
            Select(from, to);
            DisarmStyle();
        }

        // Moves or copies the selection to a slot, selected there; false for a slot inside it.
        internal bool DropSelection(CaretSlot slot, bool copy)
        {
            if (!OrderedSelection(out DocumentAddress from, out DocumentAddress to)) return false;
            if (InSelection(slot.block, slot.offset)) return false;

            DocumentFragment fragment = SelectedFragment();
            if (fragment == null) return false;

            DocumentAddress at = AddressOf(slot.block, slot.offset);
            if (!copy)
            {
                DeleteSelection();
                if (Before(to, at))
                    at = at.block == to.block
                        ? new DocumentAddress(from.block, from.offset + at.offset - to.offset)
                        : new DocumentAddress(at.block - (to.block - from.block), at.offset);
            }

            if (!Resolve(at, out BlockControl block, out _)) return false;
            if (block.stylingType == TextStyleType.Rule)
            {
                at = OffRule(at);
                if (!Resolve(at, out block, out _)) return false;
            }
            DocumentAddress end = Insert(at, ForDestination(fragment, block));
            Select(at, end);
            DisarmStyle();
            return true;
        }

        // Records and performs an insert; returns where it ends.
        private DocumentAddress Insert(DocumentAddress from, DocumentFragment fragment)
        {
            DocumentAddress to = fragment.blocks.Count == 1
                ? new DocumentAddress(from.block, from.offset + fragment.blocks[0].text.Length)
                : new DocumentAddress(from.block + fragment.blocks.Count - 1, fragment.blocks[^1].text.Length);

            undo?.Push(new InsertRangeEdit(document, from, to, fragment));
            document.InsertBetween(from, to, fragment);
            return to;
        }

        // Plain text as paragraphs, each in the style and block kind at the caret.
        private static DocumentFragment FragmentFromText(string text, BlockControl at, int offset)
        {
            StyleSpan style = at.StyleAt(offset);
            DocumentFragment fragment = new DocumentFragment();

            foreach (string line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                StringBuilder clean = new StringBuilder(line.Length);
                foreach (char c in line)
                    if (c == '\t') clean.Append(' ');
                    else if (!char.IsControl(c) && c != BlockControl.PictureChar[0]) clean.Append(c);

                BlockSnapshot block = new BlockSnapshot
                {
                    stylingType = at.stylingType,
                    alignment = at.alignment,
                    firstIndent = at.firstIndent,
                    leftIndent = at.note.leftIndent,
                    rightIndent = at.note.rightIndent,
                    rightIndentLast = at.note.rightIndentLast,
                    spaceBefore = at.spaceBefore,
                    shrinkBefore = at.shrinkBefore,
                    insert = at.insert,
                    language = at.language,
                    codeWrap = at.codeWrap,
                    listKind = at.listKind,
                    listLevel = at.listLevel,
                    listMarker = at.listMarker,
                    listIndent = at.note.listIndent,
                    lineHeight = at.note.lineHeight,
                    keepWithNext = at.note.keepWithNext,
                    keepTogether = at.note.keepTogether,
                    strut = at.note.strut,
                    text = clean.ToString()
                };

                StyleSpan span = style;
                span.count = block.text.Length;
                block.spans.Add(span);
                fragment.blocks.Add(block);
            }

            return fragment;
        }

        // The last block of a multi-block fragment takes the destination block's kind.
        private static DocumentFragment ForDestination(DocumentFragment fragment, BlockControl destination)
        {
            if (fragment.blocks.Count < 2) return fragment;

            BlockSnapshot last = fragment.blocks[^1];
            BlockSnapshot landed = new BlockSnapshot
            {
                stylingType = destination.stylingType,
                alignment = destination.alignment,
                firstIndent = destination.firstIndent,
                leftIndent = destination.note.leftIndent,
                rightIndent = destination.note.rightIndent,
                rightIndentLast = destination.note.rightIndentLast,
                spaceBefore = destination.spaceBefore,
                shrinkBefore = destination.shrinkBefore,
                insert = destination.insert,
                language = destination.language,
                codeWrap = destination.codeWrap,
                listKind = destination.listKind,
                listLevel = destination.listLevel,
                listMarker = destination.listMarker,
                listIndent = destination.note.listIndent,
                lineHeight = destination.note.lineHeight,
                keepWithNext = destination.note.keepWithNext,
                keepTogether = destination.note.keepTogether,
                strut = destination.note.strut,
                isChecked = destination.isChecked,
                text = last.text
            };
            landed.spans.AddRange(last.spans);

            DocumentFragment reshaped = new DocumentFragment();
            reshaped.blocks.AddRange(fragment.blocks.Take(fragment.blocks.Count - 1));
            reshaped.blocks.Add(landed);
            return reshaped;
        }

        // Splits the caret's block in two, the second carrying everything from the caret on.
        public void SplitBlock()
        {
            DeleteSelection();

            if (caretBlock == null) return;
            if (caretBlock.stylingType == TextStyleType.Rule)
            {
                LeaveRule();
                return;
            }
            if (caretBlock.Length == 0 && caretBlock.listKind != ListKind.None)
            {
                if (caretBlock.listLevel > 0) ShiftListLevel(-1);
                else ClearListAtCaret();
                return;
            }
            if (TypeMarkdownLine() || EndCodeBlock()) return;
            SplitBlockAt(AddressOf(caretBlock, caretOffset));
        }

        // The split itself, addressed rather than read off the caret, so redo can replay it.
        internal void SplitBlockAt(DocumentAddress at)
        {
            if (!Resolve(at, out _, out _)) return;

            document.SplitBlockAt(at);
            undo?.Push(new SplitEdit(document, at));
        }

        // Records the insert before the write, because the address is read off the caret the write is
        // about to advance. An armed style is spent here: the character is written with the style
        // beside it and then restyled, so the partition and both records are a selection restyle's.
        internal void TypeChar(char c)
        {
            if (caretBlock == null) return;
            LeaveRule();

            BlockControl block = caretBlock;
            DocumentAddress at = AddressOf(block, caretOffset);
            StyleDelta armed = pending;
            pending = default;

            undo?.Push(new TextEdit(document, at, c.ToString(), true));
            document.InsertText(block.note, at, c.ToString());

            if (c == ' ' && TypeMarkdownPrefix(block, at.block, at.offset + 1))
            {
                pending = armed;
                return;
            }

            if (TypeInlineMarkdown(block, at.block, at.offset + 1, c, out StyleDelta closed))
            {
                pending = armed.With(closed);
                return;
            }

            if (!armed.Changes(block.StyleAt(at.offset + 1))) return;

            ApplyStyleTo(at, new DocumentAddress(at.block, at.offset + 1), armed);
            CollapseSelection();
        }

        // Blocks sit between the highlight boxes at the head of the child list and the caret at its
        // end, so a new one goes in beside its neighbour rather than at either end.
        private void InsertBlockAfter(BlockControl after, BlockControl block)
        {
            if (after.parent is StackPanelControl cell)
            {
                cell.children.Insert(cell.children.IndexOf(after) + 1, block);
                block.parent = cell;
                MarkTreeOrderDirty();
                cell.InvalidateLayout();
                ListsChanged();
                return;
            }

            children.Insert(children.IndexOf(after) + 1, block);
            block.parent = this;
            MarkTreeOrderDirty();
            InvalidateLayout();
            ListsChanged();
        }

        private void RemoveBlock(BlockControl block)
        {
            block.Destroy();
            ListsChanged();
        }

        // Every block in reading order, a table's cells included.
        internal List<BlockControl> Blocks()
        {
            List<BlockControl> blocks = new List<BlockControl>();
            foreach (Entity child in children)
            {
                if (child is BlockControl block) blocks.Add(block);
                else if (TableIn(child) is TableControl table) table.AppendBlocks(blocks);
            }

            return blocks;
        }

        // The table a child of the document holds, when it is a table's viewport.
        private static TableControl? TableIn(Entity child)
        {
            if (child is not ScrollableControl viewport) return null;

            foreach (Entity entry in viewport.children)
                if (entry is TableControl table) return table;

            return null;
        }

        // The viewport of the table a block sits in; null for a block of the note itself.
        internal static ScrollableControl? TableViewport(BlockControl? block) =>
            block?.parent?.parent is TableControl table ? table.parent as ScrollableControl : null;

        // Whether every block from first to last shares one parent — the note, or one cell.
        private bool OneContainer(int first, int last)
        {
            List<BlockControl> blocks = Blocks();
            if (first < 0 || last >= blocks.Count) return false;

            for (int b = first + 1; b <= last; b++)
                if (blocks[b].parent != blocks[first].parent) return false;

            return true;
        }
        #endregion

        #region ---- text drag ----
        // Picks the selection up; the drop marker is what the drag carries.
        private bool BeginTextDrag(CaretSlot press)
        {
            if (SelectedFragment() == null) return false;

            EnsureDropCaret();
            textDragging = true;
            textDragHovered = false;
            textDragPress = press;
            dropCaret.RegisterOnDragStop(EndTextDrag);
            dropCaret.StartDrag();
            return true;
        }

        // A release nothing took, never over an editor, is a click at the press.
        private void EndTextDrag(bool accepted)
        {
            textDragging = false;
            HideDrop();

            if (!accepted && !textDragHovered) SetCaret(textDragPress.block, textDragPress.offset);
            if (!accepted && !textDragHovered && textDragPicture) SetCaret(textDragPress.block, textDragPress.offset + 1, true);
            textDragPicture = false;
        }

        // The document whose selection a drag carries, or null for any other drag.
        internal static DocumentControl? TextDragSource(Control dragged) =>
            dragged is CaretControl { parent: DocumentControl { textDragging: true } source } ? source : null;

        internal void ShowDropAt(Vector2 point)
        {
            if (!CaretOffText(point, out BlockControl block, out int offset)) return;

            EnsureDropCaret();
            dropSlot = new CaretSlot(block, offset);
            dropCaret.Focus();
            InvalidateArrange();
        }

        internal void HideDrop()
        {
            if (dropSlot == null) return;

            dropSlot = null;
            dropCaret?.Blur();
            InvalidateArrange();
        }

        private void EnsureDropCaret()
        {
            if (dropCaret != null) return;

            dropCaret = new CaretControl { hitTestable = false };
            dropCaret.PaintOr(caretColorHex, PaletteRole.Ink);
            dropCaret.Blur();
            AddChild(dropCaret);
        }
        #endregion

        #region ---- addressing ----
        internal DocumentAddress AddressOf(BlockControl block, int offset) =>
            new DocumentAddress(block != null && block == caretBlock ? CaretIndex : Blocks().IndexOf(block), offset);

        private int CaretIndex
        {
            get
            {
                if (caretIndex < 0) caretIndex = Blocks().IndexOf(caretBlock);
                else CheckCaretIndex();
                return caretIndex;
            }
        }

        // Throws when the cached caret index no longer names the caret's block.
        [System.Diagnostics.Conditional("DEBUG")]
        private void CheckCaretIndex()
        {
            int walked = Blocks().IndexOf(caretBlock);
            if (walked != caretIndex)
                throw new Exception($"[DocumentControl] cached caret index {caretIndex} is stale; the caret's block is at {walked}.");
        }

        internal bool Resolve(DocumentAddress at, out BlockControl block, out int offset)
        {
            block = null;
            offset = 0;

            if (BlockAt(at.block) is not BlockControl found) return false;

            block = found;
            offset = Math.Clamp(at.offset, 0, block.Length);
            return true;
        }

        // The block at a flat index without building the list; null past the end.
        private BlockControl? BlockAt(int index)
        {
            if (index < 0) return null;
            if (index == caretIndex && caretBlock != null)
            {
                CheckCaretIndex();
                return caretBlock;
            }

            foreach (Entity child in children)
            {
                if (child is BlockControl block)
                {
                    if (index-- == 0) return block;
                }
                else if (TableIn(child) is TableControl table)
                    foreach (Entity entry in table.children)
                    {
                        if (entry is not StackPanelControl cell) continue;

                        foreach (Entity line in cell.children)
                            if (line is BlockControl cellBlock && index-- == 0) return cellBlock;
                    }
            }

            return null;
        }

        private void CaretTo(DocumentAddress at)
        {
            if (!Resolve(at, out BlockControl block, out int offset)) return;

            SetCaret(block, offset);
            caretIndex = at.block;
        }
        #endregion

        #region ---- model changes ----
        public override void OnDestroy()
        {
            if (document != null) document.changed -= OnNoteChanged;
            base.OnDestroy();
        }

        // Where another view's change moves the caret, the anchor and a text drag's press. A selected
        // picture or formula the change touches collapses onto the caret.
        private bool MapAddress(NoteChange change, out DocumentAddress caretTo, out DocumentAddress anchorTo, out DocumentAddress pressTo)
        {
            DocumentAddress caretAt = AddressOf(caretBlock, caretOffset);
            caretTo = anchorTo = pressTo = default;
            if (caretAt.block < 0) return false;

            DocumentAddress anchorAt = anchor.Equals(Focus) ? caretAt : AddressOf(anchor.block, anchor.offset);
            caretTo = change.Map(caretAt);
            anchorTo = change.Map(anchorAt);
            if (textDragging) pressTo = change.Map(AddressOf(textDragPress.block, textDragPress.offset));

            bool moves = change.kind != NoteChangeKind.Spans;
            if (!HasSelection || !(SelectedPicture(out _, out _) || SelectedMath(out _, out _))) return moves;

            int selected = Math.Min(caretAt.block, anchorAt.block);
            bool touched = change.kind switch
            {
                NoteChangeKind.Table => true,
                NoteChangeKind.Nodes => selected >= change.first && selected < change.first + change.count,
                NoteChangeKind.Text or NoteChangeKind.Spans or NoteChangeKind.Kind or NoteChangeKind.Inserted or NoteChangeKind.Removed =>
                    (selected >= change.first && selected < change.first + change.count)
                    || (change.kind is NoteChangeKind.Inserted or NoteChangeKind.Removed && selected == change.at.block),
                _ => false
            };
            if (touched) anchorTo = caretTo;
            return moves || touched;
        }

        // Brings the controls in line with a change the model made.
        private void OnNoteChanged(NoteChange change)
        {
            if (change.kind == NoteChangeKind.Replaced) return;

            bool follows = session == null || session.lead == null || session.lead == this;
            DocumentAddress caretTo = default, anchorTo = default, pressTo = default;
            bool remap = (!follows || change.kind == NoteChangeKind.Nodes) && caretBlock != null && MapAddress(change, out caretTo, out anchorTo, out pressTo);

            if (change.kind is NoteChangeKind.Inserted or NoteChangeKind.Removed or NoteChangeKind.Table or NoteChangeKind.Nodes) caretIndex = -1;

            switch (change.kind)
            {
                case NoteChangeKind.Text:
                case NoteChangeKind.Spans:
                {
                    if (change.count == 1)
                    {
                        BlockAt(change.first)?.InvalidateLayout();
                        break;
                    }

                    List<BlockControl> blocks = Blocks();
                    for (int b = change.first; b < change.first + change.count && b < blocks.Count; b++)
                        blocks[b].InvalidateLayout();
                    break;
                }
                case NoteChangeKind.Kind:
                {
                    List<BlockControl> blocks = Blocks();
                    for (int b = change.first; b < change.first + change.count && b < blocks.Count; b++)
                        blocks[b].ApplyLayout(document.layout);
                    ListsChanged();
                    break;
                }
                case NoteChangeKind.Inserted:
                {
                    List<NoteBlock> notes = document.Blocks();
                    BlockControl after = Blocks()[change.first - 1];
                    for (int i = 0; i < change.count; i++)
                    {
                        BlockControl block = new BlockControl(notes[change.first + i]);
                        block.CopyPaint(after);
                        InsertBlockAfter(after, block);
                        block.ApplyLayout(document.layout);
                        after = block;
                    }
                    break;
                }
                case NoteChangeKind.Removed:
                {
                    List<BlockControl> blocks = Blocks();
                    for (int i = change.first + change.count - 1; i >= change.first; i--)
                        RemoveBlock(blocks[i]);
                    break;
                }
                case NoteChangeKind.Table:
                    ShowTable(change.first);
                    break;
                case NoteChangeKind.Nodes:
                    ShowNodes();
                    break;
                case NoteChangeKind.Page:
                    if (!fitWidth) page = document.layout.Page;
                    InvalidateLayout();
                    break;
                case NoteChangeKind.Layout:
                    blockSpacing = document.layout.blockSpacing;
                    foreach (Entity child in children)
                    {
                        if (child is BlockControl block) block.ApplyLayout(document.layout);
                        else if (TableIn(child) is TableControl table) table.ApplyLayout(document.layout);
                    }
                    ListsChanged();
                    break;
                case NoteChangeKind.ReadOnly:
                    readOnly = document.readOnly;
                    break;
            }

            if (remap)
            {
                if (!Resolve(caretTo, out BlockControl block, out int offset))
                {
                    caretTo = new DocumentAddress(Blocks().Count - 1, int.MaxValue);
                    Resolve(caretTo, out block, out offset);
                }
                caretBlock = block;
                caretOffset = offset;
                caretIndex = block != null ? caretTo.block : -1;
                anchor = Resolve(anchorTo, out BlockControl anchorBlock, out int anchorOffset) ? new CaretSlot(anchorBlock, anchorOffset) : Focus;
                if (textDragging && Resolve(pressTo, out BlockControl pressBlock, out int pressOffset)) textDragPress = new CaretSlot(pressBlock, pressOffset);
                InvalidateArrange();
            }

            if (!follows || change.caret is not DocumentAddress caretAt) return;
            if (change.anchor is DocumentAddress anchorAt) Select(anchorAt, caretAt);
            else CaretTo(caretAt);
        }
        #endregion
    }
}
