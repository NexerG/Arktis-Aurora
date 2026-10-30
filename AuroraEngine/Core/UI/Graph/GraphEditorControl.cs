using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Containers.Workspace;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Sheets;
using ArctisAurora.Core.UI.Text;
using System.Numerics;

namespace ArctisAurora.Core.UI.Graph
{
    // A vault's link graph: its files as nodes and their links as edges, laid out by force, panned and zoomed by pointer.
    public class GraphEditorControl : ContainerControl, IFileEditor
    {
        // Parts its owner lays out one by one.
        private sealed class GraphParts : ContainerControl
        {
            public GraphParts() => hitTestable = false;

            protected override Vector2 MeasureCore(Vector2 availableSize)
            {
                foreach (Entity child in children)
                    if (child is Control control) control.Measure(unbounded);
                arrange.desired = availableSize;
                return availableSize;
            }

            protected override void ArrangeCore(LayoutRect finalRect) => WriteArranged(finalRect);

            internal void Settle(LayoutRect rect)
            {
                SetFlag(ArrangeFlags.ArrangeDirty, true);
                Arrange(rect);
            }
        }

        private enum NodeKind { File, Boundary, Ghost, Sealed }

        private readonly record struct Node(string key, string label, NodeKind kind, PaletteRole role);

        // geometry, design pixels
        private const float nodeSize = 8f;
        private const float nodeGrowth = 1.5f;
        private const float maxNodeSize = 24f;
        private const float minShownSize = 3f;
        private const float edgeWidth = 1f;
        private const float labelGap = 2f;
        private const float grabSlop = 4f;
        private const float dragSlop = 3f;
        private const int fontSize = 12;

        // view
        private const float labelZoom = 0.8f;
        private const float minZoom = 0.1f;
        private const float maxZoom = 4f;
        private const float zoomStep = 1.1f;
        private const float dimAlpha = 0.2f;

        private static readonly Vector2 unbounded = new Vector2(float.MaxValue, float.MaxValue);

        public GraphDocument document { get; private set; } = GraphDocument.Blank(null);
        public string? path { get; private set; }
        bool IFileEditor.isDirty => document.unsaved;

        public Control tools { get; }
        private readonly LabelControl orphansCaption;
        private readonly LabelControl unresolvedCaption;

        // the graph built from the link index
        private LinkIndex? index;
        private readonly List<Node> nodes = new List<Node>();
        private readonly List<(int a, int b, bool strong)> edges = new List<(int, int, bool)>();
        private int[] degree = Array.Empty<int>();
        private bool[] near = Array.Empty<bool>();
        public GraphLayout layout { get; private set; } = new GraphLayout(0, Array.Empty<(int, int)>());

        // the layout point at the view's centre, and the scale
        private Vector2 pan;
        private float zoom = 1f;

        // pointer
        private int hovered = -1;
        private int dragged = -1;
        private bool panning;
        private bool moved;
        private Vector2 pressPoint;
        private Vector2 pressPan;

        private readonly GraphParts edgeLayer = new GraphParts();
        private readonly GraphParts nodeLayer = new GraphParts();
        private readonly GraphParts labelLayer = new GraphParts();
        private readonly List<PanelControl> edgeParts = new List<PanelControl>();
        private readonly List<PanelControl> nodeParts = new List<PanelControl>();
        private readonly List<LabelControl> labelParts = new List<LabelControl>();

        public int nodeCount => nodes.Count;

        public string KeyOf(int node) => nodes[node].key;

        public GraphEditorControl()
        {
            PaintOr(null, PaletteRole.Surface);
            AddChild(edgeLayer);
            AddChild(nodeLayer);
            AddChild(labelLayer);

            TabToolsControl toolbar = new TabToolsControl();
            tools = toolbar;
            orphansCaption = toolbar.Text("Orphans", _ => Toggle(ref document.showOrphans));
            unresolvedCaption = toolbar.Text("Unresolved", _ => Toggle(ref document.showUnresolved));
            TabViewControl.activeChanged += Activated;
        }

        public override void OnDestroy()
        {
            TabViewControl.activeChanged -= Activated;
            tools.Destroy();
            base.OnDestroy();
        }

        public void LoadPath(string graphPath)
        {
            path = Path.GetFullPath(graphPath);
            document = GraphDocument.Load(path);
            Rebuild();
        }

        private void Activated(TabViewControl view)
        {
            if (view.activeItem != null && TabViewControl.FileEditorOf(view.activeItem) == this) Rebuild();
        }

        private void Toggle(ref bool shown)
        {
            shown = !shown;
            document.unsaved = true;
            Rebuild();
        }

        #region ---- graph ----
        // Refreshes the vault's link index and builds the nodes, edges and layout from it.
        public void Rebuild()
        {
            orphansCaption.role = document.showOrphans ? PaletteRole.Ink : PaletteRole.MutedInk;
            unresolvedCaption.role = document.showUnresolved ? PaletteRole.Ink : PaletteRole.MutedInk;
            nodes.Clear();
            edges.Clear();
            index = path == null ? null : LinkIndex.indexOf?.Invoke(path);
            index?.Refresh();

            if (index != null)
            {
                Dictionary<string, int> at = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                Dictionary<(int, int), int> edgeAt = new Dictionary<(int, int), int>();
                int NodeFor(string key, string label, NodeKind kind, PaletteRole role)
                {
                    if (at.TryGetValue(key, out int found)) return found;
                    at[key] = nodes.Count;
                    nodes.Add(new Node(key, label, kind, role));
                    return nodes.Count - 1;
                }

                List<string> files = index.Files().ToList();
                foreach (string file in files)
                    NodeFor(file, LinksPanelControl.NameOf(file), index.IsSealed(file) ? NodeKind.Sealed : NodeKind.File, RoleOf(file));

                foreach (string file in files)
                    foreach (LinkEdge edge in index.Outgoing(file))
                    {
                        int to;
                        if (edge.resolved is string target)
                            to = NodeFor(target, LinksPanelControl.NameOf(target), Inside(target) ? NodeKind.File : NodeKind.Boundary, RoleOf(target));
                        else if (document.showUnresolved)
                            to = NodeFor("?" + edge.target, edge.target, NodeKind.Ghost, PaletteRole.Line);
                        else continue;

                        int from = at[file];
                        if (to == from) continue;
                        (int, int) pair = (Math.Min(from, to), Math.Max(from, to));
                        bool strong = edge.kind != LinkKind.Link;
                        if (!edgeAt.TryGetValue(pair, out int known))
                        {
                            edgeAt[pair] = edges.Count;
                            edges.Add((pair.Item1, pair.Item2, strong));
                        }
                        else if (strong) edges[known] = (pair.Item1, pair.Item2, true);
                    }
            }

            degree = new int[nodes.Count];
            foreach ((int a, int b, _) in edges)
            {
                degree[a]++;
                degree[b]++;
            }
            if (!document.showOrphans) DropOrphans();

            layout = new GraphLayout(nodes.Count, edges.Select(edge => (edge.a, edge.b)).ToList());
            for (int i = 0; i < nodes.Count; i++)
                if (document.pins.TryGetValue(PinKey(nodes[i].key), out Vector2 pin)) layout.Pin(i, pin);
            near = new bool[nodes.Count];
            hovered = -1;
            dragged = -1;
            SetTicking(true);
            InvalidateLayout();
        }

        private void DropOrphans()
        {
            int[] remap = new int[nodes.Count];
            int kept = 0;
            for (int i = 0; i < nodes.Count; i++)
            {
                remap[i] = degree[i] > 0 ? kept : -1;
                if (degree[i] > 0)
                {
                    nodes[kept] = nodes[i];
                    degree[kept] = degree[i];
                    kept++;
                }
            }
            nodes.RemoveRange(kept, nodes.Count - kept);
            Array.Resize(ref degree, kept);
            for (int e = 0; e < edges.Count; e++)
                edges[e] = (remap[edges[e].a], remap[edges[e].b], edges[e].strong);
        }

        private bool Inside(string file) =>
            index != null && file.StartsWith(Path.TrimEndingDirectorySeparator(index.vault) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        private string PinKey(string key) => index != null && !key.StartsWith('?') ? Path.GetRelativePath(index.vault, key) : key;

        private static PaletteRole RoleOf(string file) => FileTypes.KindOf(file) switch
        {
            FileKind.Sheet => PaletteRole.Sheet,
            FileKind.Planner => PaletteRole.Planner,
            _ => SheetCsv.IsCsv(file) ? PaletteRole.Sheet : PaletteRole.Doc
        };
        #endregion

        public override void OnTick()
        {
            base.OnTick();
            if (layout.settled && dragged < 0)
            {
                SetTicking(false);
                return;
            }
            layout.Step();
            InvalidateArrange();
        }

        #region ---- layout ----
        protected override Vector2 MeasureCore(Vector2 availableSize)
        {
            foreach (Entity child in children)
                if (child is Control control) control.Measure(unbounded);
            arrange.desired = availableSize;
            return availableSize;
        }

        protected override void ArrangeCore(LayoutRect finalRect)
        {
            WriteArranged(finalRect);
            edgeLayer.Arrange(finalRect);
            nodeLayer.Arrange(finalRect);
            labelLayer.Arrange(finalRect);

            float hairline = UIScaling.Hairline(this, edgeWidth);
            for (int e = 0; e < edges.Count; e++)
            {
                (int a, int b, bool strong) = edges[e];
                PanelControl part = Pooled(edgeParts, edgeLayer, e);
                Vector2 from = Screen(a);
                Vector2 d = Screen(b) - from;
                float length = d.Length();
                part.role = strong ? PaletteRole.MutedInk : PaletteRole.Line;
                part.alpha = hovered >= 0 && a != hovered && b != hovered ? dimAlpha : 1f;
                part.rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.Atan2(d.Y, d.X));
                Vector2 mid = from + d * 0.5f;
                part.Arrange(new LayoutRect(mid.X - length * 0.5f, mid.Y - hairline * 0.5f, length, hairline));
            }
            for (int e = edges.Count; e < edgeParts.Count; e++)
                edgeParts[e].Arrange(Hidden);

            int label = 0;
            for (int i = 0; i < nodes.Count; i++)
            {
                PanelControl part = Pooled(nodeParts, nodeLayer, i);
                float size = Size(i);
                Vector2 at = Screen(i);
                part.role = nodes[i].kind switch
                {
                    NodeKind.Boundary or NodeKind.Sealed => PaletteRole.MutedInk,
                    NodeKind.Ghost => PaletteRole.Line,
                    _ => nodes[i].role
                };
                part.alpha = hovered >= 0 && !near[i] ? dimAlpha : 1f;
                part.cornerRadius = new CornerRadii(size * 0.5f);
                part.Arrange(new LayoutRect(at.X - size * 0.5f, at.Y - size * 0.5f, size, size));

                if (zoom < labelZoom && !(hovered >= 0 && near[i])) continue;
                LabelControl text = Label(label++);
                text.text = nodes[i].label;
                text.role = nodes[i].kind is NodeKind.File ? PaletteRole.Ink : PaletteRole.MutedInk;
                text.alpha = part.alpha;
                text.Measure(unbounded);
                Vector2 extent = text.DesiredSize;
                text.Arrange(new LayoutRect(at.X - extent.X * 0.5f, at.Y + size * 0.5f + labelGap, extent.X, extent.Y));
            }
            for (int i = nodes.Count; i < nodeParts.Count; i++)
                nodeParts[i].Arrange(Hidden);
            for (int i = label; i < labelParts.Count; i++)
                labelParts[i].Arrange(Hidden);

            edgeLayer.Settle(finalRect);
            nodeLayer.Settle(finalRect);
            labelLayer.Settle(finalRect);
        }

        private LayoutRect Hidden => new LayoutRect(arrangedRect.x, arrangedRect.y, 0f, 0f);

        private Vector2 Centre => new Vector2(arrangedRect.x + arrangedRect.width * 0.5f, arrangedRect.y + arrangedRect.height * 0.5f);

        private float Size(int node) => MathF.Max(minShownSize, MathF.Min(maxNodeSize, nodeSize + nodeGrowth * degree[node]) * zoom);

        // A node's position on screen, and a screen point's in the layout.
        public Vector2 Screen(int node) => Centre + (layout.position[node] - pan) * zoom;

        private Vector2 ToLayout(Vector2 point) => pan + (point - Centre) / zoom;

        private PanelControl Pooled(List<PanelControl> pool, GraphParts layer, int index)
        {
            while (pool.Count <= index)
            {
                PanelControl part = new PanelControl { hitTestable = false };
                Adopt(layer, part);
                pool.Add(part);
            }
            return pool[index];
        }

        private LabelControl Label(int index)
        {
            while (labelParts.Count <= index)
            {
                LabelControl label = new LabelControl { hitTestable = false, fontSize = fontSize };
                Adopt(labelLayer, label);
                labelParts.Add(label);
            }
            return labelParts[index];
        }

        private void Adopt(GraphParts layer, Control part)
        {
            part.parent = layer;
            layer.children.Add(part);
            MarkTreeOrderDirty();
            part.Measure(unbounded);
        }
        #endregion

        #region ---- pointer ----
        // The topmost node under a point; -1 for none.
        public int NodeAt(Vector2 point)
        {
            for (int i = nodes.Count - 1; i >= 0; i--)
                if (Vector2.Distance(point, Screen(i)) <= Size(i) * 0.5f + grabSlop) return i;
            return -1;
        }

        public override bool OnPointerPress(PointerEvent e)
        {
            base.OnPointerPress(e);
            if (e.button != PointerEvent.leftButton) return false;

            pressPoint = e.point;
            pressPan = pan;
            moved = false;
            dragged = NodeAt(e.point);
            panning = dragged < 0;
            StartDrag();
            return true;
        }

        // Drags a node, pinning it, or pans the view.
        public override void OnDrag(PointerEvent e)
        {
            base.OnDrag(e);
            moved |= Vector2.Distance(e.point, pressPoint) > dragSlop;
            if (!moved) return;

            if (dragged >= 0)
            {
                layout.Pin(dragged, ToLayout(e.point));
                layout.Reheat();
                SetTicking(true);
            }
            else if (panning) pan = pressPan - (e.point - pressPoint) / zoom;
            InvalidateArrange();
        }

        public override void OnDragStop(bool accepted)
        {
            base.OnDragStop(accepted);
            if (dragged >= 0 && moved)
            {
                document.pins[PinKey(nodes[dragged].key)] = layout.position[dragged];
                document.unsaved = true;
            }
            dragged = -1;
            panning = false;
        }

        // A click on a node opens its file.
        public override bool OnPointerRelease(PointerEvent e)
        {
            int node = NodeAt(e.point);
            if (e.button != PointerEvent.leftButton || moved || node < 0 || nodes[node].kind == NodeKind.Ghost)
                return base.OnPointerRelease(e);

            LinkIndex.openFile?.Invoke(nodes[node].key);
            return true;
        }

        public override bool OnPointerMove(PointerEvent e)
        {
            bool handled = base.OnPointerMove(e);
            Hover(dragged >= 0 ? dragged : NodeAt(e.point));
            return handled;
        }

        public override bool OnPointerExit(PointerEvent e)
        {
            if (dragged < 0) Hover(-1);
            return base.OnPointerExit(e);
        }

        // Zooms about the pointer.
        public override bool OnPointerScroll(PointerEvent e)
        {
            if (e.delta.Y == 0f) return base.OnPointerScroll(e);

            Vector2 anchor = ToLayout(e.point);
            zoom = Math.Clamp(zoom * MathF.Pow(zoomStep, e.delta.Y), minZoom, maxZoom);
            pan = anchor - (e.point - Centre) / zoom;
            InvalidateArrange();
            return true;
        }

        private void Hover(int node)
        {
            if (node == hovered) return;
            hovered = node;
            Array.Clear(near);
            if (node >= 0)
            {
                near[node] = true;
                foreach ((int a, int b, _) in edges)
                {
                    if (a == node) near[b] = true;
                    else if (b == node) near[a] = true;
                }
            }
            InvalidateArrange();
        }
        #endregion

        #region ---- file ----
        public void Save()
        {
            if (path == null) return;
            document.Save(path);
            document.unsaved = false;
        }

        public void Repath(string newPath, string name)
        {
            path = newPath;
            document.name = name;
        }

        public SessionTab ViewState() => new SessionTab();

        public void RestoreView(SessionTab view) { }
        #endregion
    }
}
