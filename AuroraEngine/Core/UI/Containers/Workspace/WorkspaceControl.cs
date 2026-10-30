using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Tex;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;
using System.Numerics;

namespace ArctisAurora.Core.UI.Containers.Workspace
{
    // Where a window's panes go: one page per workspace, one shown at a time. The document declares
    // what fills it on a first run; the saved session fills it on every run after that.
    [A_XSDType("Workspace", "UI", maxChildren: 1)]
    public class WorkspaceControl : ContainerControl
    {
        [A_XSDElementProperty("Default", "UI", "UI document built into this workspace when the session has nothing for its window.")]
        public string defaultDocument = "";

        [A_XSDElementProperty("Pane", "UI", "UI document holding one empty pane, which a restored session splits to rebuild its arrangement.")]
        public string paneDocument = "";

        [A_XSDElementProperty("FirstRun", "UI", "Kinds of the workspaces a window starts with when nothing was saved, separated by spaces; the first takes Default. General alone when left out.")]
        public string firstRun = "";

        public WorkspacePageControl? shown { get; private set; }

        // Raised when a workspace is added, removed, renamed or shown.
        public static event Action<WorkspaceControl>? changed;

        public WorkspaceControl()
        {
            alpha = 0f;
        }

        public IEnumerable<WorkspacePageControl> Pages
        {
            get
            {
                foreach (Entity e in children)
                    if (e is WorkspacePageControl page) yield return page;
            }
        }

        #region ---- workspaces ----
        // An empty workspace, shown when it is the first.
        public WorkspacePageControl AddPage(string title, WorkspaceKind kind)
        {
            WorkspacePageControl page = new WorkspacePageControl
            {
                title = title,
                kind = kind,
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            AddChild(page);
            if (shown == null) shown = page;
            else page.Hide();

            changed?.Invoke(this);
            return page;
        }

        public void Show(WorkspacePageControl page)
        {
            if (ReferenceEquals(page, shown) || !ReferenceEquals(page.parent, this)) return;

            if (shown != null)
            {
                if (TabViewControl.focused is TabViewControl last && ReferenceEquals(WorkspacePageControl.Of(last), shown))
                    shown.lastFocused = last;
                shown.Hide();
            }
            shown = page;
            page.Show();
            InvalidateLayout();
            Focus(page);
            changed?.Invoke(this);
        }

        // Hands the active control to the pane last worked in on this page, or to its first pane.
        private static void Focus(WorkspacePageControl page)
        {
            TabViewControl? view = page.lastFocused is TabViewControl last && !last.destroyed && ReferenceEquals(WorkspacePageControl.Of(last), page)
                ? last
                : TabViewControl.TabViews(page).FirstOrDefault();
            if (view == null) return;

            Control? editor = view.activeItem != null ? TabViewControl.FileEditorOf(view.activeItem) as Control : null;
            UIEngine.SetActiveControl(editor ?? view);
        }

        public void Rename(WorkspacePageControl page, string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return;

            page.title = title.Trim();
            changed?.Invoke(this);
        }

        // Drops a workspace whose tabs are already closed; the last one stays.
        public void RemovePage(WorkspacePageControl page)
        {
            if (!ReferenceEquals(page.parent, this) || Pages.Count() < 2) return;

            int index = children.IndexOf(page);
            page.Destroy();

            if (shown == null && children.Count > 0)
                Show((WorkspacePageControl)children[Math.Min(index, children.Count - 1)]);
            changed?.Invoke(this);
        }

        // Puts a workspace at a gap among this one's, taking it from another window's; the primary's last stays.
        public void Move(WorkspacePageControl page, int index)
        {
            if (page.parent is not WorkspaceControl from) return;

            if (ReferenceEquals(from, this))
            {
                int old = children.IndexOf(page);
                if (index > old) index--;
                if (index == old) return;

                children.RemoveAt(old);
                children.Insert(index, page);
                MarkTreeOrderDirty();
                changed?.Invoke(this);
                return;
            }

            if (from.Pages.Count() < 2 && UIEngine.WindowOf(from) is RenderWindow home && (home == Engine.primary || home.anchor)) return;

            int left = from.children.IndexOf(page);
            from.RemoveChild(page);
            AddChild(page);
            children.Remove(page);
            children.Insert(Math.Clamp(index, 0, children.Count), page);
            Show(page);
            from.Released(page, left);
        }

        // Shows the neighbour of a workspace that left, or closes a secondary window it left empty.
        private void Released(WorkspacePageControl page, int index)
        {
            if (ReferenceEquals(shown, page)) shown = null;

            if (children.Count == 0)
            {
                RenderWindow window = UIEngine.WindowOf(this);
                if (window != null && window != Engine.primary && !window.anchor) Engine.CloseWindow(window);
                return;
            }

            if (shown == null) Show((WorkspacePageControl)children[Math.Min(index, children.Count - 1)]);
            InvalidateLayout();
            changed?.Invoke(this);
        }

        protected override void OnChildDetached(Entity child)
        {
            if (ReferenceEquals(child, shown)) shown = null;
            base.OnChildDetached(child);
        }
        #endregion

        #region ---- filling ----
        // The authored arrangement in the first workspace and an empty pane in each other one, for a
        // window the session has nothing to say about.
        public void LoadDefault()
        {
            if (string.IsNullOrEmpty(defaultDocument)) return;

            List<WorkspaceKind> kinds = FirstRunKinds();
            AddPage(kinds[0].ToString(), kinds[0]).AddChild(ParseXML(defaultDocument));
            for (int i = 1; i < kinds.Count; i++)
                LoadPane(AddPage(kinds[i].ToString(), kinds[i]));
        }

        // Every first-run workspace with one empty pane.
        public void LoadEmpty()
        {
            foreach (WorkspaceKind kind in FirstRunKinds())
                LoadPane(AddPage(kind.ToString(), kind));
        }

        // One empty pane in the shown workspace, made General when there is none.
        public TabViewControl LoadPane() => LoadPane(shown ?? AddPage(nameof(WorkspaceKind.General), WorkspaceKind.General));

        // One empty pane carrying this workspace's authored chrome. A restored arrangement is built
        // by splitting it, and SplitViewControl copies that chrome onto every pane it makes.
        public TabViewControl LoadPane(WorkspacePageControl page)
        {
            if (string.IsNullOrEmpty(paneDocument)) return null;
            if (ParseXML(paneDocument) is not TabViewControl pane) return null;

            page.AddChild(pane);
            return pane;
        }

        private List<WorkspaceKind> FirstRunKinds()
        {
            List<WorkspaceKind> kinds = new List<WorkspaceKind>();
            foreach (string word in firstRun.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (Enum.TryParse(word, true, out WorkspaceKind kind)) kinds.Add(kind);

            if (kinds.Count == 0) kinds.Add(WorkspaceKind.General);
            return kinds;
        }
        #endregion

        // The workspace in a window's tree, or null for a window that declares none.
        public static WorkspaceControl In(Control control)
        {
            if (control == null) return null;
            if (control is WorkspaceControl workspace) return workspace;

            foreach (Entity child in control.children)
                if (child is Control childControl && In(childControl) is WorkspaceControl found)
                    return found;

            return null;
        }

        // Gives the primary window's focused pane's editor the caret, as a click into it would.
        [A_XSDActionDependency("Focus", "Console")]
        private static string Focus()
        {
            switch (RibbonControl.Target(In(Engine.primary.ui.uiRoot)?.shown))
            {
                case TexEditorControl tex: tex.source.FocusCaret(); break;
                case DocumentEditorControl note: note.FocusCaret(); break;
                case Control control: UIEngine.SetActiveControl(control); break;
                default: return "error: no editor in the focused pane";
            }
            return "ok";
        }

        #region ---- layout ----
        protected override Vector2 MeasureCore(Vector2 availableSize)
        {
            float w = preferredWidth > 0 ? preferredWidth : MathF.Max(minWidth, availableSize.X);
            float h = preferredHeight > 0 ? preferredHeight : MathF.Max(minHeight, availableSize.Y);

            shown?.Measure(new Vector2(MathF.Max(0, w - padding.totalHorizontal), MathF.Max(0, h - padding.totalVertical)));

            arrange.desired = new Vector2(w, h);
            SetFlag(ArrangeFlags.MeasureDirty, false);
            return arrange.desired;
        }

        protected override void ArrangeCore(LayoutRect finalRect)
        {
            WriteArranged(finalRect);
            shown?.Arrange(finalRect.Shrink(padding));
            SetFlag(ArrangeFlags.ArrangeDirty, false);
        }
        #endregion
    }
}
