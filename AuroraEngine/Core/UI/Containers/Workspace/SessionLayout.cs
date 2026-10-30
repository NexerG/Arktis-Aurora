using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;

namespace ArctisAurora.Core.UI.Containers.Workspace
{
    // What a SessionPane may hold: more panes, or the tabs of a leaf.
    public interface ISessionChild { }

    [A_XSDType("SessionTab", "Settings")]
    public class SessionTab : ISessionChild
    {
        [A_XSDElementProperty("Path", "Settings", "Note the tab had open.")]
        public string path { get; set; } = "";

        // where the caret and selection were, by block index; -1 for none
        [A_XSDElementProperty("CaretBlock", "Settings", "Block the caret was in; -1 when the note had no caret.")]
        public int caretBlock { get; set; } = -1;

        [A_XSDElementProperty("CaretOffset", "Settings", "Caret offset within its block.")]
        public int caretOffset { get; set; }

        [A_XSDElementProperty("AnchorBlock", "Settings", "Block the selection started in.")]
        public int anchorBlock { get; set; } = -1;

        [A_XSDElementProperty("AnchorOffset", "Settings", "Selection start offset within its block.")]
        public int anchorOffset { get; set; }

        // what sat at the top of the view; -1 for nothing recorded
        [A_XSDElementProperty("TopBlock", "Settings", "Block whose line was at the top of the view; -1 when not recorded.")]
        public int topBlock { get; set; } = -1;

        [A_XSDElementProperty("TopOffset", "Settings", "Character in that block whose line was at the top of the view.")]
        public int topOffset { get; set; }

        [A_XSDElementProperty("TopDelta", "Settings", "Pixels that line sat above the top of the view.")]
        public float topDelta { get; set; }

        [A_XSDElementProperty("ScrollX", "Settings", "Horizontal scroll offset in pixels.")]
        public float scrollX { get; set; }

        [A_XSDElementProperty("PropertiesOpen", "Settings", "Whether the note's properties header was expanded.")]
        public bool propertiesOpen { get; set; }

        [A_XSDElementProperty("AsXml", "Settings", "Whether the tab showed the file as XML source.")]
        public bool asXml { get; set; }
    }

    // What a SessionWindow may hold: its workspaces, or the panes a record from before workspaces held.
    public interface ISessionWindowChild { }

    // A split when it holds panes, a leaf when it holds tabs.
    [A_XSDType("SessionPane", "Settings", AllowedChildren = typeof(ISessionChild))]
    public class SessionPane : ISessionChild, ISessionWindowChild
    {
        [A_XSDElementProperty("Orientation", "Settings", "Axis a split pane divides along.")]
        public StackPanelControl.Orientation orientation { get; set; } = StackPanelControl.Orientation.Horizontal;

        [A_XSDElementProperty("Size", "Settings", "Fixed main-axis size in pixels; 0 for the pane that takes the remainder.")]
        public int size { get; set; }

        [A_XSDElementProperty("Active", "Settings", "Index of the tab that was on top.")]
        public int active { get; set; }

        [A_XSDElementProperty("SessionTab", "Settings")]
        public List<SessionTab> tabs = new List<SessionTab>();

        [A_XSDElementProperty("SessionPane", "Settings")]
        public List<SessionPane> panes = new List<SessionPane>();
    }

    [A_XSDType("SessionWorkspace", "Settings", AllowedChildren = typeof(SessionPane))]
    public class SessionWorkspace : ISessionWindowChild
    {
        [A_XSDElementProperty("Title", "Settings", "Name on the workspace's tab.")]
        public string title { get; set; } = "";

        [A_XSDElementProperty("Kind", "Settings", "What the workspace is for.")]
        public WorkspaceKind kind { get; set; }

        [A_XSDElementProperty("Shown", "Settings", "True for the workspace that was on screen.")]
        public bool shown { get; set; }

        [A_XSDElementProperty("Ribbon", "Settings", "The ribbon category picked in a Docs or Sheets workspace.")]
        public string ribbon { get; set; } = "Format";

        [A_XSDElementProperty("Inspector", "Settings", "True while a Docs or Sheets workspace shows its inspector.")]
        public bool inspector { get; set; } = true;

        [A_XSDElementProperty("SessionPane", "Settings")]
        public List<SessionPane> panes = new List<SessionPane>();
    }

    [A_XSDType("SessionRoot", "Settings")]
    public class SessionRoot : ISessionWindowChild
    {
        [A_XSDElementProperty("Path", "Settings", "Folder the window's browser listed.")]
        public string path { get; set; } = "";
    }

    [A_XSDType("SessionWindow", "Settings", AllowedChildren = typeof(ISessionWindowChild))]
    public class SessionWindow
    {
        [A_XSDElementProperty("Document", "Settings", "UI document the window was built from.")]
        public string document { get; set; } = "";

        [A_XSDElementProperty("Primary", "Settings", "True for the window the application boots into.")]
        public bool primary { get; set; }

        // restore rect, which is where a maximized window goes back to
        [A_XSDElementProperty("X", "Settings")] public int x { get; set; }
        [A_XSDElementProperty("Y", "Settings")] public int y { get; set; }
        [A_XSDElementProperty("Width", "Settings")] public int width { get; set; }
        [A_XSDElementProperty("Height", "Settings")] public int height { get; set; }
        [A_XSDElementProperty("Maximized", "Settings")] public bool maximized { get; set; }

        [A_XSDElementProperty("Pinned", "Settings", "True while the window is kept above every other window.")]
        public bool pinned { get; set; }

        [A_XSDElementProperty("SessionRoot", "Settings")]
        public List<SessionRoot> roots = new List<SessionRoot>();

        [A_XSDElementProperty("SessionWorkspace", "Settings")]
        public List<SessionWorkspace> workspaces = new List<SessionWorkspace>();

        // read from records written before workspaces, never written
        [A_XSDElementProperty("SessionPane", "Settings")]
        public List<SessionPane> panes = new List<SessionPane>();
    }

    // One partition of the record. What a key means is the host's business — the engine only groups by it.
    [A_XSDType("SessionScope", "Settings", AllowedChildren = typeof(SessionWindow))]
    public class SessionScope
    {
        [A_XSDElementProperty("Key", "Settings", "What this set of windows belongs to.")]
        public string key { get; set; } = "";

        [A_XSDElementProperty("SessionWindow", "Settings")]
        public List<SessionWindow> windows = new List<SessionWindow>();
    }

    // The windows that were open, where they sat, and what was in them. Written by the shutdown
    // sequence and read back at boot into every Workspace.
    [A_XSDType("Session", "Settings", AllowedChildren = typeof(SessionScope))]
    public class SessionLayout : ISettingsGroup
    {
        private static readonly LogChannel Log = LogChannel.For("Session");

        [A_XSDElementProperty("SessionScope", "Settings")]
        public List<SessionScope> scopes = new List<SessionScope>();

        // Which partition capture writes and restore reads. The host sets it to whatever it wants
        // remembered separately; one application-wide session is the empty default.
        public static string scope = "";

        // A partition kept apart from scope: its windows are tagged with it, stay open across a scope
        // change and are recorded even while paused. Empty for none.
        public static string freeScope = "";

        // How the host turns a saved path into a tab. The engine can build the editor but not what
        // the host binds to it, so restoring tabs needs this set.
        public static Func<string, bool, TabItemControl> tabFactory;

        // set while the scope's layout is not on screen yet, so a capture does not record the empty windows over it
        public static bool paused;

        // secondary windows are renamed on restore, so a later tear-off cannot collide with one
        private const string restoredWindow = "session-";
        private static int restoredCount;

        #region ---- restore ----
        // Rebuilds what the last session left in this scope. The primary's tree is already parsed and
        // assigned; every other window is opened here. Falls back to the authored arrangement.
        public static void Restore()
        {
            if (!Rebuild(true)) WorkspaceControl.In(Engine.primary.ui.uiRoot)?.LoadDefault();
        }

        // Reopens the windows recorded under freeScope.
        public static void RestoreFree()
        {
            if (string.IsNullOrEmpty(freeScope)) return;
            SessionScope recorded = SettingsRegistry.Get<SessionLayout>().Recorded(freeScope);
            if (recorded == null) return;

            foreach (SessionWindow record in recorded.windows)
                if (!record.primary && OpenRecorded(record) is RenderWindow window) window.sessionScope = freeScope;
            Log.Info($"restored {recorded.windows.Count} window(s) for '{freeScope}'");
        }

        // Records this scope, empties every workspace and rebuilds key's, the primary staying where it is.
        // False when key has nothing recorded, leaving the primary its first-run workspaces, each one empty pane.
        public static bool ChangeScope(string key)
        {
            Capture();
            ClearWorkspaces();
            scope = key;

            if (Rebuild(false)) return true;
            WorkspaceControl.In(Engine.primary.ui.uiRoot)?.LoadEmpty();
            return false;
        }

        // The primary back to its first-run workspaces, every other workspace window closed but stickies.
        public static void Reset()
        {
            WorkspaceControl primary = WorkspaceControl.In(Engine.primary.ui.uiRoot);
            HashSet<string> stickies = TabViewControl.TabViews(Engine.primary.ui.uiRoot)
                .Select(view => view.stickyDocument).Where(document => !string.IsNullOrEmpty(document)).ToHashSet();

            ClearWorkspaces(window => stickies.Contains(window.uiDocument));
            primary?.LoadDefault();
            Log.Info($"reset the UI for '{scope}'");
        }

        private static bool Rebuild(bool placePrimary)
        {
            WorkspaceControl primary = WorkspaceControl.In(Engine.primary.ui.uiRoot);
            SessionScope recorded = SettingsRegistry.Get<SessionLayout>().Recorded(scope);
            if (recorded == null || recorded.windows.Count == 0) return false;

            foreach (SessionWindow record in recorded.windows)
            {
                if (record.primary)
                {
                    Engine.primary.roots = record.roots.Select(r => r.path).ToList();
                    Fill(primary, record);
                    if (placePrimary) Place(Engine.primary, record);
                    continue;
                }

                OpenRecorded(record);
            }

            // GLFW focuses each window as it is created, so the one the user asked for goes last.
            Engine.primary.Focus();
            Log.Info($"restored {recorded.windows.Count} window(s) for '{scope}'");
            return true;
        }

        // The primary's workspace emptied and every other window holding one closed.
        private static void ClearWorkspaces(Func<RenderWindow, bool> kept = null)
        {
            foreach (RenderWindow window in Engine.windows.Values.ToList())
            {
                if (window.closeRequested || WorkspaceControl.In(window.ui?.uiRoot) is not WorkspaceControl workspace) continue;
                if (window != Engine.primary && (window.sessionScope != null || (kept != null && kept(window)))) continue;

                if (window == Engine.primary)
                    foreach (Entity child in workspace.children.ToList()) child.Destroy();
                else Engine.CloseWindow(window);
            }
        }

        private static RenderWindow OpenRecorded(SessionWindow record)
        {
            if (string.IsNullOrEmpty(record.document)) return null;

            (int x, int y) = PositionFor(record);
            RenderWindow window = Engine.OpenWindow(restoredWindow + ++restoredCount,
                (uint)record.width, (uint)record.height, x, y);

            window.uiDocument = record.document;
            window.roots = record.roots.Select(r => r.path).ToList();
            window.ui.uiRoot = (WindowRoot)Control.ParseXML(record.document);

            Fill(WorkspaceControl.In(window.ui.uiRoot), record);
            if (record.maximized) window.os.Maximize();
            if (record.pinned) WindowActions.Pin(window, true);
            return window;
        }

        // A window whose saved rect no longer lands on a screen goes back to the middle of the
        // primary one, at the size it had.
        private static (int x, int y) PositionFor(SessionWindow record)
        {
            if (AGlfwWindow.RectOnAnyMonitor(record.x, record.y, record.width, record.height))
                return (record.x, record.y);

            Log.Info($"saved rect {record.x},{record.y} {record.width}x{record.height} is off every screen — centring.");
            return AGlfwWindow.CenterOnPrimary(record.width, record.height);
        }

        private static void Place(RenderWindow window, SessionWindow record)
        {
            (int x, int y) = PositionFor(record);

            window.os.Resize((uint)record.width, (uint)record.height);
            window.os.SetPosition(x, y);
            if (record.maximized) window.os.Maximize();
        }

        // A window with nothing recorded still gets its authored arrangement, so a record written
        // before a workspace existed does not open an empty window. A record written before there
        // were several workspaces becomes one General workspace.
        private static void Fill(WorkspaceControl workspace, SessionWindow record)
        {
            if (workspace == null) return;

            if (record.workspaces.Count == 0)
            {
                if (record.panes.Count == 0) { workspace.LoadDefault(); return; }
                FillPage(workspace, workspace.AddPage(nameof(WorkspaceKind.General), WorkspaceKind.General), record.panes);
                return;
            }

            WorkspacePageControl shown = null;
            foreach (SessionWorkspace saved in record.workspaces)
            {
                WorkspacePageControl page = workspace.AddPage(saved.title, saved.kind);
                page.ribbonCategory = saved.ribbon;
                page.inspectorShown = saved.inspector;
                FillPage(workspace, page, saved.panes);
                if (saved.shown) shown = page;
            }
            if (shown != null) workspace.Show(shown);
        }

        private static void FillPage(WorkspaceControl workspace, WorkspacePageControl page, List<SessionPane> panes)
        {
            TabViewControl seed = workspace.LoadPane(page);
            if (seed != null && panes.Count > 0) Build(panes[0], seed);
        }

        // A second workspace holding the same files in the same arrangement.
        public static WorkspacePageControl Duplicate(WorkspaceControl workspace, WorkspacePageControl page)
        {
            WorkspacePageControl copy = workspace.AddPage(page.title, page.kind);
            copy.ribbonCategory = page.ribbonCategory;
            copy.inspectorShown = page.inspectorShown;
            FillPage(workspace, copy, PanesOf(page));
            return copy;
        }

        // The arrangement replayed as the splits that would have produced it, so all of
        // SplitViewControl's grip and sizing rules are the ones that apply.
        private static void Build(SessionPane record, TabViewControl into)
        {
            if (record.panes.Count == 0) { FillTabs(record, into); return; }

            bool vertical = record.orientation == StackPanelControl.Orientation.Vertical;
            SplitViewControl.SplitEdge edge = vertical
                ? SplitViewControl.SplitEdge.Bottom
                : SplitViewControl.SplitEdge.Right;

            List<TabViewControl> panes = new List<TabViewControl>();
            TabViewControl current = into;

            for (int i = 0; i < record.panes.Count - 1; i++)
            {
                TabViewControl fresh = SplitViewControl.Split(current, edge);
                if (fresh == null) break;

                panes.Add(current);
                current = fresh;
            }
            panes.Add(current);

            for (int i = 0; i < panes.Count; i++)
            {
                SessionPane child = record.panes[i];

                // Split sizes the pane it makes; a recorded size is what that pane was dragged to.
                if (child.size > 0)
                {
                    if (vertical) panes[i].preferredHeight = child.size;
                    else panes[i].preferredWidth = child.size;
                }

                Build(child, panes[i]);
            }
        }

        // A note that is no longer on disk is dropped rather than reopened, so the active index is
        // resolved against the tabs that were actually built.
        private static void FillTabs(SessionPane record, TabViewControl view)
        {
            if (tabFactory == null)
            {
                Log.Warn($"no tab factory — {record.tabs.Count} tab(s) not reopened.");
                return;
            }

            TabItemControl active = null;

            for (int i = 0; i < record.tabs.Count; i++)
            {
                string path = record.tabs[i].path;
                if (!File.Exists(path))
                {
                    Log.Info($"note is gone, not reopening: {path}");
                    continue;
                }

                TabItemControl tab = tabFactory(path, record.tabs[i].asXml);
                if (tab == null) continue;

                TabViewControl.FileEditorOf(tab)?.RestoreView(record.tabs[i]);
                view.AddChild(tab);
                if (i == record.active) active = tab;
            }

            if (active != null) view.SetActive(active);
        }

        private SessionScope Recorded(string key)
        {
            foreach (SessionScope existing in scopes)
                if (string.Equals(existing.key, key, StringComparison.OrdinalIgnoreCase))
                    return existing;

            return null;
        }
        #endregion

        #region ---- capture ----
        [A_XSDActionDependency("Session.Capture", "Shutdown", "Records every open window's placement and panes")]
        public static bool Capture()
        {
            SettingsRegistry.Get<SessionLayout>().Record();
            return true;
        }

        private void Record()
        {
            List<SessionWindow> captured = new List<SessionWindow>();
            List<SessionWindow> free = new List<SessionWindow>();
            Dictionary<string, List<SessionWindow>> scoped = new Dictionary<string, List<SessionWindow>>(StringComparer.OrdinalIgnoreCase);

            foreach (RenderWindow window in Engine.windows.Values)
            {
                bool isFree = window.sessionScope != null && window.sessionScope == freeScope;
                if (window.sessionScope == null && paused) continue;
                if (RecordOf(window) is not SessionWindow record) continue;

                if (isFree) free.Add(record);
                else if (window.sessionScope == null) captured.Add(record);
                else if (scoped.TryGetValue(window.sessionScope, out List<SessionWindow>? list)) list.Add(record);
                else scoped[window.sessionScope] = [record];
            }

            if (!string.IsNullOrEmpty(freeScope))
            {
                ScopeFor(freeScope).windows = free;
                Log.Info($"captured {free.Count} window(s) for '{freeScope}'");
            }

            foreach ((string key, List<SessionWindow> windows) in scoped)
            {
                ScopeFor(key).windows = windows;
                Log.Info($"captured {windows.Count} window(s) for '{key}'");
            }

            if (paused) return;
            ScopeFor(scope).windows = captured;
            Log.Info($"captured {captured.Count} window(s) for '{scope}'");
        }

        // A window's placement and workspaces; null for one that holds no session.
        private static SessionWindow? RecordOf(RenderWindow window)
        {
            if (string.IsNullOrEmpty(window.uiDocument) || window.closeRequested) return null;

            WorkspaceControl workspace = WorkspaceControl.In(window.ui.uiRoot);
            if (workspace == null) return null;

            (int x, int y, int width, int height, bool maximized) = window.os.GetPlacement();

            SessionWindow record = new SessionWindow
            {
                document = window.uiDocument,
                primary = window == Engine.primary,
                x = x,
                y = y,
                width = width,
                height = height,
                maximized = maximized,
                pinned = window.pinned,
                roots = window.roots.Select(path => new SessionRoot { path = path }).ToList()
            };

            foreach (WorkspacePageControl page in workspace.Pages)
                record.workspaces.Add(new SessionWorkspace
                {
                    title = page.title,
                    kind = page.kind,
                    shown = ReferenceEquals(page, workspace.shown),
                    ribbon = page.ribbonCategory,
                    inspector = page.inspectorShown,
                    panes = PanesOf(page)
                });
            return record;
        }

        private static List<SessionPane> PanesOf(WorkspacePageControl page)
        {
            List<SessionPane> panes = new List<SessionPane>();
            if (page.content is Control content && PaneOf(content) is SessionPane root) panes.Add(root);
            return panes;
        }

        private static SessionPane PaneOf(Control node)
        {
            if (node is SplitViewControl split) return SplitPane(split);
            if (node is TabViewControl view) return LeafPane(view);
            return null;
        }

        private static SessionPane SplitPane(SplitViewControl split)
        {
            SessionPane record = new SessionPane { orientation = split.orientation };
            bool vertical = split.orientation == StackPanelControl.Orientation.Vertical;

            foreach (Entity child in split.children)
            {
                if (child is not Control pane || pane is SplitterControl) continue;
                if (PaneOf(pane) is not SessionPane captured) continue;

                captured.size = (int)(vertical ? pane.preferredHeight : pane.preferredWidth);
                record.panes.Add(captured);
            }

            return record;
        }

        // A tab whose editor never loaded a file cannot be reopened, so it is left out and the active
        // index counts only what was written.
        private static SessionPane LeafPane(TabViewControl view)
        {
            SessionPane record = new SessionPane();

            foreach (TabItemControl item in view.Items)
            {
                IFileEditor editor = TabViewControl.FileEditorOf(item);
                string path = editor?.path;
                if (path == null) continue;

                if (ReferenceEquals(item, view.activeItem)) record.active = record.tabs.Count;
                SessionTab tab = editor.ViewState();
                tab.path = path;
                record.tabs.Add(tab);
            }

            return record;
        }

        private SessionScope ScopeFor(string key)
        {
            if (Recorded(key) is SessionScope existing) return existing;

            SessionScope fresh = new SessionScope { key = key };
            scopes.Add(fresh);
            return fresh;
        }
        #endregion
    }
}
