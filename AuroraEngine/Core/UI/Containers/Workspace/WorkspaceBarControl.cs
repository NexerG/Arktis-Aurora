using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;
using System.Numerics;

namespace ArctisAurora.Core.UI.Containers.Workspace
{
    // The workspaces of this window's Workspace as title-bar tabs. A press shows one, a double click
    // renames it in place, a right click offers the workspace menu.
    [A_XSDType("WorkspaceBar", "UI")]
    public class WorkspaceBarControl : StackPanelControl
    {
        [A_XSDElementProperty("TabContextMenu", "UI", "Menu a workspace tab offers on right click.")]
        public string tabContextMenu = "workspace";

        [A_XSDElementProperty("TearOffDocument", "UI", "UI document a workspace dragged out of every window opens in.")]
        public string tearOffDocument = "";

        // tab geometry
        private const float tabHeight = 22f;
        private const float tabInset = 10f;
        private const int captionSize = 12;

        // drop marker
        private const float markerWidth = 2f;
        private const float markerHeight = 16f;
        private PanelControl? marker;

        // tear-off window
        private const uint tearOffWidth = 900;
        private const uint tearOffHeight = 640;
        private static int _tornWindows;

        private bool rebuildPending;

        public WorkspaceBarControl()
        {
            orientation = Orientation.Horizontal;
            Spacing = 2f;
            alpha = 0f;
            WorkspaceControl.changed += OnWorkspaceChanged;
        }

        public override void OnDestroy()
        {
            WorkspaceControl.changed -= OnWorkspaceChanged;
            base.OnDestroy();
        }

        // One workspace tab.
        public sealed class WorkspaceButton : ButtonControl
        {
            private const float dragThreshold = 12f;

            public WorkspacePageControl page = null!;
            public EditableLabelControl caption = null!;
            internal WorkspaceBarControl bar = null!;

            // press state
            private bool armed;
            private Vector2 grab;
            private bool overWindow;

            public override bool OnPointerPress(PointerEvent e)
            {
                grab = e.point;
                armed = !caption.isEditing;
                return base.OnPointerPress(e);
            }

            public override bool OnPointerMove(PointerEvent e)
            {
                if (armed)
                {
                    if (!InputHandler.instance.IsKeyDown(Keys.MouseLeft)) armed = false;
                    else if ((e.point - grab).Length() >= dragThreshold)
                    {
                        armed = false;
                        overWindow = true;
                        StartDrag();
                        DragGhost.Show(this);
                    }
                }
                return base.OnPointerMove(e);
            }

            public override bool OnPointerRelease(PointerEvent e)
            {
                armed = false;
                return base.OnPointerRelease(e);
            }

            public override void OnDrag(PointerEvent e)
            {
                overWindow = UIEngine.mouseOverWindow != null;
                base.OnDrag(e);
            }

            // Shows the workspace, or tears it off when it was dropped outside every window.
            public override void OnDragStop(bool accepted)
            {
                DragGhost.Hide();
                if (!accepted && !overWindow) bar.TearOff(page);
                else (page.parent as WorkspaceControl)?.Show(page);
                base.OnDragStop(accepted);
            }
        }

        // Posted, because a rename commits from inside the caption this would destroy.
        private void OnWorkspaceChanged(WorkspaceControl workspace)
        {
            if (rebuildPending || !ReferenceEquals(workspace, Workspace())) return;
            rebuildPending = true;
            Engine.Post(() =>
            {
                rebuildPending = false;
                if (!destroyed) Rebuild(workspace);
            });
        }

        private WorkspaceControl? Workspace()
        {
            Control root = this;
            while (root.parent is Control up) root = up;
            return WorkspaceControl.In(root);
        }

        private void Rebuild(WorkspaceControl workspace)
        {
            foreach (Entity child in children.ToList()) child.Destroy();
            marker = null;

            foreach (WorkspacePageControl page in workspace.Pages)
                AddChild(BuildTab(workspace, page));
        }

        private WorkspaceButton BuildTab(WorkspaceControl workspace, WorkspacePageControl page)
        {
            bool shown = ReferenceEquals(page, workspace.shown);
            WorkspaceButton tab = new WorkspaceButton
            {
                page = page,
                bar = this,
                preferredHeight = tabHeight,
                padding = new Thickness(0f, tabInset, 0f, tabInset),
                margin = new Thickness(26f - tabHeight, 0f, 0f, 0f),
                cornerRole = CornerRole.Tab,
                contextMenu = tabContextMenu,
                stopsContextMenu = true
            };
            tab.PaintOr(null, shown ? PaletteRole.Ground : PaletteRole.Clear);
            if (shown) tab.accentRole = AccentRole.Tab;

            tab.caption = new EditableLabelControl
            {
                text = page.title,
                fontSize = captionSize,
                horizontalPosition = 0.5f,
                verticalPosition = 0.5f
            };
            tab.caption.PaintText(null, shown ? PaletteRole.Ink : PaletteRole.MutedInk);
            tab.AddChild(tab.caption);

            tab.RegisterOnRelease(e => { workspace.Show(page); return true; });
            tab.RegisterOnTap(e =>
            {
                if (e.tapCount != 2) return false;
                BeginRename(tab);
                return true;
            });
            return tab;
        }

        #region ---- drop ----
        public override bool DraggingOverStart(Control dragged, Vector2 point) => ShowMarker(dragged, point);

        public override bool DraggingOver(Control dragged, Vector2 point) => ShowMarker(dragged, point);

        public override bool DraggingOverEnd(Control dragged)
        {
            if (dragged is not WorkspaceButton) return false;
            HideMarker();
            return true;
        }

        // Takes a workspace tab from any bar into the gap under the pointer.
        public override bool FinishDrag(Control dragged, Vector2 point)
        {
            if (dragged is not WorkspaceButton tab || Workspace() is not WorkspaceControl workspace) return false;

            HideMarker();
            workspace.Move(tab.page, GapAt(point));
            return true;
        }

        private bool ShowMarker(Control dragged, Vector2 point)
        {
            if (dragged is not WorkspaceButton) return false;

            if (marker == null)
            {
                marker = new PanelControl
                {
                    preferredWidth = markerWidth,
                    preferredHeight = markerHeight,
                    margin = new Thickness((26f - markerHeight) * 0.5f, 0f, 0f, 0f),
                    hitTestable = false
                };
                marker.PaintOr(null, PaletteRole.Accent);
                AddChild(marker);
            }

            int gap = GapAt(point);
            if (children.IndexOf(marker) != gap)
            {
                children.Remove(marker);
                children.Insert(gap, marker);
                MarkTreeOrderDirty();
                InvalidateLayout();
            }
            return true;
        }

        private void HideMarker()
        {
            marker?.Destroy();
            marker = null;
        }

        // How many tabs sit left of the point.
        private int GapAt(Vector2 point)
        {
            int gap = 0;
            foreach (Entity child in children)
                if (child is WorkspaceButton tab && tab.arrangedRect.x + tab.arrangedRect.width * 0.5f < point.X)
                    gap++;
            return gap;
        }

        // Moves a workspace into a window of its own, built from tearOffDocument and placed at the pointer.
        internal unsafe void TearOff(WorkspacePageControl page)
        {
            if (string.IsNullOrEmpty(tearOffDocument) || page.parent is not WorkspaceControl from || from.Pages.Count() < 2) return;

            RenderWindow source = UIEngine.WindowOf(this);
            AGlfwWindow._glfw.GetWindowPos(source.os.handle, out int wx, out int wy);

            RenderWindow torn = Engine.OpenWindow($"workspace-{++_tornWindows}",
                UIScaling.ToPixels(source, tearOffWidth), UIScaling.ToPixels(source, tearOffHeight),
                wx + (int)source.mousePos.X, wy + (int)source.mousePos.Y);
            torn.uiDocument = tearOffDocument;
            torn.sessionScope = source.sessionScope;
            WindowRoot root = (WindowRoot)ParseXML(tearOffDocument);
            torn.ui.uiRoot = root;
            WorkspaceControl.In(root)?.Move(page, 0);
        }
        #endregion

        private static void BeginRename(WorkspaceButton tab) =>
            tab.caption.BeginEdit(name => (tab.page.parent as WorkspaceControl)?.Rename(tab.page, name));

        // Starts renaming a workspace in whichever bar shows it.
        public static void BeginRename(WorkspacePageControl page)
        {
            if (page.parent is not WorkspaceControl workspace) return;

            Control root = workspace;
            while (root.parent is Control up) root = up;
            foreach (WorkspaceButton tab in Buttons(root))
                if (ReferenceEquals(tab.page, page)) { BeginRename(tab); return; }
        }

        private static IEnumerable<WorkspaceButton> Buttons(Control control)
        {
            if (control is WorkspaceButton tab) yield return tab;
            foreach (Entity child in control.children)
                if (child is Control childControl)
                    foreach (WorkspaceButton found in Buttons(childControl))
                        yield return found;
        }
    }
}
