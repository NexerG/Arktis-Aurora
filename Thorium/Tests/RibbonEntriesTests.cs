using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Containers.Workspace;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Sheets;
using ArctisAurora.Core.UI.Text;
using System.Runtime.InteropServices;
using Image = Arctis.Imaging.Image;

namespace Thorium.Tests
{
    internal static class RibbonEntriesTests
    {
        [A_XSDActionDependency("Note.NumbersToggle", "Test")]
        private static IEnumerator<int> NumbersToggle(TestContext t)
        {
            string folder = TempFolder();
            DocumentEditorControl editor = ShowNote(t, Path.Combine(folder, "list.axml"));
            yield return 2;

            editor.FocusCaret();
            editor.SelectAll();
            editor.ToggleNumbers();
            yield return 2;
            t.Check(Lists(editor) == "Bullet:Decimal,Bullet:Decimal", $"the selected paragraphs become a numbered list: {Lists(editor)}");

            editor.ToggleNumbers();
            yield return 2;
            t.Check(Lists(editor) == "None:,None:", $"again turns them back into text: {Lists(editor)}");

            editor.ToggleBullets();
            editor.ToggleNumbers();
            yield return 2;
            t.Check(Lists(editor) == "Bullet:Decimal,Bullet:Decimal", $"numbers on a bulleted list renumber it: {Lists(editor)}");

            editor.Undo();
            yield return 2;
            t.Check(Lists(editor) == "Bullet:,Bullet:", $"one undo step brings the bullets back: {Lists(editor)}");

            t.Show(new StackPanelControl());
            yield return 2;
            Directory.Delete(folder, true);
        }

        [A_XSDActionDependency("Note.TasksToggle", "Test")]
        private static IEnumerator<int> TasksToggle(TestContext t)
        {
            string folder = TempFolder();
            DocumentEditorControl editor = ShowNote(t, Path.Combine(folder, "tasks.axml"));
            yield return 2;

            editor.FocusCaret();
            editor.SelectAll();
            editor.ToggleTasks();
            yield return 2;
            t.Check(Lists(editor) == "Task:,Task:", $"the selected paragraphs become a task list: {Lists(editor)}");

            editor.ToggleTasks();
            yield return 2;
            t.Check(Lists(editor) == "None:,None:", $"again turns them back into text: {Lists(editor)}");

            editor.Undo();
            yield return 2;
            t.Check(Lists(editor) == "Task:,Task:", $"one undo step brings the tasks back: {Lists(editor)}");

            t.Show(new StackPanelControl());
            yield return 2;
            Directory.Delete(folder, true);
        }

        [A_XSDActionDependency("Note.InsertPictureFile", "Test")]
        private static IEnumerator<int> InsertPictureFile(TestContext t)
        {
            string folder = TempFolder();
            string source = Path.Combine(TempFolder(), "photo.png");
            Image image = new Image(30, 20);
            MemoryMarshal.Cast<byte, uint>(image.Pixels.AsSpan()).Fill(0xFFC87828u);
            image.SavePng(source);

            DocumentEditorControl editor = ShowNote(t, Path.Combine(folder, "pictures.axml"));
            yield return 2;
            editor.FocusCaret();
            PlaceCaret(editor);

            string attachments = Path.Combine(folder, "attachments");
            bool put = editor.InsertPictureFile(source);
            yield return 2;
            t.Check(put && Pictures(editor).SequenceEqual(new[] { Path.Combine(attachments, "photo.png") }),
                $"the picture is copied under attachments and put in at the caret: {string.Join(",", Pictures(editor))}");
            t.Check(File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(Path.Combine(attachments, "photo.png"))), "the copy is the file unchanged");

            editor.InsertPictureFile(source);
            yield return 2;
            t.Check(File.Exists(Path.Combine(attachments, "photo (2).png")) && Pictures(editor).Count() == 2,
                "a second insert of the same name gets its own copy");

            string plain = Path.Combine(TempFolder(), "plain.txt");
            File.WriteAllText(plain, "text\n");
            DocumentEditorControl text = ShowNote(t, plain);
            yield return 2;
            text.FocusCaret();
            PlaceCaret(text);
            t.Check(!text.InsertPictureFile(source) && !Directory.Exists(Path.Combine(Path.GetDirectoryName(plain)!, "attachments")),
                "a plain text note refuses the picture");

            t.Show(new StackPanelControl());
            yield return 2;
            Directory.Delete(folder, true);
            Directory.Delete(Path.GetDirectoryName(source)!, true);
            Directory.Delete(Path.GetDirectoryName(plain)!, true);
        }

        [A_XSDActionDependency("Sheet.ImportCsv", "Test")]
        private static IEnumerator<int> ImportCsv(TestContext t)
        {
            string folder = TempFolder();
            string path = Path.Combine(folder, "book" + SheetDocument.extension);
            SheetDocument.Blank("Book").Save(path);
            string csv = Path.Combine(folder, "Prices.csv");
            File.WriteAllText(csv, "Item,Cost\nLamp,24\n");

            SheetEditorControl editor = new SheetEditorControl();
            editor.LoadPath(path);
            t.Show(editor);
            yield return 2;

            editor.ImportPage(csv);
            yield return 2;
            t.Check(editor.document.pages.Count == 2 && editor.pageIndex == 1 && editor.page.name == "Prices",
                $"the CSV becomes a shown page named after the file: {string.Join(",", editor.document.pages.Select(p => p.name))}");
            t.Check(editor.editLayer.Get(1, 0) == "Lamp" && editor.editLayer.Get(1, 1) == "24", "holding the CSV's cells");

            editor.ImportPage(csv);
            yield return 2;
            t.Check(editor.document.pages.Count == 3 && editor.page.name != "Prices", $"a second import gets a free name: {editor.page.name}");

            editor.Undo();
            yield return 2;
            t.Check(editor.document.pages.Count == 2, "one undo step removes the imported page");

            t.Show(new StackPanelControl());
            yield return 2;
            Directory.Delete(folder, true);
        }

        [A_XSDActionDependency("Ribbon.CsvEntries", "Test")]
        private static IEnumerator<int> CsvEntries(TestContext t)
        {
            string folder = TempFolder();
            string sheet = Path.Combine(folder, "book" + SheetDocument.extension);
            SheetDocument.Blank("Book").Save(sheet);
            string csv = Path.Combine(folder, "plain.csv");
            File.WriteAllText(csv, "a,b\n1,2\n");

            int converted = 0;
            RibbonControl ribbon = new RibbonControl { convertCsv = () => converted++ };
            WorkspaceControl workspace = new WorkspaceControl
            {
                paneDocument = "tab-pane",
                defaultDocument = "tab-pane",
                horizontalAlignment = HorizontalAlignment.Stretch,
                heightStar = 1f
            };
            StackPanelControl shell = new StackPanelControl { horizontalAlignment = HorizontalAlignment.Stretch, verticalAlignment = VerticalAlignment.Stretch };
            shell.AddChild(ribbon);
            shell.AddChild(workspace);
            t.Show(shell);

            WorkspacePageControl page = workspace.AddPage("Sheets", WorkspaceKind.Sheets);
            TabViewControl view = workspace.LoadPane(page);
            TabItemControl sheetTab = SessionLayout.tabFactory(sheet, false);
            TabItemControl csvTab = SessionLayout.tabFactory(csv, false);
            view.AddChild(sheetTab);
            view.AddChild(csvTab);
            view.SetActive(sheetTab);
            page.ribbonCategory = "Data";
            yield return 3;
            t.Check(Shown(ribbon, "Import CSV...") && !Shown(ribbon, "Convert CSV to sheet"), "a sheet offers Import, not Convert");

            view.SetActive(csvTab);
            yield return 3;
            t.Check(!Shown(ribbon, "Import CSV...") && Shown(ribbon, "Convert CSV to sheet"), "a CSV offers Convert, not Import");

            Control convert = Find<LabelControl>(ribbon).First(label => label.text == "Convert CSV to sheet");
            while (convert is not ButtonControl && convert.parent is Control up) convert = up;
            yield return t.Click(convert);
            yield return 2;
            t.Check(converted == 1, "Convert runs the action the ribbon was given");

            t.Show(new StackPanelControl());
            yield return 2;
            Directory.Delete(folder, true);
        }

        private static DocumentEditorControl ShowNote(TestContext t, string path)
        {
            if (!File.Exists(path))
                File.WriteAllText(path, "<Document><Block><Run Text=\"one\"/></Block><Block><Run Text=\"two\"/></Block></Document>\n");
            DocumentEditorControl editor = new DocumentEditorControl { horizontalAlignment = HorizontalAlignment.Stretch, verticalAlignment = VerticalAlignment.Stretch };
            t.Show(editor);
            editor.LoadPath(path);
            return editor;
        }

        // The caret at the start of the note's first paragraph.
        private static void PlaceCaret(DocumentEditorControl editor)
        {
            DocumentControl content = editor.children.OfType<DocumentControl>().First();
            content.SetCaret(Find<BlockControl>(content).First(), 0);
        }

        private static string TempFolder()
        {
            string folder = Path.Combine(Path.GetTempPath(), $"aurora-entries-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static string Lists(DocumentEditorControl editor) =>
            string.Join(",", editor.activeDocument.blocks.OfType<NoteBlock>().Select(block => $"{block.listKind}:{block.listMarker}"));

        private static IEnumerable<string> Pictures(DocumentEditorControl editor) =>
            editor.CaretBlock?.spans.Where(span => span.IsPicture).Select(span => span.imageSource) ?? Enumerable.Empty<string>();

        private static bool Shown(Control root, string caption) => Find<LabelControl>(root).Any(label => label.text == caption);

        private static IEnumerable<T> Find<T>(Control root) where T : Control
        {
            foreach (Entity child in root.children)
            {
                if (child is not Control control || control.hidden) continue;
                if (control is T found) yield return found;
                foreach (T deeper in Find<T>(control)) yield return deeper;
            }
        }
    }
}
