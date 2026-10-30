using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Containers.Workspace;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Menus;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;

namespace Thorium.Tests
{
    internal static class StickyTests
    {
        private const string stickyDocument = "sticky-window";

        [A_XSDActionDependency("Sticky.PinPinRestore", "Test")]
        private static IEnumerator<int> PinPinRestore(TestContext t)
        {
            string path = TempNote();
            string before = SessionLayout.scope;
            SessionLayout.scope = "sticky-a";
            WorkspaceControl workspace = new WorkspaceControl
            {
                paneDocument = "tab-pane",
                defaultDocument = "tab-pane",
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            t.Show(workspace);
            WorkspacePageControl page = workspace.AddPage("General", WorkspaceKind.General);
            TabViewControl view = workspace.LoadPane(page);
            TabItemControl tab = SessionLayout.tabFactory(path, false);
            view.AddChild(tab);
            yield return 3;

            yield return t.Click(Find<TabStripButtonControl>(view, _ => true)!, Keys.MouseRight);
            yield return 20;
            ContextMenuControl.Row? submenu = MenuRow("Add to sticky note");
            t.Check(submenu != null, "the tab menu offers Add to sticky note");
            if (submenu == null) yield break;
            yield return t.MoveTo(submenu);
            yield return 20;
            ContextMenuControl.Row? fresh = MenuRow("New sticky");
            t.Check(fresh != null, "its submenu offers New sticky");
            if (fresh == null) yield break;
            yield return t.Click(fresh);
            yield return 3;

            RenderWindow? sticky = Sticky();
            t.Check(sticky != null, "a sticky window opens");
            if (sticky == null) yield break;
            TabViewControl? stuck = TabViewControl.TabViews(sticky.ui.uiRoot).FirstOrDefault();
            DocumentEditorControl source = TabViewControl.EditorOf(tab);
            DocumentEditorControl? copy = stuck?.activeItem != null ? TabViewControl.EditorOf(stuck.activeItem) : null;
            t.Check(view.Items.Contains(tab), "the tab stays where it was");
            t.Check(copy != null && ReferenceEquals(copy.session, source.session) && source.session.views == 2,
                "the sticky holds a second view of the same note");

            ButtonControl pin = Find<ButtonControl>(sticky.ui.uiRoot, b => b.children.OfType<IconControl>().Any(l => l.name == "pin"))!;
            yield return t.Click(pin);
            IconControl pinLabel = pin.children.OfType<IconControl>().First();
            t.Check(sticky.pinned && pinLabel.role == PaletteRole.Accent, "the pin button keeps the sticky on top and lights");

            SessionLayout.ChangeScope("sticky-b");
            yield return 3;
            RenderWindow? kept = Sticky();
            TabViewControl? keptView = kept != null ? TabViewControl.TabViews(kept.ui.uiRoot).FirstOrDefault() : null;
            t.Check(kept != null && kept.sessionScope == SessionLayout.freeScope && kept.pinned, "the sticky is global: another scope keeps it, still pinned");
            t.Check(keptView?.activeItem != null && TabViewControl.FileEditorOf(keptView.activeItem)?.path == path,
                "with the note in it");
            SessionLayout.ChangeScope("sticky-a");
            yield return 3;

            SessionLayout.scope = before;
            foreach (RenderWindow window in Engine.windows.Values.ToList())
                if (window.uiDocument == stickyDocument) Engine.CloseWindow(window);
            t.Show(new StackPanelControl());
            yield return 3;
            File.Delete(path);
        }

        [A_XSDActionDependency("Sticky.Bare", "Test")]
        private static IEnumerator<int> Bare(TestContext t)
        {
            string path = TempNote();
            WorkspaceControl workspace = new WorkspaceControl
            {
                paneDocument = "tab-pane",
                defaultDocument = "tab-pane",
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            t.Show(workspace);
            WorkspacePageControl page = workspace.AddPage("General", WorkspaceKind.General);
            TabViewControl view = workspace.LoadPane(page);
            TabItemControl tab = SessionLayout.tabFactory(path, false);
            view.AddChild(tab);
            yield return 3;

            yield return t.Click(Find<TabStripButtonControl>(view, _ => true)!, Keys.MouseRight);
            yield return 20;
            ContextMenuControl.Row? submenu = MenuRow("Add to sticky note");
            if (submenu == null) { t.Check(false, "the tab menu offers Add to sticky note"); yield break; }
            yield return t.MoveTo(submenu);
            yield return 20;
            ContextMenuControl.Row? fresh = MenuRow("New sticky");
            if (fresh == null) { t.Check(false, "its submenu offers New sticky"); yield break; }
            yield return t.Click(fresh);
            yield return 5;

            RenderWindow? sticky = Sticky();
            TabViewControl? stuck = sticky != null ? TabViewControl.TabViews(sticky.ui.uiRoot).FirstOrDefault() : null;
            DocumentEditorControl? copy = stuck?.activeItem != null ? TabViewControl.EditorOf(stuck.activeItem) : null;
            if (stuck == null || copy == null) { t.Check(false, "a sticky opens with the note in it"); yield break; }
            DocumentEditorControl source = TabViewControl.EditorOf(tab);
            DocumentControl stuckText = Find<DocumentControl>(copy, _ => true)!;
            DocumentControl sourceText = Find<DocumentControl>(source, _ => true)!;

            t.Check(stuck.bare && !stuck.children.OfType<TabToolsControl>().Any(), "the sticky's tab row has no tools");
            t.Check(view.children.OfType<TabToolsControl>().Any(), "the main pane keeps its tools");
            t.Check(copy.bare && stuckText.fitWidth && stuckText.page.mode == PageMode.Pageless && stuckText.header == null,
                "the sticky's view is pageless with no properties header");
            t.Check(stuckText.DesiredSize.X <= copy.arrangedRect.width + 0.5f, "its text fits the window's width");
            t.Check(!source.bare && !sourceText.fitWidth && sourceText.page.mode == PageMode.Paged && source.Page?.mode == PageMode.Paged,
                "the note and its main view stay paged");

            foreach (RenderWindow window in Engine.windows.Values.ToList())
                if (window.uiDocument == stickyDocument) Engine.CloseWindow(window);
            t.Show(new StackPanelControl());
            yield return 3;
            File.Delete(path);
        }

        private static RenderWindow? Sticky() =>
            Engine.windows.Values.FirstOrDefault(w => !w.closeRequested && w.uiDocument == stickyDocument);

        private static ContextMenuControl.Row? MenuRow(string text)
        {
            foreach (RenderWindow window in Engine.windows.Values)
                if (window.ui?.uiRoot != null && Find<ContextMenuControl.Row>(window.ui.uiRoot, r => Caption(r.entry) == text) is ContextMenuControl.Row row)
                    return row;
            return null;
        }

        private static string? Caption(ContextMenuEntry entry) => entry switch
        {
            ContextMenuButton button => button.text,
            ContextMenuSubmenu submenu => submenu.text,
            _ => null
        };

        private static T? Find<T>(Entity entity, Func<T, bool> match) where T : class
        {
            if (entity is T found && match(found)) return found;
            foreach (Entity child in entity.children)
                if (Find(child, match) is T hit) return hit;
            return null;
        }

        private static string TempNote()
        {
            string path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"aurora-sticky-{Guid.NewGuid():N}.axml"));
            File.WriteAllText(path, "<Document><Block><Run Text=\"sticky text\"/></Block></Document>\n");
            return path;
        }
    }
}
