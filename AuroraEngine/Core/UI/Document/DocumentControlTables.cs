using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Notes;
using System.Xml.Linq;

namespace ArctisAurora.Core.UI.Document
{
    public partial class DocumentControl
    {
        #region ---- tables ----
        private TableControl? CaretTable() => caretBlock?.parent?.parent as TableControl;

        // The caret at the very start of a list item with nothing selected, where Tab in a cell nests.
        internal bool AtListItemStart => caretBlock is { listKind: not ListKind.None } && caretOffset == 0 && !HasSelection;

        // Empty cells after the caret's block, with a paragraph kept after the table; false in a cell.
        internal bool InsertTable(int rows, int columns, float width)
        {
            if (caretBlock == null || caretBlock.parent != this) return false;

            BlockControl block = caretBlock;
            DocumentAddress caretBefore = AddressOf(block, caretOffset);
            int index = Array.IndexOf(document.blocks, block.note) + 1;
            if (index == document.blocks.Length) SplitBlockAt(AddressOf(block, block.Length));

            NoteTable model = document.PutTable(index, false, DocumentXml.NewTable(rows, columns, width))!;
            BlockControl first = ((TableControl)ViewOf(model)).CellBlocks(0, 0)[0];
            SetCaret(first, 0);
            undo?.Push(new TableEdit(document, index, null, DocumentXml.WriteTable(model), caretBefore, AddressOf(first, 0)));
            return true;
        }

        // toNew puts the caret in the new row's first cell, as Tab past the last cell does.
        internal bool InsertTableRow(bool below, bool toNew = false) => ChangeTable((xml, row, column) =>
        {
            List<XElement> rows = Named(xml, "Row");
            XElement added = new XElement(rows[row].Name,
                Enumerable.Range(0, Named(xml, "Column").Count).Select(_ => new XElement(xml.Name.Namespace + "Cell")));
            if (below) rows[row].AddAfterSelf(added);
            else rows[row].AddBeforeSelf(added);

            if (toNew) return (below ? row + 1 : row, 0, false);
            return (below ? row : row + 1, column, true);
        });

        // The new column takes the caret column's width; a merged cell it falls inside widens over it.
        internal bool InsertTableColumn(bool right) => ChangeTable((xml, row, column) =>
        {
            XNamespace ns = xml.Name.Namespace;
            List<XElement> columns = Named(xml, "Column");
            int at = right ? column + Span(Covering(Named(xml, "Row")[row], column).cell) : column;
            XElement added = new XElement(ns + "Column", new XAttribute("Width", (string)columns[column].Attribute("Width")!));
            if (at < columns.Count) columns[at].AddBeforeSelf(added);
            else columns[^1].AddAfterSelf(added);

            foreach (XElement r in Named(xml, "Row"))
            {
                if (at >= columns.Count)
                {
                    r.Add(new XElement(ns + "Cell"));
                    continue;
                }
                (XElement cell, int start) = Covering(r, at);
                if (start < at) SetSpan(cell, Span(cell) + 1);
                else cell.AddBeforeSelf(new XElement(ns + "Cell"));
            }
            return (row, right ? column : column + 1, true);
        });

        // The last row deletes the table.
        internal bool DeleteTableRow()
        {
            if (CaretTable() is { RowCount: 1 }) return DeleteTable();

            return ChangeTable((xml, row, column) =>
            {
                List<XElement> rows = Named(xml, "Row");
                rows[row].Remove();
                return (Math.Min(row, rows.Count - 2), column, false);
            });
        }

        // The last column deletes the table.
        internal bool DeleteTableColumn()
        {
            if (CaretTable() is { widths.Length: 1 }) return DeleteTable();

            return ChangeTable((xml, row, column) =>
            {
                List<XElement> columns = Named(xml, "Column");
                columns[column].Remove();
                foreach (XElement r in Named(xml, "Row"))
                {
                    XElement cell = Covering(r, column).cell;
                    if (Span(cell) > 1) SetSpan(cell, Span(cell) - 1);
                    else cell.Remove();
                }
                return (row, Math.Min(column, columns.Count - 2), false);
            });
        }

        // The next cell's blocks join the caret's, an empty side dropped; refused on a row's last cell.
        internal bool MergeTableCellRight()
        {
            if (caretBlock?.parent is not StackPanelControl caretCell || caretCell.parent is not TableControl table
                || caretCell.gridColumn + table.ColumnSpan(caretCell) >= table.widths.Length) return false;

            return ChangeTable((xml, row, column) =>
            {
                XElement cell = Covering(Named(xml, "Row")[row], column).cell;
                XElement next = cell.ElementsAfterSelf().First();
                if (EmptyCell(cell)) cell.RemoveNodes();
                if (!EmptyCell(next) || !cell.HasElements) cell.Add(next.Elements());
                SetSpan(cell, Span(cell) + Span(next));
                next.Remove();
                return (row, column, true);
            });
        }

        // A merged cell back to single cells, its blocks kept in the first; refused on a single cell.
        internal bool SplitTableCell()
        {
            if (caretBlock?.parent is not StackPanelControl caretCell || caretCell.parent is not TableControl table
                || table.ColumnSpan(caretCell) == 1) return false;

            return ChangeTable((xml, row, column) =>
            {
                XElement cell = Covering(Named(xml, "Row")[row], column).cell;
                for (int i = 1; i < Span(cell); i++)
                    cell.AddAfterSelf(new XElement(cell.Name, cell.Attributes().Where(a => a.Name.LocalName.StartsWith("Rule")).Select(a => new XAttribute(a))));
                XElement last = cell.ElementsAfterSelf().ElementAt(Span(cell) - 2);
                foreach (string name in new[] { "TrimAbove", "TrimBelow" })
                    if (Enum.TryParse((string?)cell.Attribute(name), out RuleTrim trim))
                    {
                        if ((trim & RuleTrim.Right) != 0) last.SetAttributeValue(name, "Right");
                        cell.SetAttributeValue(name, (trim & RuleTrim.Left) != 0 ? "Left" : null);
                    }
                SetSpan(cell, 1);
                return (row, column, true);
            });
        }

        // Refused when the note would be left with no paragraph to hold the caret.
        internal bool DeleteTable()
        {
            if (CaretTable() is not TableControl table || !document.blocks.Any(b => b is NoteBlock)) return false;

            int index = Array.IndexOf(document.blocks, table.model);
            int flat = Blocks().IndexOf(table.CellBlocks(0, 0)[0]);
            DocumentAddress caretBefore = AddressOf(caretBlock, caretOffset);
            XElement before = DocumentXml.WriteTable(table.model);
            document.PutTable(index, true, null);

            List<BlockControl> blocks = Blocks();
            BlockControl landing = blocks[Math.Min(flat, blocks.Count - 1)];
            SetCaret(landing, 0);
            undo?.Push(new TableEdit(document, index, before, null, caretBefore, AddressOf(landing, 0)));
            return true;
        }

        // A column drag finished: one step from the table as it was at the grab to as it is.
        internal void RecordTableResize(TableControl table, XElement before)
        {
            if (Editor is not DocumentEditorControl editor) return;

            DocumentAddress caret = caretBlock != null ? AddressOf(caretBlock, caretOffset) : default;
            using (editor.BeginStep("Resize column"))
                undo?.Push(new TableEdit(document, Array.IndexOf(document.blocks, table.model), before, DocumentXml.WriteTable(table.model), caret, caret));
            editor.MarkDirty();
        }

        // Rebuilds the caret's table from an edited copy of its XML, as one record. change gets the
        // caret's row and column and returns the cell the caret lands in; keep holds its line and offset.
        private bool ChangeTable(Func<XElement, int, int, (int row, int column, bool keep)> change)
        {
            if (caretBlock?.parent is not StackPanelControl cell || cell.parent is not TableControl table) return false;

            int index = Array.IndexOf(document.blocks, table.model);
            int line = cell.children.OfType<BlockControl>().ToList().IndexOf(caretBlock);
            int offset = caretOffset;
            DocumentAddress caretBefore = AddressOf(caretBlock, caretOffset);
            XElement before = DocumentXml.WriteTable(table.model);
            XElement edited = new XElement(before);

            (int row, int column, bool keep) = change(edited, cell.gridRow, cell.gridColumn);
            NoteTable model = document.PutTable(index, true, edited)!;

            List<BlockControl> lines = ((TableControl)ViewOf(model)).CellBlocks(row, column);
            BlockControl landing = lines[keep ? Math.Min(line, lines.Count - 1) : 0];
            SetCaret(landing, keep ? offset : 0);
            undo?.Push(new TableEdit(document, index, before, DocumentXml.WriteTable(model), caretBefore, AddressOf(landing, caretOffset)));
            return true;
        }

        // Drops the view of a table the model no longer holds, and builds one for the table at a note-level index.
        private void ShowTable(int index)
        {
            for (int i = children.Count - 1; i >= 0; i--)
                if (TableIn(children[i]) is TableControl stale && Array.IndexOf(document.blocks, stale.model) < 0)
                    children[i].Destroy();

            if (index < document.blocks.Length && document.blocks[index] is NoteTable model && ViewOf(model) == null)
            {
                TableControl table = new TableControl(model);
                table.ApplyLayout(document.layout);
                ScrollableControl viewport = table.Hosted();
                int at = index + 1 < document.blocks.Length
                    ? children.IndexOf(Hosting(ViewOf(document.blocks[index + 1])))
                    : children.IndexOf(Hosting(ViewOf(document.blocks[index - 1]))) + 1;
                children.Insert(at, viewport);
                viewport.parent = this;
            }

            MarkTreeOrderDirty();
            InvalidateLayout();
            ListsChanged();
        }

        // Drops the views of entries the note no longer holds and builds the missing ones in note order.
        private void ShowNodes()
        {
            HashSet<NoteNode> live = new HashSet<NoteNode>(document.blocks);
            for (int i = children.Count - 1; i >= 0; i--)
                if (NodeOf(children[i]) is NoteNode gone && !live.Contains(gone))
                    children[i].Destroy();

            Dictionary<NoteNode, Entity> shown = new Dictionary<NoteNode, Entity>();
            int firstView = -1;
            for (int i = 0; i < children.Count; i++)
            {
                if (NodeOf(children[i]) is not NoteNode node) continue;
                shown[node] = children[i];
                if (firstView < 0) firstView = i;
            }

            Entity? previous = null;
            foreach (NoteNode node in document.blocks)
            {
                if (shown.TryGetValue(node, out Entity? view))
                {
                    previous = view;
                    continue;
                }

                Entity added;
                if (node is NoteTable model)
                {
                    TableControl table = new TableControl(model);
                    table.ApplyLayout(document.layout);
                    added = table.Hosted();
                }
                else if (node is NoteSealed locked) added = new SealedControl(locked);
                else if (node is NoteToc toc)
                {
                    TocControl contents = new TocControl(toc);
                    contents.Rebuild(document.Blocks());
                    added = contents;
                }
                else
                {
                    BlockControl block = new BlockControl((NoteBlock)node);
                    block.StartEffects();
                    block.ApplyLayout(document.layout);
                    added = block;
                }

                int at = previous != null ? children.IndexOf(previous) + 1 : firstView >= 0 ? firstView : children.Count;
                children.Insert(at, added);
                added.parent = this;
                if (previous == null && firstView >= 0) firstView++;
                previous = added;
            }

            MarkTreeOrderDirty();
            InvalidateLayout();
            ListsChanged();
        }

        private static NoteNode? NodeOf(Entity child) => child switch
        {
            BlockControl block => block.note,
            SealedControl locked => locked.note,
            TocControl contents => contents.note,
            _ => TableIn(child)?.model
        };

        // A note-level entry as it sits among the children: a block itself, a table its viewport.
        private static Entity Hosting(Control entry) => entry is TableControl table ? table.parent : entry;

        // The control showing a note-level entry.
        internal Control ViewOf(NoteNode node)
        {
            foreach (Entity child in children)
            {
                if (child is BlockControl block && block.note == node) return block;
                if (child is SealedControl locked && locked.note == node) return locked;
                if (TableIn(child) is TableControl table && table.model == node) return table;
            }

            return null!;
        }

        private static List<XElement> Named(XElement parent, string name) =>
            parent.Elements().Where(e => e.Name.LocalName == name).ToList();

        // The <Cell> of a <Row> covering a column, and the column it starts at.
        private static (XElement cell, int start) Covering(XElement row, int column)
        {
            List<XElement> cells = Named(row, "Cell");
            int start = 0;
            foreach (XElement cell in cells)
            {
                if (column < start + Span(cell)) return (cell, start);
                start += Span(cell);
            }
            return (cells[^1], start - Span(cells[^1]));
        }

        private static int Span(XElement cell) => Math.Max(1, (int?)cell.Attribute("ColumnSpan") ?? 1);

        // A cell holding at most one block whose runs carry nothing.
        private static bool EmptyCell(XElement cell) => Named(cell, "Block").Count <= 1
            && !cell.Descendants().Any(e => e.Name.LocalName == "Run" && e.Attributes().Any(a => a.Value.Length > 0));

        private static void SetSpan(XElement cell, int span) => cell.SetAttributeValue("ColumnSpan", span > 1 ? span : (object?)null);
        #endregion
    }
}
