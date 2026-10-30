using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Security;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Text;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ArctisAurora.Core.UI.Sheets
{
    // A sheet link laid out for a note: a cell's value on the baseline, or a range's grid from the box's top.
    public sealed class SheetBox
    {
        public float width;
        public float height;
        public bool range;
        public readonly List<SheetBoxCell> cells = new List<SheetBoxCell>();
        public readonly List<LayoutRect> rules = new List<LayoutRect>();
    }

    public readonly record struct SheetBoxCell(string text, LayoutRect rect, float pen, float baseline, bool error);

    // Note links to sheet cells: "file#Page!A1" or "file#Page!A1:B2".
    public static class SheetLinks
    {
        // range geometry, design pixels
        private const float cellInset = 4f;
        private const float ruleWidth = 1f;

        private static readonly Regex xmlLink = new Regex(@"(<Run\b[^>]*?\sSheet="")([^""]*)("")");
        private static readonly Regex mdLink = new Regex(@"(!\[\[)(.*?)(\]\])");
        private static readonly Regex xmlMath = new Regex(@"(<Run\b[^>]*?\sMath="")([^""]*)("")");
        private static readonly Regex mathLink = new Regex(@"\\sheet\{([^}]*)\}");

        public static bool Parse(string reference, out string file, out string page, out int top, out int left, out int bottom, out int right)
        {
            file = page = string.Empty;
            top = left = bottom = right = 0;

            int hash = reference.IndexOf('#');
            int bang = reference.LastIndexOf('!');
            if (hash <= 0 || bang <= hash + 1) return false;

            file = reference[..hash];
            page = reference[(hash + 1)..bang];
            string[] ends = reference[(bang + 1)..].Split(':');
            if (ends.Length > 2 || !SheetDocument.TryParseAddress(ends[0], out top, out left)) return false;
            if (ends.Length == 1)
            {
                (bottom, right) = (top, left);
                return true;
            }
            if (!SheetDocument.TryParseAddress(ends[1], out bottom, out right)) return false;

            (top, bottom) = (Math.Min(top, bottom), Math.Max(top, bottom));
            (left, right) = (Math.Min(left, right), Math.Max(left, right));
            return true;
        }

        public static string Reference(string path, string page, int top, int left, int bottom, int right)
        {
            string reference = Path.GetFileName(path) + "#" + page + "!" + SheetDocument.Address(top, left);
            return top == bottom && left == right ? reference : reference + ":" + SheetDocument.Address(bottom, right);
        }

        // A markdown embed's target is a sheet link when its file part is a sheet.
        public static bool IsLink(string target)
        {
            int hash = target.IndexOf('#');
            return hash > 0 && (SheetDocument.IsSheet(target[..hash].Trim()) || SheetCsv.IsCsv(target[..hash].Trim()));
        }

        #region ---- layout ----
        // Lays a link out in the run's font; zoom scales a range's columns and rows. home is the note holding it.
        public static SheetBox Layout(string? home, string reference, in TextMeasurer.Run run, LineMetrics line, float zoom)
        {
            SheetBox box = new SheetBox();
            if (!Parse(reference, out string file, out string pageName, out int top, out int left, out int bottom, out int right)
                || PageOf(home, file, pageName) is not SheetPage page)
            {
                Inline(box, Missing(home, file), true, run);
                return box;
            }

            if (top == bottom && left == right)
            {
                SheetValue value = SheetBook.calc.Value(page, top, left);
                Inline(box, value.Display(page.Format(top, left).number) ?? string.Empty, value.kind == SheetValueKind.Error, run);
                return box;
            }

            box.range = true;
            float inset = cellInset * zoom;
            float textHeight = (line.ascent + line.descent) * run.fontSize;

            float y = 0f;
            for (int r = top; r <= bottom; r++)
            {
                float height = page.RowHeight(r) * zoom;
                float baseline = y + (height - textHeight) * 0.5f + line.ascent * run.fontSize;
                float x = 0f;
                for (int c = left; c <= right; c++)
                {
                    float width = page.ColumnWidth(c) * zoom;
                    SheetValue value = SheetBook.calc.Value(page, r, c);
                    string? text = value.Display(page.Format(r, c).number);
                    if (!string.IsNullOrEmpty(text))
                    {
                        float advance = Advance(text, run);
                        float pen = value.kind == SheetValueKind.Number && advance <= width - inset * 2f
                            ? x + width - inset - advance
                            : x + inset;
                        box.cells.Add(new SheetBoxCell(text, new LayoutRect(x, y, width, height), pen, baseline,
                                                       value.kind == SheetValueKind.Error));
                    }
                    x += width;
                }
                box.width = x;
                y += height;
            }
            box.height = y;

            float ruleX = 0f;
            for (int c = left; c <= right; c++)
            {
                box.rules.Add(new LayoutRect(ruleX, 0f, ruleWidth, box.height));
                ruleX += page.ColumnWidth(c) * zoom;
            }
            box.rules.Add(new LayoutRect(box.width - ruleWidth, 0f, ruleWidth, box.height));

            float ruleY = 0f;
            for (int r = top; r <= bottom; r++)
            {
                box.rules.Add(new LayoutRect(0f, ruleY, box.width, ruleWidth));
                ruleY += page.RowHeight(r) * zoom;
            }
            box.rules.Add(new LayoutRect(0f, box.height - ruleWidth, box.width, ruleWidth));
            return box;
        }

        private static void Inline(SheetBox box, string text, bool error, in TextMeasurer.Run run)
        {
            box.width = Advance(text, run);
            if (text.Length > 0) box.cells.Add(new SheetBoxCell(text, LayoutRect.Empty, 0f, 0f, error));
        }

        private static float Advance(string text, in TextMeasurer.Run run)
        {
            float advance = 0f;
            for (int i = 0; i < text.Length; i++)
                advance += TextMeasurer.MeasureAdvance(text[i], run) + (i + 1 < text.Length ? TextMeasurer.Kern(text[i], text[i + 1], run) : 0f);
            return advance;
        }

        private static string Missing(string? home, string? file) =>
            string.IsNullOrEmpty(file) ? SheetFormula.reference
            : SheetBook.vaultMissing?.Invoke(home, file) == true ? SheetFormula.vault
            : SheetBook.IsLocked(home, file) ? SheetFormula.locked : SheetFormula.reference;

        private static SheetPage? PageOf(string? home, string file, string name)
        {
            SheetDocument? document = SheetBook.Resolve(home, file);
            if (document == null) return null;
            foreach (SheetPage page in document.pages)
                if (page.name.Equals(name, StringComparison.OrdinalIgnoreCase)) return page;
            return null;
        }

        // The link's values as plain text: one value, or rows of tab-separated values.
        public static string Plain(string? home, string reference)
        {
            if (!Parse(reference, out string file, out string pageName, out int top, out int left, out int bottom, out int right)
                || PageOf(home, file, pageName) is not SheetPage page)
                return Missing(home, file);

            StringBuilder text = new StringBuilder();
            for (int r = top; r <= bottom; r++)
            {
                if (r > top) text.Append(Environment.NewLine);
                for (int c = left; c <= right; c++)
                {
                    if (c > left) text.Append('\t');
                    text.Append(SheetBook.calc.Value(page, r, c).Display());
                }
            }
            return text.ToString();
        }
        #endregion

        #region ---- math ----
        public static bool HasMathLinks(string? source) => source != null && source.Contains(@"\sheet{", StringComparison.Ordinal);

        // A formula with each \sheet{…} replaced by the cell's value as TeX.
        public static string ExpandMath(string? home, string source) => mathLink.Replace(source, match => MathValue(home, match.Groups[1].Value));

        private static string MathValue(string? home, string reference)
        {
            if (!Parse(reference, out string file, out string pageName, out int top, out int left, out int bottom, out int right)
                || PageOf(home, file, pageName) is not SheetPage page)
                return @"\text{" + Missing(home, file) + "}";
            if (top != bottom || left != right) return @"\text{" + SheetFormula.value + "}";

            SheetValue value = SheetBook.calc.Value(page, top, left);
            if (value.kind == SheetValueKind.Number) return "{" + value.Display() + "}";
            string text = (value.Display() ?? string.Empty).Replace("\\", string.Empty).Replace("{", string.Empty).Replace("}", string.Empty);
            return @"\text{" + text + "}";
        }

        public static IEnumerable<string> MathReferences(string source) => mathLink.Matches(source).Select(match => match.Groups[1].Value);

        // A formula with the \sheet{…} references the rename answers for rewritten.
        public static string RenameMath(string source, Func<string, string?> rename) =>
            mathLink.Replace(source, match => rename(match.Groups[1].Value) is string renamed ? @"\sheet{" + renamed + "}" : match.Value);
        #endregion

        #region ---- rename ----
        // Rewrites every link to a renamed sheet: in open notes, and in each note file that holds one.
        public static void Renamed(string oldPath, string newPath, IEnumerable<string> notes)
        {
            oldPath = Path.GetFullPath(oldPath);
            newPath = Path.GetFullPath(newPath);
            string oldName = SheetBook.BaseName(oldPath);
            string newName = SheetBook.BaseName(newPath);
            bool pageFollows = SheetCsv.IsCsv(oldPath) && SheetCsv.IsCsv(newPath);

            string? Rename(string note, string reference)
            {
                int hash = reference.IndexOf('#');
                int bang = reference.LastIndexOf('!');
                if (hash <= 0 || !SheetBook.Points(note, reference[..hash], oldPath)) return null;
                string file = SheetBook.Renamed(reference[..hash], oldPath, newPath);
                if (pageFollows && bang > hash && reference[(hash + 1)..bang].Equals(oldName, StringComparison.OrdinalIgnoreCase))
                    return file + "#" + newName + reference[bang..];
                return file + reference[hash..];
            }

            RewriteNotes(notes, Rename);
        }

        // Links to a page of the sheet at sheetPath follow its new name.
        public static void PageRenamed(string sheetPath, string oldName, string newName, IEnumerable<string> notes)
        {
            sheetPath = Path.GetFullPath(sheetPath);

            string? Rename(string note, string reference)
            {
                int hash = reference.IndexOf('#');
                int bang = reference.LastIndexOf('!');
                if (hash <= 0 || bang <= hash || !SheetBook.Points(note, reference[..hash], sheetPath)) return null;
                if (!reference[(hash + 1)..bang].Equals(oldName, StringComparison.OrdinalIgnoreCase)) return null;
                return reference[..(hash + 1)] + newName + reference[bang..];
            }

            RewriteNotes(notes, Rename);
        }

        // Links into a page of the sheet at sheetPath follow its rows or columns that moved, as SheetFormula.ShiftCells moves them.
        public static void Shifted(string sheetPath, string page, bool column, int at, int count, IEnumerable<string> notes)
        {
            sheetPath = Path.GetFullPath(sheetPath);
            int from = count < 0 ? at - count : at;

            string? Shift(string note, string reference)
            {
                int hash = reference.IndexOf('#');
                int bang = reference.LastIndexOf('!');
                if (hash <= 0 || bang <= hash || !SheetBook.Points(note, reference[..hash], sheetPath)) return null;
                if (!reference[(hash + 1)..bang].Equals(page, StringComparison.OrdinalIgnoreCase)) return null;

                string[] ends = reference[(bang + 1)..].Split(':');
                bool any = false;
                for (int k = 0; k < ends.Length; k++)
                {
                    if (!SheetDocument.TryParseAddress(ends[k], out int row, out int col) || row < 0 || col < 0) return null;
                    if ((column ? col : row) < from) continue;
                    ends[k] = column ? SheetDocument.Address(row, col + count) : SheetDocument.Address(row + count, col);
                    any = true;
                }
                return any ? reference[..(bang + 1)] + string.Join(":", ends) : null;
            }

            RewriteNotes(notes, Shift);
        }

        private static void RewriteNotes(IEnumerable<string> notes, Func<string, string, string?> rename)
        {
            foreach (string note in notes)
            {
                Func<string, string?> inNote = reference => rename(note, reference);
                if (TabViewControl.FindOpenDocument(note, out _) is TabItemControl item)
                    TabViewControl.EditorOf(item)?.RenameSheetLinks(inNote);
                RewriteFile(note, inNote);
            }
        }

        // Changes only the link text; every other byte, the BOM included, is written back as read.
        private static void RewriteFile(string path, Func<string, string?> rename)
        {
            if (!File.Exists(path)) return;
            if (EncryptedFile.IsEncrypted(path))
            {
                if (TabViewControl.FindOpenDocument(path, out _) is not TabItemControl open || TabViewControl.EditorOf(open) == null)
                    UnlockPrompts.Ask(path, "Update sheet links in", key =>
                    {
                        if (EncryptedFile.TryRead(path, key, out byte[] plain) && Rewritten(plain, false, rename) is byte[] changed)
                            EncryptedFile.Write(path, key, changed);
                    });
                return;
            }

            byte[] bytes = VaultFiles.ReadBytes(path);
            if (Rewritten(bytes, FileTypes.KindOf(path) == FileKind.Markdown, rename) is not byte[] rewrittenBytes) return;
            VaultFiles.WriteBytes(path, rewrittenBytes);
        }

        // The file's bytes with its links renamed, BOM kept; null when nothing changed.
        private static byte[]? Rewritten(byte[] bytes, bool markdown, Func<string, string?> rename)
        {
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            int skip = bom ? 3 : 0;
            string text = Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip);

            bool any = false;
            string rewritten = (markdown ? mdLink : xmlLink).Replace(text, match =>
            {
                string raw = match.Groups[2].Value;
                string reference = markdown ? raw : XElement.Parse($"<a v=\"{raw}\"/>").Attribute("v")!.Value;
                if (markdown && !IsLink(reference)) return match.Value;

                string? renamed = rename(reference);
                if (renamed == null) return match.Value;

                any = true;
                return match.Groups[1].Value + (markdown ? renamed : Escaped(renamed)) + match.Groups[3].Value;
            });

            if (markdown)
            {
                string math = RenameMath(rewritten, rename);
                any |= math != rewritten;
                rewritten = math;
            }
            else
                rewritten = xmlMath.Replace(rewritten, match =>
                {
                    string source = XElement.Parse($"<a v=\"{match.Groups[2].Value}\"/>").Attribute("v")!.Value;
                    string renamed = RenameMath(source, rename);
                    if (renamed == source) return match.Value;

                    any = true;
                    return match.Groups[1].Value + Escaped(renamed) + match.Groups[3].Value;
                });
            if (!any) return null;

            byte[] body = Encoding.UTF8.GetBytes(rewritten);
            return bom ? [.. bytes.AsSpan(0, 3), .. body] : body;
        }

        // An attribute value as XML writes it, line breaks included.
        private static string Escaped(string value) => new XAttribute("v", value).ToString()[3..^1];
        #endregion
    }
}
