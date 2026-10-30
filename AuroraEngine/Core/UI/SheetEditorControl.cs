using ArctisAurora.Core.Editing;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Filing.Serialization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Numerics;
using System.Text;

namespace ArctisAurora.Core.UI
{
    // One open sheet: the scroll viewport, the grid under it, the page tabs, and the file behind them.
    public class SheetEditorControl : StackPanelControl, IClipboardTarget, IFileEditor
    {
        public SheetDocument document { get; private set; } = null!;
        public string? path { get; private set; }
        public bool unsaved => document.unsaved;
        public UndoStack undo => document.undo;

        bool IFileEditor.isDirty => unsaved;

        // selection, in cells
        public int activeRow { get; private set; }
        public int activeColumn { get; private set; }
        public int anchorRow { get; private set; }
        public int anchorColumn { get; private set; }

        // the cell the field is open on
        public bool editing { get; private set; }
        public int editRow { get; private set; }
        public int editColumn { get; private set; }

        private SheetControl? sheet;
        public readonly ScrollableControl scroller;
        private readonly SheetPageStripControl strip;

        // the shown page; kept when it is removed so a neighbour can be shown
        public int pageIndex { get; private set; }

        // the last copy's text and the cells it came from, for Paste link
        private static string? copiedText;
        private static (SheetDocument document, string? path, SheetPage page, int top, int left, int bottom, int right) copiedFrom;

        // honoured at the end of Arrange
        private bool scrollToActivePending;
        private SessionTab? pendingView;

        // the layer edits land in: the picked one while it is on the page, else the topmost
        private SheetLayer? pickedLayer;
        private SheetLayer Layer => pickedLayer != null && sheet!.page.layers.Contains(pickedLayer) ? pickedLayer : sheet!.page.layers[^1];
        public SheetLayer editLayer => Layer;

        public SheetPage page => sheet!.page;

        // formula bar: the active cell's address and its text
        private const float formulaBarHeight = 24f;
        private readonly LabelControl cellName;
        private readonly TextBoxControl formula;

        // the tab-row tools, built the first time a tab asks
        private TabToolsControl? sheetTools;
        public Control tools => sheetTools ??= BuildTools();

        public SheetEditorControl()
        {
            horizontalAlignment = HorizontalAlignment.Stretch;
            verticalAlignment = VerticalAlignment.Stretch;
            PaintOr(null, PaletteRole.Surface);

            StackPanelControl bar = new StackPanelControl
            {
                orientation = Orientation.Horizontal,
                Spacing = 6f,
                preferredHeight = formulaBarHeight,
                horizontalAlignment = HorizontalAlignment.Stretch,
                padding = new Thickness(3f, 6f, 3f, 6f),
                edgeThickness = new Thickness(0f, 0f, 1f, 0f),
                edgeRole = PaletteRole.Line
            };
            cellName = new LabelControl { preferredWidth = 44f, fontSize = 12, role = PaletteRole.Ink, verticalPosition = 0.5f };
            formula = new TextBoxControl { widthStar = 1f, fontSize = 12, role = PaletteRole.SubField, padding = new Thickness(6f, 0f), cornerRole = CornerRole.Control };
            formula.onCommit = value => { CommitFormula(value); if (sheet != null) UIEngine.SetActiveControl(sheet); };
            formula.onCancel = () => { SyncFormula(); if (sheet != null) UIEngine.SetActiveControl(sheet); };
            formula.onBlur = () => CommitFormula(formula.text);
            bar.AddChild(cellName);
            bar.AddChild(new LabelControl { text = "fx", fontSize = 12, role = PaletteRole.MutedInk, verticalPosition = 0.5f });
            bar.AddChild(formula);

            scroller = new Scroller(this);
            strip = new SheetPageStripControl(this);
            AddChild(bar);
            AddChild(scroller);
            AddChild(strip);
            SheetBook.changed += BookChanged;
        }

        public override void OnDestroy()
        {
            SheetBook.changed -= BookChanged;
            sheetTools?.Destroy();
            base.OnDestroy();
        }

        public void LoadPath(string nameOrPath)
        {
            path = Path.GetFullPath(Path.IsPathRooted(nameOrPath) ? nameOrPath : Paths.Doc(nameOrPath));
            Load(SheetBook.Get(path));
            if (SheetCsv.IsCsv(path)) strip.Hide();
        }

        public void Load(SheetDocument loaded)
        {
            document = loaded;
            Build(0);
        }

        // A fresh grid on one page, the selection back at A1 and the view at the top.
        private void Build(int index)
        {
            bool active = sheet != null && (ReferenceEquals(UIEngine.activeControl, sheet) || ReferenceEquals(UIEngine.activeControl, sheet.field));
            pageIndex = index;
            pickedLayer = null;
            editing = false;
            activeRow = activeColumn = anchorRow = anchorColumn = 0;

            sheet?.Destroy();
            sheet = new SheetControl(this, document.pages[index]);
            sheet.field.onCommit = value => FinishEdit(value, true);
            sheet.field.onBlur = () => { if (editing) FinishEdit(sheet!.field.text, false); };
            sheet.field.onCancel = CancelEdit;
            scroller.AddChild(sheet);
            scroller.SetScrollOffset(Vector2.Zero);
            if (active) UIEngine.SetActiveControl(sheet);
            strip.Sync();
            SyncFormula();
        }

        #region ---- file ----
        public void Save()
        {
            if (editing) sheet!.field.Commit();
            if (path == null) return;
            document.Save(path);
            document.unsaved = false;
        }

        public void Repath(string newPath, string name)
        {
            path = newPath;
            document.name = name;
        }

        // Cells changed somewhere in the vault: this file's own edits make it unsaved.
        private void BookChanged(SheetDocument? edited)
        {
            if (ReferenceEquals(edited, document)) document.unsaved = true;
            if (sheet == null || destroyed) return;

            int index = document.pages.IndexOf(sheet.page);
            if (index < 0)
            {
                Build(Math.Min(pageIndex, document.pages.Count - 1));
                return;
            }
            pageIndex = index;
            if (document.fixedSize)
            {
                anchorRow = Math.Min(anchorRow, sheet.page.rows - 1);
                anchorColumn = Math.Min(anchorColumn, sheet.page.columns - 1);
                if (activeRow >= sheet.page.rows || activeColumn >= sheet.page.columns) Select(activeRow, activeColumn, true);
            }
            sheet.CellsChanged();
            strip.Sync();
            SyncFormula();
        }

        public void Undo()
        {
            if (undo.Undo()) RequestScrollToActive();
        }

        public void Redo()
        {
            if (undo.Redo()) RequestScrollToActive();
        }
        #endregion

        #region ---- pages ----
        public void ShowPage(int index)
        {
            if (sheet == null || index < 0 || index >= document.pages.Count || ReferenceEquals(sheet.page, document.pages[index])) return;
            if (editing) sheet.field.Commit();
            Build(index);
        }

        public void AddPage()
        {
            if (editing) sheet!.field.Commit();
            Record("Add page", new SheetPageEdit(document, document.pages.Count, SheetPage.Blank(FreePageName()), true));
            ShowPage(document.pages.Count - 1);
        }

        // A CSV file as a new page named after it, shown after; one undo step.
        public void ImportPage(string csvPath)
        {
            if (editing) sheet!.field.Commit();
            SheetPage page = SheetCsv.Load(csvPath).pages[0];
            string name = page.name.Trim();
            if (name.Length == 0 || name.Contains('!') || document.pages.Exists(other => other.name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                name = FreePageName();
            page.name = name;

            Record("Import CSV", new SheetPageEdit(document, document.pages.Count, page, true));
            ShowPage(document.pages.Count - 1);
        }

        // The last page stays.
        public void DeletePage(int index)
        {
            if (document.pages.Count < 2 || index < 0 || index >= document.pages.Count) return;
            if (editing) sheet!.field.Commit();
            Record("Delete page", new SheetPageEdit(document, index, document.pages[index], false));
        }

        // False for an empty name, one holding "!", or another page's.
        public bool RenamePage(int index, string name)
        {
            name = name.Trim();
            if (index < 0 || index >= document.pages.Count) return false;

            SheetPage page = document.pages[index];
            if (name == page.name) return true;
            if (name.Length == 0 || name.Contains('!')) return false;
            foreach (SheetPage other in document.pages)
                if (!ReferenceEquals(other, page) && other.name.Equals(name, StringComparison.OrdinalIgnoreCase)) return false;

            Record("Rename page", new SheetPageRenameEdit(document, page, page.name, name));
            return true;
        }

        // Writes "<sheet> - <page>.csv" beside the file, over an earlier export; the written path, or null without a file.
        public string? ExportPage(int index)
        {
            if (path == null || index < 0 || index >= document.pages.Count) return null;
            if (editing) sheet!.field.Commit();

            SheetPage page = document.pages[index];
            string name = Path.GetFileName(path)[..^SheetDocument.extension.Length];
            string target = Path.Combine(Path.GetDirectoryName(path)!, $"{name} - {page.name}{SheetCsv.extension}");
            SheetCsv.Export(page, target);
            return target;
        }

        private string FreePageName()
        {
            for (int n = 1; ; n++)
            {
                string name = "Sheet " + n;
                if (!document.pages.Exists(page => page.name.Equals(name, StringComparison.OrdinalIgnoreCase))) return name;
            }
        }

        private void Record(string label, IEditRecord edit)
        {
            using (undo.Begin(label))
            {
                edit.Redo();
                undo.Push(edit);
            }
        }
        #endregion

        #region ---- layers ----
        public void EditLayer(SheetLayer layer)
        {
            if (editing) sheet!.field.Commit();
            pickedLayer = layer;
        }

        public void ToggleLayer(SheetLayer layer)
        {
            if (editing) sheet!.field.Commit();
            Record(layer.visible ? "Hide layer" : "Show layer", new SheetLayerShowEdit(document, page, layer));
        }

        // A new layer on top, edited from then on.
        public void AddLayer()
        {
            if (editing) sheet!.field.Commit();
            SheetLayer layer = new SheetLayer { name = FreeLayerName() };
            Record("Add layer", new SheetLayerEdit(document, page, page.layers.Count, layer, true));
            pickedLayer = layer;
        }

        // Deletes the edited layer; the last one stays.
        public void DeleteLayer()
        {
            if (page.layers.Count < 2) return;
            if (editing) sheet!.field.Commit();
            SheetLayer layer = Layer;
            Record("Delete layer", new SheetLayerEdit(document, page, page.layers.IndexOf(layer), layer, false));
        }

        private string FreeLayerName()
        {
            for (int n = 1; ; n++)
            {
                string name = "Layer " + n;
                if (!page.layers.Exists(layer => layer.name.Equals(name, StringComparison.OrdinalIgnoreCase))) return name;
            }
        }
        #endregion

        #region ---- selection ----
        public void Select(int row, int column, bool extend)
        {
            bool clamped = sheet != null && document.fixedSize;
            activeRow = Math.Clamp(row, 0, clamped ? sheet!.page.rows - 1 : int.MaxValue);
            activeColumn = Math.Clamp(column, 0, clamped ? sheet!.page.columns - 1 : int.MaxValue);
            if (!extend)
            {
                anchorRow = activeRow;
                anchorColumn = activeColumn;
            }

            sheet?.InvalidateLayout();
            RequestScrollToActive();
            SyncFormula();
        }

        public void Move(int rows, int columns, bool extend) => Select(activeRow + rows, activeColumn + columns, extend);

        // Commits an open edit, then steps down (up with back).
        public void Enter(bool back)
        {
            if (editing) sheet!.field.Commit();
            Move(back ? -1 : 1, 0, false);
        }

        // Commits an open edit, then steps right (left with back).
        public void Tab(bool back)
        {
            if (editing) sheet!.field.Commit();
            Move(0, back ? -1 : 1, false);
        }

        public void SelectAll()
        {
            (int rows, int columns) = sheet!.page.Used();
            anchorRow = anchorColumn = 0;
            Select(Math.Max(0, rows - 1), Math.Max(0, columns - 1), true);
        }

        public bool IsSelected(int row, int column)
        {
            (int top, int left, int bottom, int right) = Range();
            return row >= top && row <= bottom && column >= left && column <= right;
        }

        private (int top, int left, int bottom, int right) Range() =>
            (Math.Min(anchorRow, activeRow), Math.Min(anchorColumn, activeColumn),
             Math.Max(anchorRow, activeRow), Math.Max(anchorColumn, activeColumn));

        internal void RequestScrollToActive()
        {
            scrollToActivePending = true;
            scroller.InvalidateArrange();
        }
        #endregion

        #region ---- editing ----
        // Opens the field on the active cell, holding its text with keep, empty without.
        public void BeginEdit(bool keep)
        {
            if (editing || sheet == null) return;

            editing = true;
            (editRow, editColumn) = (activeRow, activeColumn);

            TextBoxControl field = sheet.field;
            field.text = keep ? Layer.Get(editRow, editColumn) ?? string.Empty : string.Empty;
            UIEngine.SetActiveControl(field);
            field.Focus();
            if (keep) field.MoveCaret(CaretMove.LineEnd, false);

            sheet.InvalidateLayout();
            RequestScrollToActive();
        }

        // Writes chars typed over a cell that is not being edited, replacing what it held.
        public void TypeOver(Queue<char> input)
        {
            if (editing || input.Count == 0) return;

            BeginEdit(false);
            while (input.Count > 0)
                sheet!.field.WriteChar(input.Dequeue());
        }

        private void FinishEdit(string value, bool refocus)
        {
            if (!editing) return;
            editing = false;

            Write("Edit cell", new List<(int, int, string?)> { (editRow, editColumn, value) });
            if (refocus) UIEngine.SetActiveControl(sheet!);
            sheet!.InvalidateLayout();
        }

        private void CancelEdit()
        {
            if (!editing) return;
            editing = false;

            UIEngine.SetActiveControl(sheet!);
            sheet!.InvalidateLayout();
        }

        // Empties every selected cell of the edited layer.
        public void Clear() => Clear("Clear cells");

        private void Clear(string label)
        {
            if (editing || sheet == null) return;

            (int top, int left, int bottom, int right) = Range();
            List<(int, int, string?)> cleared = new List<(int, int, string?)>();
            foreach (long key in Layer.cells.Keys)
            {
                int row = SheetDocument.RowOf(key);
                int column = SheetDocument.ColumnOf(key);
                if (row >= top && row <= bottom && column >= left && column <= right)
                    cleared.Add((row, column, null));
            }

            Write(label, cleared);
        }

        // One undo step setting each cell to its value.
        private void Write(string label, List<(int row, int column, string? value)> cells)
        {
            List<(int, int, string?, string?)> changed = new List<(int, int, string?, string?)>();
            foreach ((int row, int column, string? value) in cells)
            {
                string? before = Layer.Get(row, column);
                string? after = string.IsNullOrEmpty(value) ? null : value;
                if (before != after) changed.Add((row, column, before, after));
            }
            if (changed.Count == 0) return;

            SheetCellEdit edit = new SheetCellEdit(document, sheet!.page, Layer, changed);
            using (undo.Begin(label))
            {
                edit.Redo();
                undo.Push(edit);
            }
        }
        #endregion

        #region ---- tools and formula bar ----
        private TabToolsControl BuildTools()
        {
            TabToolsControl row = new TabToolsControl();
            row.Icon("bold", _ => On(SheetActions.Bold));
            row.Separator();
            row.Text("Format", owner => TabToolsControl.Drop(owner, new List<ContextMenuEntry>
            {
                new ContextMenuButton("General", () => On(SheetActions.FormatGeneral)),
                new ContextMenuButton("Number", () => On(SheetActions.FormatNumber)),
                new ContextMenuButton("Percent", () => On(SheetActions.FormatPercent)),
                new ContextMenuButton("Currency", () => On(SheetActions.FormatCurrency))
            }), true);
            row.Text("Fill", owner => TabToolsControl.Drop(owner, DocumentToolbarControl.highlightOptions
                .Select(option => (ContextMenuEntry)new ContextMenuButton(option.caption, () => On(() => SetFill(option.hex))))
                .ToList()), true);
            row.Separator();
            row.Text("Paste link", _ => On(() => PasteLink()));
            row.Separator();
            row.Text("Sum", _ => On(AutoSum));
            return row;
        }

        // Writes =SUM() of the unbroken run of numbers above the active cell, or left of it when there are none above.
        public void AutoSum()
        {
            if (editing || sheet == null) return;

            string? range = NumberRun(-1, 0) ?? NumberRun(0, -1);
            if (range == null) return;
            Write("Sum", new List<(int, int, string?)> { (activeRow, activeColumn, "=SUM(" + range + ")") });
        }

        // The run of numbers starting beside the active cell and walking one way, as A1:A4; null when that neighbour is not a number.
        private string? NumberRun(int rowStep, int columnStep)
        {
            int nearRow = activeRow + rowStep, nearColumn = activeColumn + columnStep;
            if (!IsNumber(nearRow, nearColumn)) return null;

            int farRow = nearRow, farColumn = nearColumn;
            while (IsNumber(farRow + rowStep, farColumn + columnStep))
            {
                farRow += rowStep;
                farColumn += columnStep;
            }
            return SheetDocument.Address(farRow, farColumn) + ":" + SheetDocument.Address(nearRow, nearColumn);
        }

        private bool IsNumber(int row, int column) =>
            row >= 0 && column >= 0 && SheetBook.calc.Value(sheet!.page, row, column).kind == SheetValueKind.Number;

        // Gives this sheet's grid the active control, then acts, so a pane's tools act on their own sheet.
        internal void On(Action action)
        {
            if (sheet != null && !editing) UIEngine.SetActiveControl(sheet);
            action();
        }

        public void FocusFormula()
        {
            if (sheet != null && !editing) UIEngine.SetActiveControl(formula);
        }

        private void SyncFormula()
        {
            if (sheet == null || formula.isEditing) return;

            cellName.text = SheetDocument.Address(activeRow, activeColumn);
            formula.text = Layer.Get(activeRow, activeColumn) ?? string.Empty;
        }

        private void CommitFormula(string value)
        {
            if (sheet == null || editing) return;
            Write("Edit cell", new List<(int, int, string?)> { (activeRow, activeColumn, value) });
        }
        #endregion

        #region ---- formatting ----
        // Bolds the selection, or unbolds it when the active cell is bold.
        public void ToggleBold()
        {
            if (editing || sheet == null) return;
            bool on = !sheet.page.Format(activeRow, activeColumn).bold;
            Restyle("Bold", format => format with { bold = on });
        }

        public void SetFill(string? hex) => Restyle("Fill", format => format with { fill = string.IsNullOrEmpty(hex) ? null : hex });

        public void SetNumberFormat(string? number) => Restyle("Number format", format => format with { number = number });

        // One undo step changing the format of every selected cell.
        private void Restyle(string label, Func<SheetFormat, SheetFormat> change)
        {
            if (editing || sheet == null) return;

            (int top, int left, int bottom, int right) = Range();
            List<(int, int, SheetFormat, SheetFormat)> changed = new List<(int, int, SheetFormat, SheetFormat)>();
            for (int r = top; r <= bottom; r++)
                for (int c = left; c <= right; c++)
                {
                    SheetFormat before = sheet.page.Format(r, c);
                    SheetFormat after = change(before);
                    if (before != after) changed.Add((r, c, before, after));
                }
            if (changed.Count == 0) return;

            SheetFormatEdit edit = new SheetFormatEdit(document, sheet.page, changed);
            using (undo.Begin(label))
            {
                edit.Redo();
                undo.Push(edit);
            }
        }

        // A header drag that already wrote its size, recorded as one undo step.
        internal void ResizeBand(bool column, int index, float? before, float? after)
        {
            if (before == after) return;

            SheetBandEdit edit = new SheetBandEdit(document, sheet!.page, column, index, before, after);
            using (undo.Begin(column ? "Column width" : "Row height"))
            {
                edit.Redo();
                undo.Push(edit);
            }
        }

        // Adds rows and columns past the far edges of a fixed page.
        public void Grow(int rows, int columns)
        {
            rows = Math.Max(0, rows);
            columns = Math.Max(0, columns);
            if (sheet == null || !document.fixedSize || rows + columns == 0) return;
            if (editing) sheet.field.Commit();

            SheetPage shown = sheet.page;
            Record("Grow page", new SheetSizeEdit(document, shown, (shown.rows, shown.columns), (shown.rows + rows, shown.columns + columns)));
        }

        // Inserts as many empty rows (or columns) as the selection spans, before it.
        public void Insert(bool column)
        {
            if (sheet == null) return;
            if (editing) sheet.field.Commit();

            (int top, int left, int bottom, int right) = Range();
            int at = column ? left : top;
            int count = column ? right - left + 1 : bottom - top + 1;
            Record(column ? "Insert columns" : "Insert rows", new SheetInsertEdit(document, sheet.page, column, at, count));
        }
        #endregion

        #region ---- clipboard ----
        // The selection as tab-separated rows.
        public bool Copy()
        {
            if (editing || sheet == null) return false;

            (int top, int left, int bottom, int right) = Range();
            StringBuilder text = new StringBuilder();
            for (int r = top; r <= bottom; r++)
            {
                for (int c = left; c <= right; c++)
                {
                    if (c > left) text.Append('\t');
                    text.Append(SheetBook.calc.Value(sheet.page, r, c).Display());
                }
                if (r < bottom) text.Append("\r\n");
            }

            copiedText = text.ToString();
            copiedFrom = (document, path, sheet.page, top, left, bottom, right);
            ClipboardText.Set(copiedText);
            return true;
        }

        public bool Cut()
        {
            if (!Copy()) return false;
            copiedText = null;
            Clear("Cut");
            return true;
        }

        // Formulas reading the copied cells, from the active cell; a plain paste when the clipboard holds something else.
        public bool PasteLink()
        {
            if (editing || sheet == null) return false;

            string? text = ClipboardText.Get();
            if (string.IsNullOrEmpty(text)) return false;
            if (copiedText == null || text != copiedText) return Paste(text);

            (SheetDocument source, string? sourcePath, SheetPage page, int top, int left, int bottom, int right) = copiedFrom;
            if (!ReferenceEquals(source, document) && document.isCsv) return Paste(text);
            string prefix = ReferenceEquals(page, sheet.page) ? ""
                : ReferenceEquals(source, document) ? SheetFormula.Prefix(null, page.name) + "!"
                : SheetFormula.Prefix(sourcePath != null ? SheetBook.FilePart(path, sourcePath) : source.name ?? "", page.name) + "!";

            List<(int, int, string?)> cells = new List<(int, int, string?)>();
            for (int r = top; r <= bottom; r++)
                for (int c = left; c <= right; c++)
                    cells.Add((activeRow + r - top, activeColumn + c - left, "=" + prefix + SheetDocument.Address(r, c)));

            int row = activeRow;
            int column = activeColumn;
            Write("Paste link", cells);
            anchorRow = row;
            anchorColumn = column;
            Select(row + bottom - top, column + right - left, true);
            return true;
        }

        // A note link to the last copied cells while the clipboard still holds them; null otherwise.
        internal static string? CopiedReference(string clipboardText, string? home)
        {
            if (copiedText == null || clipboardText != copiedText || copiedFrom.path == null) return null;
            (_, string path, SheetPage page, int top, int left, int bottom, int right) = copiedFrom;
            return (SheetBook.filePrefix?.Invoke(home, path) ?? "") + SheetLinks.Reference(path, page.name, top, left, bottom, right);
        }

        // Tab-separated rows from the active cell, selected after.
        public bool Paste(string text)
        {
            if (editing || sheet == null) return false;

            string[] rows = text.Replace("\r\n", "\n").Split('\n');
            int count = rows.Length > 1 && rows[^1].Length == 0 ? rows.Length - 1 : rows.Length;

            List<(int, int, string?)> cells = new List<(int, int, string?)>();
            int width = 1;
            for (int r = 0; r < count; r++)
            {
                string[] values = rows[r].Split('\t');
                width = Math.Max(width, values.Length);
                for (int c = 0; c < values.Length; c++)
                    cells.Add((activeRow + r, activeColumn + c, values[c]));
            }

            int row = activeRow;
            int column = activeColumn;
            using (undo.Begin("Paste"))
            {
                SheetPage shown = sheet.page;
                if (document.fixedSize && (row + count > shown.rows || column + width > shown.columns))
                    Record("Paste", new SheetSizeEdit(document, shown, (shown.rows, shown.columns),
                        (Math.Max(shown.rows, row + count), Math.Max(shown.columns, column + width))));
                Write("Paste", cells);
            }
            anchorRow = row;
            anchorColumn = column;
            Select(row + count - 1, column + width - 1, true);
            return true;
        }

        public bool PasteImage(Image<Rgba32> image) => false;
        #endregion

        #region ---- view ----
        // Active cell and anchor in the caret/anchor fields, scroll in ScrollX and TopDelta, page in TopBlock.
        public SessionTab ViewState()
        {
            if (pendingView != null) return pendingView;

            Vector2 scroll = scroller.GetScrollOffset();
            return new SessionTab
            {
                topBlock = pageIndex,
                caretBlock = activeRow,
                caretOffset = activeColumn,
                anchorBlock = anchorRow,
                anchorOffset = anchorColumn,
                scrollX = scroll.X,
                topDelta = scroll.Y
            };
        }

        public void RestoreView(SessionTab view)
        {
            if (sheet != null && view.topBlock > 0 && view.topBlock < document.pages.Count) Build(view.topBlock);
            if (view.caretBlock >= 0)
            {
                anchorRow = Math.Max(0, view.anchorBlock);
                anchorColumn = Math.Max(0, view.anchorOffset);
                activeRow = view.caretBlock;
                activeColumn = Math.Max(0, view.caretOffset);
            }

            pendingView = view;
            sheet?.InvalidateLayout();
            scroller.InvalidateArrange();
        }

        // The grid's viewport; applies a restored view or scrolls the active cell into view.
        private sealed class Scroller : ScrollableControl
        {
            private readonly SheetEditorControl editor;

            public Scroller(SheetEditorControl editor)
            {
                this.editor = editor;
                scrollDirection = ScrollDirection.Both;
                horizontalAlignment = HorizontalAlignment.Stretch;
                heightStar = 1f;
                PaintOr(null, PaletteRole.Surface);
            }

            protected override void ArrangeCore(LayoutRect finalRect)
            {
                base.ArrangeCore(finalRect);
                SheetControl? sheet = editor.sheet;
                if (sheet == null) return;

                Vector2 before = GetScrollOffset();
                if (editor.pendingView != null)
                {
                    SessionTab view = editor.pendingView;
                    editor.pendingView = null;
                    editor.scrollToActivePending = false;
                    SetScrollOffset(new Vector2(view.scrollX, view.topDelta));
                }
                else if (editor.scrollToActivePending)
                {
                    editor.scrollToActivePending = false;
                    LayoutRect cell = sheet.CellRect(editor.activeRow, editor.activeColumn);
                    ScrollIntoView(new LayoutRect(cell.x - sheet.HeaderWidth, cell.y - sheet.HeaderHeight,
                        cell.width + sheet.HeaderWidth, cell.height + sheet.HeaderHeight));
                }

                if (GetScrollOffset() != before) base.ArrangeCore(finalRect);
                else SetFlag(ArrangeFlags.ArrangeDirty, false);
            }
        }
        #endregion
    }
}
