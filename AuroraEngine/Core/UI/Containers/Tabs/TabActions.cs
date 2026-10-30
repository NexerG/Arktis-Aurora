using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Containers.Workspace;
using ArctisAurora.Core.UI.Menus;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;

namespace ArctisAurora.Core.UI.Containers.Tabs
{
    // The tab menu. Every entry acts on the strip button the menu was opened on, because that button
    // is the only thing that knows which tab the pointer was over — its view knows only which tab is
    // active.
    public static class TabActions
    {
        [A_XSDActionDependency("Tab.Close", "UI", "Closes the tab the menu was opened on")]
        public static void Close()
        {
            if (Tab() is TabStripButtonControl next) next.owner.CloseTab(next.item);
        }

        [A_XSDActionDependency("Tab.CloseOthers", "UI", "Closes every tab in the view but this one")]
        public static void CloseOthers()
        {
            if (Tab() is TabStripButtonControl next) next.owner.CloseOthers(next.item);
        }

        [A_XSDActionDependency("Tab.CloseRight", "UI", "Closes every tab after this one in the strip")]
        public static void CloseRight()
        {
            if (Tab() is TabStripButtonControl next) next.owner.CloseToTheRight(next.item);
        }

        [A_XSDActionDependency("Tab.SplitRight", "UI", "Opens a file again in a new pane beside its view; moves a tab with no file there")]
        public static void SplitRight() => Split(SplitViewControl.SplitEdge.Right);

        [A_XSDActionDependency("Tab.SplitDown", "UI", "Opens a file again in a new pane below its view; moves a tab with no file there")]
        public static void SplitDown() => Split(SplitViewControl.SplitEdge.Bottom);

        // A file gets a second view of itself; a tab with no file moves.
        private static void Split(SplitViewControl.SplitEdge edge)
        {
            if (Tab() is not TabStripButtonControl next) return;

            if (CopyOf(next.item) is TabItemControl copy)
            {
                next.owner.SplitOff(copy, edge);
                if (copy.parent == null) copy.Destroy();
                return;
            }

            next.owner.SplitOff(next.item, edge);
        }

        // A second view of the tab's file, at the same place in it; null for a tab with no file.
        private static TabItemControl? CopyOf(TabItemControl item)
        {
            IFileEditor? editor = TabViewControl.FileEditorOf(item);
            if (editor?.path == null || SessionLayout.tabFactory?.Invoke(editor.path, editor is XmlEditorControl) is not TabItemControl copy) return null;

            TabViewControl.FileEditorOf(copy)?.RestoreView(editor.ViewState());
            return copy;
        }

        [A_XSDActionDependency("Tab.MoveToNewWindow", "UI", "Moves this tab into a window of its own")]
        public static void MoveToNewWindow()
        {
            if (Tab() is TabStripButtonControl next) next.owner.TearOff(next.item);
        }

        [A_XSDActionDependency("Tab.PinToSticky", "UI", "Opens a file again in a new sticky window; moves a tab with no file there")]
        public static void PinToSticky()
        {
            if (Tab() is TabStripButtonControl next) Pin(next, null);
        }

        // "Add to sticky note": a new sticky, then every open sticky of this view's kind.
        internal static List<ContextMenuEntry> StickyEntries()
        {
            List<ContextMenuEntry> entries = new List<ContextMenuEntry>();
            if (Tab() is not TabStripButtonControl next || string.IsNullOrEmpty(next.owner.stickyDocument)) return entries;

            entries.Add(new ContextMenuButton("New sticky", () => Pin(next, null)));
            RenderWindow source = UIEngine.WindowOf(next.owner);
            foreach (RenderWindow window in Engine.windows.Values)
            {
                if (window.closeRequested || window.uiDocument != next.owner.stickyDocument) continue;
                if (source == window) continue;

                string caption = TabViewControl.TabViews(window.ui.uiRoot).FirstOrDefault()?.activeItem?.header ?? "Sticky";
                entries.Add(new ContextMenuButton(caption, () => Pin(next, window)));
            }
            return entries;
        }

        private static void Pin(TabStripButtonControl tab, RenderWindow? into)
        {
            if (CopyOf(tab.item) is TabItemControl copy)
            {
                tab.owner.PinToSticky(copy, into);
                if (copy.parent == null) copy.Destroy();
                return;
            }

            tab.owner.PinToSticky(tab.item, into);
        }

        // The strip button the menu was opened on.
        private static TabStripButtonControl? Tab()
        {
            for (Control? c = ContextMenus.target; c != null; c = c.parent as Control)
                if (c is TabStripButtonControl tab) return tab;

            return null;
        }
    }
}
