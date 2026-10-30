using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using System.Numerics;

namespace ArctisAurora.Core.UI.Containers
{
    [A_XSDType("GridSizeMode", "UI")]
    public enum GridSizeMode { Fixed, Auto, Star }

    [A_XSDType("RowDefinition", "UI")]
    public class RowDefinition
    {
        [A_XSDElementProperty("Height", "UI", "Pixel size for Fixed, weight for Star, ignored for Auto.")]
        public float value = 1f;

        [A_XSDElementProperty("SizeMode", "UI", "Fixed | Auto | Star")]
        public GridSizeMode sizeMode = GridSizeMode.Auto;

        [A_XSDElementProperty("GapAfter", "UI", "Pixels of space after this row. Ignored on the last row.")]
        public float gapAfter = 0f;

        public float resolvedSize;
    }

    [A_XSDType("ColumnDefinition", "UI")]
    public class ColumnDefinition
    {
        [A_XSDElementProperty("Width", "UI", "Pixel size for Fixed, weight for Star, ignored for Auto.")]
        public float value = 1f;

        [A_XSDElementProperty("SizeMode", "UI", "Fixed | Auto | Star")]
        public GridSizeMode sizeMode = GridSizeMode.Auto;

        [A_XSDElementProperty("GapAfter", "UI", "Pixels of space after this column. Ignored on the last column.")]
        public float gapAfter = 0f;

        public float resolvedSize;
    }

    [A_XSDType("GridCell", "UI")]
    public class GridCellAssignment
    {
        [A_XSDElementProperty("Row", "UI")]
        public int row = 0;
        [A_XSDElementProperty("Column", "UI")]
        public int column = 0;
        [A_XSDElementProperty("RowSpan", "UI")]
        public int rowSpan = 1;
        [A_XSDElementProperty("ColumnSpan", "UI")]
        public int columnSpan = 1;

        public Control? child;
    }

    // Lays children into a band grid, each cell claimed by a child's Grid.Row and Grid.Column.
    [A_XSDType("GridList", "UI")]
    public class GridListControl : ContainerControl
    {
        [A_XSDElementProperty("RowDefinition", "UI", "")]
        public List<RowDefinition> rowDefinitions = new List<RowDefinition>();
        [A_XSDElementProperty("ColumnDefinition", "UI", "")]
        public List<ColumnDefinition> columnDefinitions = new List<ColumnDefinition>();

        private readonly List<GridCellAssignment> _cellAssignments = new List<GridCellAssignment>();

        // band edges, kept between arranges
        private float[] _rowOffsets = Array.Empty<float>();
        private float[] _colOffsets = Array.Empty<float>();

        public override void AddChild(Entity entity)
        {
            if (entity is not Control control)
                throw new Exception("Child entity must be a Control");

            foreach (GridCellAssignment cell in _cellAssignments)
                if (cell.column == control.gridColumn && cell.row == control.gridRow)
                    throw new Exception("One cell - one control.");

            children.Add(entity);
            control.parent = this;
            MarkTreeOrderDirty();

            _cellAssignments.Add(new GridCellAssignment
            {
                row = control.gridRow,
                column = control.gridColumn,
                child = control
            });

            InvalidateLayout();
        }

        // Columns a child's cell covers.
        internal int ColumnSpan(Control child) => _cellAssignments.Find(a => a.child == child)?.columnSpan ?? 1;

        internal void SetColumnSpan(Control child, int span)
        {
            GridCellAssignment? cell = _cellAssignments.Find(a => a.child == child);
            if (cell == null || cell.columnSpan == span) return;
            cell.columnSpan = span;
            InvalidateLayout();
        }

        private void EnsureDefaults()
        {
            if (rowDefinitions.Count == 0)
                rowDefinitions.Add(new RowDefinition { sizeMode = GridSizeMode.Star, value = 1 });
            if (columnDefinitions.Count == 0)
                columnDefinitions.Add(new ColumnDefinition { sizeMode = GridSizeMode.Star, value = 1 });
        }

        protected override Vector2 MeasureCore(Vector2 availableSize)
        {
            EnsureDefaults();

            // A pinned axis is the box the bands divide, not the offer that came in — a stack offers
            // float.MaxValue on its main axis, and a star band would take all of it.
            float boxWidth = preferredWidth > 0 ? preferredWidth : availableSize.X;
            float boxHeight = preferredHeight > 0 ? preferredHeight : availableSize.Y;

            LayoutRect inner = new LayoutRect(0, 0, boxWidth, boxHeight).Shrink(padding);

            int rows = rowDefinitions.Count;
            int cols = columnDefinitions.Count;

            // Pass 1 — Fixed bands.
            for (int r = 0; r < rows; r++)
                rowDefinitions[r].resolvedSize = rowDefinitions[r].sizeMode == GridSizeMode.Fixed
                    ? rowDefinitions[r].value : 0;
            for (int c = 0; c < cols; c++)
                columnDefinitions[c].resolvedSize = columnDefinitions[c].sizeMode == GridSizeMode.Fixed
                    ? columnDefinitions[c].value : 0;

            // Pass 2 — Auto columns take the widest child living entirely in them.
            foreach (GridCellAssignment assignment in _cellAssignments)
            {
                if (assignment.child == null) continue;
                if (assignment.columnSpan != 1 || columnDefinitions[assignment.column].sizeMode != GridSizeMode.Auto) continue;
                Control child = assignment.child;

                Vector2 childDesired = child.Measure(inner.size);
                columnDefinitions[assignment.column].resolvedSize = MathF.Max(
                    columnDefinitions[assignment.column].resolvedSize,
                    childDesired.X + child.margin.totalHorizontal);
            }

            // Pass 3 — what is left across goes to the Star columns, by weight.
            float totalColGaps = 0f, fixedAndAutoW = 0f, totalColStars = 0f;
            for (int c = 0; c < cols; c++)
            {
                ColumnDefinition column = columnDefinitions[c];
                if (c < cols - 1) totalColGaps += column.gapAfter;
                fixedAndAutoW += column.resolvedSize;
                if (column.sizeMode == GridSizeMode.Star) totalColStars += column.value;
            }
            float starW = MathF.Max(0, inner.width - fixedAndAutoW - totalColGaps);

            for (int c = 0; c < cols; c++)
                if (columnDefinitions[c].sizeMode == GridSizeMode.Star)
                    columnDefinitions[c].resolvedSize = totalColStars > 0
                        ? starW * (columnDefinitions[c].value / totalColStars) : 0;

            // Pass 4 — every child measures at its columns' width; Auto rows take the tallest.
            foreach (GridCellAssignment assignment in _cellAssignments)
            {
                if (assignment.child == null) continue;
                Control child = assignment.child;

                int cEnd = Math.Min(assignment.column + assignment.columnSpan, cols);
                float spanW = 0f;
                for (int c = assignment.column; c < cEnd; c++)
                    spanW += columnDefinitions[c].resolvedSize + (c < cEnd - 1 ? columnDefinitions[c].gapAfter : 0f);

                Vector2 childDesired = child.Measure(new Vector2(
                    MathF.Max(0f, spanW - child.margin.totalHorizontal), inner.height));

                if (assignment.rowSpan == 1 && rowDefinitions[assignment.row].sizeMode == GridSizeMode.Auto)
                    rowDefinitions[assignment.row].resolvedSize = MathF.Max(
                        rowDefinitions[assignment.row].resolvedSize,
                        childDesired.Y + child.margin.totalVertical);
            }

            // Pass 5 — what is left down goes to the Star rows, by weight.
            float totalRowGaps = 0f, fixedAndAutoH = 0f, totalRowStars = 0f;
            for (int r = 0; r < rows; r++)
            {
                RowDefinition row = rowDefinitions[r];
                if (r < rows - 1) totalRowGaps += row.gapAfter;
                fixedAndAutoH += row.resolvedSize;
                if (row.sizeMode == GridSizeMode.Star) totalRowStars += row.value;
            }
            float starH = MathF.Max(0, inner.height - fixedAndAutoH - totalRowGaps);

            for (int r = 0; r < rows; r++)
                if (rowDefinitions[r].sizeMode == GridSizeMode.Star)
                    rowDefinitions[r].resolvedSize = totalRowStars > 0
                        ? starH * (rowDefinitions[r].value / totalRowStars) : 0;

            float totalW = totalColGaps + padding.totalHorizontal;
            for (int c = 0; c < cols; c++) totalW += columnDefinitions[c].resolvedSize;
            float totalH = totalRowGaps + padding.totalVertical;
            for (int r = 0; r < rows; r++) totalH += rowDefinitions[r].resolvedSize;

            if (preferredWidth > 0) totalW = MathF.Max(totalW, preferredWidth);
            if (preferredHeight > 0) totalH = MathF.Max(totalH, preferredHeight);

            arrange.desired = new Vector2(totalW, totalH);
            SetFlag(ArrangeFlags.MeasureDirty, false);
            return arrange.desired;
        }

        protected override void ArrangeCore(LayoutRect finalRect)
        {
            EnsureDefaults();
            WriteArranged(finalRect);

            LayoutRect inner = finalRect.Shrink(padding);

            float[] rowOffsets = BuildOffsets(rowDefinitions, static r => r.resolvedSize, static r => r.gapAfter, inner.y, ref _rowOffsets);
            float[] colOffsets = BuildOffsets(columnDefinitions, static c => c.resolvedSize, static c => c.gapAfter, inner.x, ref _colOffsets);

            foreach (GridCellAssignment assignment in _cellAssignments)
            {
                if (assignment.child == null) continue;
                Control child = assignment.child;

                int r = Math.Clamp(assignment.row, 0, rowDefinitions.Count - 1);
                int c = Math.Clamp(assignment.column, 0, columnDefinitions.Count - 1);
                int rEnd = Math.Clamp(r + assignment.rowSpan, 1, rowDefinitions.Count);
                int cEnd = Math.Clamp(c + assignment.columnSpan, 1, columnDefinitions.Count);

                float cellX = colOffsets[c];
                float cellY = rowOffsets[r];
                float gapRight = cEnd < columnDefinitions.Count ? columnDefinitions[cEnd - 1].gapAfter : 0f;
                float gapBelow = rEnd < rowDefinitions.Count ? rowDefinitions[rEnd - 1].gapAfter : 0f;
                LayoutRect cellRect = new LayoutRect(cellX, cellY,
                    colOffsets[cEnd] - cellX - gapRight, rowOffsets[rEnd] - cellY - gapBelow).Shrink(child.margin);

                float childW = (child.preferredWidth == 0 || child.horizontalAlignment == HorizontalAlignment.Stretch)
                    ? cellRect.width : MathF.Min(child.DesiredSize.X, cellRect.width);
                float childH = (child.preferredHeight == 0 || child.verticalAlignment == VerticalAlignment.Stretch)
                    ? cellRect.height : MathF.Min(child.DesiredSize.Y, cellRect.height);

                float ox = child.horizontalAlignment switch
                {
                    HorizontalAlignment.Center => (cellRect.width - childW) * 0.5f,
                    HorizontalAlignment.Right => cellRect.width - childW,
                    _ => 0f
                };
                float oy = child.verticalAlignment switch
                {
                    VerticalAlignment.Center => (cellRect.height - childH) * 0.5f,
                    VerticalAlignment.Bottom => cellRect.height - childH,
                    _ => 0f
                };

                child.Arrange(new LayoutRect(cellRect.x + ox, cellRect.y + oy, childW, childH));
            }

            SetFlag(ArrangeFlags.ArrangeDirty, false);
        }

        // Band edges, one more than there are bands, so a span reads its end straight off.
        private static float[] BuildOffsets<T>(List<T> bands, Func<T, float> size, Func<T, float> gap, float start, ref float[] offsets)
        {
            if (offsets.Length != bands.Count + 1) offsets = new float[bands.Count + 1];
            offsets[0] = start;
            for (int i = 0; i < bands.Count; i++)
                offsets[i + 1] = offsets[i] + size(bands[i]) + (i < bands.Count - 1 ? gap(bands[i]) : 0f);
            return offsets;
        }
    }
}
