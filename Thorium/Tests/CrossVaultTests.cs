using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Containers.Workspace;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Sheets;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;
using Thorium.Editor;
using Thorium.Editor.CustomControls;

namespace Thorium.Tests
{
    internal static class CrossVaultTests
    {
        [A_XSDActionDependency("CrossVault.Resolve", "Test")]
        private static IEnumerator<int> Resolve(TestContext t)
        {
            string a = TempVault();
            string b = TempVault();
            VaultManifest.AddDependency(a, VaultManifest.IdOf(b), "B");
            string home = Path.Combine(a, "note.axml");

            t.Check(VaultBrowserControl.FindSheet(home, "@B/Budget")?.StartsWith(b, StringComparison.OrdinalIgnoreCase) == true,
                "an @alias sheet reference resolves in the vault the alias names");
            t.Check(VaultBrowserControl.FindNote(home, "@B/note")?.StartsWith(b, StringComparison.OrdinalIgnoreCase) == true,
                "and so does an @alias note link");
            t.Check(VaultBrowserControl.FindSheet(home, "@Nope/Budget") == null && !VaultBrowserControl.VaultMissing(home, "@Nope/Budget"),
                "an alias the manifest doesn't list resolves to nothing and is not a missing vault");

            Unknow(b);
            t.Check(VaultBrowserControl.FindSheet(home, "@B/Budget") == null && VaultBrowserControl.VaultMissing(home, "@B/Budget"),
                "a listed vault this machine doesn't know is missing");

            Forget(a);
            Forget(b);
            yield break;
        }

        [A_XSDActionDependency("CrossVault.Formulas", "Test")]
        private static IEnumerator<int> Formulas(TestContext t)
        {
            string a = TempVault();
            string b = TempVault();
            VaultManifest.AddDependency(a, VaultManifest.IdOf(b), "B");
            VaultManifest.AddDependency(b, VaultManifest.IdOf(a), "A");

            SheetDocument reads = Sheet(a, "Reads", "=[@B/Budget]Data!A1");
            SheetDocument x = Sheet(a, "X", "=[@B/Y]Data!A1");
            Sheet(b, "Y", "=[@A/X]Data!A1");
            SheetDocument readsLoaded = SheetBook.Get(Path.Combine(a, "Reads" + SheetDocument.extension));
            SheetDocument xLoaded = SheetBook.Get(Path.Combine(a, "X" + SheetDocument.extension));
            t.Check(SheetBook.calc.Value(readsLoaded.pages[0], 0, 0).Display() == "10", "a formula reads a cell in a referenced vault");
            t.Check(SheetBook.calc.Value(xLoaded.pages[0], 0, 0).Display() == SheetFormula.cycle, "a cycle across two vaults is #CYCLE!");

            string note = Path.Combine(a, "note.axml");
            t.Check(SheetLinks.Plain(note, "@B/Budget.sheet.axml#Data!A1") == "10", "a note embed reads a referenced vault's sheet");

            Unknow(b);
            Sheet(a, "Later", "=[@B/Budget]Data!A1");
            SheetDocument later = SheetBook.Get(Path.Combine(a, "Later" + SheetDocument.extension));
            t.Check(SheetLinks.Plain(note, "@B/Budget.sheet.axml#Data!A1") == SheetFormula.vault, "with the vault gone a note embed shows #VAULT!");
            t.Check(SheetBook.calc.Value(later.pages[0], 0, 0).Display() == SheetFormula.reference, "and a formula shows #REF!");

            foreach (SheetDocument document in new[] { readsLoaded, xLoaded, later }) SheetBook.Unregister(document);
            foreach (string path in Directory.EnumerateFiles(b, "*" + SheetDocument.extension)) SheetBook.Unregister(SheetBook.Get(path));
            Forget(a);
            Forget(b);
            yield break;
        }

        [A_XSDActionDependency("CrossVault.Index", "Test")]
        private static IEnumerator<int> Index(TestContext t)
        {
            string a = TempVault();
            string b = TempVault();
            VaultManifest.AddDependency(a, VaultManifest.IdOf(b), "B");
            string links = Path.Combine(a, "links.axml");
            File.WriteAllText(links, "<Document><Block><Run Sheet=\"@B/Budget.sheet.axml#Data!A1\"/><Run Text=\"[[@B/note]]\"/></Block></Document>\n");

            string? folder = LinkIndex.cacheFolder;
            LinkIndex.cacheFolder = a + "-cache";
            LinkIndex index = LinkIndex.For(a, VaultManifest.IdOf(a));
            index.Refresh();
            IReadOnlyList<LinkEdge> edges = index.Outgoing(links);
            t.Check(edges.Count == 2 && edges.All(e => e.resolved?.StartsWith(b, StringComparison.OrdinalIgnoreCase) == true),
                "the link index resolves @alias edges into the referenced vault");

            LinkIndex.Close(a);
            LinkIndex.cacheFolder = folder;
            if (Directory.Exists(a + "-cache")) Directory.Delete(a + "-cache", true);
            Forget(a);
            Forget(b);
            yield break;
        }

        [A_XSDActionDependency("CrossVault.Sidebar", "Test")]
        private static IEnumerator<int> Sidebar(TestContext t)
        {
            const string goneId = "0123456789abcdef0123456789abcdef";
            string a = TempVault();
            string b = TempVault();
            string c = TempVault();
            VaultManifest.AddDependency(a, VaultManifest.IdOf(b), "B");
            VaultManifest.AddDependency(b, VaultManifest.IdOf(c), "C");
            VaultManifest.AddDependency(a, goneId, "Gone");
            t.Show(new StackPanelControl());

            VaultWindows.OpenInNewWindow(a);
            RenderWindow? window = VaultWindows.Editors().FirstOrDefault(w => w != Engine.primary && w.roots.Any(r => KnownVaults.SamePath(r, a)));
            t.Check(window != null, "the vault opens in a window");
            if (window == null) yield break;
            yield return 3;

            t.Check(Shows(window, b) && Shows(window, c) && VaultWindows.IsOpen(b) && VaultWindows.IsOpen(c),
                "a referenced vault, and the vault it references, get browsers and count as open");
            t.Check(Labels(window).Contains("via " + VaultWindows.NameOf(a)) && Labels(window).Contains("via " + VaultWindows.NameOf(b)),
                "each referenced group says which vault it came through");
            t.Check(Labels(window).Contains("Gone") && Labels(window).Contains("not on this machine"), "a vault not on this machine shows by its alias");
            TabItemControl tab = SessionLayout.tabFactory(Path.Combine(c, "note.axml"), false);
            t.Check(TabViewControl.FileEditorOf(tab) is not ClosedVaultControl, "a referenced vault's file opens live");
            tab.Destroy();

            string plain = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"aurora-cross-plain-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(plain);
            t.Check(!VaultWindows.Locate(goneId, plain) && !File.Exists(VaultManifest.PathOf(plain)), "a folder with no manifest is refused and left untouched");
            t.Check(!VaultWindows.Locate(goneId, c), "a folder that is another vault is refused");

            Directory.CreateDirectory(Path.GetDirectoryName(VaultManifest.PathOf(plain))!);
            File.WriteAllText(VaultManifest.PathOf(plain), $"<Vault Version=\"1\" Id=\"{goneId}\"/>\n");
            t.Check(VaultWindows.Locate(goneId, plain), "the right vault is taken");
            yield return 3;
            t.Check(Shows(window, plain) && !Labels(window).Contains("not on this machine"), "and its group replaces the missing one");

            string added = TempVault();
            t.Check(VaultWindows.AddReference(a, added) && VaultManifest.AliasTarget(a, VaultWindows.NameOf(added)) == VaultManifest.IdOf(added),
                "adding a referenced vault lists it under its folder name");
            yield return 3;
            t.Check(Shows(window, added), "and shows it");

            VaultWindows.CloseVault(window, a);
            yield return 3;
            t.Check(!VaultBrowserControl.Browsers(window).Any() && !VaultWindows.IsOpen(b) && !VaultWindows.IsOpen(c),
                "closing the vault takes the vaults it references with it");

            VaultWindows.CloseWindow(window);
            yield return 3;
            foreach (string vault in new[] { a, b, c, plain, added }) Forget(vault);
        }

        [A_XSDActionDependency("CrossVault.Rename", "Test")]
        private static IEnumerator<int> Rename(TestContext t)
        {
            string a = TempVault();
            string b = TempVault();
            VaultManifest.AddDependency(a, VaultManifest.IdOf(b), "B");
            string reads = Path.Combine(a, "Reads" + SheetDocument.extension);
            SheetDocument onDisk = Sheet(a, "Reads", "=[@B/Budget]Data!A1");
            onDisk.pages[0].layers[0].Set(1, 0, "=[Budget]Data!A1");
            onDisk.Save(reads);
            Sheet(a, "Loaded", "=[@B/Budget]Data!A1");
            SheetDocument loaded = SheetBook.Get(Path.Combine(a, "Loaded" + SheetDocument.extension));
            string md = Path.Combine(a, "plan.md");
            File.WriteAllText(md, "x ![[@B/Budget.sheet.axml#Data!A1]] y $\\sheet{@B/Budget.sheet.axml#Data!A1}$ z ![[Budget.sheet.axml#Data!A1]]\n");
            string axml = Path.Combine(a, "links.axml");
            File.WriteAllText(axml, "<Document><Block><Run Sheet=\"@B/Budget.sheet.axml#Data!A1\"/></Block></Document>\n");

            t.Show(new StackPanelControl());
            VaultWindows.OpenInNewWindow(a);
            RenderWindow? window = VaultWindows.Editors().FirstOrDefault(w => w != Engine.primary && w.roots.Any(r => KnownVaults.SamePath(r, a)));
            yield return 3;

            VaultBrowserControl.RenameNote(Path.Combine(b, "Budget" + SheetDocument.extension), "Money");
            SheetLayer cells = SheetDocument.Load(reads).pages[0].layers[0];
            t.Check(cells.Get(0, 0)?.Contains("[@B/Money]Data") == true, $"a sheet on disk in the open referencing vault follows the rename: {cells.Get(0, 0)}");
            t.Check(cells.Get(1, 0) == "=[Budget]Data!A1", $"and its reference to its own vault's Budget is left alone: {cells.Get(1, 0)}");
            t.Check(loaded.pages[0].layers[0].Get(0, 0)?.Contains("[@B/Money]Data") == true && SheetBook.calc.Value(loaded.pages[0], 0, 0).Display() == "10",
                "a loaded sheet follows it and still reads the value");
            string text = File.ReadAllText(md);
            t.Check(text.Split("@B/Money.sheet.axml#Data!A1").Length == 3 && text.Contains("![[Budget.sheet.axml#Data!A1]]"),
                $"a markdown embed and its math follow, the own-vault embed doesn't: {text.Trim()}");
            t.Check(File.ReadAllText(axml).Contains("Sheet=\"@B/Money.sheet.axml#Data!A1\""), "an axml note's link follows");

            if (window != null) VaultWindows.CloseWindow(window);
            yield return 3;
            VaultBrowserControl.RenameNote(Path.Combine(b, "Money" + SheetDocument.extension), "Cash");
            t.Check(SheetDocument.Load(reads).pages[0].layers[0].Get(0, 0)?.Contains("[@B/Money]Data") == true,
                "a referencing vault that isn't open is left alone");

            SheetBook.Unregister(loaded);
            foreach (string path in Directory.EnumerateFiles(b, "*" + SheetDocument.extension)) SheetBook.Unregister(SheetBook.Get(path));
            Forget(a);
            Forget(b);
        }

        [A_XSDActionDependency("CrossVault.PageAndShift", "Test")]
        private static IEnumerator<int> PageAndShift(TestContext t)
        {
            string a = TempVault();
            string b = TempVault();
            VaultManifest.AddDependency(a, VaultManifest.IdOf(b), "B");
            string reads = Path.Combine(a, "Reads" + SheetDocument.extension);
            Sheet(a, "Reads", "=[@B/Budget]Data!A2");
            string md = Path.Combine(a, "plan.md");
            File.WriteAllText(md, "x ![[@B/Budget.sheet.axml#Data!A2]]\n");

            t.Show(new StackPanelControl());
            VaultWindows.OpenInNewWindow(a);
            RenderWindow? window = VaultWindows.Editors().FirstOrDefault(w => w != Engine.primary && w.roots.Any(r => KnownVaults.SamePath(r, a)));
            yield return 3;

            SheetDocument budget = SheetBook.Get(Path.Combine(b, "Budget" + SheetDocument.extension));
            budget.pages[0].name = "Figures";
            SheetBook.PageRenamed(budget, "Data", "Figures");
            string? cell = SheetDocument.Load(reads).pages[0].layers[0].Get(0, 0);
            t.Check(cell?.Contains("[@B/Budget]Figures") == true && File.ReadAllText(md).Contains("@B/Budget.sheet.axml#Figures!A2"),
                $"a page rename in the referenced vault follows into the referencing one: {cell}");

            SheetBook.Shifted(budget, budget.pages[0], false, 0, 1);
            cell = SheetDocument.Load(reads).pages[0].layers[0].Get(0, 0);
            t.Check(cell?.EndsWith("!A3") == true && File.ReadAllText(md).Contains("@B/Budget.sheet.axml#Figures!A3"),
                $"and so does an inserted row: {cell}");

            SheetBook.Unregister(budget);
            if (window != null) VaultWindows.CloseWindow(window);
            yield return 3;
            Forget(a);
            Forget(b);
        }

        [A_XSDActionDependency("CrossVault.PasteLink", "Test")]
        private static IEnumerator<int> PasteLink(TestContext t)
        {
            string a = TempVault();
            string b = TempVault();
            string c = TempVault();
            VaultManifest.AddDependency(a, VaultManifest.IdOf(b), "B");
            Sheet(a, "Summary", "");

            StackPanelControl all = new StackPanelControl { orientation = StackPanelControl.Orientation.Horizontal };
            SheetEditorControl summary = new SheetEditorControl { preferredWidth = 300f, preferredHeight = 300f };
            SheetEditorControl fromB = new SheetEditorControl { preferredWidth = 300f, preferredHeight = 300f };
            SheetEditorControl fromC = new SheetEditorControl { preferredWidth = 300f, preferredHeight = 300f };
            summary.LoadPath(Path.Combine(a, "Summary" + SheetDocument.extension));
            fromB.LoadPath(Path.Combine(b, "Budget" + SheetDocument.extension));
            fromC.LoadPath(Path.Combine(c, "Budget" + SheetDocument.extension));
            all.AddChild(summary);
            all.AddChild(fromB);
            all.AddChild(fromC);
            t.Show(all);
            yield return 2;
            SheetLayer cells = summary.document.pages[0].layers[0];

            fromB.Select(0, 0, false);
            fromB.Copy();
            summary.Select(2, 0, false);
            t.Check(summary.PasteLink(), "Paste link takes a copy from another vault");
            t.Check(cells.Get(2, 0)?.Contains("[@B/Budget]Data") == true && SheetBook.calc.Value(summary.document.pages[0], 2, 0).Display() == "10",
                $"and names the file behind the referenced vault's alias: {cells.Get(2, 0)}");
            t.Check(SheetBook.filePrefix?.Invoke(Path.Combine(a, "note.axml"), Path.Combine(b, "Budget" + SheetDocument.extension)) == "@B/",
                "a note in the same vault gets the same prefix");

            fromC.Select(0, 0, false);
            fromC.Copy();
            summary.Select(4, 0, false);
            summary.PasteLink();
            string alias = VaultWindows.NameOf(c);
            t.Check(VaultManifest.AliasTarget(a, alias) == VaultManifest.IdOf(c) && cells.Get(4, 0)?.Contains($"[@{alias}/Budget]Data") == true,
                $"a copy from a vault not yet referenced adds the reference: {cells.Get(4, 0)}");

            t.Show(new StackPanelControl());
            yield return 2;
            foreach (string vault in new[] { a, b, c })
                foreach (string path in Directory.EnumerateFiles(vault, "*" + SheetDocument.extension)) SheetBook.Unregister(SheetBook.Get(path));
            foreach (string vault in new[] { a, b, c }) Forget(vault);
        }

        private static bool Shows(RenderWindow window, string vault) =>
            VaultBrowserControl.Browsers(window).Any(browser => KnownVaults.SamePath(browser.vault, vault));

        private static List<string> Labels(RenderWindow window)
        {
            List<string> labels = new List<string>();
            void Walk(Control control)
            {
                if (control is LabelControl label && label.text != null) labels.Add(label.text);
                foreach (Control child in control.children.OfType<Control>()) Walk(child);
            }
            if (window.ui.uiRoot != null) Walk(window.ui.uiRoot);
            return labels;
        }

        private static SheetDocument Sheet(string vault, string name, string formula)
        {
            SheetDocument sheet = SheetDocument.Blank(name);
            sheet.pages[0].name = "Data";
            sheet.pages[0].layers[0].Set(0, 0, formula);
            sheet.Save(Path.Combine(vault, name + SheetDocument.extension));
            return sheet;
        }

        private static string TempVault()
        {
            string vault = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"aurora-cross-vault-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(vault);
            File.WriteAllText(Path.Combine(vault, "note.axml"), "<Document><Block><Run Text=\"cross vault\"/></Block></Document>\n");
            SheetDocument budget = SheetDocument.Blank("Budget");
            budget.pages[0].name = "Data";
            budget.pages[0].layers[0].Set(0, 0, "10");
            budget.Save(Path.Combine(vault, "Budget" + SheetDocument.extension));
            SettingsRegistry.Get<KnownVaults>().Remember(vault);
            return vault;
        }

        private static void Unknow(string vault) =>
            SettingsRegistry.Get<KnownVaults>().vaults.RemoveAll(v => KnownVaults.SamePath(v.path, vault));

        private static void Forget(string vault)
        {
            Unknow(vault);
            if (Directory.Exists(vault)) Directory.Delete(vault, true);
        }
    }
}
