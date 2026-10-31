using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Windows;
using ArctisAurora.EngineWork;
using System.Diagnostics;
using System.Numerics;
using System.Xml.Linq;

namespace ArctisAurora.Tests
{
    internal static class NoteTests
    {
        [A_XSDActionDependency("Notes.FileKinds", "Test")]
        private static IEnumerator<int> FileKinds(TestContext t)
        {
            t.Check(FileTypes.KindOf("a/Budget.sheet.axml") == FileKind.Sheet, "a .sheet.axml is a sheet, not a note");
            t.Check(FileTypes.KindOf("a/Week.PLANNER.AXML") == FileKind.Planner, "suffixes match without case");
            t.Check(FileTypes.KindOf("a/note.axml") == FileKind.Note, "a .axml is a note");
            t.Check(FileTypes.KindOf("a/data.xml") == FileKind.Xml, "a .xml is plain XML");
            t.Check(FileTypes.KindOf("a/picture.png") == null, "an unknown suffix has no kind");

            string path = Path.Combine(Path.GetTempPath(), $"aurora-kind-{Guid.NewGuid():N}.xml");
            File.WriteAllText(path, "<Document><Block><Run Text=\"raw\"/></Block></Document>\n");
            RichTextDocument document = RichTextDocument.Load(path);
            File.Delete(path);
            t.Check(document.sourceLanguage == "xml", "a .xml opens as XML source, even with a Document root");
            yield break;
        }

        [A_XSDActionDependency("Notes.DirtyTracking", "Test")]
        private static IEnumerator<int> DirtyTracking(TestContext t)
        {
            RichTextDocument d = DocumentXml.Parse(Fixture);
            d.InsertText(new DocumentAddress(0, 4), "!");
            t.Check(Marked(d) == "0", $"typing marks its block: {Marked(d)}");

            d = DocumentXml.Parse(Fixture);
            d.InsertText(new DocumentAddress(1, 1), "!");
            t.Check(Marked(d) == "1.0", $"typing in a cell marks the cell's block: {Marked(d)}");

            d = DocumentXml.Parse(Fixture);
            d.ChangeBlocks(3, 4, b => b.alignment = TextAlignment.Center);
            t.Check(Marked(d) == "2,3", $"a block change marks every block it names: {Marked(d)}");

            d = DocumentXml.Parse(Fixture);
            d.SplitBlockAt(new DocumentAddress(4, 2));
            t.Check(Marked(d) == "3", $"a split marks the head, not the new tail: {Marked(d)}");

            d = DocumentXml.Parse(Fixture);
            d.SplitBlockAt(new DocumentAddress(1, 1));
            t.Check(Marked(d) == "1,1.0", $"a split in a cell marks the table and the head: {Marked(d)}");
            Clear(d);
            d.JoinBlockWithNext(new DocumentAddress(1, 1));
            t.Check(Marked(d) == "1,1.0", $"a join in a cell marks the table and the head: {Marked(d)}");

            d = DocumentXml.Parse(Fixture);
            d.SetPage(new PageLayout());
            t.Check(Marked(d) == "", $"a page change marks no node: {Marked(d)}");
            d.PutTable(1, true, DocumentXml.WriteTable((NoteTable)d.blocks[1]));
            t.Check(Marked(d) == "", $"a table put back whole is new and unmarked: {Marked(d)}");

            string path = Path.Combine(Path.GetTempPath(), $"aurora-dirty-{Guid.NewGuid():N}.axml");
            DocumentXml.Save(DocumentXml.Parse(Fixture), path);
            DocumentEditorControl editor = new DocumentEditorControl
            {
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            t.Show(editor);
            editor.LoadPath(path);
            File.Delete(path);
            yield return 2;

            Control? grip = Find(editor, "ColumnGrip");
            t.Check(grip != null, "the table shows a column grip");
            if (grip != null)
            {
                LayoutRect r = grip.arrangedRect;
                yield return t.Drag(grip, new Vector2(r.x + r.width * 0.5f + 30f, r.y + r.height * 0.5f));
                t.Check(Marked(editor.activeDocument) == "1", $"a column drag marks its table: {Marked(editor.activeDocument)}");
            }

            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Notes.PartialWrite", "Test")]
        private static IEnumerator<int> PartialWrite(TestContext t)
        {
            string path = Path.Combine(Path.GetTempPath(), $"aurora-partial-{Guid.NewGuid():N}.axml");
            RichTextDocument d = DocumentXml.Parse(Fixture);
            List<NoteSegment> map = NoteSegments.Write(d, path, null);
            t.Check(ReadsBack(d, path), "a whole write reads back as the note");

            NoteSegment two = Holding(map, d.blocks[2]);
            byte[] before = File.ReadAllBytes(path);
            d.InsertText(new DocumentAddress(3, 3), "!");
            map = NoteSegments.Write(d, path, map);
            byte[] after = File.ReadAllBytes(path);
            t.Check(after.Length > before.Length && Same(before, after, 0, two.start), "outgrowing a segment moves only what follows it");
            t.Check(!d.blocks[2].dirty, "a write clears the flags it wrote");

            two = Holding(map, d.blocks[2]);
            before = after;
            d.InsertText(new DocumentAddress(3, 4), "?");
            map = NoteSegments.Write(d, path, map);
            after = File.ReadAllBytes(path);
            t.Check(after.Length == before.Length && Outside(before, after, two.start, two.start + two.capacity),
                "an edit inside the slack rewrites only its own segment");
            t.Check(ReadsBack(d, path), "the patched file reads back as the note");

            d.InsertText(new DocumentAddress(4, 0), new string('x', 2000));
            map = NoteSegments.Write(d, path, map);
            before = File.ReadAllBytes(path);
            d.RemoveText(new DocumentAddress(4, 0), 1990);
            map = NoteSegments.Write(d, path, map);
            after = File.ReadAllBytes(path);
            t.Check(after.Length < before.Length - 1500 && ReadsBack(d, path), "a large deletion trims the file");

            d.SplitBlockAt(new DocumentAddress(0, 2));
            map = NoteSegments.Write(d, path, map);
            t.Check(ReadsBack(d, path), "a split writes the new block");
            d.JoinBlockWithNext(new DocumentAddress(0, 2));
            map = NoteSegments.Write(d, path, map);
            t.Check(ReadsBack(d, path), "a join drops the joined block");
            d.InsertText(new DocumentAddress(1, 1), "!");
            map = NoteSegments.Write(d, path, map);
            t.Check(ReadsBack(d, path), "an edit in a cell rewrites its table");
            d.SetPalette("thorium-light");
            map = NoteSegments.Write(d, path, map);
            t.Check(ReadsBack(d, path), "a header change rewrites the header");
            d.SetLayout(new DocumentLayout { blockSpacing = 9f });
            map = NoteSegments.Write(d, path, map);
            t.Check(ReadsBack(d, path), "a layout first set is written after the header");

            DocumentXml.Save(DocumentXml.Parse(Fixture), path);
            RichTextDocument saved = DocumentXml.Load(path);
            map = NoteSegments.Map(File.ReadAllBytes(path), saved)!;
            NoteSegment three = Holding(map, saved.blocks[3]);
            before = File.ReadAllBytes(path);
            saved.InsertText(new DocumentAddress(4, 0), "!");
            NoteSegments.Write(saved, path, map);
            t.Check(ReadsBack(saved, path) && Same(before, File.ReadAllBytes(path), 0, three.start),
                "a file XDocument saved is patched from the edited segment on");

            File.Delete(path);
            yield break;
        }

        [A_XSDActionDependency("Notes.JournalReplay", "Test")]
        private static IEnumerator<int> JournalReplay(TestContext t)
        {
            string path = Path.Combine(Path.GetTempPath(), $"aurora-journal-{Guid.NewGuid():N}.txt");
            File.WriteAllText(path, "abcdef");

            FileJournal.Record(path, [(2, "XY"u8.ToArray()), (6, "gh"u8.ToArray())], 8);
            t.Check(File.ReadAllText(path) == "abcdef", "recording a batch leaves the file alone");
            t.Check(FileJournal.Replay(path) && File.ReadAllText(path) == "abXYefgh", "a recorded batch is applied on replay");
            t.Check(!File.Exists(FileJournal.JournalOf(path)), "replay drops the journal");

            string journal = FileJournal.Record(path, [(0, "zz"u8.ToArray())], 8);
            byte[] whole = File.ReadAllBytes(journal);
            File.WriteAllBytes(journal, whole[..^4]);
            t.Check(!FileJournal.Replay(path) && File.ReadAllText(path) == "abXYefgh", "a journal cut off mid-record is dropped unapplied");
            t.Check(!File.Exists(journal), "the cut-off journal is deleted");

            journal = FileJournal.Record(path, [(0, "zz"u8.ToArray())], 8);
            whole = File.ReadAllBytes(journal);
            whole[^1] ^= 0xFF;
            File.WriteAllBytes(journal, whole);
            t.Check(!FileJournal.Replay(path) && File.ReadAllText(path) == "abXYefgh", "a record that fails its checksum is dropped unapplied");

            File.Delete(path);
            yield break;
        }

        [A_XSDActionDependency("Notes.Autosave", "Test")]
        private static IEnumerator<int> Autosave(TestContext t)
        {
            string path = Path.Combine(Path.GetTempPath(), $"aurora-autosave-{Guid.NewGuid():N}.axml");
            DocumentXml.Save(DocumentXml.Parse(Fixture), path);
            DocumentEditSession session = NoteSessions.Open(path);

            session.document.InsertText(new DocumentAddress(0, 4), "!");
            session.MarkDirty();
            yield return 2;
            t.Check(!session.isDirty && ReadsBack(session.document, path), "an edit reaches the file on the next tick, without a save");

            Stopwatch clock = Stopwatch.StartNew();
            DateTime seen = File.GetLastWriteTimeUtc(path);
            int writes = 0;
            while (clock.ElapsedMilliseconds < 1000)
            {
                session.document.InsertText(new DocumentAddress(4, 0), "x");
                session.MarkDirty();
                yield return 1;
                DateTime now = File.GetLastWriteTimeUtc(path);
                if (now == seen) continue;
                writes++;
                seen = now;
            }
            t.Check(writes >= 2 && writes <= 6, $"a second of edits is written at most once every 250 ms: {writes} writes");

            clock.Restart();
            while (session.isDirty && clock.ElapsedMilliseconds < 1000) yield return 1;
            t.Check(!session.isDirty && ReadsBack(session.document, path), "the last edit of a burst is written");

            NoteSessions.Close(session);
            File.Delete(path);
        }

        [A_XSDActionDependency("Notes.DiskReload", "Test")]
        private static IEnumerator<int> DiskReload(TestContext t)
        {
            string path = Path.Combine(Path.GetTempPath(), $"aurora-reload-{Guid.NewGuid():N}.axml");
            DocumentXml.Save(DocumentXml.Parse(Fixture), path);
            DocumentEditorControl editor = new DocumentEditorControl
            {
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            t.Show(editor);
            editor.LoadPath(path);
            yield return 2;

            RichTextDocument d = editor.activeDocument;
            DocumentControl content = editor.children.OfType<DocumentControl>().First();
            NoteNode[] kept = { d.blocks[0], d.blocks[1], d.blocks[3] };
            Control[] views = kept.Select(content.ViewOf).ToArray();

            WriteOutside(path, "two", "TWO");
            Stopwatch clock = Stopwatch.StartNew();
            while (Text(d.blocks[2]) != "TWO" && clock.ElapsedMilliseconds < 2000) yield return 1;
            yield return 2;
            t.Check(Text(d.blocks[2]) == "TWO", "a write made outside reaches the open note");
            t.Check(kept.SequenceEqual(new[] { d.blocks[0], d.blocks[1], d.blocks[3] }) && kept.Select(content.ViewOf).SequenceEqual(views),
                "entries the write left alone keep their model and their views");
            t.Check(content.ViewOf(d.blocks[2]) is BlockControl, "the changed entry gets a view of its own");
            t.Check(!editor.session.isDirty, "taking in a write leaves nothing to save");

            editor.session.undo.Undo();
            yield return 2;
            t.Check(Text(d.blocks[2]) == "two" && content.ViewOf(d.blocks[2]) is BlockControl, "one undo takes the reload back out");

            File.WriteAllText(path, "<Document><Block>");
            clock.Restart();
            while (clock.ElapsedMilliseconds < 500) yield return 1;
            t.Check(Text(d.blocks[2]) == "two", "a write that does not read as a note leaves the note alone");
            d.InsertText(new DocumentAddress(0, 0), "!");
            editor.session.MarkDirty();
            clock.Restart();
            while (editor.session.isDirty && clock.ElapsedMilliseconds < 1000) yield return 1;
            t.Check(ReadsBack(d, path), "the next save writes the whole note over it");

            t.Show(new StackPanelControl());
            yield return 2;
            File.Delete(path);
        }

        [A_XSDActionDependency("Notes.LocalWins", "Test")]
        private static IEnumerator<int> LocalWins(TestContext t)
        {
            string path = Path.Combine(Path.GetTempPath(), $"aurora-wins-{Guid.NewGuid():N}.axml");
            DocumentXml.Save(DocumentXml.Parse(Fixture), path);
            DocumentEditSession session = NoteSessions.Open(path);
            RichTextDocument d = session.document;

            d.InsertText(new DocumentAddress(0, 0), "a");
            session.MarkDirty();
            Stopwatch clock = Stopwatch.StartNew();
            while (session.isDirty && clock.ElapsedMilliseconds < 1000) yield return 1;

            d.InsertText(new DocumentAddress(0, 0), "b");
            session.MarkDirty();
            WriteOutside(path, "three", "OUT");
            clock.Restart();
            while (clock.ElapsedMilliseconds < 1000) yield return 1;

            t.Check(Text(d.blocks[0]) == "bazero" && Text(d.blocks[3]) == "three", "an outside write does not touch a note with unsaved edits");
            t.Check(!session.isDirty && ReadsBack(d, path), "the note's own content ends up on disk");

            NoteSessions.Close(session);
            File.Delete(path);
        }

        [A_XSDActionDependency("Notes.NoNamingPrompt", "Test")]
        private static IEnumerator<int> NoNamingPrompt(TestContext t)
        {
            string path = Path.Combine(Path.GetTempPath(), $"aurora-unnamed-{Guid.NewGuid():N}.axml");
            DocumentXml.Save(DocumentXml.Parse(Fixture), path);
            DocumentEditorControl editor = new DocumentEditorControl
            {
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            editor.LoadPath(path);
            TabItemControl item = new TabItemControl { header = "note" };
            item.AddChild(editor);
            TabViewControl view = new TabViewControl
            {
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            view.AddChild(item);
            t.Show(view);
            yield return 2;

            RichTextDocument d = editor.activeDocument;
            d.InsertText(new DocumentAddress(0, 4), "!");
            editor.MarkDirty();
            t.Check(editor.session.isDirty && d.name == null, "the note is edited and has no name");

            t.Check(Shutdown.RunPhase(Shutdown.requestPhase) && !NoteNameWindow.isOpen, "a shutdown request does not ask for a name");

            view.CloseTab(item);
            t.Check(!NoteNameWindow.isOpen && !view.children.Contains(item), "closing the tab does not ask for a name");
            t.Check(ReadsBack(d, path), "closing writes the edit");

            t.Show(new StackPanelControl());
            yield return 2;
            File.Delete(path);
        }

        // Rewrites one paragraph's text the way another program would: a whole new file.
        [A_XSDActionDependency("Notes.HeadingFolds", "Test")]
        private static IEnumerator<int> HeadingFolds(TestContext t)
        {
            string path = Path.Combine(Path.GetTempPath(), $"aurora-folds-{Guid.NewGuid():N}.axml");
            DocumentXml.Save(DocumentXml.Parse(Outline), path);
            DocumentEditorControl editor = new DocumentEditorControl
            {
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            t.Show(editor);
            editor.LoadPath(path);
            yield return 2;

            RichTextDocument d = editor.activeDocument;
            DocumentControl content = null!;
            foreach (Entity child in editor.children)
                if (child is DocumentControl found) content = found;
            BlockControl[] views = new BlockControl[d.blocks.Length];
            for (int i = 0; i < views.Length; i++)
                views[i] = (BlockControl)content.ViewOf(d.blocks[i]);

            content.ToggleFold(views[2]);
            yield return 2;
            t.Check(views[3].hidden && !views[2].hidden && !views[4].hidden && !views[1].hidden,
                "a folded H2 hides its text and stops at the next H1");

            content.ToggleFold(views[0]);
            yield return 2;
            t.Check(views[1].hidden && views[2].hidden && views[3].hidden && !views[4].hidden,
                "a folded H1 hides the H2 inside it and stops at the next H1");
            t.Check(content.AdjacentBlock(views[0], 1) == views[4] && content.AdjacentBlock(views[4], -1) == views[0],
                "the caret steps over a fold both ways");

            content.SetCaret(views[3], 0);
            yield return 2;
            t.Check(!views[0].note.folded && !views[2].note.folded && !views[3].hidden,
                "a caret moved into a fold opens every fold above it");

            content.ToggleFold(views[0]);
            yield return 2;
            d.ChangeBlocks(0, 0, b => b.stylingType = TextStyleType.Text);
            yield return 2;
            t.Check(!views[0].note.folded && !views[1].hidden && !views[3].hidden,
                "a heading turned back into text loses its fold");

            d.ChangeBlocks(0, 0, b => b.stylingType = TextStyleType.Heading1);
            content.ToggleFold(views[2]);
            yield return 2;
            string copy = Path.Combine(Path.GetTempPath(), $"aurora-folds-{Guid.NewGuid():N}.axml");
            DocumentXml.Save(d, copy);
            RichTextDocument read = DocumentXml.Load(copy);
            File.Delete(copy);
            t.Check(((NoteBlock)read.blocks[2]).folded && !((NoteBlock)read.blocks[0]).folded,
                "an .axml note writes and reads back which headings are folded");

            content.SetCaret(views[2], views[2].Length);
            content.SplitBlock();
            yield return 2;
            BlockControl? added = d.blocks.Length > 6 ? content.ViewOf(d.blocks[4]) as BlockControl : null;
            t.Check(added != null && added.stylingType == TextStyleType.Heading2 && added.Length == 0 && content.caretBlock == added
                && views[2].note.folded && views[3].hidden && !added.hidden,
                "Enter at a folded heading's end adds an empty heading of its level after the section, still folded");

            t.Show(new StackPanelControl());
            yield return 2;
            File.Delete(path);
        }

        private static void WriteOutside(string path, string from, string to)
        {
            XDocument file = XDocument.Load(path);
            foreach (XElement run in file.Descendants().Where(e => e.Name.LocalName == "Run" && (string?)e.Attribute("Text") == from))
                run.SetAttributeValue("Text", to);
            file.Save(path);
        }

        private static string Text(NoteNode node) => ((NoteBlock)node).run.text ?? "";

        private static NoteSegment Holding(List<NoteSegment> map, NoteNode node) => map.First(s => s.nodes.Contains(node));

        private static bool ReadsBack(RichTextDocument document, string path)
        {
            FileWriteQueue.Drain();
            return DocumentXml.ToXml(DocumentXml.Load(path)).ToString() == DocumentXml.ToXml(document).ToString();
        }

        private static bool Same(byte[] a, byte[] b, long from, long to) =>
            a.Length >= to && b.Length >= to && a.AsSpan((int)from, (int)(to - from)).SequenceEqual(b.AsSpan((int)from, (int)(to - from)));

        // Equal lengths, and every byte outside [from, to) unchanged.
        private static bool Outside(byte[] a, byte[] b, long from, long to) =>
            a.Length == b.Length && Same(a, b, 0, from) && Same(a, b, to, a.Length);

        // Dirty nodes by note-level index, a table's cell blocks as index.n.
        private static string Marked(RichTextDocument document)
        {
            List<string> marked = new List<string>();
            for (int i = 0; i < document.blocks.Length; i++)
            {
                if (document.blocks[i].dirty) marked.Add($"{i}");
                if (document.blocks[i] is not NoteTable table) continue;

                int n = 0;
                foreach (NoteCell[] row in table.rows)
                    foreach (NoteCell cell in row)
                        foreach (NoteBlock block in cell.blocks)
                        {
                            if (block.dirty) marked.Add($"{i}.{n}");
                            n++;
                        }
            }
            return string.Join(",", marked);
        }

        private static void Clear(RichTextDocument document)
        {
            foreach (NoteNode node in document.blocks)
            {
                node.dirty = false;
                if (node is NoteTable table)
                    foreach (NoteCell[] row in table.rows)
                        foreach (NoteCell cell in row)
                            foreach (NoteBlock block in cell.blocks)
                                block.dirty = false;
            }
        }

        private static Control? Find(Control control, string typeName)
        {
            if (control.GetType().Name == typeName) return control;
            foreach (Entity child in control.children)
                if (child is Control c && Find(c, typeName) is Control found)
                    return found;
            return null;
        }

        private static XElement Block(string text) => new XElement("Block", new XElement("Run", new XAttribute("Text", text)));

        // flat blocks: 0 "zero", 1 "a" and 2 "b" in the table, 3 "two", 4 "three"
        private static readonly XElement Fixture = new XElement("Document",
            Block("zero"),
            new XElement("Table",
                new XElement("Column", new XAttribute("Width", "120")),
                new XElement("Column", new XAttribute("Width", "120")),
                new XElement("Row",
                    new XElement("Cell", Block("a")),
                    new XElement("Cell", Block("b")))),
            Block("two"),
            Block("three"));

        // an H1 holding text and an H2 with its own text, then a second H1
        private static readonly XElement Outline = new XElement("Document",
            Heading("A", 1), Block("a1"), Heading("B", 2), Block("b1"), Heading("C", 1), Block("c1"));

        private static XElement Heading(string text, int level) =>
            new XElement("Block", new XAttribute("StylingType", "Heading" + level), new XElement("Run", new XAttribute("Text", text)));
    }
}
