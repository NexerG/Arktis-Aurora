using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Menus;
using ArctisAurora.Core.UI.Text;
using System.Numerics;

namespace ArctisAurora.Core.UI.Notes
{
    // The note names, or after # the target's headings, that complete a [[ being typed. Keys reach it through
    // the text actions, so the caret keeps the focus.
    public sealed class LinkSuggest
    {
        // list sizing
        private const int maxRows = 8;
        private const float width = 260f;
        private const float rowHeight = 24f;
        private const float gap = 4f;
        private const int captionSize = 13;

        // How the host lists the note names a [[ in the note at home can complete to.
        public static Func<string?, string[]>? linkNames;

        // the open list; null when none is
        internal static LinkSuggest? open;

        // heading lists read since launch
        public static int headingLoads { get; private set; }

        // what the open list shows
        public static bool IsOpen => open != null;
        public static int ShownCount => open?.shown.Count ?? 0;
        public static string? ShownAt(int row) => open != null && row < open.shown.Count ? open.shown[row] : null;

        private readonly DocumentControl document;
        private readonly BlockControl block;
        private readonly int bracket;
        private readonly string[] names;

        // the name a heading list was read for, and its headings
        private string? headingsOf;
        private string[] headings = Array.Empty<string>();
        private bool inHeading;

        // what the rows show, and the row Enter takes
        private readonly List<string> shown = new List<string>(maxRows);
        private readonly StackPanelControl list;
        private readonly ButtonControl[] rows = new ButtonControl[maxRows];
        private readonly LabelControl[] captions = new LabelControl[maxRows];
        private int highlight;

        private LinkSuggest(DocumentControl document, int bracket)
        {
            this.document = document;
            block = document.caretBlock;
            this.bracket = bracket;
            names = linkNames?.Invoke(document.session?.path) ?? Array.Empty<string>();

            list = new StackPanelControl { orientation = StackPanelControl.Orientation.Vertical, preferredWidth = width };
            for (int i = 0; i < maxRows; i++)
            {
                int row = i;
                captions[i] = new LabelControl { text = string.Empty, fontSize = captionSize, preferredHeight = rowHeight, horizontalPosition = 0f };
                rows[i] = new ButtonControl { preferredHeight = rowHeight, role = PaletteRole.Surface, cornerRole = CornerRole.Control };
                rows[i].AddChild(captions[i]);
                rows[i].RegisterOnRelease(_ =>
                {
                    Take(row);
                    return true;
                });
                list.AddChild(rows[i]);
            }
        }

        #region ---- opening ----
        // After a typed character: refilters the open list, or opens one when the caret follows an unclosed [[.
        internal static void Typed(DocumentControl document)
        {
            if (open != null && open.document == document)
            {
                open.Refresh();
                return;
            }

            int bracket = Bracket(document);
            if (bracket < 0 || document.caretBlock == null) return;

            LinkSuggest suggest = new LinkSuggest(document, bracket);
            if (!suggest.Filter()) return;
            suggest.Show();
        }

        // After the caret moved: refilters, or closes once the caret left the [[.
        internal static void CaretMoved(DocumentControl document)
        {
            if (open != null && open.document == document) open.Refresh();
        }

        private void Show()
        {
            open = this;
            Paint();
            LayoutRect anchor = document.MathAnchor(document.AddressOf(block, bracket));
            ContextMenus.Open(new List<ContextMenuEntry> { new ContextMenuContent(list) }, Editor,
                new Vector2(anchor.x, anchor.Bottom + gap), width, onClosed: Closed);
        }

        private void Refresh()
        {
            if (document.caretBlock != block || Bracket(document) != bracket || !Filter())
            {
                Close();
                return;
            }
            Paint();
        }

        private void Close()
        {
            if (open != this) return;
            open = null;
            ContextMenus.Close();
        }

        private void Closed()
        {
            if (open == this) open = null;
        }
        #endregion

        #region ---- keys ----
        // Moves the highlight; false when no list is open, so the caret moves instead.
        internal static bool Move(int delta)
        {
            if (open == null) return false;

            int count = open.shown.Count;
            open.highlight = (open.highlight + delta + count) % count;
            open.Paint();
            return true;
        }

        // Takes the highlighted row; false when no list is open.
        internal static bool Accept()
        {
            if (open == null) return false;
            open.Take(open.highlight);
            return true;
        }

        // Closes the list and keeps what was typed; false when none is open.
        internal static bool Dismiss()
        {
            if (open == null) return false;
            open.Close();
            return true;
        }
        #endregion

        // Replaces the [[ and what follows it up to the caret with the row's link, as one step.
        private void Take(int row)
        {
            if (row >= shown.Count) return;

            string target = inHeading ? headingsOf + "#" + shown[row] : shown[row];
            int to = document.caretOffset;
            int index = document.AddressOf(block, 0).block;
            DocumentEditorControl? editor = Editor;
            Close();

            using (editor?.BeginStep("Insert link") ?? default)
                document.ReplaceWithLink(block, index, bracket, to, target, target);
            editor?.MarkDirty();
            editor?.FocusCaret();
        }

        // Fills shown from the names, or the target's headings past a #; false when nothing matches.
        private bool Filter()
        {
            string s = block.text ?? string.Empty;
            string query = s[(bracket + 2)..document.caretOffset];
            int hash = query.IndexOf('#');
            inHeading = hash >= 0;

            shown.Clear();
            if (!inHeading) Matching(names, query);
            else
            {
                string name = query[..hash].Trim();
                if (name != headingsOf)
                {
                    headingsOf = name;
                    headings = ReadHeadings(name);
                }
                Matching(headings, query[(hash + 1)..]);
            }

            if (shown.Count == 0) return false;
            highlight = Math.Min(highlight, shown.Count - 1);
            return true;
        }

        private string[] ReadHeadings(string name)
        {
            headingLoads++;
            string? home = document.session?.path;
            string? path = name.Length == 0 ? home : LinkIndex.findNote?.Invoke(home, name);
            return path == null ? Array.Empty<string>() : NoteHeadings.Of(path);
        }

        // Prefix matches first, then the rest that contain the query, up to the row count.
        private void Matching(string[] source, string query)
        {
            query = query.Trim();
            for (int i = 0; i < source.Length && shown.Count < maxRows; i++)
                if (source[i].StartsWith(query, StringComparison.OrdinalIgnoreCase)) shown.Add(source[i]);
            for (int i = 0; i < source.Length && shown.Count < maxRows; i++)
                if (!source[i].StartsWith(query, StringComparison.OrdinalIgnoreCase)
                    && source[i].Contains(query, StringComparison.OrdinalIgnoreCase)) shown.Add(source[i]);
        }

        private void Paint()
        {
            for (int i = 0; i < maxRows; i++)
            {
                if (i >= shown.Count)
                {
                    rows[i].Hide();
                    continue;
                }
                captions[i].text = shown[i];
                rows[i].role = i == highlight ? PaletteRole.SubField : PaletteRole.Surface;
                rows[i].Show();
            }
        }

        private DocumentEditorControl? Editor => document.parent as DocumentEditorControl;

        // Where the unclosed [[ before the caret starts; -1 when the caret follows none.
        private static int Bracket(DocumentControl document)
        {
            BlockControl? block = document.caretBlock;
            int caret = document.caretOffset;
            if (block == null || caret < 2) return -1;

            string s = block.text ?? string.Empty;
            int bracket = s.LastIndexOf("[[", caret - 1, StringComparison.Ordinal);
            if (bracket < 0) return -1;

            for (int i = bracket + 2; i < caret; i++)
                if (s[i] is '[' or ']' or '|' or '\n' || s[i] == BlockControl.PictureChar[0]) return -1;
            return bracket;
        }
    }
}
