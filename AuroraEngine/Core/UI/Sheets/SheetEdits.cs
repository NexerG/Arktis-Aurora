using ArctisAurora.Core.Editing;

namespace ArctisAurora.Core.UI.Sheets
{
    // Cells of one layer before and after an edit.
    public sealed class SheetCellEdit : IEditRecord
    {
        private readonly SheetDocument document;
        private readonly SheetPage page;
        private readonly SheetLayer layer;
        private readonly List<(int row, int column, string? before, string? after)> cells;

        public SheetCellEdit(SheetDocument document, SheetPage page, SheetLayer layer, List<(int, int, string?, string?)> cells)
        {
            this.document = document;
            this.page = page;
            this.layer = layer;
            this.cells = cells;
        }

        public void Undo()
        {
            foreach ((int row, int column, string? before, _) in cells)
                layer.Set(row, column, before);
            SheetBook.Changed(document, page, cells.Select(cell => (cell.row, cell.column)));
        }

        public void Redo()
        {
            foreach ((int row, int column, _, string? after) in cells)
                layer.Set(row, column, after);
            SheetBook.Changed(document, page, cells.Select(cell => (cell.row, cell.column)));
        }
    }

    // Cell formats of one page before and after an edit.
    public sealed class SheetFormatEdit : IEditRecord
    {
        private readonly SheetDocument document;
        private readonly SheetPage page;
        private readonly List<(int row, int column, SheetFormat before, SheetFormat after)> cells;

        public SheetFormatEdit(SheetDocument document, SheetPage page, List<(int, int, SheetFormat, SheetFormat)> cells)
        {
            this.document = document;
            this.page = page;
            this.cells = cells;
        }

        public void Undo()
        {
            foreach ((int row, int column, SheetFormat before, _) in cells)
                page.SetFormat(row, column, before);
            SheetBook.Changed(document, page, []);
        }

        public void Redo()
        {
            foreach ((int row, int column, _, SheetFormat after) in cells)
                page.SetFormat(row, column, after);
            SheetBook.Changed(document, page, []);
        }
    }

    // One column's width or row's height before and after a drag; null is the default size.
    public sealed class SheetBandEdit : IEditRecord
    {
        private readonly SheetDocument document;
        private readonly SheetPage page;
        private readonly bool column;
        private readonly int index;
        private readonly float? before;
        private readonly float? after;

        public SheetBandEdit(SheetDocument document, SheetPage page, bool column, int index, float? before, float? after)
        {
            this.document = document;
            this.page = page;
            this.column = column;
            this.index = index;
            this.before = before;
            this.after = after;
        }

        public void Undo() => Apply(before);

        public void Redo() => Apply(after);

        private void Apply(float? size)
        {
            Dictionary<int, float> bands = column ? page.columnWidths : page.rowHeights;
            if (size is float value) bands[index] = value;
            else bands.Remove(index);
            SheetBook.Changed(document, page, []);
        }
    }

    // A page added at, or removed from, an index.
    public sealed class SheetPageEdit : IEditRecord
    {
        private readonly SheetDocument document;
        private readonly int index;
        private readonly SheetPage page;
        private readonly bool added;

        public SheetPageEdit(SheetDocument document, int index, SheetPage page, bool added)
        {
            this.document = document;
            this.index = index;
            this.page = page;
            this.added = added;
        }

        public void Undo() => Apply(!added);

        public void Redo() => Apply(added);

        private void Apply(bool present)
        {
            if (present) document.pages.Insert(index, page);
            else document.pages.Remove(page);
            SheetBook.Restructured(document);
        }
    }

    // A layer added at, or removed from, an index of its page.
    public sealed class SheetLayerEdit : IEditRecord
    {
        private readonly SheetDocument document;
        private readonly SheetPage page;
        private readonly int index;
        private readonly SheetLayer layer;
        private readonly bool added;

        public SheetLayerEdit(SheetDocument document, SheetPage page, int index, SheetLayer layer, bool added)
        {
            this.document = document;
            this.page = page;
            this.index = index;
            this.layer = layer;
            this.added = added;
        }

        public void Undo() => Apply(!added);

        public void Redo() => Apply(added);

        private void Apply(bool present)
        {
            if (present) page.layers.Insert(index, layer);
            else page.layers.Remove(layer);
            SheetBook.Restructured(document);
        }
    }

    // A layer shown or hidden; undo and redo both flip it.
    public sealed class SheetLayerShowEdit : IEditRecord
    {
        private readonly SheetDocument document;
        private readonly SheetLayer layer;

        public SheetLayerShowEdit(SheetDocument document, SheetPage page, SheetLayer layer)
        {
            this.document = document;
            this.layer = layer;
        }

        public void Undo() => Flip();

        public void Redo() => Flip();

        private void Flip()
        {
            layer.visible = !layer.visible;
            SheetBook.Restructured(document);
        }
    }

    // A page's name before and after, with every reference to it.
    public sealed class SheetPageRenameEdit : IEditRecord
    {
        private readonly SheetDocument document;
        private readonly SheetPage page;
        private readonly string before;
        private readonly string after;

        public SheetPageRenameEdit(SheetDocument document, SheetPage page, string before, string after)
        {
            this.document = document;
            this.page = page;
            this.before = before;
            this.after = after;
        }

        public void Undo() => Apply(after, before);

        public void Redo() => Apply(before, after);

        private void Apply(string from, string to)
        {
            page.name = to;
            SheetBook.PageRenamed(document, from, to);
        }
    }

    // A fixed page's rows and columns before and after it grew.
    public sealed class SheetSizeEdit : IEditRecord
    {
        private readonly SheetDocument document;
        private readonly SheetPage page;
        private readonly (int rows, int columns) before;
        private readonly (int rows, int columns) after;

        public SheetSizeEdit(SheetDocument document, SheetPage page, (int rows, int columns) before, (int rows, int columns) after)
        {
            this.document = document;
            this.page = page;
            this.before = before;
            this.after = after;
        }

        public void Undo() => Apply(before);

        public void Redo() => Apply(after);

        private void Apply((int rows, int columns) size)
        {
            (page.rows, page.columns) = size;
            SheetBook.Changed(document, page, []);
        }
    }

    // Empty rows or columns inserted before an index, with every reference to the bands they moved.
    public sealed class SheetInsertEdit : IEditRecord
    {
        private readonly SheetDocument document;
        private readonly SheetPage page;
        private readonly bool column;
        private readonly int at;
        private readonly int count;

        public SheetInsertEdit(SheetDocument document, SheetPage page, bool column, int at, int count)
        {
            this.document = document;
            this.page = page;
            this.column = column;
            this.at = at;
            this.count = count;
        }

        public void Undo() => Apply(-count);

        public void Redo() => Apply(count);

        private void Apply(int by)
        {
            page.Shift(column, at, by);
            SheetBook.Shifted(document, page, column, at, by);
        }
    }
}
