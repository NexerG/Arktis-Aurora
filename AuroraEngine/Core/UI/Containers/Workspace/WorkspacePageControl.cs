using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Containers.Tabs;

namespace ArctisAurora.Core.UI.Containers.Workspace
{
    // What a workspace is for: which files the vault lists and what a new file is.
    [A_XSDType("WorkspaceKind", "UI")]
    public enum WorkspaceKind
    {
        General, Docs, Sheets, LaTeX, Manager, Calendar, Xml
    }

    // One workspace of a WorkspaceControl: a name, a kind and one pane tree.
    public class WorkspacePageControl : PanelControl
    {
        public string title = "";
        public WorkspaceKind kind;

        // the ribbon's picked category and whether the inspector shows
        public string ribbonCategory = "Format";
        public bool inspectorShown = true;

        // the pane last worked in, focused again when the page is shown
        internal TabViewControl? lastFocused;

        public WorkspacePageControl()
        {
            alpha = 0f;
        }

        public bool isShown => parent is WorkspaceControl workspace && ReferenceEquals(workspace.shown, this);

        // The pane tree, or null while the page is empty.
        public Control? content => children.Count > 0 ? children[0] as Control : null;

        // The page a control sits in, or null outside every workspace.
        public static WorkspacePageControl? Of(Control? control)
        {
            for (Control? c = control; c != null; c = c.parent as Control)
                if (c is WorkspacePageControl page) return page;

            return null;
        }
    }
}
