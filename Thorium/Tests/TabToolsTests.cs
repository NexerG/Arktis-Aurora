using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Containers.Workspace;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Sheets;
using ArctisAurora.Core.UI.Tex;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;
using System.Numerics;

namespace Thorium.Tests
{
    internal static class TabToolsTests
    {
        [A_XSDActionDependency("TabTools.Mount", "Test")]
        private static IEnumerator<int> Mount(TestContext t)
        {
            string note = TempFile(".axml", "<Document><Block><Run Text=\"tools\"/></Block></Document>\n");
            string sheet = TempFile(SheetDocument.extension, null);
            WorkspaceControl workspace = ShowWorkspace(t);
            TabViewControl view = workspace.LoadPane();
            TabItemControl noteTab = SessionLayout.tabFactory(note, false);
            TabItemControl sheetTab = SessionLayout.tabFactory(sheet, false);
            view.AddChild(noteTab);
            view.AddChild(sheetTab);
            view.SetActive(sheetTab);
            yield return 2;

            Control noteTools = TabViewControl.FileEditorOf(noteTab).tools!;
            Control sheetTools = TabViewControl.FileEditorOf(sheetTab).tools!;
            t.Check(ReferenceEquals(sheetTools.parent, view) && noteTools.parent == null, "the active sheet's tools are on the tab row, the note's are not");
            t.Check(MathF.Abs(sheetTools.arrangedRect.Right - view.arrangedRect.Right) < 1f && sheetTools.arrangedRect.y == view.arrangedRect.y,
                $"the tools sit at the right end of the tab row: {sheetTools.arrangedRect.Right} vs {view.arrangedRect.Right}");

            view.SetActive(noteTab);
            yield return 2;
            t.Check(ReferenceEquals(noteTools.parent, view) && sheetTools.parent == null, "showing the note swaps its tools in");

            view.CloseTab(noteTab);
            yield return 2;
            t.Check(ReferenceEquals(sheetTools.parent, view), "closing the note brings the sheet's tools back");

            t.Show(new StackPanelControl());
            yield return 2;
            File.Delete(note);
            File.Delete(sheet);
        }

        [A_XSDActionDependency("TabTools.Bullets", "Test")]
        private static IEnumerator<int> Bullets(TestContext t)
        {
            string note = TempFile(".axml", "<Document><Block><Run Text=\"one\"/></Block><Block><Run Text=\"two\"/></Block></Document>\n");
            DocumentEditorControl editor = new DocumentEditorControl { horizontalAlignment = HorizontalAlignment.Stretch, verticalAlignment = VerticalAlignment.Stretch };
            t.Show(editor);
            editor.LoadPath(note);
            yield return 2;

            editor.FocusCaret();
            editor.SelectAll();
            editor.ToggleBullets();
            yield return 2;
            t.Check(Kinds(editor) == "Bullet,Bullet", $"the selected paragraphs become a bulleted list: {Kinds(editor)}");

            editor.ToggleBullets();
            yield return 2;
            t.Check(Kinds(editor) == "None,None", $"again turns them back into text: {Kinds(editor)}");

            editor.Undo();
            yield return 2;
            t.Check(Kinds(editor) == "Bullet,Bullet", $"one undo step brings the list back: {Kinds(editor)}");

            t.Show(new StackPanelControl());
            yield return 2;
            File.Delete(note);
        }

        [A_XSDActionDependency("TabTools.AutoSum", "Test")]
        private static IEnumerator<int> AutoSum(TestContext t)
        {
            string path = TempFile(SheetDocument.extension, null);
            SheetEditorControl editor = ShowSheet(t, path);
            yield return 2;

            editor.Select(0, 0, false);
            editor.Paste("1\n2\n3");
            editor.Select(5, 1, false);
            editor.Paste("4\t5");
            yield return 2;

            editor.Select(3, 0, false);
            editor.AutoSum();
            editor.Select(5, 3, false);
            editor.AutoSum();
            yield return 2;
            t.Check(editor.editLayer.Get(3, 0) == "=SUM(A1:A3)", $"Sum under a column of numbers adds them: {editor.editLayer.Get(3, 0)}");
            t.Check(editor.editLayer.Get(5, 3) == "=SUM(B6:C6)", $"with nothing above it adds the row to its left: {editor.editLayer.Get(5, 3)}");

            t.Show(new StackPanelControl());
            yield return 2;
            File.Delete(path);
        }

        [A_XSDActionDependency("TabTools.FormulaBar", "Test")]
        private static IEnumerator<int> FormulaBar(TestContext t)
        {
            string path = TempFile(SheetDocument.extension, null);
            SheetEditorControl editor = ShowSheet(t, path);
            yield return 2;

            Control bar = (Control)editor.children[0];
            TextBoxControl formula = bar.children.OfType<TextBoxControl>().First();
            editor.Select(1, 2, false);
            yield return 2;
            t.Check(bar.children.OfType<LabelControl>().Any(label => label.text == "C2") && formula.text == "",
                "the formula bar names the active cell and shows what it holds");

            formula.text = "=1+1";
            formula.onCommit!("=1+1");
            yield return 2;
            t.Check(editor.editLayer.Get(1, 2) == "=1+1" && formula.text == "=1+1", "committing the bar writes the cell");

            editor.Undo();
            yield return 2;
            t.Check(editor.editLayer.Get(1, 2) == null && formula.text == "", "undo empties the cell and the bar follows");

            t.Show(new StackPanelControl());
            yield return 2;
            File.Delete(path);
        }

        [A_XSDActionDependency("TabTools.Tex", "Test")]
        private static IEnumerator<int> Tex(TestContext t)
        {
            string path = TempFile(".tex", "\\documentclass{article}\n\\begin{document}\ncat\n\ndog\n\\end{document}\n");
            TexEditorControl editor = new TexEditorControl();
            t.Show(editor);
            editor.LoadPath(path);
            yield return 2;

            editor.SetLive(false);
            RichTextDocument source = editor.source.activeDocument;
            source.InsertText((NoteBlock)source.blocks[2], new DocumentAddress(2, 0), "x");
            for (int i = 0; i < 45; i++) yield return 1;
            t.Check(PreviewHas(editor, "cat") && !PreviewHas(editor, "xcat"), $"with Live off an edit does not recompile: {PreviewText(editor)}");

            editor.Recompile();
            yield return 2;
            t.Check(PreviewHas(editor, "xcat"), $"Build recompiles: {PreviewText(editor)}");

            editor.SetPreviewZoom(150);
            yield return 2;
            t.Check(editor.preview.zoomOverride == 1.5f, "the preview takes its own zoom");

            editor.source.GoTo(4, 0);
            editor.SyncPreview();
            yield return 2;
            t.Check(editor.preview.CaretBlock?.note is NoteBlock block && block.run.text.Contains("dog"),
                "Sync puts the preview on the block the source caret's line made");

            t.Show(new StackPanelControl());
            yield return 2;
            File.Delete(path);
        }

        [A_XSDActionDependency("TabTools.PreviewWheelZoom", "Test")]
        private static IEnumerator<int> PreviewWheelZoom(TestContext t)
        {
            string path = TempFile(".tex", "\\documentclass{article}\n\\begin{document}\ncat\n\\end{document}\n");
            TexEditorControl editor = new TexEditorControl();
            t.Show(editor);
            editor.LoadPath(path);
            DocumentZoomSetting zoom = SettingsRegistry.Get<UISettings>().documentZoom;
            float before = zoom.percent;
            yield return 2;

            editor.SetPreviewZoom(150);
            yield return 2;
            yield return t.Scroll(editor.preview, new Vector2(0f, 1f), new[] { Keys.LeftControl });
            yield return 2;
            t.Check(MathF.Abs(editor.preview.zoomOverride!.Value - 1.6f) < 0.001f && zoom.percent == before,
                $"Ctrl+wheel over a zoomed preview steps the preview's own zoom: {editor.preview.zoomOverride}, setting {zoom.percent}");
            t.Check(HasText(editor.tools, "160%"), "the zoom caption follows");

            t.Show(new StackPanelControl());
            yield return 2;
            File.Delete(path);

            static bool HasText(Control control, string text) =>
                control is LabelControl label && label.text == text || control.children.OfType<Control>().Any(child => HasText(child, text));
        }

        private static WorkspaceControl ShowWorkspace(TestContext t)
        {
            WorkspaceControl workspace = new WorkspaceControl
            {
                paneDocument = "tab-pane",
                defaultDocument = "tab-pane",
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            t.Show(workspace);
            return workspace;
        }

        private static SheetEditorControl ShowSheet(TestContext t, string path)
        {
            SheetEditorControl editor = new SheetEditorControl();
            editor.LoadPath(path);
            t.Show(editor);
            return editor;
        }

        // A temp file with the text, or a blank sheet when text is null.
        private static string TempFile(string extension, string? text)
        {
            string path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"aurora-tools-{Guid.NewGuid():N}{extension}"));
            if (text == null) SheetDocument.Blank("Tools").Save(path);
            else File.WriteAllText(path, text);
            return path;
        }

        private static string Kinds(DocumentEditorControl editor) =>
            string.Join(",", editor.activeDocument.blocks.OfType<NoteBlock>().Select(block => block.listKind));

        private static string PreviewText(TexEditorControl editor) =>
            string.Join("|", editor.preview.activeDocument.blocks.OfType<NoteBlock>().Select(block => block.run.text));

        private static bool PreviewHas(TexEditorControl editor, string text) =>
            editor.preview.activeDocument.blocks.OfType<NoteBlock>().Any(block => block.run.text.Contains(text));
    }
}
