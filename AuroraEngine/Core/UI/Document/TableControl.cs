using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Notes;
using Silk.NET.GLFW;
using System.Numerics;
using System.Xml.Linq;

namespace ArctisAurora.Core.UI.Document
{
    public enum TableRule { None, Plain, Double, Heavy, Light, Cmid }

    [Flags]
    public enum RuleTrim { None = 0, Left = 1, Right = 2, Both = 3 }

    // a cell's rules along its top and bottom edges; widths px, 0 = the kind's own
    public struct CellRules
    {
        public TableRule above;
        public TableRule below;
        public float aboveWidth;
        public float belowWidth;
        public RuleTrim aboveTrim;
        public RuleTrim belowTrim;
    }

    // A table in a note: fixed-width columns of cells, each cell a stack of blocks.
    public class TableControl : GridListControl
    {
        // the table this control shows, and the model cell behind each view cell
        public readonly NoteTable model;
        private readonly Dictionary<StackPanelControl, NoteCell> cellOf = new Dictionary<StackPanelControl, NoteCell>();

        // column widths in design pixels, before zoom
        public float[] widths => model.widths;

        // cell borders, laid over the cells
        public bool showBorders { get => model.showBorders; set => model.showBorders = value; }
        private readonly List<PanelControl> borders = new List<PanelControl>();

        // the gap above the table, px; null takes the layout's block spacing
        public float? spaceBefore { get => model.spaceBefore; set => model.spaceBefore = value; }
        public float shrinkBefore { get => model.shrinkBefore; set => model.shrinkBefore = value; }

        // where a table narrower than the note sits; Justify is Left
        public TextAlignment alignment { get => model.alignment; set => model.alignment = value; }

        // LaTeX rules: along cell tops and bottoms, down column edges; drawn whether or not the borders are
        public TableRule[] leftRules => model.leftRules;
        public TableRule[] rightRules => model.rightRules;
        private readonly List<PanelControl> ruleLines = new List<PanelControl>();

        // the cell inset across and down, px; null = cellInset all round
        public Vector2? cellPadding { get => model.cellPadding; set => model.cellPadding = value; }

        // a break before the table, and the float it is part of; null in the flow
        public PageBreak pageBreak { get => model.pageBreak; set => model.pageBreak = value; }
        public PageInsert? insert { get => model.insert; set => model.insert = value; }

        // column edges, one per column, over the borders
        private readonly List<ColumnGrip> grips = new List<ColumnGrip>();

        // column resize in progress
        private int resizeColumn = -1;
        private float resizeGrab;
        private float resizeWidth;
        private XElement? resizeBefore;

        private float zoom = 1f;

        private const float cellInset = 6f;
        private const float borderWidth = 1f;
        private const float gripWidth = 6f;
        private const float minColumnWidth = 24f;

        // \arrayrulewidth and \doublerulesep in pt; booktabs rule widths and \cmidrulekern in em, rule seps in ex
        private const float pxPerPt = 96f / 72.27f;
        private const float arrayRule = 0.4f;
        private const float doubleRuleSep = 2f;
        private const float heavyRule = 0.08f;
        private const float lightRule = 0.05f;
        private const float cmidRule = 0.03f;
        private const float cmidRuleKern = 0.5f;
        private const float aboveRuleSep = 0.4f;
        private const float belowRuleSep = 0.65f;
        private const float exPerEm = 0.430555f;

        public TableControl(NoteTable model)
        {
            this.model = model;
            foreach (float width in widths)
                columnDefinitions.Add(new ColumnDefinition { sizeMode = GridSizeMode.Fixed, value = width });
            foreach (NoteCell[] row in model.rows)
                AddRow(row);
            ApplyInsets();
        }

        // Appends a view row over a model row, one cell per model cell.
        private void AddRow(NoteCell[] cells)
        {
            int row = rowDefinitions.Count;
            rowDefinitions.Add(new RowDefinition { sizeMode = GridSizeMode.Auto });

            int c = 0;
            foreach (NoteCell source in cells)
            {
                StackPanelControl cell = new StackPanelControl
                {
                    gridRow = (short)row,
                    gridColumn = (short)c,
                    margin = new Thickness(cellInset * zoom)
                };
                c += source.span;

                foreach (NoteBlock note in source.blocks)
                {
                    BlockControl block = new BlockControl(note);
                    block.StartEffects();
                    cell.AddChild(block);
                }
                AddChild(cell);
                cellOf[cell] = source;
                if (source.span > 1) SetColumnSpan(cell, source.span);
            }
        }

        // The table inside the sideways scroller a note holds it in.
        public ScrollableControl Hosted()
        {
            ScrollableControl viewport = new ScrollableControl { scrollDirection = ScrollDirection.Horizontal };
            viewport.AddChild(this);
            return viewport;
        }

        public int RowCount => rowDefinitions.Count;

        // A cell's blocks by grid position; a column inside a span gives the spanning cell's.
        internal List<BlockControl> CellBlocks(int row, int column) =>
            Cells().Last(c => c.gridRow == row && c.gridColumn <= column).children.OfType<BlockControl>().ToList();

        // Cells in reading order.
        public List<StackPanelControl> Cells()
        {
            List<StackPanelControl> cells = new List<StackPanelControl>();
            foreach (Entity child in children)
                if (child is StackPanelControl cell) cells.Add(cell);

            return cells;
        }

        // Every cell's blocks in reading order, appended to a document's flat block list.
        internal void AppendBlocks(List<BlockControl> into)
        {
            foreach (Entity child in children)
            {
                if (child is not StackPanelControl cell) continue;

                foreach (Entity entry in cell.children)
                    if (entry is BlockControl block) into.Add(block);
            }
        }

        // First block of the cell after or before the one holding from; null past either end.
        internal BlockControl? StepCell(BlockControl from, int direction)
        {
            List<StackPanelControl> cells = Cells();

            int index = cells.IndexOf(from.parent as StackPanelControl);
            int next = index + direction;
            if (index < 0 || next < 0 || next >= cells.Count) return null;

            foreach (Entity entry in cells[next].children)
                if (entry is BlockControl block) return block;

            return null;
        }

        internal void SetZoom(float value)
        {
            bool changed = zoom != value;
            if (changed)
            {
                zoom = value;
                for (int c = 0; c < widths.Length; c++)
                    columnDefinitions[c].value = widths[c] * zoom;
                InvalidateLayout();
            }

            foreach (Entity child in children)
            {
                if (child is not StackPanelControl cell) continue;

                foreach (Entity entry in cell.children)
                    if (entry is BlockControl block) block.SetZoom(zoom);
            }
            if (changed) ApplyInsets();
        }

        public void ApplyLayout(DocumentLayout layout)
        {
            foreach (Entity child in children)
            {
                if (child is not StackPanelControl cell) continue;

                foreach (Entity entry in cell.children)
                    if (entry is BlockControl block) block.ApplyLayout(layout);
            }
            ApplyInsets();
        }

        // Each cell's inset: the padding, plus the room its row's rules and their gaps take.
        public void ApplyInsets()
        {
            Vector2 pad = (cellPadding ?? new Vector2(cellInset)) * zoom;
            List<StackPanelControl> cells = Cells();
            float[] top = new float[rowDefinitions.Count];
            float[] bottom = new float[rowDefinitions.Count];
            foreach (StackPanelControl cell in cells)
            {
                if (cellOf[cell].rules is not CellRules r) continue;
                float em = Em(cell);
                int row = cell.gridRow;
                if (r.above != TableRule.None)
                {
                    top[row] = MathF.Max(top[row], Band(r.above, r.aboveWidth, em) + Gap(r.above, belowRuleSep, em));
                    if (row > 0) bottom[row - 1] = MathF.Max(bottom[row - 1], Gap(r.above, aboveRuleSep, em));
                }
                if (r.below != TableRule.None)
                    bottom[row] = MathF.Max(bottom[row], Gap(r.below, aboveRuleSep, em) + Band(r.below, r.belowWidth, em));
            }

            foreach (StackPanelControl cell in cells)
                cell.margin = new Thickness(pad.Y + top[cell.gridRow], pad.X, pad.Y + bottom[cell.gridRow], pad.X);
        }

        // The cell's type size, zoomed.
        private float Em(StackPanelControl cell)
        {
            foreach (Entity entry in cell.children)
                if (entry is BlockControl block) return block.fontSize * zoom;
            return 0f;
        }

        // One line of a rule, px, never under 1.
        private float Line(TableRule kind, float width, float em) => MathF.Max(1f, width > 0f ? width * zoom : kind switch
        {
            TableRule.Heavy => heavyRule * em,
            TableRule.Light => lightRule * em,
            TableRule.Cmid => cmidRule * em,
            _ => arrayRule * pxPerPt * zoom
        });

        // The height a rule takes: one line, or two and the gap between.
        private float Band(TableRule kind, float width, float em) =>
            kind == TableRule.Double ? 2f * Line(kind, width, em) + doubleRuleSep * pxPerPt * zoom : Line(kind, width, em);

        // A booktabs rule's sep, ex; none for \hline.
        private static float Gap(TableRule kind, float ex, float em) =>
            kind is TableRule.Heavy or TableRule.Light or TableRule.Cmid ? ex * exPerEm * em : 0f;

        #region ---- pages ----
        internal float FirstRowHeight => rowDefinitions.Count > 0 ? rowDefinitions[0].resolvedSize : 0f;
        internal float Axis => model.axis * zoom;

        // How far the table rises above the baseline of the line it is centred on, as TeX's \vcenter.
        internal float AboveAxis()
        {
            float height = 0f;
            for (int r = 0; r < rowDefinitions.Count; r++)
                height += rowDefinitions[r].resolvedSize;
            return height * 0.5f + Axis;
        }

        // Pushes each row that would cross a page break to the next page, through the gap above it;
        // returns the table's height.
        internal float Paginate(float top, PageBands bands, PageSpace? space = null)
        {
            float y = top;
            for (int r = 0; r < rowDefinitions.Count; r++)
            {
                if (r > 0)
                {
                    float pushed = space?.Push(bands, y, rowDefinitions[r].resolvedSize) ?? bands.Push(y, rowDefinitions[r].resolvedSize);
                    rowDefinitions[r - 1].gapAfter = pushed - y;
                    y = pushed;
                }
                y += rowDefinitions[r].resolvedSize;
            }

            if (rowDefinitions.Count > 0) rowDefinitions[^1].gapAfter = 0f;
            return y - top;
        }
        #endregion

        #region ---- layout ----
        // Measures without page gaps; Paginate sets them after.
        protected override Vector2 MeasureCore(Vector2 availableSize)
        {
            foreach (RowDefinition row in rowDefinitions)
                row.gapAfter = 0f;

            Vector2 desired = base.MeasureCore(availableSize);
            foreach (PanelControl border in borders)
                border.Measure(availableSize);
            foreach (PanelControl rule in ruleLines)
                rule.Measure(availableSize);
            foreach (ColumnGrip grip in grips)
                grip.Measure(availableSize);

            return desired;
        }

        // Moves the columns across the slot by the alignment.
        protected override void ArrangeCore(LayoutRect finalRect)
        {
            float slack = finalRect.width - arrange.desired.X;
            if (slack > 0f && alignment is TextAlignment.Center or TextAlignment.Right)
                finalRect = new LayoutRect(finalRect.x + (alignment == TextAlignment.Center ? slack * 0.5f : slack), finalRect.y,
                    arrange.desired.X, finalRect.height);
            else if (model.indent > 0f)
                finalRect = new LayoutRect(finalRect.x + model.indent * zoom, finalRect.y, arrange.desired.X, finalRect.height);
            base.ArrangeCore(finalRect);
            ArrangeBorders();
            ArrangeRules();
            ArrangeGrips();
        }

        // Cell rules across each cell, trimmed by \cmidrulekern; column rules down each run of rows a page gap does not split.
        private void ArrangeRules()
        {
            List<LayoutRect> across = new List<LayoutRect>();
            foreach (StackPanelControl cell in Cells())
            {
                if (cellOf[cell].rules is not CellRules r) continue;
                LayoutRect box = Box(cell);
                float em = Em(cell);
                if (r.above != TableRule.None)
                    Across(across, box, box.y, r.above, r.aboveWidth, r.aboveTrim, em);
                if (r.below != TableRule.None)
                    Across(across, box, box.Bottom - Band(r.below, r.belowWidth, em), r.below, r.belowWidth, r.belowTrim, em);
            }
            int used = 0;
            foreach (LayoutRect line in across)
                Pooled(ruleLines, used++, PaletteRole.Ink).Arrange(line);

            LayoutRect rect = arrangedRect;
            float x = rect.x + padding.left;
            for (int c = 0; c < widths.Length; c++)
            {
                float right = x + columnDefinitions[c].resolvedSize;
                if (leftRules[c] != TableRule.None) Down(ref used, x, false, leftRules[c]);
                if (rightRules[c] != TableRule.None) Down(ref used, right, true, rightRules[c]);
                x = right + columnDefinitions[c].gapAfter;
            }

            for (int i = used; i < ruleLines.Count; i++)
                ruleLines[i].Arrange(LayoutRect.Empty);
        }

        private void Across(List<LayoutRect> into, LayoutRect box, float y, TableRule kind, float width, RuleTrim trim, float em)
        {
            float kern = cmidRuleKern * em;
            float left = box.x + ((trim & RuleTrim.Left) != 0 ? kern : 0f);
            float right = box.Right - ((trim & RuleTrim.Right) != 0 ? kern : 0f);
            float line = Line(kind, width, em);
            Join(into, new LayoutRect(left, y, right - left, line));
            if (kind == TableRule.Double)
                Join(into, new LayoutRect(left, y + line + doubleRuleSep * pxPerPt * zoom, right - left, line));
        }

        // Extends a line this one continues, so neighbouring cells' rules draw as one.
        private static void Join(List<LayoutRect> into, LayoutRect line)
        {
            for (int i = 0; i < into.Count; i++)
            {
                LayoutRect had = into[i];
                if (had.y != line.y || had.height != line.height || MathF.Abs(had.Right - line.x) > 0.01f) continue;
                into[i] = new LayoutRect(had.x, had.y, line.Right - had.x, had.height);
                return;
            }
            into.Add(line);
        }

        // A column rule at edge x, inside the table: rightward from a left edge, leftward from a right one.
        private void Down(ref int used, float x, bool fromRight, TableRule kind)
        {
            float line = Line(kind, 0f, 0f);
            float band = Band(kind, 0f, 0f);
            float start = fromRight ? x - band : x;
            float y = arrangedRect.y;
            for (int r = 0; r < rowDefinitions.Count; r++)
            {
                float top = y;
                while (r < rowDefinitions.Count - 1 && rowDefinitions[r].gapAfter <= 0.5f)
                    y += rowDefinitions[r++].resolvedSize;
                y += rowDefinitions[r].resolvedSize;
                Pooled(ruleLines, used++, PaletteRole.Ink).Arrange(new LayoutRect(start, top, line, y - top));
                if (kind == TableRule.Double)
                    Pooled(ruleLines, used++, PaletteRole.Ink).Arrange(new LayoutRect(start + band - line, top, line, y - top));
                y += rowDefinitions[r].gapAfter;
            }
        }

        // A grip centred on each column's right edge, the table's full height.
        private void ArrangeGrips()
        {
            List<StackPanelControl> cells = Cells();
            LayoutRect rect = arrangedRect;
            for (int c = 0; c < widths.Length && c < cells.Count; c++)
                Grip(c).Arrange(new LayoutRect(Box(cells[c]).Right - gripWidth * 0.5f, rect.y, gripWidth, rect.height));
        }

        private ColumnGrip Grip(int column)
        {
            while (grips.Count <= column)
            {
                ColumnGrip grip = new ColumnGrip(this, grips.Count) { alpha = 0f };
                grip.parent = this;
                children.Add(grip);
                grips.Add(grip);
                MarkTreeOrderDirty();
                grip.Measure(arrange.measuredOffer);
            }
            return grips[column];
        }

        // Top and left on every cell, right on the last column, bottom where no row follows directly.
        private void ArrangeBorders()
        {
            List<StackPanelControl> cells = showBorders ? Cells() : new List<StackPanelControl>();
            int used = 0;

            for (int i = 0; i < cells.Count; i++)
            {
                LayoutRect box = Box(cells[i]);
                int row = cells[i].gridRow;
                bool lastColumn = cells[i].gridColumn + ColumnSpan(cells[i]) == widths.Length;

                Border(used++).Arrange(new LayoutRect(box.x, box.y, box.width, borderWidth));
                Border(used++).Arrange(new LayoutRect(box.x, box.y, borderWidth, box.height));
                if (lastColumn)
                    Border(used++).Arrange(new LayoutRect(box.Right - borderWidth, box.y, borderWidth, box.height));
                if (row == rowDefinitions.Count - 1 || rowDefinitions[row].gapAfter > 0.5f)
                    Border(used++).Arrange(new LayoutRect(box.x, box.Bottom - borderWidth, box.width, borderWidth));
            }

            for (int i = used; i < borders.Count; i++)
                borders[i].Arrange(LayoutRect.Empty);
        }

        // A cell's rect with its inset added back.
        private static LayoutRect Box(StackPanelControl cell)
        {
            LayoutRect r = cell.arrangedRect;
            Thickness m = cell.margin;
            return new LayoutRect(r.x - m.left, r.y - m.top, r.width + m.totalHorizontal, r.height + m.totalVertical);
        }

        // The border line at index, created on first use.
        private PanelControl Border(int index) => Pooled(borders, index, PaletteRole.Line);

        // The line at index in a pool, created on first use.
        private PanelControl Pooled(List<PanelControl> pool, int index, PaletteRole role)
        {
            while (pool.Count <= index)
            {
                PanelControl line = new PanelControl { hitTestable = false };
                line.PaintOr(null, role);
                line.parent = this;
                children.Add(line);
                pool.Add(line);
                MarkTreeOrderDirty();
                line.Measure(arrange.measuredOffer);
            }
            return pool[index];
        }
        #endregion

        #region ---- column resize ----
        private void BeginResize(int column, float x)
        {
            if (parent?.parent is not DocumentControl { readOnly: false }) return;

            resizeColumn = column;
            resizeGrab = x;
            resizeWidth = widths[column];
            resizeBefore = DocumentXml.WriteTable(model);
        }

        // Sized from where the grab started, in design pixels.
        private void Resize(float x)
        {
            if (resizeColumn < 0) return;

            float width = MathF.Max(minColumnWidth, MathF.Round(resizeWidth + (x - resizeGrab) / zoom));
            if (width == widths[resizeColumn]) return;

            widths[resizeColumn] = width;
            columnDefinitions[resizeColumn].value = width * zoom;
            InvalidateLayout();
        }

        private void EndResize()
        {
            if (resizeColumn < 0) return;

            bool changed = widths[resizeColumn] != resizeWidth;
            resizeColumn = -1;
            if (changed) model.dirty = true;
            if (changed && parent?.parent is DocumentControl document) document.RecordTableResize(this, resizeBefore!);
            resizeBefore = null;
        }

        // The draggable right edge of one column.
        private sealed class ColumnGrip : PanelControl
        {
            private readonly TableControl table;
            private readonly int column;

            internal ColumnGrip(TableControl table, int column)
            {
                this.table = table;
                this.column = column;
            }

            public override bool OnPointerEnter(PointerEvent e)
            {
                UIEngine.WindowOf(this)?.os.ChangeCursor(CursorShape.HResize);
                return base.OnPointerEnter(e);
            }

            public override bool OnPointerExit(PointerEvent e)
            {
                UIEngine.WindowOf(this)?.os.ChangeCursor(CursorShape.Arrow);
                return base.OnPointerExit(e);
            }

            public override bool OnPointerPress(PointerEvent e)
            {
                table.BeginResize(column, e.point.X);
                StartDrag();
                return true;
            }

            public override void OnDrag(PointerEvent e)
            {
                table.Resize(e.point.X);
                base.OnDrag(e);
            }

            public override void OnDragStop(bool accepted)
            {
                table.EndResize();
                base.OnDragStop(accepted);
            }
        }
        #endregion
    }
}
