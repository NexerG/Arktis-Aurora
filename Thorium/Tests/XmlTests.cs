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
using ArctisAurora.Core.UI.Text;
using System.Text;

namespace Thorium.Tests
{
    internal static class XmlTests
    {
        [A_XSDActionDependency("Xml.NoteSync", "Test")]
        private static IEnumerator<int> NoteSync(TestContext t)
        {
            string path = Temp(".axml", "<Document><Block><Run Text=\"shared\"/></Block></Document>\n");
            DocumentEditorControl note = new DocumentEditorControl { widthStar = 1f };
            note.LoadPath(path);
            XmlEditorControl xml = new XmlEditorControl { widthStar = 1f };
            xml.LoadPath(path);
            t.Show(Row(note, xml));
            yield return 2;

            t.Check(Text(xml) == File.ReadAllText(path), "the XML view opens on the file's text as it is on disk");

            Type(xml, Text(xml).IndexOf("shared"), "x");
            yield return 2;
            t.Check(RunText(note) == "xshared" && xml.valid, $"a valid keystroke in the XML reaches the note: {RunText(note)}");

            Type(xml, Text(xml).IndexOf("xshared"), "y");
            yield return 2;
            note.session.undo.Undo();
            yield return 2;
            t.Check(RunText(note) == "shared", $"typing on in the XML is one undo step in the note: {RunText(note)}");
            t.Check(Text(xml).Contains("Text=\"shared\""), $"and the undo comes back into the XML: {Text(xml)}");

            note.session.document.InsertText((NoteBlock)note.session.document.blocks[0], new DocumentAddress(0, 0), "z");
            yield return 2;
            t.Check(Text(xml).Contains("Text=\"zshared\""), $"an edit in the note regenerates the XML: {Text(xml)}");

            t.Show(new StackPanelControl());
            yield return 2;
            File.Delete(path);
        }

        [A_XSDActionDependency("Xml.Invalid", "Test")]
        private static IEnumerator<int> Invalid(TestContext t)
        {
            string path = Temp(".axml", "<Document><Block><Run Text=\"kept\"/></Block></Document>\n");
            DocumentEditorControl note = new DocumentEditorControl { widthStar = 1f };
            note.LoadPath(path);
            XmlEditorControl xml = new XmlEditorControl { widthStar = 1f };
            xml.LoadPath(path);
            t.Show(Row(note, xml));
            yield return 2;

            Type(xml, Text(xml).IndexOf("<Block>"), "<");
            yield return 2;
            t.Check(!xml.valid && RunText(note) == "kept", "half-typed XML leaves the note as it was");
            t.Check(((IFileEditor)xml).isDirty, "and the XML view counts as unsaved");

            string broken = Text(xml);
            xml.Save();
            t.Check(File.ReadAllText(path) == broken, "saving broken XML writes the text as typed");
            t.Check(RunText(note) == "kept", "and the note stays at its last good state");

            t.Show(new StackPanelControl());
            yield return 2;
            File.Delete(path);
        }

        [A_XSDActionDependency("Xml.SheetSync", "Test")]
        private static IEnumerator<int> SheetSync(TestContext t)
        {
            string path = Temp(SheetDocument.extension,
                "<Sheet Name=\"S\"><Page Name=\"P\"><Layer Name=\"L\"><Cell At=\"A1\">5</Cell><Cell At=\"B1\">=A1*2</Cell></Layer></Page></Sheet>");
            XmlEditorControl xml = new XmlEditorControl();
            xml.LoadPath(path);
            t.Show(xml);
            yield return 2;

            SheetDocument sheet = SheetBook.Get(path);
            SetText(xml, Text(xml).Replace(">5<", ">7<"));
            yield return 2;
            t.Check(SheetBook.calc.Value(sheet.pages[0], 0, 1).Display() == "14", "a cell changed in the XML recalculates the formula on it");

            sheet.undo.Undo();
            yield return 2;
            t.Check(SheetBook.calc.Value(sheet.pages[0], 0, 1).Display() == "10" && Text(xml).Contains(">5<"),
                $"undo in the sheet restores the cell and the XML: {Text(xml)}");

            t.Show(new StackPanelControl());
            yield return 2;
            SheetBook.Deleted(path);
            File.Delete(path);
        }

        [A_XSDActionDependency("Xml.PlannerSync", "Test")]
        private static IEnumerator<int> PlannerSync(TestContext t)
        {
            string path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"aurora-xml-{Guid.NewGuid():N}{PlannerDocument.extension}"));
            PlannerDocument.Blank("Plan").Save(path);
            XmlEditorControl xml = new XmlEditorControl();
            xml.LoadPath(path);
            t.Show(xml);
            yield return 2;

            PlannerDocument planner = PlannerBook.Open(path);
            SetText(xml, Text(xml).Replace("\"General\"", "\"Work\""));
            yield return 2;
            t.Check(planner.categories.Count == 1 && planner.categories[0].name == "Work" && planner.unsaved,
                "a category renamed in the XML is renamed in the planner, which is now unsaved");

            planner.undo.Undo();
            yield return 2;
            t.Check(planner.categories[0].name == "General" && Text(xml).Contains("\"General\""), $"undo in the planner comes back into the XML: {Text(xml)}");

            PlannerBook.Close(path, planner);
            t.Show(new StackPanelControl());
            yield return 2;
            File.Delete(path);
        }

        [A_XSDActionDependency("Xml.Restore", "Test")]
        private static IEnumerator<int> Restore(TestContext t)
        {
            string path = Temp(".axml", "<Document><Block><Run Text=\"tab\"/></Block></Document>\n");
            string before = SessionLayout.scope;

            WorkspaceControl workspace = new WorkspaceControl
            {
                paneDocument = "tab-pane",
                defaultDocument = "tab-pane",
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            t.Show(workspace);
            SessionLayout.scope = "xml-a";
            WorkspaceActions.NewXml();
            WorkspacePageControl page = workspace.shown!;
            t.Check(page.kind == WorkspaceKind.Xml, "New XML workspace adds a workspace of the XML kind");

            TabViewControl view = TabViewControl.TabViews(page).First();
            view.AddChild(SessionLayout.tabFactory(path, false));
            view.AddChild(SessionLayout.tabFactory(path, true));
            yield return 2;
            t.Check(Kinds(page) == "note,xml", $"one file opens as a note and as XML side by side: {Kinds(page)}");

            SessionLayout.ChangeScope("xml-b");
            yield return 2;
            SessionLayout.ChangeScope("xml-a");
            yield return 2;
            t.Check(workspace.shown?.kind == WorkspaceKind.Xml && Kinds(workspace.shown) == "note,xml", $"both tabs come back as they were: {Kinds(workspace.shown!)}");

            SessionLayout.scope = before;
            t.Show(new StackPanelControl());
            yield return 2;
            File.Delete(path);
        }

        private static string Temp(string extension, string text)
        {
            string path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"aurora-xml-{Guid.NewGuid():N}{extension}"));
            File.WriteAllText(path, text);
            return path;
        }

        private static StackPanelControl Row(Control a, Control b)
        {
            StackPanelControl row = new StackPanelControl
            {
                orientation = StackPanelControl.Orientation.Horizontal,
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            row.AddChild(a);
            row.AddChild(b);
            return row;
        }

        private static string Text(XmlEditorControl xml) => xml.source.activeDocument.SourceText();

        // Inserts at a character offset of the whole text, as typing there would.
        private static void Type(XmlEditorControl xml, int offset, string text)
        {
            RichTextDocument source = xml.source.activeDocument;
            for (int line = 0; line < source.blocks.Length; line++)
            {
                NoteBlock block = (NoteBlock)source.blocks[line];
                int length = block.run.text?.Length ?? 0;
                if (offset <= length)
                {
                    source.InsertText(block, new DocumentAddress(line, offset), text);
                    return;
                }
                offset -= length + source.sourceNewline.Length;
            }
        }

        private static void SetText(XmlEditorControl xml, string text) =>
            xml.source.activeDocument.ReplaceAll(RichTextDocument.SourceFromBytes(Encoding.UTF8.GetBytes(text), "set", "xml"));

        private static string RunText(DocumentEditorControl note) => ((NoteBlock)note.session.document.blocks[0]).run.text;

        private static string Kinds(WorkspacePageControl page) => string.Join(",", TabViewControl.TabViews(page)
            .SelectMany(view => view.Items).Select(item => TabViewControl.FileEditorOf(item) is XmlEditorControl ? "xml" : "note"));
    }
}
