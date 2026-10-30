using ArctisAurora.Core.Editing;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Security;
using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace ArctisAurora.Core.UI.Sheets
{
    // A sheet file: pages of a grid, each page a stack of layers over the same cells.
    public class SheetDocument
    {
        public const string extension = ".sheet.axml";

        public string? name;
        public readonly List<SheetPage> pages = new List<SheetPage>();

        // pages clamped to their rows and columns, grown by hand
        public bool fixedSize;

        // elements this version does not read, written back as they came
        public readonly List<XElement> extra = new List<XElement>();

        // one history per file, whichever tab edits it
        public readonly UndoStack undo = new UndoStack();
        public bool unsaved;

        // the key the file is encrypted under; null for a plain file
        public PasswordKey? encryption;

        // a CSV file, and how it was written, kept for writing it back
        public bool isCsv;
        public char csvDelimiter = ',';
        public bool csvBom;

        public static bool IsSheet(string path) => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

        public static SheetDocument Load(string path, PasswordKey? key = null) => SheetCsv.IsCsv(path) ? SheetCsv.Load(path) : SheetXml.Load(path, key);

        public void Save(string path)
        {
            if (SheetCsv.IsCsv(path)) SheetCsv.Save(this, path);
            else SheetXml.Save(this, path);
        }

        // Takes on another sheet's content whole.
        public void ReplaceAll(SheetDocument from)
        {
            name = from.name;
            fixedSize = from.fixedSize;
            pages.Clear();
            pages.AddRange(from.pages);
            extra.Clear();
            extra.AddRange(from.extra);
            SheetBook.Restructured(this);
        }

        // One page holding one layer.
        public static SheetDocument Blank(string? name, bool fixedSize = false)
        {
            SheetDocument document = new SheetDocument { name = name, fixedSize = fixedSize };
            document.pages.Add(SheetPage.Blank("Sheet 1"));
            return document;
        }

        #region ---- addressing ----
        public static long Key(int row, int column) => ((long)row << 32) | (uint)column;

        public static int RowOf(long key) => (int)(key >> 32);

        public static int ColumnOf(long key) => (int)(key & 0xFFFFFFFFL);

        // 0 → A, 25 → Z, 26 → AA.
        public static string ColumnName(int column)
        {
            StringBuilder name = new StringBuilder();
            for (int c = column + 1; c > 0; c = (c - 1) / 26)
                name.Insert(0, (char)('A' + (c - 1) % 26));
            return name.ToString();
        }

        public static string Address(int row, int column) => ColumnName(column) + (row + 1).ToString(CultureInfo.InvariantCulture);

        // "B3" → row 2, column 1; a bare "B" or "3" reads only its own half.
        public static bool TryParseAddress(string text, out int row, out int column)
        {
            row = -1;
            column = -1;
            if (string.IsNullOrEmpty(text)) return false;

            int i = 0;
            int c = 0;
            for (; i < text.Length && char.IsAsciiLetter(text[i]); i++)
                c = c * 26 + (char.ToUpperInvariant(text[i]) - 'A' + 1);
            if (i > 0) column = c - 1;

            if (i < text.Length)
            {
                if (!int.TryParse(text.AsSpan(i), NumberStyles.None, CultureInfo.InvariantCulture, out int r) || r < 1) return false;
                row = r - 1;
            }

            return row >= 0 || column >= 0;
        }
        #endregion
    }

    // One tab of a sheet file: band sizes shared by its layers.
    public class SheetPage
    {
        public const float defaultColumnWidth = 100f;
        public const float defaultRowHeight = 24f;
        public const int defaultSize = 25;

        public string name = "";

        // grid size on a fixed document
        public int rows = defaultSize;
        public int columns = defaultSize;

        public readonly Dictionary<int, float> columnWidths = new Dictionary<int, float>();
        public readonly Dictionary<int, float> rowHeights = new Dictionary<int, float>();
        public readonly Dictionary<long, SheetFormat> formats = new Dictionary<long, SheetFormat>();
        public readonly List<SheetLayer> layers = new List<SheetLayer>();
        public readonly List<XElement> extra = new List<XElement>();

        public static SheetPage Blank(string name)
        {
            SheetPage page = new SheetPage { name = name };
            page.layers.Add(new SheetLayer { name = "Layer 1" });
            return page;
        }

        #region ---- bands ----
        public float ColumnWidth(int column) => columnWidths.TryGetValue(column, out float w) ? w : defaultColumnWidth;

        public float RowHeight(int row) => rowHeights.TryGetValue(row, out float h) ? h : defaultRowHeight;

        public float ColumnLeft(int column) => Start(column, defaultColumnWidth, columnWidths);

        public float RowTop(int row) => Start(row, defaultRowHeight, rowHeights);

        public int ColumnAt(float x) => IndexAt(x, defaultColumnWidth, columnWidths);

        public int RowAt(float y) => IndexAt(y, defaultRowHeight, rowHeights);

        private static float Start(int index, float size, Dictionary<int, float> custom)
        {
            float start = index * size;
            foreach (KeyValuePair<int, float> band in custom)
                if (band.Key < index) start += band.Value - size;
            return start;
        }

        // The band holding a position: the last one starting at or before it.
        private static int IndexAt(float position, float size, Dictionary<int, float> custom)
        {
            if (position <= 0f) return 0;

            float smallest = size;
            foreach (float value in custom.Values)
                if (value > 0f) smallest = MathF.Min(smallest, value);

            int lo = 0;
            int hi = (int)MathF.Min(int.MaxValue / 2, position / smallest) + 1;
            while (lo < hi)
            {
                int mid = lo + (hi - lo + 1) / 2;
                if (Start(mid, size, custom) <= position) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }
        #endregion

        public SheetFormat Format(int row, int column) =>
            formats.TryGetValue(SheetDocument.Key(row, column), out SheetFormat format) ? format : default;

        // An unformatted cell is absent.
        public void SetFormat(int row, int column, SheetFormat format)
        {
            long key = SheetDocument.Key(row, column);
            if (format == default) formats.Remove(key);
            else formats[key] = format;
        }

        // The text a cell shows: the topmost visible layer holding it.
        public string? Shown(int row, int column)
        {
            long key = SheetDocument.Key(row, column);
            for (int i = layers.Count - 1; i >= 0; i--)
                if (layers[i].visible && layers[i].cells.TryGetValue(key, out SheetCell? cell))
                    return cell.raw;
            return null;
        }

        // One past the last row and column any layer uses.
        public (int rows, int columns) Used()
        {
            int rows = 0;
            int columns = 0;
            foreach (SheetLayer layer in layers)
                foreach (long key in layer.cells.Keys)
                {
                    rows = Math.Max(rows, SheetDocument.RowOf(key) + 1);
                    columns = Math.Max(columns, SheetDocument.ColumnOf(key) + 1);
                }
            return (rows, columns);
        }

        // Grows the size to at least rows × columns.
        public void Extend(int rows, int columns)
        {
            this.rows = Math.Max(this.rows, rows);
            this.columns = Math.Max(this.columns, columns);
        }

        // Moves every row (or column) from at onward by count; a negative count first drops the -count bands at at.
        public void Shift(bool column, int at, int count)
        {
            foreach (SheetLayer layer in layers)
                Shift(layer.cells, column, at, count);
            Shift(formats, column, at, count);

            Dictionary<int, float> bands = column ? columnWidths : rowHeights;
            List<KeyValuePair<int, float>> moved = bands.Where(b => b.Key >= at).ToList();
            foreach (KeyValuePair<int, float> band in moved)
                bands.Remove(band.Key);
            foreach (KeyValuePair<int, float> band in moved)
                if (band.Key >= at - count) bands[band.Key + count] = band.Value;

            if (column) columns = Math.Max(1, columns + count);
            else rows = Math.Max(1, rows + count);
        }

        private static void Shift<T>(Dictionary<long, T> cells, bool column, int at, int count)
        {
            List<KeyValuePair<long, T>> moved = cells
                .Where(c => (column ? SheetDocument.ColumnOf(c.Key) : SheetDocument.RowOf(c.Key)) >= at).ToList();
            foreach (KeyValuePair<long, T> cell in moved)
                cells.Remove(cell.Key);
            foreach (KeyValuePair<long, T> cell in moved)
            {
                int row = SheetDocument.RowOf(cell.Key);
                int col = SheetDocument.ColumnOf(cell.Key);
                if ((column ? col : row) < at - count) continue;
                cells[column ? SheetDocument.Key(row, col + count) : SheetDocument.Key(row + count, col)] = cell.Value;
            }
        }
    }

    // Cells over a page's grid; an empty cell is absent.
    public class SheetLayer
    {
        public string name = "";
        public bool visible = true;
        public readonly Dictionary<long, SheetCell> cells = new Dictionary<long, SheetCell>();
        public readonly List<XElement> extra = new List<XElement>();

        public string? Get(int row, int column) =>
            cells.TryGetValue(SheetDocument.Key(row, column), out SheetCell? cell) ? cell.raw : null;

        public void Set(int row, int column, string? raw)
        {
            long key = SheetDocument.Key(row, column);
            if (string.IsNullOrEmpty(raw)) cells.Remove(key);
            else cells[key] = new SheetCell { raw = raw };
        }
    }

    public sealed class SheetCell
    {
        public string raw = "";
    }

    // How a cell draws; number is a .NET numeric format, null for General.
    public readonly record struct SheetFormat(bool bold, string? fill, string? number);

    [A_XSDType("SheetGrow", "Settings")]
    public class SheetGrowSetting : Setting
    {
        [A_XSDElementProperty("Step", "Settings", "Rows or columns a Shift-click on a fixed sheet's + edge adds.")]
        public int step { get; set; } = 10;
    }

    [A_XSDType("Sheets", "Settings", AllowedChildren = typeof(Setting))]
    public class SheetSettings : SettingCategory
    {
        public readonly SheetGrowSetting grow = new SheetGrowSetting();
    }
}
