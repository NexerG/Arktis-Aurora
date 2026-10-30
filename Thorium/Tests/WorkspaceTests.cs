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
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;
using System.Numerics;

namespace Thorium.Tests
{
    internal static class WorkspaceTests
    {
        [A_XSDActionDependency("Workspace.StandaloneFile", "Test")]
        private static IEnumerator<int> StandaloneFile(TestContext t)
        {
            string note = TempNote("alone");
            string document = Engine.primary.uiDocument;
            bool paused = SessionLayout.paused;
            global::Thorium.Thorium.OpenStandalone(note);
            yield return 2;

            TabViewControl? tabs = Engine.primary.ui.uiRoot.FindByName("Tabs") as TabViewControl;
            t.Check(Engine.primary.uiDocument == "tab-window" && SessionLayout.paused, "the primary window is a bare tab window and the layout is not recorded");
            t.Check(tabs != null && tabs.Items.Count() == 1 && TabViewControl.FileEditorOf(tabs.Items.First())?.path == note,
                $"its one tab holds the file: {(tabs == null ? "no tabs" : string.Join(",", tabs.Items.Select(item => TabViewControl.FileEditorOf(item)?.path)))}");

            t.Show(new StackPanelControl());
            Engine.primary.uiDocument = document;
            SessionLayout.paused = paused;
            yield return 1;
            File.Delete(note);
        }

        [A_XSDActionDependency("Workspace.Switch", "Test")]
        private static IEnumerator<int> Switch(TestContext t)
        {
            string note = TempNote("shared");
            WorkspaceControl workspace = ShowWorkspace(t);
            WorkspacePageControl a = workspace.AddPage("General", WorkspaceKind.General);
            workspace.LoadPane(a).AddChild(SessionLayout.tabFactory(note, false));
            WorkspacePageControl b = workspace.AddPage("Docs", WorkspaceKind.Docs);
            workspace.LoadPane(b).AddChild(SessionLayout.tabFactory(note, false));
            yield return 2;

            DocumentEditorControl inA = Editor(a), inB = Editor(b);
            t.Check(ReferenceEquals(workspace.shown, a) && !a.hidden && b.hidden, "the first workspace is shown and the second hidden");
            t.Check(ReferenceEquals(inA.session, inB.session) && inA.session.views == 2, "a note open in two workspaces is one session with two views");

            workspace.Show(b);
            yield return 2;
            t.Check(ReferenceEquals(workspace.shown, b) && a.hidden && !b.hidden && inB.arrangedRect.width > 0f,
                "showing the second hides the first and lays the second out");
            t.Check(ReferenceEquals(WorkspacePageControl.Of(TabViewControl.focused), b), "the shown workspace's pane takes the focus");

            inB.session.document.InsertText((NoteBlock)inB.session.document.blocks[0], new DocumentAddress(0, 0), "x");
            workspace.Show(a);
            yield return 2;
            t.Check(((NoteBlock)inA.session.document.blocks[0]).run.text == "xshared" && inA.arrangedRect.width > 0f,
                "an edit made in the hidden workspace is there when it is shown again");

            t.Show(new StackPanelControl());
            yield return 2;
            File.Delete(note);
        }

        [A_XSDActionDependency("Workspace.Menu", "Test")]
        private static IEnumerator<int> Menu(TestContext t)
        {
            string note = TempNote("menu");
            WorkspaceControl workspace = ShowWorkspace(t);
            WorkspacePageControl first = workspace.AddPage("General", WorkspaceKind.General);
            workspace.LoadPane(first).AddChild(SessionLayout.tabFactory(note, false));
            yield return 2;

            WorkspaceActions.Duplicate();
            yield return 2;
            WorkspacePageControl copy = workspace.shown!;
            t.Check(workspace.Pages.Count() == 2 && !ReferenceEquals(copy, first) && copy.title == "General" && Paths(copy) == note,
                $"Duplicate adds a workspace with the same files and shows it: {Paths(copy)}");

            WorkspaceActions.NewSheets();
            yield return 2;
            t.Check(workspace.Pages.Count() == 3 && workspace.shown!.kind == WorkspaceKind.Sheets
                && TabViewControl.TabViews(workspace.shown).Count() == 1 && Paths(workspace.shown) == "",
                "New Sheets workspace adds an empty pane of that kind and shows it");

            WorkspaceActions.Close();
            yield return 2;
            WorkspaceActions.Close();
            yield return 2;
            t.Check(workspace.Pages.Count() == 1 && ReferenceEquals(workspace.shown, first) && Editor(first).session.views == 1,
                "Close drops a workspace and its views");

            WorkspaceActions.Close();
            yield return 2;
            t.Check(workspace.Pages.Count() == 1 && Paths(first) == note, "the last workspace stays");

            t.Show(new StackPanelControl());
            yield return 2;
            File.Delete(note);
        }

        [A_XSDActionDependency("Workspace.SessionRoundTrip", "Test")]
        private static IEnumerator<int> SessionRoundTrip(TestContext t)
        {
            string note = TempNote("kept");
            string sheet = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"aurora-ws-{Guid.NewGuid():N}{SheetDocument.extension}"));
            SheetDocument.Blank("Budget").Save(sheet);
            string before = SessionLayout.scope;

            WorkspaceControl workspace = ShowWorkspace(t);
            SessionLayout.scope = "ws-a";
            WorkspacePageControl a = workspace.AddPage("General", WorkspaceKind.General);
            workspace.LoadPane(a).AddChild(SessionLayout.tabFactory(note, false));
            WorkspacePageControl b = workspace.AddPage("Sheets", WorkspaceKind.Sheets);
            workspace.LoadPane(b).AddChild(SessionLayout.tabFactory(sheet, false));
            workspace.Rename(b, "Budget");
            workspace.Show(b);
            yield return 2;

            bool restored = SessionLayout.ChangeScope("ws-b");
            yield return 2;
            t.Check(!restored && workspace.Pages.Count() == 1 && Paths(workspace.shown!) == "", "a scope with nothing recorded gets one empty workspace");

            restored = SessionLayout.ChangeScope("ws-a");
            yield return 2;
            List<WorkspacePageControl> pages = workspace.Pages.ToList();
            t.Check(restored && pages.Count == 2 && pages[0].title == "General" && pages[0].kind == WorkspaceKind.General
                && pages[1].title == "Budget" && pages[1].kind == WorkspaceKind.Sheets && ReferenceEquals(workspace.shown, pages[1]),
                "both workspaces come back with their names, kinds and which one was shown");
            t.Check(pages.Count == 2 && Paths(pages[0]) == note && Paths(pages[1]) == sheet,
                $"and each keeps its own files: {string.Join(" | ", pages.Select(Paths))}");

            SessionLayout.scope = before;
            t.Show(new StackPanelControl());
            yield return 2;
            File.Delete(note);
            File.Delete(sheet);
        }

        [A_XSDActionDependency("Workspace.LegacySession", "Test")]
        private static IEnumerator<int> LegacySession(TestContext t)
        {
            string note = TempNote("legacy");
            string before = SessionLayout.scope;
            SessionLayout layout = SettingsRegistry.Get<SessionLayout>();

            SessionPane leaf = new SessionPane();
            leaf.tabs.Add(new SessionTab { path = note });
            SessionWindow window = new SessionWindow { primary = true, width = 800, height = 600 };
            window.panes.Add(leaf);
            SessionScope legacy = new SessionScope { key = "ws-legacy" };
            legacy.windows.Add(window);
            layout.scopes.Add(legacy);

            WorkspaceControl workspace = ShowWorkspace(t);
            SessionLayout.scope = "ws-other";
            workspace.LoadEmpty();
            yield return 2;

            bool restored = SessionLayout.ChangeScope("ws-legacy");
            yield return 2;
            t.Check(restored && workspace.Pages.Count() == 1 && workspace.shown!.kind == WorkspaceKind.General && Paths(workspace.shown) == note,
                "a record from before workspaces opens as one General workspace");

            SessionLayout.ChangeScope("ws-other");
            yield return 2;
            SessionWindow saved = layout.scopes.First(s => s.key == "ws-legacy").windows.First(w => w.primary);
            t.Check(saved.panes.Count == 0 && saved.workspaces.Count == 1 && saved.workspaces[0].panes.Count == 1
                && saved.workspaces[0].panes[0].tabs.Count == 1 && saved.workspaces[0].panes[0].tabs[0].path == note,
                "it is written back as a workspace, with its tab");

            SessionLayout.scope = before;
            t.Show(new StackPanelControl());
            yield return 2;
            File.Delete(note);
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

        [A_XSDActionDependency("Workspace.Reorder", "Test")]
        private static IEnumerator<int> Reorder(TestContext t)
        {
            (WorkspaceBarControl bar, WorkspaceControl workspace) = ShowBar(t);
            WorkspacePageControl a = AddEmpty(workspace, "A"), b = AddEmpty(workspace, "B"), c = AddEmpty(workspace, "C");
            yield return 3;

            List<WorkspaceBarControl.WorkspaceButton> tabs = Tabs(bar);
            t.Check(tabs.Count == 3, "the bar shows a tab per workspace");
            if (tabs.Count != 3) yield break;

            LayoutRect last = tabs[2].arrangedRect;
            Vector2 pastLast = new Vector2(last.x + last.width - 2f, last.y + last.height * 0.5f);
            bar.DraggingOver(tabs[0], pastLast);
            t.Check(Marker(bar) is PanelControl marker && bar.children.IndexOf(marker) == 3, "the drop marker sits in the gap past the last tab");
            bar.DraggingOverEnd(tabs[0]);
            t.Check(Marker(bar) == null, "and goes when the drag leaves");

            yield return t.Drag(tabs[0], pastLast, 24);
            yield return 3;
            t.Check(workspace.Pages.SequenceEqual(new[] { b, c, a }) && ReferenceEquals(workspace.shown, a) && Marker(bar) == null,
                $"dragging the first tab past the last moves it there and shows it: {string.Join(",", workspace.Pages.Select(p => p.title))}");

            tabs = Tabs(bar);
            LayoutRect first = tabs[0].arrangedRect;
            Vector2 nudge = new Vector2(first.x + first.width * 0.5f + 4f, first.y + first.height * 0.5f);
            yield return t.Drag(tabs[0], nudge);
            yield return 3;
            t.Check(workspace.Pages.SequenceEqual(new[] { b, c, a }) && ReferenceEquals(workspace.shown, b),
                "a press that moves less than the threshold only shows the workspace");

            t.Show(new StackPanelControl());
            yield return 2;
        }

        [A_XSDActionDependency("Workspace.MoveAcross", "Test")]
        private static IEnumerator<int> MoveAcross(TestContext t)
        {
            WorkspaceControl left = new WorkspaceControl { paneDocument = "tab-pane", widthStar = 1f, verticalAlignment = VerticalAlignment.Stretch };
            WorkspaceControl right = new WorkspaceControl { paneDocument = "tab-pane", widthStar = 1f, verticalAlignment = VerticalAlignment.Stretch };
            StackPanelControl row = new StackPanelControl
            {
                orientation = StackPanelControl.Orientation.Horizontal,
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            row.AddChild(left);
            row.AddChild(right);
            t.Show(row);
            WorkspacePageControl a = AddEmpty(left, "A"), b = AddEmpty(left, "B"), c = AddEmpty(right, "C");
            yield return 2;

            right.Move(a, 0);
            yield return 2;
            t.Check(right.Pages.SequenceEqual(new[] { a, c }) && ReferenceEquals(right.shown, a) && !a.hidden && c.hidden,
                "a workspace moved into another lands at the gap and is shown there");
            t.Check(left.Pages.SequenceEqual(new[] { b }) && ReferenceEquals(left.shown, b) && !b.hidden,
                "the one it left shows its neighbour");

            right.Move(b, 0);
            yield return 2;
            t.Check(left.Pages.SequenceEqual(new[] { b }) && right.Pages.Count() == 2, "the main window's last workspace stays");

            t.Show(new StackPanelControl());
            yield return 2;
        }

        [A_XSDActionDependency("Workspace.TearOff", "Test")]
        private static IEnumerator<int> TearOff(TestContext t)
        {
            (WorkspaceBarControl bar, WorkspaceControl workspace) = ShowBar(t);
            WorkspacePageControl a = AddEmpty(workspace, "A");
            yield return 3;

            Tabs(bar)[0].OnDragStop(false);
            yield return 3;
            t.Check(Torn() == null && workspace.Pages.SequenceEqual(new[] { a }), "a window's only workspace is not torn off");

            WorkspacePageControl b = AddEmpty(workspace, "B");
            yield return 3;
            Tabs(bar)[1].OnDragStop(false);
            yield return 3;

            RenderWindow? torn = Torn();
            WorkspaceControl? there = torn != null ? WorkspaceControl.In(torn.ui.uiRoot) : null;
            t.Check(there != null && there.Pages.SequenceEqual(new[] { b }) && ReferenceEquals(there.shown, b),
                "a workspace dropped outside every window opens in a window of its own, alone");
            t.Check(workspace.Pages.SequenceEqual(new[] { a }) && ReferenceEquals(workspace.shown, a), "and leaves the main window");
            WorkspaceBarControl? tornBar = torn != null ? Find<WorkspaceBarControl>(torn.ui.uiRoot) : null;
            t.Check(tornBar != null && Tabs(tornBar).Count == 1, "the new window's bar shows it");
            if (torn == null) yield break;

            workspace.Move(b, 1);
            yield return 3;
            t.Check(workspace.Pages.SequenceEqual(new[] { a, b }) && torn.closeRequested,
                "moving it back closes the window it left empty");

            t.Show(new StackPanelControl());
            yield return 2;
        }

        [A_XSDActionDependency("Workspace.Reset", "Test")]
        private static IEnumerator<int> Reset(TestContext t)
        {
            string note = TempNote("reset");
            WorkspaceControl workspace = ShowWorkspace(t);
            WorkspacePageControl docs = workspace.AddPage("Docs", WorkspaceKind.Docs);
            workspace.LoadPane(docs).AddChild(SessionLayout.tabFactory(note, false));
            AddEmpty(workspace, "Extra");
            RenderWindow sticky = OpenWithWorkspace("sticky-window");
            RenderWindow torn = OpenWithWorkspace("tab-window");
            yield return 3;

            SessionLayout.Reset();
            yield return 3;
            List<WorkspacePageControl> pages = workspace.Pages.ToList();
            t.Check(pages.Count == 1 && pages[0].kind == WorkspaceKind.General && ReferenceEquals(workspace.shown, pages[0])
                && TabViewControl.TabViews(pages[0]).Count() == 1 && Paths(pages[0]) == "",
                $"the window is back to its first-run workspace with nothing open: {string.Join(" | ", pages.Select(p => p.title + ":" + Paths(p)))}");
            t.Check(torn.closeRequested, "a torn-off window closes");
            t.Check(!sticky.closeRequested, "a sticky stays");

            Engine.CloseWindow(sticky);
            t.Show(new StackPanelControl());
            yield return 3;
            File.Delete(note);
        }

        private static RenderWindow OpenWithWorkspace(string document)
        {
            RenderWindow window = Engine.OpenWindow($"reset-{document}", 300, 300, 100, 100);
            window.uiDocument = document;
            window.ui.uiRoot = (WindowRoot)Control.ParseXML(document);
            WorkspaceControl.In(window.ui.uiRoot)?.LoadDefault();
            return window;
        }

        private static (WorkspaceBarControl bar, WorkspaceControl workspace) ShowBar(TestContext t)
        {
            WorkspaceBarControl bar = new WorkspaceBarControl { preferredHeight = 26f, tearOffDocument = "tab-window" };
            WorkspaceControl workspace = new WorkspaceControl { paneDocument = "tab-pane", heightStar = 1f, horizontalAlignment = HorizontalAlignment.Stretch };
            StackPanelControl column = new StackPanelControl
            {
                orientation = StackPanelControl.Orientation.Vertical,
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            column.AddChild(bar);
            column.AddChild(workspace);
            t.Show(column);
            return (bar, workspace);
        }

        private static WorkspacePageControl AddEmpty(WorkspaceControl workspace, string title)
        {
            WorkspacePageControl page = workspace.AddPage(title, WorkspaceKind.General);
            workspace.LoadPane(page);
            return page;
        }

        private static List<WorkspaceBarControl.WorkspaceButton> Tabs(WorkspaceBarControl bar) =>
            bar.children.OfType<WorkspaceBarControl.WorkspaceButton>().ToList();

        private static PanelControl? Marker(WorkspaceBarControl bar) =>
            bar.children.OfType<PanelControl>().FirstOrDefault(p => p.GetType() == typeof(PanelControl));

        private static RenderWindow? Torn() =>
            Engine.windows.FirstOrDefault(w => !w.Value.closeRequested && w.Key.StartsWith("workspace-")).Value;

        private static T? Find<T>(Control control) where T : Control
        {
            if (control is T found) return found;
            foreach (Entity child in control.children)
                if (child is Control childControl && Find<T>(childControl) is T inner) return inner;
            return null;
        }

        private static string TempNote(string text)
        {
            string path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"aurora-ws-{Guid.NewGuid():N}.axml"));
            File.WriteAllText(path, $"<Document><Block><Run Text=\"{text}\"/></Block></Document>\n");
            return path;
        }

        private static DocumentEditorControl Editor(WorkspacePageControl page) =>
            TabViewControl.EditorOf(TabViewControl.TabViews(page).First().Items.First());

        private static string Paths(WorkspacePageControl page) => string.Join(",", TabViewControl.TabViews(page)
            .SelectMany(view => view.Items).Select(item => TabViewControl.FileEditorOf(item)?.path));
    }
}
