using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Security;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Containers.Workspace;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Sheets;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.Core.UI.Windows;
using System.Xml.Linq;

namespace Thorium.Tests
{
    internal static class EncryptionTests
    {
        private const int unlockTicks = 600;

        [A_XSDActionDependency("Encryption.LockedTab", "Test")]
        private static IEnumerator<int> LockedTab(TestContext t)
        {
            string path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"aurora-locked-{Guid.NewGuid():N}.axml"));
            using (PasswordKey key = PasswordKey.Derive("pw", 1024, 1, 1, new byte[16]))
            {
                RichTextDocument document = DocumentXml.Parse(XElement.Parse("<Document><Block><Run Text=\"locked text\"/></Block></Document>"));
                document.encryption = key;
                document.Save(path);
            }

            string before = SessionLayout.scope;
            SessionLayout.scope = "locked-a";
            WorkspaceControl workspace = new WorkspaceControl
            {
                paneDocument = "tab-pane",
                defaultDocument = "tab-pane",
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            t.Show(workspace);
            WorkspacePageControl page = workspace.AddPage("General", WorkspaceKind.General);
            workspace.LoadPane(page).AddChild(SessionLayout.tabFactory(path, false));
            yield return 3;

            LockedFileControl? locked = Locked(path);
            t.Check(locked != null, "an encrypted note opens as a locked tab");
            if (locked == null) yield break;

            SessionLayout.ChangeScope("locked-b");
            yield return 3;
            SessionLayout.ChangeScope("locked-a");
            yield return 3;
            locked = Locked(path);
            t.Check(locked != null, "the restored session brings it back locked");
            if (locked == null) yield break;

            TextBoxControl field = Find<TextBoxControl>(locked, _ => true)!;
            field.text = "wrong";
            locked.Unlock();
            for (int i = 0; i < unlockTicks && !WrongShown(locked); i++) yield return 1;
            t.Check(WrongShown(locked) && Locked(path) != null, "a wrong password says so and stays locked");

            field.text = "pw";
            locked.Unlock();
            for (int i = 0; i < unlockTicks && Editor(path) == null; i++) yield return 1;
            yield return 2;

            DocumentEditorControl? editor = Editor(path);
            t.Check(editor != null && editor.session.document.encryption != null, "the right password swaps in the note's editor");
            t.Check(!FileKeys.Has(path), "the key is not remembered unless asked");
            if (editor != null)
            {
                editor.session.Save();
                t.Check(EncryptedFile.IsEncrypted(path) && !File.ReadAllText(path).Contains("locked text"), "saving keeps the file encrypted");
            }

            SessionLayout.scope = before;
            t.Show(new StackPanelControl());
            yield return 3;
            File.Delete(path);
        }

        [A_XSDActionDependency("Encryption.LockedSheets", "Test")]
        private static IEnumerator<int> LockedSheets(TestContext t)
        {
            string folder = Path.Combine(Path.GetTempPath(), $"aurora-locked-sheets-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            Func<string?, string, string?>? before = SheetBook.findSheet;
            SheetBook.findSheet = (_, file) =>
            {
                if (file.EndsWith(SheetDocument.extension, StringComparison.OrdinalIgnoreCase)) file = file[..^SheetDocument.extension.Length];
                string path = Path.Combine(folder, file + SheetDocument.extension);
                return File.Exists(path) ? path : null;
            };
            string PathOf(string name) => Path.Combine(folder, name + SheetDocument.extension);
            PasswordKey Key() => PasswordKey.Derive("pw", 1024, 1, 1, new byte[16]);

            using (PasswordKey key = Key())
            {
                SheetDocument budget = SheetDocument.Blank("Budget");
                budget.pages[0].name = "Data";
                budget.pages[0].layers[0].Set(0, 0, "10");
                budget.encryption = key;
                budget.Save(PathOf("Budget"));

                SheetDocument hidden = SheetDocument.Blank("Hidden");
                hidden.pages[0].layers[0].Set(0, 0, "=[Summary]Main!A1");
                hidden.encryption = key;
                hidden.Save(PathOf("Hidden"));
            }

            SheetDocument summary = SheetDocument.Blank("Summary");
            summary.pages[0].name = "Main";
            summary.pages[0].layers[0].Set(0, 0, "=[Budget]Data!A1*2");
            summary.Save(PathOf("Summary"));

            SheetEditorControl editor = new SheetEditorControl { preferredWidth = 400f, preferredHeight = 300f };
            editor.LoadPath(PathOf("Summary"));
            t.Show(editor);
            yield return 2;
            t.Check(SheetBook.calc.Value(editor.document.pages[0], 0, 0).Display() == SheetFormula.locked, "a formula into a locked sheet is #LOCKED!");

            using (new FileStream(PathOf("Budget"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                SheetBook.calc.RecalcAll();
                t.Check(SheetBook.calc.Value(editor.document.pages[0], 0, 0).Display() == SheetFormula.locked && SheetBook.IsLocked(null, "Budget"),
                    "a recalc answers from the locked-file check without opening the file");
            }

            FileKeys.Use(PathOf("Hidden"), Key(), true);
            FileKeys.Use(PathOf("Budget"), Key(), true);
            SheetBook.calc.RecalcAll();
            t.Check(SheetBook.calc.Value(editor.document.pages[0], 0, 0).Display() == "20", "a remembered key gets past the check");
            File.Move(PathOf("Summary"), PathOf("Report"));
            SheetBook.Renamed(PathOf("Summary"), PathOf("Report"), Directory.EnumerateFiles(folder));
            yield return 2;
            using (PasswordKey key = Key())
                t.Check(SheetDocument.Load(PathOf("Hidden"), key).pages[0].Shown(0, 0) == "=[Report]Main!A1" && EncryptedFile.IsEncrypted(PathOf("Hidden")),
                    "a rename rewrites a locked sheet with its remembered key and keeps it encrypted");
            t.Check(!PasswordWindow.isOpen, "a remembered key asks nothing");

            t.Show(new StackPanelControl());
            yield return 2;
            FileKeys.Clear();
            SheetBook.findSheet = before;
            foreach (string file in Directory.EnumerateFiles(folder))
                SheetBook.Deleted(file);
            Directory.Delete(folder, true);
        }

        [A_XSDActionDependency("Encryption.SealedView", "Test")]
        private static IEnumerator<int> SealedView(TestContext t)
        {
            string path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"aurora-sealed-view-{Guid.NewGuid():N}.axml"));
            RichTextDocument made = DocumentXml.Parse(XElement.Parse(
                "<Document><Block><Run Text=\"above\"/></Block><Block><Run Text=\"hidden\"/></Block><Block><Run Text=\"below\"/></Block></Document>"));
            made.SetSeals(1, [new NoteSeal(PasswordKey.Derive("pw", 1024, 1, 1, new byte[16]))]);
            made.Save(path);

            TabViewControl view = new TabViewControl { horizontalAlignment = HorizontalAlignment.Stretch, verticalAlignment = VerticalAlignment.Stretch };
            t.Show(view);
            view.AddChild(SessionLayout.tabFactory(path, false));
            yield return 3;

            DocumentEditorControl? editor = Editor(path);
            SealedControl? button = editor != null ? Find<SealedControl>(editor, _ => true) : null;
            BlockControl? below = editor != null ? Find<BlockControl>(editor, b => b.note.run.text == "below") : null;
            t.Check(button != null && button.arrangedRect.height > 0f, "a sealed run shows as a laid-out [ENCRYPTED] button");
            t.Check(button != null && below != null && below.arrangedRect.y >= button.arrangedRect.Bottom, "the blocks after it sit below it");
            if (editor == null || button == null) yield break;

            PasswordKey? key = NoteSeals.Unlock(button.note.element, "pw", out byte[] plain);
            editor.session.document.ReplaceNodes(1, 1, NoteSeals.Entries(plain, key!));
            yield return 3;
            t.Check(Find<SealedControl>(editor, _ => true) == null && Find<BlockControl>(editor, b => b.note.run.text == "hidden") != null,
                "unlocked, its blocks take the button's place");

            t.Show(new StackPanelControl());
            yield return 3;
            File.Delete(path);
        }

        private static LockedFileControl? Locked(string path) =>
            TabViewControl.FindOpenDocument(path, out _) is TabItemControl tab ? TabViewControl.FileEditorOf(tab) as LockedFileControl : null;

        private static DocumentEditorControl? Editor(string path) =>
            TabViewControl.FindOpenDocument(path, out _) is TabItemControl tab ? TabViewControl.FileEditorOf(tab) as DocumentEditorControl : null;

        private static bool WrongShown(LockedFileControl locked) =>
            Find<LabelControl>(locked, l => l.text == "Wrong password" && !l.hidden) != null;

        private static T? Find<T>(Entity entity, Func<T, bool> match) where T : class
        {
            if (entity is T found && match(found)) return found;
            foreach (Entity child in entity.children)
                if (Find(child, match) is T hit) return hit;
            return null;
        }
    }
}
