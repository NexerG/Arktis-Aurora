using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;
using Silk.NET.GLFW;
using System.Globalization;
using System.Numerics;

namespace ArctisAurora.Core.UI.Sheets
{
    // One page of a sheet as a grid; only the cells in the viewport have controls.
    public class SheetControl : ContainerControl
    {
        // Parts its owner lays out one by one.
        private sealed class Parts : ContainerControl
        {
            public Parts() => hitTestable = false;

            protected override Vector2 MeasureCore(Vector2 availableSize)
            {
                foreach (Entity child in children)
                    if (child is Control control) control.Measure(unbounded);
                arrange.desired = availableSize;
                return availableSize;
            }

            protected override void ArrangeCore(LayoutRect finalRect) => WriteArranged(finalRect);

            // Arranges again once its children are placed, so its bounds hold this frame's.
            internal void Settle(LayoutRect rect)
            {
                SetFlag(ArrangeFlags.ArrangeDirty, true);
                Arrange(rect);
            }
        }

        // geometry, design pixels
        public const float headerWidth = 48f;
        public const float headerHeight = 20f;
        private const float cellInset = 4f;
        private const float lineWidth = 1f;
        private const float outlineWidth = 2f;
        private const int fontSize = 12;
        private const float grabWidth = 4f;
        private const float minimumBand = 8f;
        private const float growWidth = 20f;
        private const float growGap = 4f;

        // grid past the last used cell
        private const int spareRows = 100;
        private const int spareColumns = 26;

        private static readonly Vector2 unbounded = new Vector2(float.MaxValue, float.MaxValue);

        private readonly SheetEditorControl editor;
        public readonly SheetPage page;

        // parts, in draw order
        private readonly Parts fills = new Parts();
        private readonly Parts grid = new Parts();
        private readonly PanelControl selection = new PanelControl { hitTestable = false, alpha = 0.18f };
        private readonly PanelControl[] outline = new PanelControl[4];
        internal readonly TextBoxControl field = new TextBoxControl { fontSize = fontSize };
        private readonly PanelControl growRows = new PanelControl { hitTestable = false };
        private readonly PanelControl growColumns = new PanelControl { hitTestable = false };
        private readonly LabelControl growRowsMark = new LabelControl { text = "+", fontSize = fontSize, hitTestable = false };
        private readonly LabelControl growColumnsMark = new LabelControl { text = "+", fontSize = fontSize, hitTestable = false };
        private readonly Parts headers = new Parts();

        // pooled grid and header parts
        private readonly List<PanelControl> fillParts = new List<PanelControl>();
        private readonly List<PanelControl> lines = new List<PanelControl>();
        private readonly List<LabelControl> labels = new List<LabelControl>();
        private readonly List<PanelControl> headerParts = new List<PanelControl>();
        private readonly List<LabelControl> headerNames = new List<LabelControl>();

        // used extent, rebuilt after an edit
        private bool usedStale = true;
        private (int rows, int columns) used;

        // header edge drag
        private bool resizing;
        private bool resizeColumn;
        private int resizeIndex;
        private float? resizeBefore;
        private CursorShape shownCursor = CursorShape.Arrow;

        // a right press on a + edge, true for the rows edge, opened on release
        private bool? growMenu;

        // whether each of the last two left presses was on a + edge, newest in bit 0
        private int growPresses;

        // content zoom, multiplying every design size above and the stored band sizes
        private float zoom = 1f;

        public SheetControl(SheetEditorControl editor, SheetPage page)
        {
            this.editor = editor;
            this.page = page;
            SetZoom(DocumentEditorControl.DocumentZoom);

            selection.PaintOr(null, PaletteRole.Accent);
            field.PaintOr(null, PaletteRole.Field);
            foreach (PanelControl strip in new[] { growRows, growColumns })
            {
                strip.PaintOr(null, PaletteRole.Chrome);
                strip.gradient = "sheet-grow";
                strip.cornerRole = CornerRole.Control;
            }
            growRowsMark.PaintOr(null, PaletteRole.MutedInk);
            growColumnsMark.PaintOr(null, PaletteRole.MutedInk);

            AddChild(fills);
            AddChild(grid);
            AddChild(selection);
            for (int i = 0; i < outline.Length; i++)
            {
                outline[i] = new PanelControl { hitTestable = false };
                outline[i].PaintOr(null, PaletteRole.Accent);
                AddChild(outline[i]);
            }
            AddChild(field);
            AddChild(growRows);
            AddChild(growColumns);
            AddChild(growRowsMark);
            AddChild(growColumnsMark);
            AddChild(headers);
        }

        // Cells were written; the extent may have moved.
        internal void CellsChanged()
        {
            usedStale = true;
            InvalidateLayout();
        }

        // Re-lays the grid at a content zoom.
        internal void SetZoom(float value)
        {
            if (value == zoom) return;
            zoom = value;
            int size = FontSize;
            field.fontSize = size;
            growRowsMark.fontSize = size;
            growColumnsMark.fontSize = size;
            foreach (LabelControl label in labels) label.fontSize = size;
            foreach (LabelControl label in headerNames) label.fontSize = size;
            InvalidateLayout();
        }

        #region ---- geometry ----
        private int FontSize => Math.Max(1, (int)MathF.Round(fontSize * zoom));

        // zoomed header bands and page geometry
        public float HeaderWidth => headerWidth * zoom;
        public float HeaderHeight => headerHeight * zoom;
        private float ColumnLeft(int column) => page.ColumnLeft(column) * zoom;
        private float RowTop(int row) => page.RowTop(row) * zoom;
        private float ColumnWidth(int column) => page.ColumnWidth(column) * zoom;
        private float RowHeight(int row) => page.RowHeight(row) * zoom;
        private int ColumnAt(float x) => page.ColumnAt(x / zoom);
        private int RowAt(float y) => page.RowAt(y / zoom);

        private Vector2 Origin => new Vector2(arrangedRect.x + HeaderWidth, arrangedRect.y + HeaderHeight);

        // A spare pooled part: no size, at the grid's corner so it stays inside its layer's bounds.
        private LayoutRect Hidden => new LayoutRect(arrangedRect.x, arrangedRect.y, 0f, 0f);

        // The scroller's inner rect: the part of the grid on screen.
        private LayoutRect Viewport() =>
            parent is ScrollableControl scroller ? scroller.arrangedRect.Shrink(scroller.padding) : arrangedRect;

        public LayoutRect CellRect(int row, int column)
        {
            Vector2 origin = Origin;
            return new LayoutRect(origin.X + ColumnLeft(column), origin.Y + RowTop(row),
                ColumnWidth(column), RowHeight(row));
        }

        // The cell under a point; false over the headers.
        public bool CellAt(Vector2 point, out int row, out int column)
        {
            LayoutRect view = Viewport();
            Vector2 origin = Origin;
            row = RowAt(point.Y - origin.Y);
            column = ColumnAt(point.X - origin.X);
            return point.X >= view.x + HeaderWidth && point.Y >= view.y + HeaderHeight;
        }

        // The column or row whose far edge is under a header point.
        private bool EdgeAt(Vector2 point, out bool column, out int index)
        {
            LayoutRect view = Viewport();
            Vector2 origin = Origin;
            index = -1;
            column = point.Y < view.y + HeaderHeight && point.X >= view.x + HeaderWidth;
            if (column)
            {
                float x = point.X - origin.X;
                int c = ColumnAt(x + grabWidth);
                if (c > 0 && MathF.Abs(x - ColumnLeft(c)) <= grabWidth) index = c - 1;
            }
            else if (point.X < view.x + HeaderWidth && point.Y >= view.y + HeaderHeight)
            {
                float y = point.Y - origin.Y;
                int r = RowAt(y + grabWidth);
                if (r > 0 && MathF.Abs(y - RowTop(r)) <= grabWidth) index = r - 1;
            }
            return index >= 0;
        }

        private LayoutRect RangeRect()
        {
            LayoutRect first = CellRect(Math.Min(editor.anchorRow, editor.activeRow), Math.Min(editor.anchorColumn, editor.activeColumn));
            LayoutRect last = CellRect(Math.Max(editor.anchorRow, editor.activeRow), Math.Max(editor.anchorColumn, editor.activeColumn));
            return new LayoutRect(first.x, first.y, last.Right - first.x, last.Bottom - first.y);
        }
        #endregion

        #region ---- layout ----
        protected override Vector2 MeasureCore(Vector2 availableSize)
        {
            if (usedStale)
            {
                used = page.Used();
                usedStale = false;
            }

            foreach (Entity child in children)
                if (child is Control control) control.Measure(unbounded);

            if (editor.document.fixedSize)
            {
                arrange.desired = new Vector2(HeaderWidth + ColumnLeft(page.columns) + (growWidth + growGap * 2f) * zoom,
                    HeaderHeight + RowTop(page.rows) + (growWidth + growGap * 2f) * zoom);
                return arrange.desired;
            }

            int rows = Math.Max(used.rows, editor.activeRow + 1) + spareRows;
            int columns = Math.Max(used.columns, editor.activeColumn + 1) + spareColumns;
            arrange.desired = new Vector2(HeaderWidth + ColumnLeft(columns), HeaderHeight + RowTop(rows));
            return arrange.desired;
        }

        protected override void ArrangeCore(LayoutRect finalRect)
        {
            WriteArranged(finalRect);
            fills.Arrange(finalRect);
            grid.Arrange(finalRect);
            headers.Arrange(finalRect);

            LayoutRect view = Viewport();
            Vector2 origin = Origin;
            float cellsLeft = view.x + HeaderWidth;
            float cellsTop = view.y + HeaderHeight;

            bool fixedSize = editor.document.fixedSize;
            int columnLimit = fixedSize ? page.columns : int.MaxValue;
            int rowLimit = fixedSize ? page.rows : int.MaxValue;

            int firstColumn = Math.Min(ColumnAt(cellsLeft - origin.X), columnLimit - 1);
            int firstRow = Math.Min(RowAt(cellsTop - origin.Y), rowLimit - 1);
            int lastColumn = firstColumn;
            while (lastColumn + 1 < columnLimit && origin.X + ColumnLeft(lastColumn + 1) < view.Right) lastColumn++;
            int lastRow = firstRow;
            while (lastRow + 1 < rowLimit && origin.Y + RowTop(lastRow + 1) < view.Bottom) lastRow++;

            ArrangeGrid(view, origin, firstRow, lastRow, firstColumn, lastColumn);
            ArrangeSelection();
            ArrangeGrow(view, origin);
            ArrangeHeaders(view, origin, firstRow, lastRow, firstColumn, lastColumn);
            fills.Settle(finalRect);
            grid.Settle(finalRect);
            headers.Settle(finalRect);
        }

        // The + edges under the last row and right of the last column, cut to the viewport.
        private void ArrangeGrow(LayoutRect view, Vector2 origin)
        {
            if (!editor.document.fixedSize)
            {
                growRows.Arrange(Hidden);
                growColumns.Arrange(Hidden);
                growRowsMark.Arrange(Hidden);
                growColumnsMark.Arrange(Hidden);
                return;
            }

            float gap = growGap * zoom;
            float width = growWidth * zoom;
            float right = origin.X + ColumnLeft(page.columns);
            float bottom = origin.Y + RowTop(page.rows);
            float left = MathF.Max(origin.X + gap, view.x + HeaderWidth);
            float top = MathF.Max(origin.Y + gap, view.y + HeaderHeight);

            LayoutRect rows = new LayoutRect(left, bottom + gap,
                MathF.Max(0f, MathF.Min(right - gap, view.Right) - left), width);
            LayoutRect columns = new LayoutRect(right + gap, top,
                width, MathF.Max(0f, MathF.Min(bottom - gap, view.Bottom) - top));
            growRows.Arrange(rows);
            growColumns.Arrange(columns);
            PlaceCentred(growRowsMark, "+", rows);
            PlaceCentred(growColumnsMark, "+", columns);
        }

        // The + edge under a point; rows is true for the bottom one.
        private bool GrowAt(Vector2 point, out bool rows)
        {
            rows = growRows.arrangedRect.Contains(point);
            return editor.document.fixedSize && (rows || growColumns.arrangedRect.Contains(point));
        }

        // Lines at every visible band's far edge, then the text of every visible cell.
        private void ArrangeGrid(LayoutRect view, Vector2 origin, int firstRow, int lastRow, int firstColumn, int lastColumn)
        {
            bool fixedSize = editor.document.fixedSize;
            float lineHeight = fixedSize ? MathF.Min(view.Bottom, origin.Y + RowTop(page.rows)) - view.y : view.height;
            float lineLength = fixedSize ? MathF.Min(view.Right, origin.X + ColumnLeft(page.columns)) - view.x : view.width;

            float hairline = UIScaling.Hairline(this, lineWidth);
            int line = 0;
            for (int c = firstColumn; c <= lastColumn; c++)
            {
                float right = UIScaling.Snap(this, origin.X + ColumnLeft(c + 1));
                Pooled(lines, grid, line++, PaletteRole.Line).Arrange(new LayoutRect(right - hairline, view.y, hairline, MathF.Max(0f, lineHeight)));
            }
            for (int r = firstRow; r <= lastRow; r++)
            {
                float bottom = UIScaling.Snap(this, origin.Y + RowTop(r + 1));
                Pooled(lines, grid, line++, PaletteRole.Line).Arrange(new LayoutRect(view.x, bottom - hairline, MathF.Max(0f, lineLength), hairline));
            }
            for (int i = line; i < lines.Count; i++)
                lines[i].Arrange(Hidden);

            int label = 0;
            int fill = 0;
            for (int r = firstRow; r <= lastRow; r++)
                for (int c = firstColumn; c <= lastColumn; c++)
                {
                    SheetFormat format = page.Format(r, c);
                    if (format.fill != null)
                    {
                        PanelControl part = Pooled(fillParts, fills, fill++, PaletteRole.Accent);
                        if (part.colorHex != format.fill) part.PaintOr(format.fill, PaletteRole.Accent);
                        part.Arrange(CellRect(r, c));
                    }

                    if (editor.editing && r == editor.editRow && c == editor.editColumn) continue;

                    SheetValue value = SheetBook.calc.Value(page, r, c);
                    string? text = value.Display(format.number);
                    if (string.IsNullOrEmpty(text)) continue;

                    LabelControl cellLabel = Label(labels, grid, label++, PaletteRole.Ink);
                    FontStyle style = format.bold ? FontStyle.Bold : FontStyle.Regular;
                    if (cellLabel.style != style)
                    {
                        cellLabel.style = style;
                        cellLabel.InvalidateLayout();
                    }
                    PlaceText(cellLabel, text, CellRect(r, c), value.kind == SheetValueKind.Number);
                }
            for (int i = fill; i < fillParts.Count; i++)
                fillParts[i].Arrange(Hidden);
            for (int i = label; i < labels.Count; i++)
                labels[i].Arrange(Hidden);
        }

        // Shaded range, outlined active cell, and the field over the cell being edited.
        private void ArrangeSelection()
        {
            bool range = editor.anchorRow != editor.activeRow || editor.anchorColumn != editor.activeColumn;
            selection.Arrange(range ? RangeRect() : LayoutRect.Empty);

            LayoutRect cellRect = CellRect(editor.activeRow, editor.activeColumn);
            float left = UIScaling.Snap(this, cellRect.x);
            float top = UIScaling.Snap(this, cellRect.y);
            LayoutRect active = new LayoutRect(left, top, UIScaling.Snap(this, cellRect.Right) - left, UIScaling.Snap(this, cellRect.Bottom) - top);
            float edge = UIScaling.Hairline(this, outlineWidth);
            outline[0].Arrange(new LayoutRect(active.x, active.y, active.width, edge));
            outline[1].Arrange(new LayoutRect(active.x, active.Bottom - edge, active.width, edge));
            outline[2].Arrange(new LayoutRect(active.x, active.y, edge, active.height));
            outline[3].Arrange(new LayoutRect(active.Right - edge, active.y, edge, active.height));

            if (!editor.editing)
            {
                field.Arrange(LayoutRect.Empty);
                return;
            }

            LayoutRect cell = CellRect(editor.editRow, editor.editColumn);
            field.Arrange(new LayoutRect(cell.x + outlineWidth, cell.y + outlineWidth,
                cell.width - outlineWidth * 2f, cell.height - outlineWidth * 2f));
        }

        // Column letters along the top, row numbers down the side, both pinned to the viewport.
        private void ArrangeHeaders(LayoutRect view, Vector2 origin, int firstRow, int lastRow, int firstColumn, int lastColumn)
        {
            Pooled(headerParts, headers, 0, PaletteRole.Chrome).Arrange(new LayoutRect(view.x, view.y, view.width, HeaderHeight));
            Pooled(headerParts, headers, 1, PaletteRole.Chrome).Arrange(new LayoutRect(view.x, view.y, HeaderWidth, view.height));
            float hairline = UIScaling.Hairline(this, lineWidth);
            Pooled(headerParts, headers, 2, PaletteRole.Line).Arrange(new LayoutRect(view.x, UIScaling.Snap(this, view.y + HeaderHeight) - hairline, view.width, hairline));
            Pooled(headerParts, headers, 3, PaletteRole.Line).Arrange(new LayoutRect(UIScaling.Snap(this, view.x + HeaderWidth) - hairline, view.y, hairline, view.height));

            int name = 0;
            for (int c = firstColumn; c <= lastColumn; c++)
            {
                float left = origin.X + ColumnLeft(c);
                if (left + ColumnWidth(c) <= view.x + HeaderWidth) continue;
                LayoutRect band = new LayoutRect(left, view.y, ColumnWidth(c), HeaderHeight);
                PlaceCentred(Label(headerNames, headers, name++, PaletteRole.MutedInk), SheetDocument.ColumnName(c), band);
            }
            for (int r = firstRow; r <= lastRow; r++)
            {
                float top = origin.Y + RowTop(r);
                if (top + RowHeight(r) <= view.y + HeaderHeight) continue;
                LayoutRect band = new LayoutRect(view.x, top, HeaderWidth, RowHeight(r));
                PlaceCentred(Label(headerNames, headers, name++, PaletteRole.MutedInk), (r + 1).ToString(CultureInfo.InvariantCulture), band);
            }
            for (int i = name; i < headerNames.Count; i++)
                headerNames[i].Arrange(Hidden);
        }

        private void PlaceText(LabelControl label, string text, LayoutRect cell, bool alignRight)
        {
            label.text = text;
            label.Measure(unbounded);

            float inset = cellInset * zoom;
            float room = MathF.Max(0f, cell.width - inset * 2f);
            Vector2 size = label.DesiredSize;
            float x = alignRight && size.X <= room ? cell.Right - inset - size.X : cell.x + inset;
            label.Arrange(new LayoutRect(x, cell.y + (cell.height - size.Y) * 0.5f, MathF.Min(size.X, room), size.Y));
        }

        private static void PlaceCentred(LabelControl label, string text, LayoutRect band)
        {
            label.text = text;
            label.Measure(unbounded);

            Vector2 size = label.DesiredSize;
            float width = MathF.Min(size.X, band.width);
            label.Arrange(new LayoutRect(band.x + (band.width - width) * 0.5f, band.y + (band.height - size.Y) * 0.5f, width, size.Y));
        }

        private PanelControl Pooled(List<PanelControl> pool, Parts layer, int index, PaletteRole role)
        {
            while (pool.Count <= index)
            {
                PanelControl part = new PanelControl { hitTestable = false };
                part.PaintOr(null, role);
                Adopt(layer, part);
                pool.Add(part);
            }
            return pool[index];
        }

        private LabelControl Label(List<LabelControl> pool, Parts layer, int index, PaletteRole role)
        {
            while (pool.Count <= index)
            {
                LabelControl label = new LabelControl { hitTestable = false, fontSize = FontSize };
                label.PaintOr(null, role);
                Adopt(layer, label);
                pool.Add(label);
            }
            return pool[index];
        }

        private void Adopt(Parts layer, Control part)
        {
            part.parent = layer;
            layer.children.Add(part);
            MarkTreeOrderDirty();
            part.Measure(unbounded);
        }
        #endregion

        #region ---- pointer ----
        public override bool OnPointerPress(PointerEvent e)
        {
            base.OnPointerPress(e);
            growMenu = null;
            stopsContextMenu = false;
            bool grow = GrowAt(e.point, out bool bottom);
            if (e.button == PointerEvent.leftButton) growPresses = (growPresses << 1 | (grow ? 1 : 0)) & 3;
            if (grow)
            {
                if (e.button == PointerEvent.leftButton)
                {
                    int step = InputHandler.instance.IsModifierDown(InputModifier.Extend) ? SettingsRegistry.Get<SheetSettings>().grow.step : 1;
                    editor.Grow(bottom ? step : 0, bottom ? 0 : step);
                }
                else if (e.button == PointerEvent.rightButton)
                {
                    growMenu = bottom;
                    stopsContextMenu = true;
                }
                return true;
            }
            if (e.button == PointerEvent.rightButton)
            {
                if (CellAt(e.point, out int row, out int column) && !editor.IsSelected(row, column))
                    editor.Select(row, column, false);
                return false;
            }
            if (e.button != PointerEvent.leftButton) return false;

            if (EdgeAt(e.point, out bool edgeColumn, out int index))
            {
                resizing = true;
                resizeColumn = edgeColumn;
                resizeIndex = index;
                resizeBefore = (edgeColumn ? page.columnWidths : page.rowHeights).TryGetValue(index, out float size) ? size : null;
                StartDrag();
            }
            else if (CellAt(e.point, out int row, out int column))
            {
                editor.Select(row, column, InputHandler.instance.IsModifierDown(InputModifier.Extend));
                StartDrag();
            }
            return true;
        }

        public override void OnDrag(PointerEvent e)
        {
            base.OnDrag(e);
            if (resizing)
            {
                Vector2 origin = Origin;
                float start = resizeColumn ? origin.X + ColumnLeft(resizeIndex) : origin.Y + RowTop(resizeIndex);
                float size = MathF.Max(minimumBand, ((resizeColumn ? e.point.X : e.point.Y) - start) / zoom);
                (resizeColumn ? page.columnWidths : page.rowHeights)[resizeIndex] = size;
                InvalidateLayout();
                return;
            }
            if (CellAt(e.point, out int row, out int column)) editor.Select(row, column, true);
        }

        public override void OnDragStop(bool accepted)
        {
            base.OnDragStop(accepted);
            if (!resizing) return;

            resizing = false;
            float? after = (resizeColumn ? page.columnWidths : page.rowHeights).TryGetValue(resizeIndex, out float size) ? size : null;
            editor.ResizeBand(resizeColumn, resizeIndex, resizeBefore, after);
            ShowCursor(CursorShape.Arrow);
        }

        // Opens the grow popup after a right press on a + edge.
        public override bool OnPointerRelease(PointerEvent e)
        {
            bool handled = base.OnPointerRelease(e);
            if (e.button != PointerEvent.rightButton || growMenu is not bool rows) return handled;

            growMenu = null;
            Vector2 point = e.point;
            Engine.Post(() =>
            {
                stopsContextMenu = false;
                if (!destroyed) SheetGrowPopup.Open(editor, this, point, rows);
            });
            return true;
        }

        public override bool OnPointerMove(PointerEvent e)
        {
            bool handled = base.OnPointerMove(e);
            if (!resizing)
                ShowCursor(EdgeAt(e.point, out bool column, out _) ? column ? CursorShape.HResize : CursorShape.VResize : CursorShape.Arrow);
            return handled;
        }

        public override bool OnPointerExit(PointerEvent e)
        {
            if (!resizing) ShowCursor(CursorShape.Arrow);
            return base.OnPointerExit(e);
        }

        private void ShowCursor(CursorShape shape)
        {
            if (shownCursor == shape) return;
            shownCursor = shape;
            UIEngine.WindowOf(this)?.os.ChangeCursor(shape);
        }

        public override bool OnPointerTap(PointerEvent e)
        {
            if (e.tapCount != 2 || growPresses != 0 || !CellAt(e.point, out _, out _)) return base.OnPointerTap(e);

            editor.BeginEdit(true);
            return true;
        }
        #endregion
    }
}
