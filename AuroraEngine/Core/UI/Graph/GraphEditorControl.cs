using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Containers.Workspace;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Sheets;
using ArctisAurora.Core.UI.Text;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ArctisAurora.Core.UI.Graph
{
    // A vault's link graph: its files as nodes and their links as edges, laid out by force, panned and zoomed by pointer.
    public sealed class GraphEditorControl : ContainerControl, IFileEditor
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
        private const float NodeSize = 8f;
        private const float NodeGrowth = 1.5f;
        private const float MaxNodeSize = 24f;
        private const float MinShownSize = 3f;
        private const float EdgeWidth = 1f;
        private const float LabelGap = 2f;
        private const float GrabSlop = 4f;
        private const float DragSlop = 3f;
        private const int FontSize = 12;

        // view
        private const float LabelZoom = 0.8f;
        private const float MinZoom = 0.1f;
        private const float MaxZoom = 4f;
        private const float ZoomStep = 1.1f;
        private const float DimAlpha = 0.2f;

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
        private PanelControl[] edgeParts = Array.Empty<PanelControl>();
        private PanelControl[] nodeParts = Array.Empty<PanelControl>();
        private LabelControl[] labelParts = Array.Empty<LabelControl>();

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
                nodes.EnsureCapacity(index.fileCount);
                edges.EnsureCapacity(index.fileCount);
                Dictionary<string, int> nodeOfKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                Dictionary<(int, int), int> edgeAt = new Dictionary<(int, int), int>();
                int NodeFor(string key, string label, NodeKind kind, PaletteRole role)
                {
                    if (nodeOfKey.TryGetValue(key, out int found)) return found;
                    nodeOfKey[key] = nodes.Count;
                    nodes.Add(new Node(key, label, kind, role));
                    return nodes.Count - 1;
                }

                foreach (string file in index.Files())
                    NodeFor(file, LinksPanelControl.NameOf(file), index.IsSealed(file) ? NodeKind.Sealed : NodeKind.File, RoleOf(file));

                foreach (string file in index.Files())
                    foreach (LinkEdge edge in index.Outgoing(file))
                    {
                        int to;
                        if (edge.resolved is string target)
                            to = NodeFor(target, LinksPanelControl.NameOf(target), IsInsideVault(target) ? NodeKind.File : NodeKind.Boundary, RoleOf(target));
                        else if (document.showUnresolved)
                            to = NodeFor("?" + edge.target, edge.target, NodeKind.Ghost, PaletteRole.Line);
                        else continue;

                        int from = nodeOfKey[file];
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

            (int a, int b)[] links = new (int, int)[edges.Count];
            for (int e = 0; e < links.Length; e++)
                links[e] = (edges[e].a, edges[e].b);
            layout = new GraphLayout(nodes.Count, links);
            for (int i = 0; i < nodes.Count; i++)
                if (document.pins.TryGetValue(PinKey(nodes[i].key), out Vector2 pin)) layout.Pin(i, pin);
            near = new bool[nodes.Count];
            hovered = -1;
            dragged = -1;
            EnsureParts();
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

        // Grows the part arrays to the graph's counts; labels never outnumber nodes.
        private void EnsureParts()
        {
            int had = edgeParts.Length;
            if (edges.Count > had)
            {
                Array.Resize(ref edgeParts, edges.Count);
                for (int i = had; i < edgeParts.Length; i++)
                    Adopt(edgeLayer, edgeParts[i] = new PanelControl { hitTestable = false });
            }

            had = nodeParts.Length;
            if (nodes.Count > had)
            {
                Array.Resize(ref nodeParts, nodes.Count);
                for (int i = had; i < nodeParts.Length; i++)
                    Adopt(nodeLayer, nodeParts[i] = new PanelControl { hitTestable = false });
            }

            had = labelParts.Length;
            if (nodes.Count > had)
            {
                Array.Resize(ref labelParts, nodes.Count);
                for (int i = had; i < labelParts.Length; i++)
                    Adopt(labelLayer, labelParts[i] = new LabelControl { hitTestable = false, fontSize = FontSize });
            }
        }

        private bool IsInsideVault(string file) =>
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

            Vector2 centre = Centre;
            Vector2 pan = this.pan;
            float zoom = this.zoom;
            int hovered = this.hovered;
            bool[] near = this.near;
            Vector2[] position = layout.position;
            LayoutRect hidden = Hidden;
            PanelControl[] edgeParts = this.edgeParts;
            PanelControl[] nodeParts = this.nodeParts;
            LabelControl[] labelParts = this.labelParts;
            ReadOnlySpan<(int a, int b, bool strong)> edgeSpan = CollectionsMarshal.AsSpan(edges);
            ReadOnlySpan<Node> nodeSpan = CollectionsMarshal.AsSpan(nodes);

            float hairline = UIScaling.Hairline(this, EdgeWidth);
            for (int e = 0; e < edgeSpan.Length; e++)
            {
                (int a, int b, bool strong) = edgeSpan[e];
                PanelControl part = edgeParts[e];
                Vector2 from = centre + (position[a] - pan) * zoom;
                Vector2 d = (position[b] - position[a]) * zoom;
                float length = d.Length();
                part.role = strong ? PaletteRole.MutedInk : PaletteRole.Line;
                part.alpha = hovered >= 0 && a != hovered && b != hovered ? DimAlpha : 1f;
                part.rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.Atan2(d.Y, d.X));
                Vector2 mid = from + d * 0.5f;
                part.Arrange(new LayoutRect(mid.X - length * 0.5f, mid.Y - hairline * 0.5f, length, hairline));
            }
            for (int e = edgeSpan.Length; e < edgeParts.Length; e++)
                edgeParts[e].Arrange(hidden);

            int label = 0;
            for (int i = 0; i < nodeSpan.Length; i++)
            {
                ref readonly Node node = ref nodeSpan[i];
                PanelControl part = nodeParts[i];
                float size = Size(i);
                Vector2 at = centre + (position[i] - pan) * zoom;
                part.role = node.kind switch
                {
                    NodeKind.Boundary or NodeKind.Sealed => PaletteRole.MutedInk,
                    NodeKind.Ghost => PaletteRole.Line,
                    _ => node.role
                };
                part.alpha = hovered >= 0 && !near[i] ? DimAlpha : 1f;
                part.cornerRadius = new CornerRadii(size * 0.5f);
                part.Arrange(new LayoutRect(at.X - size * 0.5f, at.Y - size * 0.5f, size, size));

                if (zoom < LabelZoom && !(hovered >= 0 && near[i])) continue;
                LabelControl text = labelParts[label++];
                text.text = node.label;
                text.role = node.kind is NodeKind.File ? PaletteRole.Ink : PaletteRole.MutedInk;
                text.alpha = part.alpha;
                text.Measure(unbounded);
                Vector2 extent = text.DesiredSize;
                text.Arrange(new LayoutRect(at.X - extent.X * 0.5f, at.Y + size * 0.5f + LabelGap, extent.X, extent.Y));
            }
            for (int i = nodeSpan.Length; i < nodeParts.Length; i++)
                nodeParts[i].Arrange(hidden);
            for (int i = label; i < labelParts.Length; i++)
                labelParts[i].Arrange(hidden);

            edgeLayer.Settle(finalRect);
            nodeLayer.Settle(finalRect);
            labelLayer.Settle(finalRect);
        }

        private LayoutRect Hidden => new LayoutRect(arrangedRect.x, arrangedRect.y, 0f, 0f);

        private Vector2 Centre => new Vector2(arrangedRect.x + arrangedRect.width * 0.5f, arrangedRect.y + arrangedRect.height * 0.5f);

        private float Size(int node) => MathF.Max(MinShownSize, MathF.Min(MaxNodeSize, NodeSize + NodeGrowth * degree[node]) * zoom);

        // A node's position on screen, and a screen point's in the layout.
        public Vector2 Screen(int node) => Centre + (layout.position[node] - pan) * zoom;

        private Vector2 ToLayout(in Vector2 point) => pan + (point - Centre) / zoom;

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
        public int NodeAt(in Vector2 point)
        {
            Vector2 at = ToLayout(in point);
            float toLayout = 1f / zoom;
            Vector2[] position = layout.position;
            for (int i = position.Length - 1; i >= 0; i--)
                if (Vector2.Distance(at, position[i]) <= (Size(i) * 0.5f + GrabSlop) * toLayout) return i;
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
            moved |= Vector2.Distance(e.point, pressPoint) > DragSlop;
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
            if (e.button != PointerEvent.leftButton || moved) return base.OnPointerRelease(e);
            int node = NodeAt(e.point);
            if (node < 0 || nodes[node].kind == NodeKind.Ghost) return base.OnPointerRelease(e);

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
            zoom = Math.Clamp(zoom * MathF.Pow(ZoomStep, e.delta.Y), MinZoom, MaxZoom);
            pan = anchor - (e.point - Centre) / zoom;
            InvalidateArrange();
            return true;
        }

        private void Hover(int node)
        {
            if (node == hovered) return;
            hovered = node;
            bool[] near = this.near;
            Array.Clear(near);
            if (node >= 0)
            {
                near[node] = true;
                ReadOnlySpan<(int a, int b, bool strong)> edgeSpan = CollectionsMarshal.AsSpan(edges);
                for (int e = 0; e < edgeSpan.Length; e++)
                {
                    (int a, int b, _) = edgeSpan[e];
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
