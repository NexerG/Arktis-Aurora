using ArctisAurora.Core.Filing;
using System.Text;

namespace ArctisAurora.Core.UI.Sheets
{
    // Delimited text to rows of fields and back, quoted as RFC 4180 has it.
    public static class SheetCsv
    {
        public const string extension = ".csv";

        private static readonly char[] delimiters = { ',', ';', '\t' };

        public static bool IsCsv(string path) => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

        // The delimiter the first record uses most among comma, semicolon and tab; comma on a tie or none.
        public static char Delimiter(string text)
        {
            int[] counts = new int[delimiters.Length];
            bool quoted = false;
            foreach (char c in text)
            {
                if (c == '"') quoted = !quoted;
                else if (!quoted && (c == '\r' || c == '\n')) break;
                else if (!quoted)
                    for (int d = 0; d < delimiters.Length; d++)
                        if (c == delimiters[d]) counts[d]++;
            }

            int best = 0;
            for (int d = 1; d < delimiters.Length; d++)
                if (counts[d] > counts[best]) best = d;
            return delimiters[best];
        }

        public static List<List<string>> Read(string text, char delimiter)
        {
            List<List<string>> rows = new List<List<string>>();
            List<string> row = new List<string>();
            StringBuilder field = new StringBuilder();
            int i = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
            bool any = false;

            while (i < text.Length)
            {
                char c = text[i];
                if (c == '"' && field.Length == 0)
                {
                    for (i++; i < text.Length; i++)
                    {
                        if (text[i] != '"') field.Append(text[i]);
                        else if (i + 1 < text.Length && text[i + 1] == '"') field.Append(text[++i]);
                        else break;
                    }
                    i++;
                    any = true;
                    continue;
                }

                if (c == delimiter)
                {
                    row.Add(field.ToString());
                    field.Clear();
                    any = true;
                }
                else if (c == '\r' || c == '\n')
                {
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = new List<string>();
                    any = false;
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                }
                else
                {
                    field.Append(c);
                    any = true;
                }
                i++;
            }

            if (any)
            {
                row.Add(field.ToString());
                rows.Add(row);
            }
            return rows;
        }

        // CRLF after every record; a field holding the delimiter, a quote, a line break or edge spaces is quoted.
        public static string Write(IEnumerable<IReadOnlyList<string>> rows, char delimiter)
        {
            StringBuilder text = new StringBuilder();
            foreach (IReadOnlyList<string> row in rows)
            {
                for (int c = 0; c < row.Count; c++)
                {
                    if (c > 0) text.Append(delimiter);
                    string field = row[c];
                    bool quote = field.IndexOfAny(new[] { delimiter, '"', '\r', '\n' }) >= 0
                        || field.Length > 0 && (field[0] == ' ' || field[^1] == ' ');
                    if (quote) text.Append('"').Append(field.Replace("\"", "\"\"")).Append('"');
                    else text.Append(field);
                }
                text.Append("\r\n");
            }
            return text.ToString();
        }

        // A CSV file as one page of one layer, named after the file; cells hold the text as read.
        public static SheetDocument Load(string path)
        {
            byte[] bytes = VaultFiles.ReadBytes(path);
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            string text = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            char delimiter = Delimiter(text);

            string name = Path.GetFileNameWithoutExtension(path);
            SheetDocument document = new SheetDocument { name = name, isCsv = true, csvDelimiter = delimiter, csvBom = bom };
            SheetPage page = SheetPage.Blank(name);
            document.pages.Add(page);

            List<List<string>> rows = Read(text, delimiter);
            for (int r = 0; r < rows.Count; r++)
                for (int c = 0; c < rows[r].Count; c++)
                    page.layers[0].Set(r, c, rows[r][c]);
            return document;
        }

        // The first page's shown text, formulas as written, in the delimiter and BOM the file was read with.
        public static void Save(SheetDocument document, string path)
        {
            SheetPage page = document.pages[0];
            (int rows, int columns) = page.Used();
            List<IReadOnlyList<string>> lines = new List<IReadOnlyList<string>>();
            for (int r = 0; r < rows; r++)
            {
                string[] line = new string[columns];
                for (int c = 0; c < columns; c++)
                    line[c] = page.Shown(r, c) ?? string.Empty;
                lines.Add(line);
            }
            VaultFiles.WriteText(path, Write(lines, document.csvDelimiter), new UTF8Encoding(document.csvBom));
        }

        // The page's used range as the values it shows, numbers unformatted; UTF-8 with a BOM, comma-separated.
        public static void Export(SheetPage page, string path)
        {
            (int rows, int columns) = page.Used();
            List<IReadOnlyList<string>> lines = new List<IReadOnlyList<string>>();
            for (int r = 0; r < rows; r++)
            {
                string[] line = new string[columns];
                for (int c = 0; c < columns; c++)
                    line[c] = SheetBook.calc.Value(page, r, c).Display() ?? string.Empty;
                lines.Add(line);
            }
            File.WriteAllText(path, Write(lines, ','), new UTF8Encoding(true));
        }
    }
}
