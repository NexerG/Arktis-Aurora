using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Registry.Assets;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Registry;
using ArctisAurora.EngineWork.Rendering;
using Silk.NET.GLFW;
using System.Numerics;
using Silk.NET.Vulkan;

namespace ArctisAurora.Core.UI.Menus
{
    // Resolves, opens, hosts and closes the context menu on the new stack.
    public static class ContextMenus
    {
        private static readonly LogChannel Log = LogChannel.For("ContextMenus");

        // the control the open menu was opened on
        public static Control? target { get; private set; }

        // menus by name — parsed documents and ones registered from code
        private static readonly Dictionary<string, ContextMenu> _menus = new Dictionary<string, ContextMenu>();

        // submenu entry builders by name, called each time their submenu opens
        private static readonly Dictionary<string, Func<List<ContextMenuEntry>>> _sources = new Dictionary<string, Func<List<ContextMenuEntry>>>();

        // entry conditions by name, asked each time a menu holding them opens
        private static readonly Dictionary<string, Func<Control, bool>> _conditions = new Dictionary<string, Func<Control, bool>>();

        // open panels, index = depth
        private static readonly List<ContextMenuControl> _open = new List<ContextMenuControl>();
        private static RenderWindow? _origin;
        private static int _windowSerial;

        // runs once when the open menu closes, whoever closes it
        private static Action? _onClosed;

        // the control whose menu the current press closed
        private static Control? _closedOn;

        private static readonly ContextMenuLine groupLine = new ContextMenuLine();

        #region ---- menus ----
        // Makes a menu built in code nameable by ContextMenu="…".
        public static void Register(string name, ContextMenu menu) => _menus[name] = menu;

        // Makes a builder nameable by a submenu's Source="…".
        public static void RegisterSource(string name, Func<List<ContextMenuEntry>> build) => _sources[name] = build;

        // Makes a check nameable by an entry's When="…".
        public static void RegisterCondition(string name, Func<Control, bool> shown) => _conditions[name] = shown;

        // Whether an entry shows on a control; one naming an unknown condition shows.
        private static bool Shown(ContextMenuEntry entry, Control on)
        {
            if (entry.when == null) return true;
            if (_conditions.TryGetValue(entry.when, out Func<Control, bool>? shown)) return shown(on);

            Log.Once().Warn($"no context menu condition named '{entry.when}'");
            return true;
        }

        // A submenu's entries: built by its source when it names one, else the authored ones that show.
        private static List<ContextMenuEntry> EntriesOf(ContextMenuSubmenu submenu)
        {
            if (submenu.source == null) return submenu.entries.FindAll(e => Shown(e, target!));
            if (_sources.TryGetValue(submenu.source, out Func<List<ContextMenuEntry>>? build)) return build();

            Log.Once().Warn($"no context menu source named '{submenu.source}'");
            return new List<ContextMenuEntry>();
        }

        // A registered menu, or the document of that name, parsed on first use.
        public static ContextMenu? Get(string name)
        {
            if (_menus.TryGetValue(name, out ContextMenu? menu)) return menu;

            Dictionary<string, ContextMenuAsset>? documents =
                AssetRegistries.GetRegistryByValueType<string, ContextMenuAsset>(typeof(ContextMenuAsset));
            if (documents == null || !documents.TryGetValue(name, out ContextMenuAsset? asset))
            {
                Log.Warn($"no context menu named '{name}'");
                return null;
            }

            menu = Control.ParseMenu(asset.path);
            _menus[name] = menu;
            return menu;
        }

        // Entries from the control up, one group per menu, until a control stops the walk.
        public static List<ContextMenuEntry> Collect(Control control)
        {
            List<ContextMenuEntry> entries = new List<ContextMenuEntry>();
            for (Control? c = control; c != null; c = c.parent as Control)
            {
                ContextMenu? menu = c.contextMenu == null ? null : Get(c.contextMenu);
                List<ContextMenuEntry>? shown = menu?.entries.FindAll(e => Shown(e, control));
                if (shown != null && shown.Count > 0)
                {
                    if (entries.Count > 0) entries.Add(groupLine);
                    entries.AddRange(shown);
                }
                if (c.stopsContextMenu) break;
            }
            return entries;
        }
        #endregion

        #region ---- open and close ----
        internal static void OpenOn(Control? control, Vector2 point)
        {
            if (control == null) return;
            Open(Collect(control), control, point);
        }

        // Opens a menu of entries on a control, at a point in its window's design space.
        public static void Open(List<ContextMenuEntry> entries, Control on, Vector2 point, float width = 0f, bool centered = false, Action? onClosed = null)
        {
            Close();
            if (entries.Count == 0) return;
            _onClosed = onClosed;

            RenderWindow? window = UIEngine.WindowOf(on);
            if (window?.ui.uiRoot == null) return;

            _origin = window;
            target = on;
            Host(new ContextMenuControl(entries, 0) { position = point, preferredWidth = width, centered = centered });
        }

        // Opens one level under the open panel holding the control, which stays open; like Open when none holds it.
        public static void OpenFrom(List<ContextMenuEntry> entries, Control on, Vector2 point, float width = 0f, bool centered = false)
        {
            ContextMenuControl? owner = null;
            for (Control? c = on; c != null && owner == null; c = c.parent as Control)
                if (c is ContextMenuControl panel && _open.Contains(panel)) owner = panel;
            if (owner == null)
            {
                Open(entries, on, point, width, centered);
                return;
            }

            int deeper = owner.depth + 1;
            CloseFrom(deeper);
            if (entries.Count == 0) return;

            LayoutRect p = owner.arrangedRect;
            Vector2 at = owner.position + new Vector2(point.X - p.x, point.Y - p.y);
            Host(new ContextMenuControl(entries, deeper) { position = at, preferredWidth = width, centered = centered });
        }

        public static void Close() => CloseFrom(0);

        // Closes the panel at depth and every one opened from it.
        private static void CloseFrom(int depth)
        {
            if (depth == 0 && _onClosed != null)
            {
                Action closed = _onClosed;
                _onClosed = null;
                closed();
            }

            for (int i = _open.Count - 1; i >= depth; i--)
            {
                ContextMenuControl panel = _open[i];
                _open.RemoveAt(i);

                if (panel.window != null)
                {
                    panel.window.ui.uiRoot = null;
                    Engine.CloseWindow(panel.window);
                    continue;
                }
                (panel.parent as Control)?.RemoveChild(panel);
                panel.Destroy();
            }

            if (depth > 0) return;
            target = null;
            _origin = null;
        }

        // Over the origin window's tree when the panel fits inside it, in a window of its own when not.
        // Opens in a window of its own at a screen point, sized by scaleFrom and kept inside the monitor's
        // work area — above the point when it would cross the bottom.
        public static void OpenAtScreen(List<ContextMenuEntry> entries, int x, int y, RenderWindow scaleFrom)
        {
            Close();
            if (entries.Count == 0 || scaleFrom.ui.uiRoot == null) return;

            _origin = scaleFrom;
            target = scaleFrom.ui.uiRoot;
            ContextMenuControl panel = new ContextMenuControl(entries, 0);
            Vector2 viewport = scaleFrom.ui.uiRoot.arrangedRect.size;
            Vector2 size = panel.Measure(viewport);
            HostInWindow(panel, size, viewport);
            _open.Add(panel);

            int width = (int)UIScaling.ToPixels(scaleFrom, (uint)MathF.Ceiling(size.X));
            int height = (int)UIScaling.ToPixels(scaleFrom, (uint)MathF.Ceiling(size.Y));
            (int ax, int ay, int aw, int ah) = AGlfwWindow.WorkAreaAt(x, y);
            int px = Math.Max(ax, Math.Min(x, ax + aw - width));
            int py = Math.Max(ay, y + height > ay + ah ? y - height : y);
            panel.window!.os.SetPosition(px, py);
            panel.window.Focus();
        }

        private static void Host(ContextMenuControl panel)
        {
            WindowRoot root = _origin!.ui.uiRoot;
            Vector2 viewport = root.arrangedRect.size;
            Vector2 size = panel.Measure(viewport);
            Vector2 at = panel.position;

            if (at.X >= 0 && at.Y >= 0 && at.X + size.X <= viewport.X && at.Y + size.Y <= viewport.Y)
                root.AddChild(panel);
            else
                HostInWindow(panel, size, viewport);

            _open.Add(panel);
        }

        private static unsafe void HostInWindow(ContextMenuControl panel, Vector2 size, Vector2 viewport)
        {
            RenderWindow window = Engine.OpenMenuWindow($"context-menu-{_windowSerial++}",
                                                        UIScaling.ToPixels(_origin!, (uint)MathF.Ceiling(size.X)),
                                                        UIScaling.ToPixels(_origin!, (uint)MathF.Ceiling(size.Y)));
            panel.window = window;

            WindowRoot root = new WindowRoot();
            root.AddChild(panel);
            window.ui.uiRoot = root;

            AGlfwWindow._glfw.GetWindowPos(_origin!.os.handle, out int x, out int y);
            Extent2D pixels = _origin.os.windowSize;
            window.os.SetPosition(x + (int)(panel.position.X * pixels.Width / viewport.X),
                                  y + (int)(panel.position.Y * pixels.Height / viewport.Y));
            window.os.Show();
        }
        #endregion

        #region ---- input ----
        // A hovered row closes the panels deeper than its own; a submenu row opens one beside it.
        internal static void Entered(ContextMenuControl panel, ContextMenuControl.Row row)
        {
            int deeper = panel.depth + 1;
            if (_open.Count > deeper && ReferenceEquals(_open[deeper].opener, row)) return;

            CloseFrom(deeper);
            if (row.entry is not ContextMenuSubmenu submenu) return;
            List<ContextMenuEntry> entries = EntriesOf(submenu);
            if (entries.Count == 0) return;

            LayoutRect r = row.arrangedRect;
            LayoutRect p = panel.arrangedRect;
            Vector2 at = panel.position + new Vector2(r.x + r.width - p.x, r.y - p.y - panel.padding.top);

            Host(new ContextMenuControl(entries, deeper) { position = at, opener = row });
        }

        // A clicked button row runs its action, then closes the menu, or only the list opened from inside a panel.
        internal static void Clicked(ContextMenuControl.Row row)
        {
            if (row.entry is not ContextMenuButton button) return;

            int from = NestedFrom((ContextMenuControl)row.parent);
            button.action?.Invoke();
            CloseFrom(from);
        }

        // A press anywhere but on an open panel closes the menu; inside one, it closes a list opened from it.
        internal static void DismissUnlessInside(Control? pressed)
        {
            _closedOn = null;
            if (_open.Count == 0) return;

            for (Control? c = pressed; c != null; c = c.parent as Control)
                if (c is ContextMenuControl panel)
                {
                    int deeper = panel.depth + 1;
                    if (_open.Count > deeper && _open[deeper].opener == null) CloseFrom(deeper);
                    return;
                }
            _closedOn = target;
            Close();
        }

        // Whether the current press just closed a menu opened on this control; answers true once.
        public static bool ClosedOn(Control on)
        {
            if (!ReferenceEquals(on, _closedOn)) return false;
            _closedOn = null;
            return true;
        }

        // The shallowest panel at or above this one that was opened from inside another; 0 when none was.
        private static int NestedFrom(ContextMenuControl panel)
        {
            for (int i = 1; i <= panel.depth && i < _open.Count; i++)
                if (_open[i].opener == null) return i;
            return 0;
        }

        // Closes the menu once no window of the application has focus.
        internal static unsafe void Tick()
        {
            if (_open.Count == 0 || Testing.TestRunner.active) return;

            foreach (RenderWindow window in Engine.windows.Values)
                if (!window.closeRequested && AGlfwWindow._glfw.GetWindowAttrib(window.os.handle, WindowAttributeGetter.Focused))
                    return;
            Close();
        }
        #endregion
    }
}
