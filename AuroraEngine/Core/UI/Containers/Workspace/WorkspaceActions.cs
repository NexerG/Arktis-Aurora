using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Menus;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.EngineWork;

namespace ArctisAurora.Core.UI.Containers.Workspace
{
    // The workspace menus. An entry opened on a workspace tab acts on that workspace; one opened
    // anywhere else acts on the shown workspace of the menu's window.
    public static class WorkspaceActions
    {
        [A_XSDActionDependency("Workspace.NewGeneral", "UI", "Adds a General workspace and shows it")]
        public static void NewGeneral() => New(WorkspaceKind.General);

        [A_XSDActionDependency("Workspace.NewDocs", "UI", "Adds a Docs workspace and shows it")]
        public static void NewDocs() => New(WorkspaceKind.Docs);

        [A_XSDActionDependency("Workspace.NewSheets", "UI", "Adds a Sheets workspace and shows it")]
        public static void NewSheets() => New(WorkspaceKind.Sheets);

        [A_XSDActionDependency("Workspace.NewLaTeX", "UI", "Adds a LaTeX workspace and shows it")]
        public static void NewLaTeX() => New(WorkspaceKind.LaTeX);

        [A_XSDActionDependency("Workspace.NewManager", "UI", "Adds a Manager workspace and shows it")]
        public static void NewManager() => New(WorkspaceKind.Manager);

        [A_XSDActionDependency("Workspace.NewCalendar", "UI", "Adds a Calendar workspace and shows it")]
        public static void NewCalendar() => New(WorkspaceKind.Calendar);

        [A_XSDActionDependency("Workspace.NewXml", "UI", "Adds an XML workspace and shows it")]
        public static void NewXml() => New(WorkspaceKind.Xml);

        [A_XSDActionDependency("Workspace.Duplicate", "UI", "Adds a workspace holding the same files in the same arrangement")]
        public static void Duplicate()
        {
            if (Target() is not WorkspacePageControl page || page.parent is not WorkspaceControl workspace) return;
            workspace.Show(SessionLayout.Duplicate(workspace, page));
        }

        [A_XSDActionDependency("Workspace.Rename", "UI", "Renames the workspace in place")]
        public static void Rename()
        {
            if (Target() is WorkspacePageControl page) WorkspaceBarControl.BeginRename(page);
        }

        [A_XSDActionDependency("Workspace.Close", "UI", "Closes the workspace and its tabs; the last one stays")]
        public static void Close()
        {
            if (Target() is not WorkspacePageControl page || page.parent is not WorkspaceControl workspace) return;
            if (workspace.Pages.Count() < 2) return;

            List<TabItemControl> tabs = TabViewControl.TabViews(page).SelectMany(view => view.Items).ToList();
            CloseNext(tabs, 0, () => workspace.RemovePage(page));
        }

        [A_XSDActionDependency("Session.Reset", "UI", "Puts every workspace back to its first-run arrangement; stickies stay")]
        public static void ResetUI() => Engine.Post(() => NoteActions.SettleAll(SessionLayout.Reset));

        // One tab at a time, each from the previous one's completion, because the naming prompt is one window.
        private static void CloseNext(List<TabItemControl> tabs, int index, Action onClosed)
        {
            if (index >= tabs.Count) { onClosed(); return; }

            if (tabs[index].parent is TabViewControl view) view.CloseTab(tabs[index], () => CloseNext(tabs, index + 1, onClosed));
            else CloseNext(tabs, index + 1, onClosed);
        }

        private static void New(WorkspaceKind kind)
        {
            if (Workspace() is not WorkspaceControl workspace) return;

            WorkspacePageControl page = workspace.AddPage(kind.ToString(), kind);
            workspace.LoadPane(page);
            workspace.Show(page);
        }

        // The workspace tab the menu was opened on, or the shown workspace.
        private static WorkspacePageControl? Target()
        {
            for (Control? c = ContextMenus.target; c != null; c = c.parent as Control)
                if (c is WorkspaceBarControl.WorkspaceButton tab) return tab.page;

            return Workspace()?.shown;
        }

        private static WorkspaceControl? Workspace()
        {
            Control? root = ContextMenus.target;
            while (root?.parent is Control up) root = up;
            return WorkspaceControl.In(root ?? UIActions.Invoking().ui.uiRoot) ?? WorkspaceControl.In(Engine.primary.ui.uiRoot);
        }
    }
}
