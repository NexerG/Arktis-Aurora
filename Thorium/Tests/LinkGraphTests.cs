using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Containers.Workspace;
using ArctisAurora.Core.UI.Graph;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Sheets;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;
using System.Numerics;
using Thorium.Editor;

namespace Thorium.Tests
{
    internal static class LinkGraphTests
    {
        [A_XSDActionDependency("LinkGraph.Backlinks", "Test")]
        private static IEnumerator<int> Backlinks(TestContext t)
        {
            string a = TempVault();
            string b = TempVault();
            VaultManifest.AddDependency(a, VaultManifest.IdOf(b), "B");
            string x = Path.Combine(a, "x.md");
            string y = Path.Combine(a, "y.md");
            File.WriteAllText(x, "see [[y]]\n");
            File.WriteAllText(y, "plain\n");
            SheetDocument calc = SheetDocument.Blank("Calc");
            calc.pages[0].layers[0].Set(0, 0, "=[Budget]Data!A1");
            calc.Save(Path.Combine(a, "Calc" + SheetDocument.extension));
            File.WriteAllText(Path.Combine(a, "cross.md"), "![[@B/Budget.sheet.axml#Data!A1]]\n");

            string? folder = LinkIndex.cacheFolder;
            LinkIndex.cacheFolder = a + "-cache";
            t.Show(new StackPanelControl());
            VaultWindows.OpenInNewWindow(a);
            RenderWindow? window = VaultWindows.Editors().FirstOrDefault(w => w != Engine.primary && w.roots.Any(r => KnownVaults.SamePath(r, a)));
            t.Check(window != null, "the vault opens in a window");
            if (window == null) yield break;
            yield return 3;

            LinksPanelControl panel = new LinksPanelControl();
            panel.Show(y);
            t.Check(Labels(panel).Contains("Backlinks (1)") && Labels(panel).Contains("x"), "a note lists the note linking to it");
            panel.Show(Path.Combine(a, "Budget" + SheetDocument.extension));
            t.Check(Labels(panel).Contains("Calc") && Labels(panel).Contains("formula"), "a sheet lists the sheet whose formula reads it");
            panel.Show(Path.Combine(b, "Budget" + SheetDocument.extension));
            t.Check(Labels(panel).Contains("cross") && Labels(panel).Contains("embed"), "a file in a referenced vault lists the referencing vault's backlink");
            panel.Show(x);
            t.Check(Labels(panel).Contains("Outgoing (1)") && Labels(panel).Contains("y"), "a note lists its outgoing link");

            File.WriteAllText(x, "no links any more\n");
            panel.Show(y);
            t.Check(Labels(panel).Contains("Backlinks (0)"), "a backlink goes once its source drops the link");
            panel.Destroy();

            VaultWindows.CloseWindow(window);
            yield return 3;
            t.Check(LinkIndex.indexOf?.Invoke(y) == null, "a vault no window shows has no index");

            LinkIndex.cacheFolder = folder;
            if (Directory.Exists(a + "-cache")) Directory.Delete(a + "-cache", true);
            Forget(a);
            Forget(b);
        }

        [A_XSDActionDependency("LinkGraph.Layout", "Test")]
        private static IEnumerator<int> Layout(TestContext t)
        {
            (int, int)[] links = { (0, 1), (1, 2), (2, 0), (3, 4) };
            GraphLayout layout = new GraphLayout(6, links);
            int steps = 0;
            while (!layout.settled && steps < 2000)
            {
                layout.Step();
                steps++;
            }
            t.Check(layout.settled && steps < 1000, $"the layout settles: {steps} steps");

            float linked = links.Average(l => Vector2.Distance(layout.position[l.Item1], layout.position[l.Item2]));
            (int, int)[] apart = { (0, 3), (1, 4), (2, 5), (0, 5) };
            float unlinked = apart.Average(l => Vector2.Distance(layout.position[l.Item1], layout.position[l.Item2]));
            t.Check(linked < unlinked, $"linked nodes sit closer than unlinked ones: {linked} against {unlinked}");

            GraphLayout pinned = new GraphLayout(6, links);
            pinned.Pin(5, new Vector2(100f, 100f));
            for (int i = 0; i < 50; i++) pinned.Step();
            t.Check(pinned.position[5] == new Vector2(100f, 100f), "a pinned node stays put");

            GraphLayout counted = new GraphLayout(6, links);
            counted.Step();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) counted.Step();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            t.Check(allocated == 0, $"a step allocates nothing: {allocated} bytes over 100");
            yield break;
        }

        [A_XSDActionDependency("LinkGraph.View", "Test")]
        private static IEnumerator<int> View(TestContext t)
        {
            string a = TempVault();
            string b = TempVault();
            VaultManifest.AddDependency(a, VaultManifest.IdOf(b), "B");
            string y = Path.Combine(a, "y.md");
            File.WriteAllText(Path.Combine(a, "x.md"), "see [[y]] and [[ghost]]\n");
            File.WriteAllText(y, "plain\n");
            File.WriteAllText(Path.Combine(a, "lone.md"), "nothing\n");
            File.WriteAllText(Path.Combine(a, "cross.md"), "![[@B/Budget.sheet.axml#Data!A1]]\n");
            string graph = Path.Combine(a, "View" + GraphDocument.extension);
            GraphDocument.Blank("View").Save(graph);

            string? folder = LinkIndex.cacheFolder;
            LinkIndex.cacheFolder = a + "-cache";
            t.Show(new StackPanelControl());
            VaultWindows.OpenInNewWindow(a);
            RenderWindow? window = VaultWindows.Editors().FirstOrDefault(w => w != Engine.primary && w.roots.Any(r => KnownVaults.SamePath(r, a)));
            t.Check(window != null, "the vault opens in a window");
            if (window == null) yield break;
            yield return 3;

            TabItemControl tab = SessionLayout.tabFactory(graph, false);
            GraphEditorControl? editor = TabViewControl.FileEditorOf(tab) as GraphEditorControl;
            t.Check(editor != null, "a graph file opens in the graph editor");
            if (editor == null) yield break;
            t.Check(editor.nodeCount == 7, $"every file, the unresolved link and the referenced vault's file are nodes, the graph file isn't: {editor.nodeCount}");

            editor.document.showOrphans = false;
            editor.Rebuild();
            t.Check(editor.nodeCount == 5, $"hiding orphans drops the two unlinked files: {editor.nodeCount}");
            editor.document.showOrphans = true;
            editor.Rebuild();

            t.Show(tab);
            yield return 400;
            t.Check(editor.layout.settled, "the shown graph settles");

            int node = Enumerable.Range(0, editor.nodeCount).First(i => string.Equals(editor.KeyOf(i), y, StringComparison.OrdinalIgnoreCase));
            Vector2 from = editor.Screen(node);
            Vector2 to = from + new Vector2(40f, 30f);
            yield return t.Drag(editor, from, to);
            yield return 2;
            t.Check(editor.document.unsaved && editor.document.pins.ContainsKey("y.md"), "dragging a node pins it and marks the graph unsaved");
            t.Check(Vector2.Distance(editor.Screen(node), to) < 1f, $"the node stays where it was dropped: {editor.Screen(node)} against {to}");

            editor.Save();
            GraphDocument reloaded = GraphDocument.Load(graph);
            t.Check(reloaded.pins.TryGetValue("y.md", out Vector2 pin) && pin == editor.layout.position[node], "the pin survives a save and reload");

            Action<string>? opener = LinkIndex.openFile;
            string? opened = null;
            LinkIndex.openFile = file => opened = file;
            yield return t.Click(editor, editor.Screen(node));
            yield return 3;
            LinkIndex.openFile = opener;
            t.Check(string.Equals(opened, y, StringComparison.OrdinalIgnoreCase), $"clicking a node opens its file: {opened}");

            t.Show(new StackPanelControl());
            tab.Destroy();
            VaultWindows.CloseWindow(window);
            yield return 3;

            LinkIndex.cacheFolder = folder;
            if (Directory.Exists(a + "-cache")) Directory.Delete(a + "-cache", true);
            Forget(a);
            Forget(b);
        }

        private static List<string> Labels(Control root)
        {
            List<string> labels = new List<string>();
            void Walk(Control control)
            {
                if (control is LabelControl label && label.text != null) labels.Add(label.text);
                foreach (Control child in control.children.OfType<Control>()) Walk(child);
            }
            Walk(root);
            return labels;
        }

        private static string TempVault()
        {
            string vault = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"aurora-graph-vault-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(vault);
            SheetDocument budget = SheetDocument.Blank("Budget");
            budget.pages[0].name = "Data";
            budget.pages[0].layers[0].Set(0, 0, "10");
            budget.Save(Path.Combine(vault, "Budget" + SheetDocument.extension));
            SettingsRegistry.Get<KnownVaults>().Remember(vault);
            return vault;
        }

        private static void Forget(string vault)
        {
            SettingsRegistry.Get<KnownVaults>().vaults.RemoveAll(v => KnownVaults.SamePath(v.path, vault));
            if (Directory.Exists(vault)) Directory.Delete(vault, true);
        }
    }
}
