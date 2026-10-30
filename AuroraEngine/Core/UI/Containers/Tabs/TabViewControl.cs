using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Containers.Workspace;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Menus;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;
using System.Numerics;

namespace ArctisAurora.Core.UI.Containers.Tabs
{
    // A strip of tabs over one page. Only the active item is measured and arranged; the rest are
    // hidden. Closing a tab destroys it.
    [A_XSDType("TabView", "UI")]
    public class TabViewControl : ContainerControl
    {
        #region properties
        // strip metrics
        [A_XSDElementProperty("TabHeight", "UI", "Height of the tab strip in pixels.")]
        public float tabHeight = 28f;
        [A_XSDElementProperty("TabWidth", "UI", "Width of a single tab in pixels.")]
        public float tabWidth = 160f;
        [A_XSDElementProperty("MinTabWidth", "UI", "Width a tab shrinks to before the strip clips, in pixels.")]
        public float minTabWidth = 64f;

        // strip palette
        [A_XSDElementProperty("TabColorHex", "UI", "Ground of an inactive tab.")]
        public string? tabColorHex;
        [A_XSDElementProperty("ActiveTabColorHex", "UI", "Ground of the active tab.")]
        public string? activeTabColorHex;
        [A_XSDElementProperty("TabHoverColorHex", "UI", "Ground of a hovered inactive tab.")]
        public string? tabHoverColorHex;
        [A_XSDElementProperty("TabInkColorHex", "UI", "Color of a tab's caption and close mark.")]
        public string? tabInkColorHex;

        // carried onto the splitter of any pane split off this one, and onto that pane in turn
        [A_XSDElementProperty("GripColorHex", "UI", "Ground of the splitter between panes.")]
        public string? gripColorHex;
        [A_XSDElementProperty("GripHoverColorHex", "UI", "Ground of a hovered pane splitter.")]
        public string? gripHoverColorHex;
        [A_XSDElementProperty("GripPressColorHex", "UI", "Ground of a held pane splitter.")]
        public string? gripPressColorHex;

        // tear-off
        [A_XSDElementProperty("TearOffDocument", "UI", "UI document a tab dragged out of every window opens in.")]
        public string tearOffDocument = "";

        [A_XSDElementProperty("StickyDocument", "UI", "UI document a tab pinned to the desktop opens in.")]
        public string stickyDocument = "";

        [A_XSDElementProperty("Bare", "UI", "Pane holds plain text views: no tab-row tools, pageless full-width editors.")]
        public bool bare;

        // menu each tab in the strip names
        [A_XSDElementProperty("TabContextMenu", "UI", "Menu a tab in the strip offers on right click.")]
        public string tabContextMenu = "tab";

        [A_XSDElementProperty("NewButton", "UI", "Whether the strip ends in a + button that asks the host for a new file.")]
        public bool newButton
        {
            get => plus != null;
            set
            {
                if (value == (plus != null)) return;
                if (value) base.AddChild(plus = BuildPlus());
                else { plus!.Destroy(); plus = null; }
            }
        }
        #endregion

        // caption and close button geometry
        protected const int captionSize = 12;
        private const float captionInset = 8f;
        private const float closeWidth = 20f;
        private const int closeCaptionSize = 12;
        private const string closeCaption = "x";
        private const string closeHoverColorHex = "#C42B1E";
        private const string closePressColorHex = "#A82318";

        // torn-off window geometry
        private const uint tearOffWidth = 900;
        private const uint tearOffHeight = 640;
        private static int _tornWindows;

        // sticky window geometry
        private const uint stickySize = 300;
        private static int _stickyWindows;

        // share of a side inside which a dropped tab splits instead of moving in
        private const float edgeBand = 0.25f;

        private readonly StackPanelControl strip = new StackPanelControl();

        // tab row beside the strip: the + button and the active editor's tools
        private const float plusWidth = 24f;
        private ButtonControl? plus;
        private Control? tools;
        private float stripWidth;
        private float fittedTab;

        // Raised by the + button with the view and the button, for the host to make a file in.
        public static event Action<TabViewControl, Control>? newPressed;

        // drop hint — the wash and the edge it is showing, null when nothing is being dragged over us
        private HintControl? hint;
        private SplitViewControl.SplitEdge? hintEdge;

        public TabItemControl? activeItem { get; private set; }

        // Raised when any view's active tab changes.
        public static event Action<TabViewControl>? activeChanged;

        // The view last worked in; only its active tab carries the accent.
        public static TabViewControl? focused { get; private set; }

        static TabViewControl()
        {
            Context.changed += OnContextChanged;
            ContextMenus.RegisterSource("tab-stickies", TabActions.StickyEntries);
        }

        public int ItemCount
        {
            get
            {
                int count = 0;
                foreach (TabItemControl _ in Items) count++;
                return count;
            }
        }

        public TabViewControl()
        {
            strip.orientation = StackPanelControl.Orientation.Horizontal;
            strip.alpha = 0f;
            strip.clipOutOfBounds = true;
            base.AddChild(strip);
        }

        public IEnumerable<TabItemControl> Items
        {
            get
            {
                foreach (Entity e in children)
                    if (e is TabItemControl item) yield return item;
            }
        }

        public override void AddChild(Entity entity)
        {
            if (entity is not TabItemControl item)
                throw new Exception("TabView children must be TabItem controls");

            base.AddChild(item);
            if (activeItem != null) item.Hide();

            strip.AddChild(BuildTab(item));
            ApplyTabColors();
            if (activeItem == null) SetActive(item);
        }

        // A tab moved to another view takes its strip button's pending release with it, so an item
        // that is no longer ours is ignored rather than resurrected as the active one.
        public override void RemoveChild(Entity entity)
        {
            bool wasActive = ReferenceEquals(entity, activeItem);
            base.RemoveChild(entity);

            if (entity is not TabItemControl item) return;

            TabItemControl next = wasActive ? Neighbour(item) : activeItem;
            if (wasActive) activeItem = null;

            ButtonOf(item)?.Destroy();
            ApplyTabColors();
            if (activeItem == null && next != null) SetActive(next);
            if (wasActive && activeItem == null) activeChanged?.Invoke(this);
            SyncTools();
            InvalidateLayout();

            CloseIfEmptied();
        }

        public override void OnDestroy()
        {
            if (ReferenceEquals(focused, this)) focused = null;
            base.OnDestroy();
        }

        #region ---- drop ----
        // What a drop at this point would do — an edge to split on, or null to take the tab in.
        // The button comes back out so a caller can tell "no edge" from "not a tab drag at all".
        private SplitViewControl.SplitEdge? PendingEdge(Control dropped, Vector2 point, out TabStripButtonControl button)
        {
            button = dropped as TabStripButtonControl;
            if (button == null || !button.dragging) { button = null; return null; }

            // splitting off our own only tab would empty us and collapse the split straight back
            bool ownOnly = ReferenceEquals(button.item.parent, this) && ItemCount < 2;
            return ownOnly ? null : EdgeAt(point);
        }

        // Accepts a tab dragged out of any strip, including our own. A drop in the outer band of a
        // side splits this view instead of taking the tab in.
        public override bool FinishDrag(Control dragged, Vector2 point)
        {
            SplitViewControl.SplitEdge? edge = PendingEdge(dragged, point, out TabStripButtonControl button);
            if (button == null) return false;

            if (edge != null)
            {
                TabViewControl pane = SplitViewControl.Split(this, edge.Value);
                if (pane != null)
                {
                    button.item.SetParent(pane);
                    pane.SetActive(button.item);
                    return true;
                }
            }

            if (ReferenceEquals(button.item.parent, this)) return true;

            button.item.SetParent(this);
            SetActive(button.item);
            return true;
        }

        // Claims the hint for any live tab drag over us, whether or not it is over an edge — the
        // middle is a drop we take too, it just splits nothing and so washes nothing.
        public override bool DraggingOverStart(Control dragged, Vector2 point) => ShowHint(dragged, point);

        public override bool DraggingOver(Control dragged, Vector2 point) => ShowHint(dragged, point);

        public override bool DraggingOverEnd(Control dragged)
        {
            SetHint(null);
            return false;
        }

        private bool ShowHint(Control dragged, Vector2 point)
        {
            SplitViewControl.SplitEdge? edge = PendingEdge(dragged, point, out TabStripButtonControl button);
            if (button == null) return false;

            SetHint(edge);
            return true;
        }

        // The wash is built on the first hint this view ever shows and kept after that. It has to be
        // the last child every time it is raised, because dense order is DFS and tabs are appended.
        private void SetHint(SplitViewControl.SplitEdge? edge)
        {
            if (hintEdge == edge) return;
            hintEdge = edge;

            if (edge != null)
            {
                if (hint == null)
                {
                    hint = new HintControl { hitTestable = false };
                    base.AddChild(hint);
                }
                children.Remove(hint);
                children.Add(hint);
                MarkTreeOrderDirty();
            }

            InvalidateLayout();
        }

        // The half the new pane would take, and nothing at all when there is no edge.
        private LayoutRect HintRect(LayoutRect inner) => hintEdge switch
        {
            SplitViewControl.SplitEdge.Left => new LayoutRect(inner.x, inner.y, inner.width * 0.5f, inner.height),
            SplitViewControl.SplitEdge.Right => new LayoutRect(inner.x + inner.width * 0.5f, inner.y, inner.width * 0.5f, inner.height),
            SplitViewControl.SplitEdge.Top => new LayoutRect(inner.x, inner.y, inner.width, inner.height * 0.5f),
            SplitViewControl.SplitEdge.Bottom => new LayoutRect(inner.x, inner.y + inner.height * 0.5f, inner.width, inner.height * 0.5f),
            _ => new LayoutRect(inner.x, inner.y, 0, 0)
        };

        // The outer band of a side, nearest side winning. Null in the middle and anywhere over the
        // strip, where a drop means "put the tab here".
        private SplitViewControl.SplitEdge? EdgeAt(Vector2 point)
        {
            LayoutRect rect = arrangedRect;
            if (rect.width <= 0f || rect.height <= 0f) return null;
            if (point.Y < rect.y + tabHeight) return null;

            float left = (point.X - rect.x) / rect.width;
            float top = (point.Y - rect.y) / rect.height;
            float right = 1f - left;
            float bottom = 1f - top;

            float nearest = MathF.Min(MathF.Min(left, right), MathF.Min(top, bottom));
            if (nearest > edgeBand) return null;

            if (nearest == left) return SplitViewControl.SplitEdge.Left;
            if (nearest == right) return SplitViewControl.SplitEdge.Right;
            if (nearest == top) return SplitViewControl.SplitEdge.Top;
            return SplitViewControl.SplitEdge.Bottom;
        }
        #endregion

        // Moves a tab into a window of its own, built from tearOffDocument and placed at the pointer.
        internal unsafe void TearOff(TabItemControl item)
        {
            if (string.IsNullOrEmpty(tearOffDocument)) return;

            RenderWindow source = UIEngine.WindowOf(this);
            AGlfwWindow._glfw.GetWindowPos(source.os.handle, out int wx, out int wy);

            RenderWindow torn = Engine.OpenWindow($"tab-{++_tornWindows}",
                UIScaling.ToPixels(source, tearOffWidth), UIScaling.ToPixels(source, tearOffHeight),
                wx + (int)source.mousePos.X, wy + (int)source.mousePos.Y);
            torn.uiDocument = tearOffDocument;
            torn.sessionScope = source.sessionScope;
            WindowRoot root = (WindowRoot)ParseXML(tearOffDocument);
            torn.ui.uiRoot = root;
            WorkspaceControl.In(root)?.LoadDefault();

            TabViewControl view = FirstTabView(root);
            if (view == null) return;

            item.SetParent(view);
            view.SetActive(item);
        }

        // Puts a tab into a sticky window: the one given, or a new one built from stickyDocument at the pointer.
        internal void PinToSticky(TabItemControl item, RenderWindow? into)
        {
            if (into == null)
            {
                if (string.IsNullOrEmpty(stickyDocument)) return;

                OpenStickyAtPointer(item, stickyDocument, UIEngine.WindowOf(this));
                return;
            }

            TabViewControl view = FirstTabView(into.ui.uiRoot);
            if (view == null) return;

            item.SetParent(view);
            view.SetActive(item);
        }

        // A new sticky window from document at source's pointer holding item.
        public static unsafe RenderWindow? OpenStickyAtPointer(TabItemControl item, string document, RenderWindow source)
        {
            AGlfwWindow._glfw.GetWindowPos(source.os.handle, out int wx, out int wy);
            return OpenSticky(item, document, wx + (int)source.mousePos.X, wy + (int)source.mousePos.Y,
                string.IsNullOrEmpty(SessionLayout.freeScope) ? source.sessionScope : SessionLayout.freeScope);
        }

        // A new sticky window from document at x, y holding item.
        public static RenderWindow? OpenSticky(TabItemControl item, string document, int x, int y, string? sessionScope)
        {
            RenderWindow scaleFrom = Engine.primary;
            RenderWindow into = Engine.OpenWindow($"sticky-{++_stickyWindows}",
                UIScaling.ToPixels(scaleFrom, stickySize), UIScaling.ToPixels(scaleFrom, stickySize), x, y);
            into.uiDocument = document;
            into.sessionScope = sessionScope;
            WindowRoot root = (WindowRoot)ParseXML(document);
            into.ui.uiRoot = root;
            WorkspaceControl.In(root)?.LoadDefault();

            TabViewControl view = FirstTabView(into.ui.uiRoot);
            if (view == null) return into;

            item.SetParent(view);
            view.SetActive(item);
            return into;
        }

        private static TabViewControl FirstTabView(Control control)
        {
            if (control is TabViewControl view) return view;
            foreach (Entity child in control.children)
                if (child is Control childControl && FirstTabView(childControl) is TabViewControl found)
                    return found;
            return null;
        }

        public void SetActive(TabItemControl item)
        {
            if (item != null && !children.Contains(item)) return;
            if (ReferenceEquals(activeItem, item)) return;

            activeItem?.Hide();
            activeItem = item;
            activeItem?.Show();

            ApplyTabColors();
            SyncTools();
            InvalidateLayout();
            activeChanged?.Invoke(this);
        }

        // Mounts the active editor's tools on the tab row, taking them from wherever they were.
        internal void SyncTools()
        {
            IFileEditor? editor = activeItem != null ? FileEditorOf(activeItem) : null;
            if (editor is DocumentEditorControl document) document.bare = bare;
            Control? wanted = bare || RibbonControl.Covers(this, editor) ? null : editor?.tools;
            if (ReferenceEquals(wanted, tools) && (wanted == null || ReferenceEquals(wanted.parent, this))) return;

            if (tools != null && ReferenceEquals(tools.parent, this)) base.RemoveChild(tools);
            tools = wanted;
            if (tools == null) return;

            tools.parent?.RemoveChild(tools);
            base.AddChild(tools);
        }

        // Writes an edited note, then tears the whole subtree down — the strip button with it.
        public void CloseTab(TabItemControl item) => CloseTab(item, null);

        // onClosed runs once the tab is gone.
        internal void CloseTab(TabItemControl item, Action onClosed)
        {
            if (item == null || !children.Contains(item)) { onClosed?.Invoke(); return; }

            IFileEditor file = FileEditorOf(item);
            if (file != null && file.isDirty) file.Save();

            FinishClose(item);
            onClosed?.Invoke();
        }

        // Snapshotted, because closing detaches the tab from children as it goes.
        public void CloseOthers(TabItemControl keep)
        {
            List<TabItemControl> queue = new List<TabItemControl>();
            foreach (TabItemControl item in Items)
                if (!ReferenceEquals(item, keep)) queue.Add(item);

            CloseNext(queue, 0);
        }

        public void CloseToTheRight(TabItemControl from)
        {
            List<TabItemControl> queue = new List<TabItemControl>();
            bool passed = false;
            foreach (TabItemControl item in Items)
            {
                if (ReferenceEquals(item, from)) { passed = true; continue; }
                if (passed) queue.Add(item);
            }

            CloseNext(queue, 0);
        }

        // One tab at a time, each opened from the previous one's completion — the naming prompt is a
        // single window and a second ask while one is up is refused.
        private void CloseNext(List<TabItemControl> queue, int index)
        {
            if (index >= queue.Count) return;

            CloseTab(queue[index], () => CloseNext(queue, index + 1));
        }

        // Moves one named tab into a new pane beside this view. Splitting off our own only tab would
        // empty us and collapse the split straight back, which is the same guard a drop applies.
        public void SplitOff(TabItemControl item, SplitViewControl.SplitEdge edge)
        {
            if (item == null) return;
            if (ReferenceEquals(item.parent, this) && ItemCount < 2) return;

            TabViewControl pane = SplitViewControl.Split(this, edge);
            if (pane == null) return;

            item.SetParent(pane);
            pane.SetActive(item);
        }

        // A view of this kind, for a split to fill.
        protected internal virtual TabViewControl NewOfSameKind() => new TabViewControl();

        // Also the discard path: a note deleted on disk closes through here, unwritten.
        public void FinishClose(TabItemControl item)
        {
            if (item == null || !children.Contains(item)) return;

            TabItemControl next = Neighbour(item);
            bool wasActive = ReferenceEquals(item, activeItem);
            if (wasActive) activeItem = null;
            item.Destroy();

            ButtonOf(item)?.Destroy();
            ApplyTabColors();
            if (activeItem == null && next != null) SetActive(next);
            if (wasActive && activeItem == null) activeChanged?.Invoke(this);
            SyncTools();
            InvalidateLayout();

            CloseIfEmptied();
        }

        // A pane that empties hands its split back to its neighbour. A window that exists to hold tabs
        // has nothing left to be once the last one goes. The main window stays — closing that is the
        // application closing.
        private void CloseIfEmptied()
        {
            if (activeItem != null) return;

            if (parent is SplitViewControl split)
            {
                SplitViewControl.Collapse(split, this);
                return;
            }

            RenderWindow window = UIEngine.WindowOf(this);
            if (window == null || window == Engine.primary || window.anchor) return;

            Engine.CloseWindow(window);
        }

        // The tab after this one, or the one before it if it is last.
        private TabItemControl Neighbour(TabItemControl item)
        {
            TabItemControl previous = null;
            bool passed = false;
            foreach (TabItemControl candidate in Items)
            {
                if (ReferenceEquals(candidate, item)) { passed = true; continue; }
                if (passed) return candidate;
                previous = candidate;
            }
            return previous;
        }

        #region ---- open notes ----
        // Recaptions one tab and its strip caption.
        public void Retitle(TabItemControl item, string header)
        {
            if (item == null) return;

            item.header = header;
            TabStripButtonControl button = ButtonOf(item);
            if (button != null) SetCaption(CaptionOf(button), header);
        }

        public static DocumentEditorControl EditorOf(TabItemControl item) =>
            item.children.Count > 0 ? item.children[0] as DocumentEditorControl : null;

        public static IFileEditor FileEditorOf(TabItemControl item) =>
            item.children.Count > 0 ? item.children[0] as IFileEditor : null;

        // The tab showing this note, in whichever window it is open. Identity is the file the editor
        // loaded, not the tab's caption — a tab seeded from a UI document carries no name to match on.
        public static TabItemControl FindOpenDocument(string path, out TabViewControl owner)
        {
            foreach ((TabItemControl item, TabViewControl view) in FindOpenDocuments(path))
            {
                owner = view;
                return item;
            }

            owner = null;
            return null;
        }

        // Every tab showing this note. The tree is the register of what is open, so a rename that
        // walks it cannot be told about a view that has since been closed.
        public static IEnumerable<(TabItemControl item, TabViewControl view)> FindOpenDocuments(string path)
        {
            string target = Path.GetFullPath(path);

            foreach (RenderWindow window in Engine.windows.Values)
            {
                if (window.ui?.uiRoot == null) continue;

                foreach (TabViewControl view in TabViews(window.ui.uiRoot))
                    foreach (TabItemControl item in view.Items)
                    {
                        string open = FileEditorOf(item)?.path;
                        if (open != null && string.Equals(open, target, StringComparison.OrdinalIgnoreCase))
                            yield return (item, view);
                    }
            }
        }

        // Every tab view under a control, itself included.
        public static IEnumerable<TabViewControl> TabViews(Control control)
        {
            if (control == null) yield break;
            if (control is TabViewControl view) yield return view;

            foreach (Entity child in control.children)
                if (child is Control childControl)
                    foreach (TabViewControl found in TabViews(childControl))
                        yield return found;
        }
        #endregion

        #region ---- strip ----
        // The strip button standing for one tab.
        private TabStripButtonControl ButtonOf(TabItemControl item)
        {
            foreach (Entity e in strip.children)
                if (e is TabStripButtonControl button && ReferenceEquals(button.item, item))
                    return button;
            return null;
        }

        // The row tiles the tab exactly and the wrapper carries the caption inset, so no container
        // inside a tab has bare area a press can land on.
        private Control BuildTab(TabItemControl item)
        {
            TabStripButtonControl tab = new TabStripButtonControl
            {
                preferredWidth = tabWidth,
                preferredHeight = tabHeight,
                cornerRole = CornerRole.Tab,
                hoverColorHex = tabHoverColorHex,
                pressColorHex = tabHoverColorHex,
                item = item,
                owner = this,
                contextMenu = tabContextMenu,
                stopsContextMenu = true
            };
            tab.RegisterOnRelease(e => { SetActive(item); return true; });

            Control caption = BuildCaption(item, tab);
            caption.margin = new Thickness(0, 0, 0, captionInset);
            tab.AddChild(caption);
            tab.AddChild(BuildCloseButton(item));
            return tab;
        }

        // The caption for one tab, with the strip button a derivative may bind gestures on.
        protected virtual Control BuildCaption(TabItemControl item, TabStripButtonControl tab)
        {
            LabelControl caption = new LabelControl
            {
                text = item.header,
                fontSize = captionSize,
                horizontalPosition = 0f
            };
            caption.PaintOr(tabInkColorHex, PaletteRole.Ink);
            return caption;
        }

        // Rewrites a caption BuildCaption made.
        protected virtual void SetCaption(Control caption, string header)
        {
            if (caption is LabelControl label) label.text = header;
        }

        private Control BuildCloseButton(TabItemControl item)
        {
            CloseButtonControl close = new CloseButtonControl
            {
                preferredWidth = closeWidth,
                preferredHeight = tabHeight,
                cornerRole = CornerRole.TabEnd,
                hoverColorHex = closeHoverColorHex,
                pressColorHex = closePressColorHex
            };
            LabelControl mark = new LabelControl { text = closeCaption, fontSize = closeCaptionSize };
            mark.PaintOr(tabInkColorHex, PaletteRole.Ink);
            close.AddChild(mark);
            close.RegisterOnRelease(e => { CloseTab(item); return true; });
            return close;
        }

        // Enter and exit bubble so the tab keeps its hover tint; release does not, so closing never
        // also activates.
        private sealed class CloseButtonControl : ButtonControl
        {
            public override bool OnPointerEnter(PointerEvent e)
            {
                base.OnPointerEnter(e);
                return false;
            }

            public override bool OnPointerExit(PointerEvent e)
            {
                base.OnPointerExit(e);
                return false;
            }
        }

        private void ApplyTabColors()
        {
            AccentRole bar = focused == null || ReferenceEquals(focused, this) ? AccentRole.Tab : AccentRole.TabIdle;
            int i = 0;
            foreach (TabItemControl item in Items)
            {
                bool active = ReferenceEquals(item, activeItem);
                string? hex = active ? activeTabColorHex : tabColorHex;
                PaletteRole ground = active ? PaletteRole.Ground : PaletteRole.Surface;
                AccentRole accent = active ? bar : AccentRole.None;
                if (i < strip.children.Count && strip.children[i] is ButtonControl tab)
                {
                    tab.PaintOr(hex, ground);
                    tab.accentRole = accent;
                    ButtonControl close = CloseButtonOf(tab);
                    close?.PaintOr(hex, ground);
                    if (close != null) close.accentRole = accent;
                }
                i++;
            }
        }

        // The nearest view above the active control becomes the focused one; a control outside every view leaves it.
        private static void OnContextChanged(string name, object? value)
        {
            if (name != "ActiveControl") return;

            Entity? entity = value as Entity;
            while (entity != null && entity is not TabViewControl) entity = entity.parent;
            if (entity is not TabViewControl view || ReferenceEquals(view, focused)) return;

            focused = view;
            foreach (RenderWindow window in Engine.windows.Values)
                foreach (TabViewControl each in TabViews(window.ui?.uiRoot))
                    each.ApplyTabColors();
        }

        // Shares the strip between the tabs, each between minTabWidth and tabWidth.
        private void FitTabs(float width)
        {
            int count = strip.children.Count;
            fittedTab = 0f;
            if (count == 0) return;

            fittedTab = MathF.Floor(MathF.Max(MathF.Min(tabWidth, width / count), MathF.Min(minTabWidth, tabWidth)));
            foreach (Entity e in strip.children)
                if (e is Control tab) tab.preferredWidth = fittedTab;
        }

        private ButtonControl BuildPlus()
        {
            ButtonControl button = new ButtonControl { preferredWidth = plusWidth, preferredHeight = tabHeight };
            button.PaintOr(null, PaletteRole.Clear);
            button.AddChild(new LabelControl
            {
                text = "+",
                fontSize = captionSize + 2,
                role = PaletteRole.MutedInk,
                hitTestable = false,
                horizontalPosition = 0.5f,
                verticalPosition = 0.5f
            });
            button.RegisterOnRelease(e => { newPressed?.Invoke(this, button); return true; });
            return button;
        }

        // tab -> row -> [caption wrapper, close]
        private static ButtonControl CloseButtonOf(Control tab) =>
            tab.children.Count > 1 ? tab.children[1] as ButtonControl : null;

        private static Control CaptionOf(Control tab) =>
            tab.children.Count > 0 ? tab.children[0] as Control : null;
        #endregion

        #region ---- layout ----
        protected override Vector2 MeasureCore(Vector2 availableSize)
        {
            float w = preferredWidth > 0 ? preferredWidth : MathF.Max(minWidth, availableSize.X);
            float h = preferredHeight > 0 ? preferredHeight : MathF.Max(minHeight, availableSize.Y);

            float innerW = MathF.Max(0, w - padding.totalHorizontal);
            float innerH = MathF.Max(0, h - padding.totalVertical);

            float toolsW = 0f;
            if (tools != null && ReferenceEquals(tools.parent, this))
                toolsW = tools.Measure(new Vector2(innerW, tabHeight)).X;
            float plusW = plus != null ? plusWidth : 0f;
            stripWidth = MathF.Max(0, innerW - plusW - toolsW);

            FitTabs(stripWidth);
            strip.Measure(new Vector2(stripWidth, tabHeight));
            plus?.Measure(new Vector2(plusW, tabHeight));
            activeItem?.Measure(new Vector2(innerW, MathF.Max(0, innerH - tabHeight)));
            hint?.Measure(new Vector2(innerW, innerH));

            arrange.desired = new Vector2(w, h);
            SetFlag(ArrangeFlags.MeasureDirty, false);
            return arrange.desired;
        }

        protected override void ArrangeCore(LayoutRect finalRect)
        {
            WriteArranged(finalRect);

            LayoutRect inner = finalRect.Shrink(padding);

            strip.Arrange(new LayoutRect(inner.x, inner.y, stripWidth, tabHeight));
            plus?.Arrange(new LayoutRect(inner.x + MathF.Min(fittedTab * strip.children.Count, stripWidth), inner.y, plusWidth, tabHeight));
            if (tools != null && ReferenceEquals(tools.parent, this))
                tools.Arrange(new LayoutRect(inner.x + inner.width - tools.DesiredSize.X, inner.y, tools.DesiredSize.X, tabHeight));

            activeItem?.Arrange(new LayoutRect(inner.x, inner.y + tabHeight, inner.width,
                MathF.Max(0, inner.height - tabHeight)));

            hint?.Arrange(HintRect(inner));

            SetFlag(ArrangeFlags.ArrangeDirty, false);
        }
        #endregion
    }
}
