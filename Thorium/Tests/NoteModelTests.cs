using ArctisAurora.Core.Data;
using ArctisAurora.Core.Editing;
using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Containers.Workspace;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Planner;
using ArctisAurora.Core.UI.Sheets;
using ArctisAurora.EngineWork;
using System.Xml.Linq;
using Thorium.Editor.CustomControls;

namespace Thorium.Tests
{
    internal static class NoteModelTests
    {
        [A_XSDActionDependency("NoteModel.SaveIsStable", "Test")]
        private static IEnumerator<int> SaveIsStable(TestContext t)
        {
            string notes = Path.GetFullPath(Paths.Doc("../../Notes"));
            List<(string name, string extension, string text)> sources = !Directory.Exists(notes) ? new() : Directory
                .EnumerateFiles(notes, "*", SearchOption.AllDirectories)
                .Where(path => RichTextDocument.Opens(path)
                    && !SheetDocument.IsSheet(path) && !PlannerDocument.IsPlanner(path))
                .Select(path => (Path.GetRelativePath(notes, path), Path.GetExtension(path).ToLowerInvariant(), File.ReadAllText(path)))
                .ToList();

            sources.Add(("everything.axml", ".axml", Everything.ToString()));
            sources.Add(("plain.txt", ".txt", "first line\n\nthird line"));
            sources.Add(("source.tex", ".tex", "\\documentclass{article}\n\\begin{document}\nx\n\\end{document}\n"));

            foreach ((string name, string extension, string text) in sources)
            {
                string once = RoundTrip(text, extension, name);
                string twice = RoundTrip(once, extension, name);
                t.Check(once == twice, $"{name} saves the same after a reload");
            }

            yield break;
        }

        [A_XSDActionDependency("NoteModel.LoadHeadless", "Test")]
        private static IEnumerator<int> LoadHeadless(TestContext t)
        {
            DataPool controls = DataManager.Get("UIElements");
            int before = controls.Count;
            RichTextDocument document = DocumentXml.Parse(Everything);
            t.Check(controls.Count == before, $"parsing a note with blocks, tables and inserts builds no control ({controls.Count - before} rows)");
            t.Check(document.blocks.OfType<NoteTable>().Count() == 2 && document.blocks.OfType<NoteBlock>().Count() == 9,
                "the model holds the note's tables and blocks");
            yield break;
        }

        [A_XSDActionDependency("NoteModel.Changes", "Test")]
        private static IEnumerator<int> Changes(TestContext t)
        {
            RichTextDocument document = DocumentXml.Parse(Small);
            List<NoteChange> seen = new List<NoteChange>();
            document.changed += seen.Add;

            bool Raised(string what, params (NoteChangeKind kind, int first, int count)[] expected)
            {
                bool same = seen.Count == expected.Length;
                for (int i = 0; same && i < expected.Length; i++)
                    same = seen[i].kind == expected[i].kind && seen[i].first == expected[i].first && seen[i].count == expected[i].count;
                t.Check(same, $"{what} raises {string.Join(", ", expected.Select(e => $"{e.kind} {e.first}+{e.count}"))} " +
                    $"(got {string.Join(", ", seen.Select(c => $"{c.kind} {c.first}+{c.count}"))})");
                return same;
            }

            bool CaretAt(int block, int offset) => seen[^1].caret is DocumentAddress c && c.block == block && c.offset == offset;

            string Text(int block) => document.Blocks()[block].run.text;

            seen.Clear();
            document.InsertText(new DocumentAddress(0, 3), "!");
            if (Raised("typing", (NoteChangeKind.Text, 0, 1)))
                t.Check(seen[0].length == 1 && CaretAt(0, 4) && Text(0) == "one!", "typing carries its length and leaves the caret after it");

            seen.Clear();
            document.RemoveText(new DocumentAddress(0, 3), 1);
            if (Raised("a removal", (NoteChangeKind.Text, 0, 1)))
                t.Check(seen[0].length == -1 && CaretAt(0, 3) && Text(0) == "one", "a removal carries a negative length");

            seen.Clear();
            document.SplitBlockAt(new DocumentAddress(0, 1));
            if (Raised("a split", (NoteChangeKind.Spans, 0, 1), (NoteChangeKind.Inserted, 1, 1)))
                t.Check(CaretAt(1, 0) && Text(0) == "o" && Text(1) == "ne", "a split leaves the caret at the tail's start");

            seen.Clear();
            document.JoinBlockWithNext(new DocumentAddress(0, 1));
            if (Raised("a join", (NoteChangeKind.Removed, 1, 1), (NoteChangeKind.Kind, 0, 1)))
                t.Check(CaretAt(0, 1) && Text(0) == "one", "a join puts the text back");

            seen.Clear();
            document.InsertText(new DocumentAddress(3, 4), "!");
            if (Raised("typing in a cell", (NoteChangeKind.Text, 3, 1)))
                t.Check(((NoteTable)document.blocks[3]).rows[0][0].blocks[0].run.text == "cell!", "a cell's block is addressed after the note's blocks");

            seen.Clear();
            document.ChangeBlocks(1, 2, b => b.alignment = TextAlignment.Center);
            Raised("a block change", (NoteChangeKind.Kind, 1, 2));

            seen.Clear();
            document.ApplyStyleBetween(new DocumentAddress(0, 0), new DocumentAddress(1, 2), new StyleDelta(bold: true));
            if (Raised("a restyle", (NoteChangeKind.Spans, 0, 2)))
                t.Check(seen[0].anchor is DocumentAddress a && a.block == 0 && a.offset == 0 && CaretAt(1, 2), "a restyle leaves its range selected");

            seen.Clear();
            DocumentFragment cut = document.CaptureFragment(new DocumentAddress(0, 1), new DocumentAddress(2, 2));
            document.DeleteBetween(new DocumentAddress(0, 1), new DocumentAddress(2, 2));
            if (Raised("a delete across blocks", (NoteChangeKind.Removed, 1, 2), (NoteChangeKind.Kind, 0, 1)))
                t.Check(CaretAt(0, 1) && Text(0) == "oree", "a delete across blocks merges the head and the tail");

            seen.Clear();
            document.InsertFragment(new DocumentAddress(0, 1), cut);
            if (Raised("putting a fragment back", (NoteChangeKind.Inserted, 1, 2), (NoteChangeKind.Kind, 0, 1)))
                t.Check(Text(0) == "one" && Text(1) == "two" && Text(2) == "three", "the fragment's blocks come back");

            seen.Clear();
            document.PutTable(3, true, null);
            if (Raised("a table removed", (NoteChangeKind.Table, 3, 1)))
                t.Check(document.blocks.Length == 3, "the table leaves the model");

            seen.Clear();
            document.SetPage(null);
            document.SetPalette(null);
            document.SetReadOnly(true);
            Raised("note setters", (NoteChangeKind.Page, 0, 1), (NoteChangeKind.Palette, 0, 1), (NoteChangeKind.ReadOnly, 0, 1));
            yield break;
        }

        [A_XSDActionDependency("NoteModel.EditHeadless", "Test")]
        private static IEnumerator<int> EditHeadless(TestContext t)
        {
            DataPool controls = DataManager.Get("UIElements");
            int before = controls.Count;
            RichTextDocument document = DocumentXml.Parse(Small);
            UndoStack undo = new UndoStack();
            string original = DocumentXml.ToXml(document).ToString();

            DocumentAddress typed = new DocumentAddress(1, 3);
            using (undo.Begin("Type"))
            {
                undo.Push(new TextEdit(document, typed, "x", true));
                document.InsertText(typed, "x");
            }

            DocumentAddress split = new DocumentAddress(0, 2);
            using (undo.Begin("Split"))
            {
                document.SplitBlockAt(split);
                undo.Push(new SplitEdit(document, split));
            }

            DocumentAddress from = new DocumentAddress(1, 0);
            DocumentAddress to = new DocumentAddress(3, 2);
            using (undo.Begin("Delete"))
            {
                DocumentFragment fragment = document.CaptureFragment(from, to);
                undo.Push(new DeleteRangeEdit(document, from, to, fragment, from, to));
                document.DeleteBetween(from, to);
            }

            string edited = DocumentXml.ToXml(document).ToString();
            t.Check(edited != original, "the edits change the note");

            while (undo.Undo()) { }
            t.Check(DocumentXml.ToXml(document).ToString() == original, "undoing every edit with no view gives the note back as loaded");

            while (undo.Redo()) { }
            t.Check(DocumentXml.ToXml(document).ToString() == edited, "redoing every edit gives the edited note again");
            t.Check(controls.Count == before, $"editing a note with no view builds no control ({controls.Count - before} rows)");
            yield break;
        }

        [A_XSDActionDependency("NoteModel.TwoViews", "Test")]
        private static IEnumerator<int> TwoViews(TestContext t)
        {
            string path = Fixture("alpha", "beta", "gamma");
            DocumentEditorControl a = Pane();
            DocumentEditorControl b = Pane();
            StackPanelControl both = new StackPanelControl
            {
                orientation = StackPanelControl.Orientation.Vertical,
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            both.AddChild(a);
            both.AddChild(b);
            t.Show(both);
            a.LoadPath(path);
            b.LoadPath(path);
            File.Delete(path);
            yield return 2;

            DocumentEditSession session = a.session;
            RichTextDocument document = session.document;
            t.Check(ReferenceEquals(b.session, session) && session.views == 2, "two views of one path share one session");

            Content(b).SetCaret(Blocks(b)[1], 2);
            Content(a).SetCaret(Blocks(a)[0], 5);
            a.FocusCaret();
            yield return 1;
            yield return t.Type("XY");
            t.Check(Blocks(b)[0].text == "alphaXY", $"typing in A shows in B: {Blocks(b)[0].text}");
            t.Check(Caret(b) == (1, 2) && Tail(b) == "ta", $"B's caret stays where it was: {Caret(b)}");

            document.InsertText(new DocumentAddress(1, 0), "<");
            t.Check(Caret(b) == (1, 3) && Tail(b) == "ta", $"an insert before B's caret in its block shifts it: {Caret(b)}");

            document.InsertText(new DocumentAddress(1, 3), ">");
            t.Check(Caret(b) == (1, 3) && Tail(b) == ">ta", $"an insert at B's caret leaves the caret in front of it: {Caret(b)}");

            document.SplitBlockAt(new DocumentAddress(0, 2));
            t.Check(Caret(b) == (2, 3) && Tail(b) == ">ta", $"a split above moves B's caret down a block: {Caret(b)}");

            document.JoinBlockWithNext(new DocumentAddress(0, 2));
            t.Check(Caret(b) == (1, 3) && Tail(b) == ">ta", $"a join above moves it back: {Caret(b)}");

            document.SplitBlockAt(new DocumentAddress(1, 1));
            t.Check(Caret(b) == (2, 2) && Tail(b) == ">ta", $"a split before B's caret carries it into the tail: {Caret(b)}");

            document.JoinBlockWithNext(new DocumentAddress(1, 1));
            t.Check(Caret(b) == (1, 3) && Tail(b) == ">ta", $"a join brings it back into the head: {Caret(b)}");

            DocumentFragment pasted = document.CaptureFragment(new DocumentAddress(0, 0), new DocumentAddress(1, 1));
            document.InsertFragment(new DocumentAddress(1, 1), pasted);
            t.Check(Caret(b) == (2, 3) && Tail(b) == ">ta", $"a paste before B's caret carries it into the last pasted block: {Caret(b)}");

            Content(a).SetCaret(Blocks(a)[0], 7);
            b.FocusCaret();
            yield return 1;
            yield return t.Key(Keys.Z, Keys.LeftControl);
            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(Blocks(a)[0].text == "alpha", $"undo in B reverts A's typing: {Blocks(a)[0].text}");
            t.Check(Caret(a) == (0, 5), $"and A's caret follows the text back: {Caret(a)}");

            a.FocusCaret();
            yield return 1;
            Content(b).SetCaret(Blocks(b)[2], 2);
            document.DeleteBetween(new DocumentAddress(1, 1), new DocumentAddress(3, 1));
            t.Check(Caret(b) == (1, 1), $"deleting B's caret block clamps its caret to the cut: {Caret(b)}");

            a.Destroy();
            yield return 1;
            document.InsertText(new DocumentAddress(0, 0), "!");
            t.Check(session.views == 1 && Blocks(b)[0].text == "!alpha", "closing A keeps B on the note");

            t.Show(new StackPanelControl());
            yield return 1;
            t.Check(session.views == 0, "closing both releases the session");
        }

        [A_XSDActionDependency("NoteModel.RestoreTwice", "Test")]
        private static IEnumerator<int> RestoreTwice(TestContext t)
        {
            string path = Fixture("alpha", "beta", "gamma");
            TabItemControl first = SessionLayout.tabFactory(path, false);
            TabItemControl second = SessionLayout.tabFactory(path, false);
            TabViewControl.FileEditorOf(first).RestoreView(new SessionTab { caretBlock = 1, caretOffset = 2, anchorBlock = 1, anchorOffset = 2 });
            TabViewControl.FileEditorOf(second).RestoreView(new SessionTab { caretBlock = 2, caretOffset = 4, anchorBlock = 2, anchorOffset = 4 });

            TabViewControl top = new TabViewControl { horizontalAlignment = HorizontalAlignment.Stretch, preferredHeight = 300f };
            TabViewControl bottom = new TabViewControl { horizontalAlignment = HorizontalAlignment.Stretch, preferredHeight = 300f };
            top.AddChild(first);
            bottom.AddChild(second);
            StackPanelControl both = new StackPanelControl
            {
                orientation = StackPanelControl.Orientation.Vertical,
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            both.AddChild(top);
            both.AddChild(bottom);
            t.Show(both);
            File.Delete(path);
            yield return 3;

            DocumentEditorControl a = TabViewControl.EditorOf(first);
            DocumentEditorControl b = TabViewControl.EditorOf(second);
            t.Check(ReferenceEquals(a.session, b.session) && a.session.views == 2, "a note restored twice opens one session");
            t.Check(Caret(a) == (1, 2) && Caret(b) == (2, 4), $"each view keeps its own caret: {Caret(a)} and {Caret(b)}");

            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("NoteModel.SegmentMap", "Test")]
        private static IEnumerator<int> SegmentMap(TestContext t)
        {
            string path = Path.Combine(Path.GetTempPath(), "aurora-segments.axml");
            string notes = Path.GetFullPath(Paths.Doc("../../Notes"));
            List<(string name, RichTextDocument document)> sources = !Directory.Exists(notes) ? new() : Directory
                .EnumerateFiles(notes, "*.axml", SearchOption.AllDirectories)
                .Where(file => !SheetDocument.IsSheet(file) && !PlannerDocument.IsPlanner(file) && !VaultBrowserControl.InDotFolder(notes, file))
                .Select(file => (Path.GetRelativePath(notes, file), DocumentXml.Load(file)))
                .ToList();
            sources.Add(("everything.axml", DocumentXml.Parse(Everything)));

            foreach ((string name, RichTextDocument document) in sources)
            {
                byte[] built = NoteSegments.Build(document, path, out List<NoteSegment> map);
                t.Check(XDocument.Load(new MemoryStream(built)).ToString() == DocumentXml.ToFile(document, path).ToString(),
                    $"{name}: the note's own writer writes what ToFile does");
                CheckSegments(t, $"{name} as written", built, map, document);
                t.Check(SameSegments(map, NoteSegments.Map(built, document)), $"{name}: mapping the written file finds the segments it was written with");

                using MemoryStream saved = new MemoryStream();
                DocumentXml.ToFile(document, path).Save(saved);
                byte[] file = saved.ToArray();
                RichTextDocument reread = DocumentXml.Parse(XDocument.Load(new MemoryStream(file)).Root!);
                CheckSegments(t, $"{name} as XDocument saves it", file, NoteSegments.Map(file, reread), reread);
            }

            yield break;
        }

        // Segments tile the file, open on their element, end in whitespace slack and hold the note's nodes in order.
        private static void CheckSegments(TestContext t, string name, byte[] file, List<NoteSegment>? map, RichTextDocument document)
        {
            t.Check(map != null && map.Count >= 2, $"{name}: the file maps");
            if (map == null) return;

            long at = 0;
            bool tiled = true, opens = true, slack = true, elements = true;
            List<NoteNode> nodes = new List<NoteNode>();
            foreach (NoteSegment segment in map)
            {
                tiled &= segment.start == at && segment.length <= segment.capacity;
                at = segment.start + segment.capacity;
                for (long b = segment.start + segment.length; b < at; b++)
                    slack &= file[b] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

                if (segment.kind == NoteSegmentKind.Header) continue;
                opens &= file[segment.start] == (byte)'<';
                if (segment.kind == NoteSegmentKind.Footer) continue;

                string local = XElement.Parse(System.Text.Encoding.UTF8.GetString(file, (int)segment.start, segment.length)).Name.LocalName;
                elements &= segment.kind == NoteSegmentKind.Layout ? local == "DocumentLayout"
                    : segment.nodes.Length == 1 && segment.nodes[0].insert == null ? local is "Block" or "Table"
                    : local is "Footnote" or "Float";
                nodes.AddRange(segment.nodes);
            }

            t.Check(tiled && at == file.Length, $"{name}: segments tile the file");
            t.Check(opens, $"{name}: every segment after the header opens on '<'");
            t.Check(slack, $"{name}: every segment's slack is whitespace");
            t.Check(elements, $"{name}: every segment holds one whole element of its kind");
            t.Check(nodes.SequenceEqual(document.blocks), $"{name}: the segments hold the note's nodes in order");
        }

        private static bool SameSegments(List<NoteSegment> a, List<NoteSegment>? b) =>
            b != null && a.Count == b.Count && a.Zip(b).All(p => p.First.kind == p.Second.kind && p.First.start == p.Second.start
                && p.First.length == p.Second.length && p.First.capacity == p.Second.capacity && p.First.nodes.SequenceEqual(p.Second.nodes));

        // A note's text read and written the way RichTextDocument does, without the file's timestamps.
        private static string RoundTrip(string text, string extension, string name)
        {
            string stem = Path.GetFileNameWithoutExtension(name);
            RichTextDocument document = DocumentXml.Parse(extension switch
            {
                ".md" => MarkdownFormat.Read(text, stem),
                ".txt" => PlainTextFormat.Read(text, stem),
                ".tex" => TexSourceFormat.Read(text, stem),
                _ => XElement.Parse(text)
            });
            XElement root = DocumentXml.ToXml(document);
            return extension switch
            {
                ".md" => MarkdownFormat.Write(root),
                ".txt" => PlainTextFormat.Write(root),
                ".tex" => TexSourceFormat.Write(root, "\n"),
                _ => root.ToString()
            };
        }

        private static XElement Block(string text, params object[] attributes) =>
            new XElement("Block", attributes, new XElement("Run", new XAttribute("Text", text)));

        private static XElement Cell(params object[] content) => new XElement("Cell", content);

        // A note of plain paragraphs written to a temp file.
        private static string Fixture(params string[] texts)
        {
            string path = Path.Combine(Path.GetTempPath(), $"aurora-views-{Guid.NewGuid():N}.axml");
            DocumentXml.Save(DocumentXml.Parse(new XElement("Document", texts.Select(text => Block(text)))), path);
            return path;
        }

        private static DocumentEditorControl Pane() => new DocumentEditorControl
        {
            horizontalAlignment = HorizontalAlignment.Stretch,
            preferredHeight = 300f
        };

        private static DocumentControl Content(DocumentEditorControl editor) => editor.children.OfType<DocumentControl>().First();

        private static List<BlockControl> Blocks(DocumentEditorControl editor) => Content(editor).children.OfType<BlockControl>().ToList();

        // a view's caret as flat block index and offset, and the text after it
        private static (int block, int offset) Caret(DocumentEditorControl editor) =>
            (Blocks(editor).IndexOf(Content(editor).caretBlock), Content(editor).caretOffset);

        private static string Tail(DocumentEditorControl editor) => Content(editor).caretBlock.text[Content(editor).caretOffset..];

        // Three blocks and a one-cell table.
        private static readonly XElement Small = new XElement("Document",
            Block("one"), Block("two"), Block("three"),
            new XElement("Table", new XElement("Column", new XAttribute("Width", "100")), new XElement("Row", Cell(Block("cell")))));

        // Every block, run, table and insert feature the note format carries.
        private static readonly XElement Everything = new XElement("Document",
            new XAttribute("Name", "Everything"), new XAttribute("Palette", "thorium-light"), new XAttribute("ReadOnly", "true"),
            new XElement("DocumentLayout", new XAttribute("BlockSpacing", "8"),
                new XElement("Page", new XAttribute("Mode", "Paged"), new XAttribute("PageNumbers", "true"))),
            Block("Title", new XAttribute("StylingType", "Heading1")),
            new XElement("Block",
                new XAttribute("Align", "Center"), new XAttribute("Indent", "12"), new XAttribute("SpaceBefore", "6"),
                new XAttribute("PageBreak", "Page"), new XAttribute("PageStyle", "plain"),
                new XAttribute("MarkLeft", "L"), new XAttribute("MarkRight", "R"),
                new XElement("Run", new XAttribute("Text", "plain ")),
                new XElement("Run", new XAttribute("Text", "bold"), new XAttribute("Bold", "true"), new XAttribute("Italic", "true")),
                new XElement("Run", new XAttribute("Text", " red"), new XAttribute("ColorHex", "#C42B1E"), new XAttribute("Underline", "true"),
                    new XAttribute("Strikethrough", "true"), new XAttribute("HighlightHex", "#F6E7A8")),
                new XElement("Run", new XAttribute("Text", " big"), new XAttribute("FontSize", "24"), new XAttribute("FontSizeAuthored", "true"))),
            Block("item", new XAttribute("List", "Bullet"), new XAttribute("Level", "1"), new XAttribute("Marker", "LowerAlpha"), new XAttribute("Start", "3")),
            Block("done", new XAttribute("List", "Task"), new XAttribute("Checked", "true")),
            Block("int x = 1;", new XAttribute("StylingType", "Code"), new XAttribute("Language", "csharp"), new XAttribute("Wrap", "true")),
            new XElement("Block",
                new XElement("Run", new XAttribute("Text", "pic ")),
                new XElement("Run", new XAttribute("Image", "nosuch.png"), new XAttribute("Width", "40"), new XAttribute("Height", "30"),
                    new XAttribute("Wrap", "Square"), new XAttribute("X", "5"), new XAttribute("Y", "6"), new XAttribute("Rotation", "10")),
                new XElement("Run", new XAttribute("Text", " math ")),
                new XElement("Run", new XAttribute("Math", "x^2")),
                new XElement("Run", new XAttribute("Math", "y"), new XAttribute("Display", "true")),
                new XElement("Run", new XAttribute("Sheet", "Budget.sheet.axml#Sheet1!A1")),
                new XElement("Run", new XAttribute("Space", "12")),
                new XElement("Run", new XAttribute("Text", "fn"), new XAttribute("Note", "n1"))),
            new XElement("Table",
                new XAttribute("Borders", "false"), new XAttribute("Align", "Center"), new XAttribute("SpaceBefore", "4"),
                new XAttribute("PageBreak", "Clear"), new XAttribute("Padding", "3 2"),
                new XElement("Column", new XAttribute("Width", "100"), new XAttribute("RuleLeft", "Plain")),
                new XElement("Column", new XAttribute("Width", "80")),
                new XElement("Column", new XAttribute("Width", "60"), new XAttribute("RuleRight", "Double")),
                new XElement("Row",
                    Cell(new XAttribute("ColumnSpan", "2"), new XAttribute("RuleAbove", "Heavy"), new XAttribute("RuleBelow", "Light 0.5"),
                        new XAttribute("TrimBelow", "Left"), Block("span"), Block("second line")),
                    Cell(Block("c"))),
                new XElement("Row", Cell(), Cell(Block("b")), Cell(Block("c2")))),
            new XElement("Float", new XAttribute("Kind", "table"), new XAttribute("Placement", "h"),
                new XElement("Table", new XElement("Column", new XAttribute("Width", "50")), new XElement("Row", Cell(Block("in float")))),
                Block("caption")),
            new XElement("Footnote", new XAttribute("Id", "n1"), Block("note text")),
            Block("end"));
    }
}
