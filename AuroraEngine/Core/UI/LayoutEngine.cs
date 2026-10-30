using ArctisAurora.Core.Animation;
using ArctisAurora.Core.Data;
using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;

namespace ArctisAurora.Core.UI
{
    // UIElements rows as tree ranges: each row's parent, subtree size and layout kind; measure and arrange walk them.
    public static class LayoutEngine
    {
        private static readonly LogChannel Log = LogChannel.For("Layout");

        // the pool order the structure describes
        private static ulong _builtOrder = ulong.MaxValue;

        // DEBUG: VerifyLayout's pass without skips
        private static bool _noSkip;
        internal static bool NoSkip => _noSkip;

        private static readonly Dictionary<Type, LayoutNodeKind> _kinds = new Dictionary<Type, LayoutNodeKind>();

        // The rows still describe the tree: built for this order, nothing re-parented since.
        internal static bool StructureCurrent => Current(UIEngine.Elements);

        private static bool Current(DataPool pool) => _builtOrder == pool.OrderVersion && !pool.OrderDirty;

        #region ---- measure and arrange ----
        // A row's children: its range while the structure is current, the object's list otherwise.
        private struct ChildCursor
        {
            private readonly List<Entity>? _list;
            private readonly int _end;
            private int _next;

            public ChildCursor(int row, int count)
            {
                _list = null;
                _next = row + 1;
                _end = row + count;
            }

            public ChildCursor(List<Entity> list)
            {
                _list = list;
                _next = 0;
                _end = 0;
            }

            public bool Next(LayoutNode[] nodes, DataPool pool, out int row, out Control? control)
            {
                if (_list == null)
                {
                    control = null;
                    row = _next;
                    if (row >= _end) return false;
                    _next += nodes[row].count;
                    return true;
                }

                while (_next < _list.Count)
                {
                    if (_list[_next++] is Control child)
                    {
                        control = child;
                        row = pool.DenseOf(child.dataHandle);
                        return true;
                    }
                }
                control = null;
                row = -1;
                return false;
            }
        }

        private static ChildCursor ChildrenOf(DataPool pool, int row, Control? control, bool ranged, LayoutNode[] nodes)
            => ranged ? new ChildCursor(row, nodes[row].count) : new ChildCursor((control ?? (Control)pool.OwnerAt(row)).children);

        // Measures a control, or returns what it last measured when nothing changed.
        public static Vector2 Measure(Control control, Vector2 offer)
        {
            DataPool pool = UIEngine.Elements;
            return MeasureRow(pool, pool.DenseOf(control.dataHandle), control, offer);
        }

        // Arranges a control, or leaves its subtree when nothing changed.
        public static void Arrange(Control control, LayoutRect rect)
        {
            DataPool pool = UIEngine.Elements;
            int parent = control.parent is Control owner ? pool.DenseOf(owner.dataHandle) : -1;
            ArrangeRow(pool, pool.DenseOf(control.dataHandle), control, rect, parent);
        }

        // The measure a control's own MeasureCore falls back to: stack or single child.
        internal static Vector2 MeasureOwn(Control control, Vector2 offer)
        {
            DataPool pool = UIEngine.Elements;
            int row = pool.DenseOf(control.dataHandle);
            bool ranged = Current(pool) && pool.Backing<LayoutNode>()[row].count > 0;
            return control is StackPanelControl ? MeasureStack(pool, row, control, offer, ranged)
                 : control is WrapPanelControl ? MeasureWrap(pool, row, control, offer, ranged)
                 : MeasureSingle(pool, row, control, offer, ranged);
        }

        // Arranges a control's children the way its own ArrangeCore falls back to.
        internal static void ArrangeOwn(Control control, LayoutRect rect)
        {
            DataPool pool = UIEngine.Elements;
            int row = pool.DenseOf(control.dataHandle);
            bool ranged = Current(pool) && pool.Backing<LayoutNode>()[row].count > 0;
            if (control is StackPanelControl) ArrangeStack(pool, row, control, rect, ranged);
            else if (control is WrapPanelControl) ArrangeWrap(pool, row, control, rect, ranged);
            else ArrangeSingle(pool, row, control, rect, ranged);
        }

        internal static LayoutRect ClipOf(LayoutRect rect, bool hasParent, LayoutRect parentClip, byte flags)
            => !hasParent ? rect
             : ((ArrangeFlags)flags & ArrangeFlags.Clip) != 0 ? LayoutRect.Intersect(rect, parentClip)
             : parentClip;

        private static bool Same(in LayoutRect a, in LayoutRect b)
            => a.x == b.x && a.y == b.y && a.width == b.width && a.height == b.height;

        private static bool Empty(in LayoutRect r) => r.width <= 0 || r.height <= 0;

        private static Vector2 MeasureRow(DataPool pool, int row, Control? control, Vector2 offer)
        {
            ref ArrangeData a = ref pool.GetSpan<ArrangeData>()[row];
            if (!_noSkip && ((ArrangeFlags)a.flags & ArrangeFlags.MeasureDirty) == 0 && a.measuredOffer == offer)
                return a.desired;

            LayoutNode[] nodes = pool.Backing<LayoutNode>();
            bool ranged = Current(pool) && nodes[row].count > 0;
            LayoutNodeKind kind = ranged ? nodes[row].kind : LayoutNodeKind.Custom;
            Vector2 desired = kind switch
            {
                LayoutNodeKind.Stack => MeasureStack(pool, row, control, offer, true),
                LayoutNodeKind.Wrap => MeasureWrap(pool, row, control, offer, true),
                LayoutNodeKind.Single => MeasureSingle(pool, row, control, offer, true),
                _ => (control ?? (Control)pool.OwnerAt(row)).CallMeasureCore(offer),
            };

            a = ref pool.GetSpan<ArrangeData>()[row];
            a.measuredOffer = offer;
            a.flags = (byte)(((ArrangeFlags)a.flags & ~ArrangeFlags.MeasureDirty) | ArrangeFlags.Remeasured);
            return desired;
        }

        private static void ArrangeRow(DataPool pool, int row, Control? control, LayoutRect rect, int parent)
        {
            Span<ArrangeData> arrange = pool.GetSpan<ArrangeData>();
            ref ArrangeData a = ref arrange[row];
            LayoutRect clipRect = ((ArrangeFlags)a.flags & ArrangeFlags.Rotated) != 0 ? rect.Turned((control ?? (Control)pool.OwnerAt(row)).rotation) : rect;
            LayoutRect clip = ClipOf(clipRect, parent >= 0, parent >= 0 ? arrange[parent].clip : default, a.flags);
            if (!_noSkip && ((ArrangeFlags)a.flags & (ArrangeFlags.ArrangeDirty | ArrangeFlags.Remeasured)) == 0
                && Same(a.arranged, rect) && Same(a.clip, clip))
                return;

            LayoutNode[] nodes = pool.Backing<LayoutNode>();
            bool ranged = Current(pool) && nodes[row].count > 0;
            LayoutNodeKind kind = ranged ? nodes[row].kind : LayoutNodeKind.Custom;
            if (kind == LayoutNodeKind.Custom)
            {
                control ??= (Control)pool.OwnerAt(row);
                control.CallArrangeCore(rect);
            }
            else
            {
                a.arranged = rect;
                a.clip = clip;
                if (kind == LayoutNodeKind.Stack) ArrangeStack(pool, row, control, rect, true);
                else if (kind == LayoutNodeKind.Wrap) ArrangeWrap(pool, row, control, rect, true);
                else ArrangeSingle(pool, row, control, rect, true);
            }

            arrange = pool.GetSpan<ArrangeData>();
            a = ref arrange[row];
            LayoutRect bounds = a.arranged;
            if (((ArrangeFlags)a.flags & ArrangeFlags.Rotated) != 0)
                bounds = bounds.Turned((control ?? (Control)pool.OwnerAt(row)).rotation);
            ChildCursor children = kind == LayoutNodeKind.Custom ? new ChildCursor(control!.children) : new ChildCursor(row, nodes[row].count);
            while (children.Next(nodes, pool, out int child, out _))
                bounds = LayoutRect.Union(bounds, arrange[child].subtreeBounds);

            a.subtreeBounds = bounds;
            a.flags = (byte)((ArrangeFlags)a.flags & ~(ArrangeFlags.ArrangeDirty | ArrangeFlags.Remeasured));
        }

        // The one child a single-child control lays out, if it has exactly one.
        private static bool OnlyChild(DataPool pool, int row, Control? control, bool ranged, out int child, out Control? childControl)
        {
            childControl = null;
            if (ranged)
            {
                LayoutNode[] nodes = pool.Backing<LayoutNode>();
                int count = nodes[row].count;
                child = row + 1;
                return count > 1 && 1 + nodes[child].count == count;
            }

            List<Entity> children = (control ?? (Control)pool.OwnerAt(row)).children;
            if (children.Count == 1 && children[0] is Control only)
            {
                childControl = only;
                child = pool.DenseOf(only.dataHandle);
                return true;
            }
            child = -1;
            return false;
        }

        private static Vector2 MeasureSingle(DataPool pool, int row, Control? control, Vector2 offer, bool ranged)
        {
            ref ArrangeData a = ref pool.GetSpan<ArrangeData>()[row];
            float w = a.preferredWidth > 0 ? a.preferredWidth : MathF.Max(a.minWidth, offer.X);
            float h = a.preferredHeight > 0 ? a.preferredHeight : MathF.Max(a.minHeight, offer.Y);
            if (OnlyChild(pool, row, control, ranged, out int child, out Control? childControl))
            {
                Vector2 childDesired = MeasureRow(pool, child, childControl, new Vector2(
                    MathF.Max(0, w - a.padding.totalHorizontal),
                    MathF.Max(0, h - a.padding.totalVertical)));
                a = ref pool.GetSpan<ArrangeData>()[row];
                if (a.preferredWidth == 0) w = childDesired.X + a.padding.totalHorizontal;
                if (a.preferredHeight == 0) h = childDesired.Y + a.padding.totalVertical;
            }

            Vector2 desired = new Vector2(w, h);
            a.desired = desired;
            return desired;
        }

        private static void ArrangeSingle(DataPool pool, int row, Control? control, LayoutRect rect, bool ranged)
        {
            if (!OnlyChild(pool, row, control, ranged, out int child, out Control? childControl)) return;

            Span<ArrangeData> arrange = pool.GetSpan<ArrangeData>();
            LayoutRect inner = rect.Shrink(arrange[row].padding);
            ref ArrangeData ca = ref arrange[child];
            LayoutRect childRect = inner.Shrink(ca.margin);
            float cx = childRect.x + (childRect.width - ca.desired.X) * ca.horizontalPosition;
            float cy = childRect.y + (childRect.height - ca.desired.Y) * ca.verticalPosition;
            ArrangeRow(pool, child, childControl, new LayoutRect(cx, cy, ca.desired.X, ca.desired.Y), row);
        }

        private static Vector2 MeasureStack(DataPool pool, int row, Control? control, Vector2 offer, bool ranged)
        {
            LayoutNode[] nodes = pool.Backing<LayoutNode>();
            bool vertical = nodes[row].axis == (byte)StackPanelControl.Orientation.Vertical;
            float spacing = nodes[row].spacing;

            Span<ArrangeData> arrange = pool.GetSpan<ArrangeData>();
            ref ArrangeData a = ref arrange[row];

            // A pinned axis is the box the children divide, not the offer that came in.
            float boxWidth = a.preferredWidth > 0 ? a.preferredWidth : offer.X;
            float boxHeight = a.preferredHeight > 0 ? a.preferredHeight : offer.Y;
            LayoutRect inner = new LayoutRect(0, 0, boxWidth, boxHeight).Shrink(a.padding);

            float totalMain = 0f;
            float maxCross = 0f;
            int childCount = 0;
            float totalStarWeight = 0f;

            // Pass 1 — measure non-star children, accumulate star weights.
            ChildCursor children = ChildrenOf(pool, row, control, ranged, nodes);
            while (children.Next(nodes, pool, out int child, out Control? childControl))
            {
                ref ArrangeData ca = ref arrange[child];
                if (((ArrangeFlags)ca.flags & ArrangeFlags.Hidden) != 0) continue;
                childCount++;

                float star = vertical ? ca.heightStar : ca.widthStar;
                if (star > 0f)
                {
                    totalStarWeight += star;
                    continue;
                }

                Vector2 desired = MeasureRow(pool, child, childControl, vertical
                    ? new Vector2(inner.width, float.MaxValue)
                    : new Vector2(float.MaxValue, inner.height));
                arrange = pool.GetSpan<ArrangeData>();

                totalMain += vertical ? desired.Y + ca.margin.totalVertical : desired.X + ca.margin.totalHorizontal;
                maxCross = MathF.Max(maxCross, vertical ? desired.X + ca.margin.totalHorizontal : desired.Y + ca.margin.totalVertical);
            }

            if (childCount > 1)
                totalMain += spacing * (childCount - 1);

            // Pass 2 — if there are star children, distribute the remaining main-axis space.
            if (totalStarWeight > 0f)
            {
                float availMain = vertical ? inner.height : inner.width;
                float remaining = MathF.Max(0, availMain - totalMain);
                float starUnit = remaining / totalStarWeight;

                children = ChildrenOf(pool, row, control, ranged, nodes);
                while (children.Next(nodes, pool, out int child, out Control? childControl))
                {
                    ref ArrangeData ca = ref arrange[child];
                    if (((ArrangeFlags)ca.flags & ArrangeFlags.Hidden) != 0) continue;
                    float star = vertical ? ca.heightStar : ca.widthStar;
                    if (star <= 0f) continue;

                    float starMain = MathF.Max(star * starUnit, vertical ? ca.minHeight : ca.minWidth);
                    Vector2 desired = MeasureRow(pool, child, childControl, vertical
                        ? new Vector2(inner.width, starMain)
                        : new Vector2(starMain, inner.height));
                    arrange = pool.GetSpan<ArrangeData>();

                    maxCross = MathF.Max(maxCross, vertical ? desired.X + ca.margin.totalHorizontal : desired.Y + ca.margin.totalVertical);
                    totalMain += starMain + (vertical ? ca.margin.totalVertical : ca.margin.totalHorizontal);
                }
            }

            a = ref arrange[row];
            float w = vertical ? maxCross + a.padding.totalHorizontal : totalMain + a.padding.totalHorizontal;
            float h = vertical ? totalMain + a.padding.totalVertical : maxCross + a.padding.totalVertical;
            if (a.preferredWidth > 0) w = MathF.Max(w, a.preferredWidth);
            if (a.preferredHeight > 0) h = MathF.Max(h, a.preferredHeight);

            Vector2 result = new Vector2(w, h);
            a.desired = result;
            return result;
        }

        private static void ArrangeStack(DataPool pool, int row, Control? control, LayoutRect rect, bool ranged)
        {
            LayoutNode[] nodes = pool.Backing<LayoutNode>();
            bool vertical = nodes[row].axis == (byte)StackPanelControl.Orientation.Vertical;
            float spacing = nodes[row].spacing;

            Span<ArrangeData> arrange = pool.GetSpan<ArrangeData>();
            LayoutRect inner = rect.Shrink(arrange[row].padding);

            // Recompute star allocation against the real final size.
            float totalFixed = 0f;
            float totalStarWeight = 0f;
            int childCount = 0;

            ChildCursor children = ChildrenOf(pool, row, control, ranged, nodes);
            while (children.Next(nodes, pool, out int child, out _))
            {
                ref ArrangeData ca = ref arrange[child];
                if (((ArrangeFlags)ca.flags & ArrangeFlags.Hidden) != 0) continue;
                childCount++;
                float star = vertical ? ca.heightStar : ca.widthStar;
                if (star > 0f)
                    totalStarWeight += star;
                else
                    totalFixed += vertical
                        ? ca.desired.Y + ca.margin.totalVertical
                        : ca.desired.X + ca.margin.totalHorizontal;
            }

            if (childCount > 1)
                totalFixed += spacing * (childCount - 1);

            float availMain = vertical ? inner.height : inner.width;
            float starPool = totalStarWeight > 0f ? MathF.Max(0, availMain - totalFixed) : 0f;
            float starUnit = totalStarWeight > 0f ? starPool / totalStarWeight : 0f;

            float cursor = vertical ? inner.y : inner.x;
            bool first = true;

            children = ChildrenOf(pool, row, control, ranged, nodes);
            while (children.Next(nodes, pool, out int child, out Control? childControl))
            {
                ref ArrangeData ca = ref arrange[child];
                if (((ArrangeFlags)ca.flags & ArrangeFlags.Hidden) != 0) continue;

                if (!first) cursor += spacing;
                first = false;

                bool isStar = vertical ? ca.heightStar > 0f : ca.widthStar > 0f;
                Thickness margin = ca.margin;
                LayoutRect childRect;

                if (vertical)
                {
                    // Auto (preferredWidth 0) or Stretch fills the cross axis, anything else is
                    // clamped to it. Measure offers a loose size and Arrange tightens, so an
                    // unclamped DesiredSize overflows the panel.
                    float availCrossW = inner.width - margin.totalHorizontal;
                    float childW = ca.preferredWidth == 0 || (HorizontalAlignment)ca.horizontalAlignment == HorizontalAlignment.Stretch
                        ? availCrossW
                        : MathF.Min(ca.desired.X, availCrossW);
                    float childX = (HorizontalAlignment)ca.horizontalAlignment switch
                    {
                        HorizontalAlignment.Left => inner.x + margin.left,
                        HorizontalAlignment.Right => inner.x + margin.left + (availCrossW - childW),
                        HorizontalAlignment.Center => inner.x + margin.left + (availCrossW - childW) * 0.5f,
                        _ => inner.x + margin.left,
                    };

                    // clamped to what is left of the panel: an unsized child measures the whole offer
                    float childH = isStar
                        ? ca.heightStar * starUnit - margin.totalVertical
                        : ca.desired.Y;
                    childH = Math.Clamp(childH, 0, MathF.Max(0, inner.Bottom - cursor - margin.totalVertical));
                    if (isStar) childH = MathF.Max(childH, ca.minHeight);

                    childRect = new LayoutRect(childX, cursor + margin.top, childW, childH);
                    cursor += childH + margin.totalVertical;
                }
                else
                {
                    float availCrossH = inner.height - margin.totalVertical;
                    float childH = ca.preferredHeight == 0 || (VerticalAlignment)ca.verticalAlignment == VerticalAlignment.Stretch
                        ? availCrossH
                        : MathF.Min(ca.desired.Y, availCrossH);
                    float childY = (VerticalAlignment)ca.verticalAlignment switch
                    {
                        VerticalAlignment.Top => inner.y + margin.top,
                        VerticalAlignment.Bottom => inner.y + margin.top + (availCrossH - childH),
                        VerticalAlignment.Center => inner.y + margin.top + (availCrossH - childH) * 0.5f,
                        _ => inner.y + margin.top,
                    };

                    float childW = isStar
                        ? ca.widthStar * starUnit - margin.totalHorizontal
                        : ca.desired.X;
                    childW = Math.Clamp(childW, 0, MathF.Max(0, inner.Right - cursor - margin.totalHorizontal));
                    if (isStar) childW = MathF.Max(childW, ca.minWidth);

                    childRect = new LayoutRect(cursor + margin.left, childY, childW, childH);
                    cursor += childW + margin.totalHorizontal;
                }

                ArrangeRow(pool, child, childControl, childRect, row);
                arrange = pool.GetSpan<ArrangeData>();
            }
        }

        // Lines of children at their desired size, each line as wide as the box allows.
        private static Vector2 MeasureWrap(DataPool pool, int row, Control? control, Vector2 offer, bool ranged)
        {
            LayoutNode[] nodes = pool.Backing<LayoutNode>();
            float spacing = nodes[row].spacing;

            Span<ArrangeData> arrange = pool.GetSpan<ArrangeData>();
            ref ArrangeData a = ref arrange[row];
            float boxWidth = a.preferredWidth > 0 ? a.preferredWidth : offer.X;
            float innerWidth = MathF.Max(0f, boxWidth - a.padding.totalHorizontal);

            float x = 0f;
            float lineHeight = 0f;
            float top = 0f;
            float widest = 0f;
            bool lineEmpty = true;
            ChildCursor children = ChildrenOf(pool, row, control, ranged, nodes);
            while (children.Next(nodes, pool, out int child, out Control? childControl))
            {
                if (((ArrangeFlags)arrange[child].flags & ArrangeFlags.Hidden) != 0) continue;
                Vector2 desired = MeasureRow(pool, child, childControl, new Vector2(MathF.Max(0f, innerWidth - arrange[child].margin.totalHorizontal), float.MaxValue));
                arrange = pool.GetSpan<ArrangeData>();
                Thickness margin = arrange[child].margin;
                float w = desired.X + margin.totalHorizontal;
                float h = desired.Y + margin.totalVertical;

                if (!lineEmpty && x + spacing + w > innerWidth)
                {
                    top += lineHeight + spacing;
                    x = 0f;
                    lineHeight = 0f;
                    lineEmpty = true;
                }
                x += lineEmpty ? w : spacing + w;
                lineEmpty = false;
                lineHeight = MathF.Max(lineHeight, h);
                widest = MathF.Max(widest, x);
            }

            a = ref arrange[row];
            float width = widest + a.padding.totalHorizontal;
            float height = top + lineHeight + a.padding.totalVertical;
            if (a.preferredWidth > 0) width = MathF.Max(width, a.preferredWidth);
            if (a.preferredHeight > 0) height = MathF.Max(height, a.preferredHeight);

            Vector2 result = new Vector2(width, height);
            a.desired = result;
            return result;
        }

        private static void ArrangeWrap(DataPool pool, int row, Control? control, LayoutRect rect, bool ranged)
        {
            LayoutNode[] nodes = pool.Backing<LayoutNode>();
            float spacing = nodes[row].spacing;

            Span<ArrangeData> arrange = pool.GetSpan<ArrangeData>();
            LayoutRect inner = rect.Shrink(arrange[row].padding);

            float x = inner.x;
            float y = inner.y;
            float lineHeight = 0f;
            bool lineEmpty = true;
            ChildCursor children = ChildrenOf(pool, row, control, ranged, nodes);
            while (children.Next(nodes, pool, out int child, out Control? childControl))
            {
                ref ArrangeData ca = ref arrange[child];
                if (((ArrangeFlags)ca.flags & ArrangeFlags.Hidden) != 0) continue;
                Thickness margin = ca.margin;
                float w = MathF.Min(ca.desired.X, MathF.Max(0f, inner.width - margin.totalHorizontal));
                float h = ca.desired.Y;

                if (!lineEmpty && x + spacing + w + margin.totalHorizontal > inner.Right)
                {
                    y += lineHeight + spacing;
                    x = inner.x;
                    lineHeight = 0f;
                    lineEmpty = true;
                }
                if (!lineEmpty) x += spacing;
                lineEmpty = false;

                ArrangeRow(pool, child, childControl, new LayoutRect(x + margin.left, y + margin.top, w, h), row);
                arrange = pool.GetSpan<ArrangeData>();
                x += w + margin.totalHorizontal;
                lineHeight = MathF.Max(lineHeight, h + margin.totalVertical);
            }
        }

        // Sets a row's dirty flags and its parents' until one already has them; safe from several animation chunks at once.
        internal static void MarkDirty(Span<ArrangeData> arrange, ReadOnlySpan<LayoutNode> nodes, int row, LayoutChange change)
        {
            byte test = (byte)(change == LayoutChange.Measure ? ArrangeFlags.MeasureDirty : ArrangeFlags.ArrangeDirty);
            byte bits = (byte)(change == LayoutChange.Measure ? ArrangeFlags.MeasureDirty | ArrangeFlags.ArrangeDirty : ArrangeFlags.ArrangeDirty);
            for (int r = row; r >= 0; r = nodes[r].parent)
            {
                ref byte flags = ref arrange[r].flags;
                byte seen = Volatile.Read(ref flags);
                while (true)
                {
                    if ((seen & test) != 0) return;
                    byte prev = Interlocked.CompareExchange(ref flags, (byte)(seen | bits), seen);
                    if (prev == seen) break;
                    seen = prev;
                }
            }
        }

        // DEBUG: lays the same roots out again without skips, logs the rows that differ, then puts the skipped result back.
        [Conditional("DEBUG")]
        internal static void VerifyLayout(Control[] roots)
        {
            DataPool pool = UIEngine.Elements;
            ArrangeData[] kept = pool.GetSpan<ArrangeData>().ToArray();

            _noSkip = true;
            foreach (Control root in roots)
            {
                if (root.parent is Control || root.destroyed) continue;
                ArrangeData a = kept[pool.DenseOf(root.dataHandle)];
                Measure(root, a.measuredOffer);
                Arrange(root, a.arranged);
            }
            _noSkip = false;

            Span<ArrangeData> now = pool.GetSpan<ArrangeData>();
            int stale = 0;
            for (int i = 0; i < kept.Length; i++)
            {
                ref ArrangeData k = ref kept[i];
                ref ArrangeData n = ref now[i];
                string? field = k.desired != n.desired ? "desired"
                    : !Same(k.arranged, n.arranged) ? "arranged"
                    : !Same(k.clip, n.clip) && !(Empty(k.clip) && Empty(n.clip)) ? "clip"
                    : !Same(k.subtreeBounds, n.subtreeBounds) ? "subtreeBounds"
                    : null;
                if (field == null || stale++ >= 8) continue;
                Control? control = pool.OwnerAt(i) as Control;
                Log.Error($"skipped layout left '{control?.name}' ({control?.GetType().Name}) row {i} {field} stale");
            }
            if (stale > 8) Log.Error($"skipped layout left {stale} rows stale");
            kept.CopyTo(now);
        }
        #endregion

        // Rewrites parent, count and kind for every row a window's tree reaches, once per order change.
        internal static void BuildStructure()
        {
            DataPool pool = UIEngine.Elements;
            ulong order = pool.OrderVersion;
            if (order == _builtOrder) return;
            _builtOrder = order;

            Profiling.Zone.Start("Layout.Structure");
            Span<LayoutNode> nodes = pool.GetSpan<LayoutNode>();
            for (int i = 0; i < nodes.Length; i++)
                nodes[i].count = 0;

            HashSet<Control> walked = new HashSet<Control>();
            foreach (RenderWindow window in Engine.windows.Values)
            {
                WindowRoot? root = window.ui?.uiRoot;
                if (root != null && walked.Add(root))
                    Walk(root, -1, nodes, pool);
            }
            Profiling.Zone.End("Layout.Structure");

            VerifyStructure(walked, nodes, pool);
        }

        private static int Walk(Control control, int parent, Span<LayoutNode> nodes, DataPool pool)
        {
            int row = pool.DenseOf(control.dataHandle);
            int count = 1;
            foreach (Entity e in control.children)
                if (e is Control child)
                    count += Walk(child, row, nodes, pool);

            ref LayoutNode node = ref nodes[row];
            node.parent = parent;
            node.count = count;
            node.kind = KindOf(control);
            return count;
        }

        // Custom when the type lays out through its own MeasureCore/ArrangeCore.
        private static LayoutNodeKind KindOf(Control control)
        {
            Type type = control.GetType();
            if (_kinds.TryGetValue(type, out LayoutNodeKind kind)) return kind;

            kind = Overrides(type, "MeasureCore") || Overrides(type, "ArrangeCore") ? LayoutNodeKind.Custom
                 : control is StackPanelControl ? LayoutNodeKind.Stack
                 : control is WrapPanelControl ? LayoutNodeKind.Wrap
                 : LayoutNodeKind.Single;
            _kinds[type] = kind;
            return kind;
        }

        private static bool Overrides(Type type, string method)
        {
            Type declaring = type.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType!;
            return declaring != typeof(Control) && declaring != typeof(StackPanelControl) && declaring != typeof(WrapPanelControl);
        }

        // DEBUG: every reached row sits at its pre-order position, so its subtree is the rows [row, row + count).
        [Conditional("DEBUG")]
        private static void VerifyStructure(HashSet<Control> roots, Span<LayoutNode> nodes, DataPool pool)
        {
            foreach (Control root in roots)
            {
                int next = pool.DenseOf(root.dataHandle);
                if (!VerifyRange(root, ref next, nodes, pool)) return;
            }
        }

        private static bool VerifyRange(Control control, ref int next, Span<LayoutNode> nodes, DataPool pool)
        {
            int row = pool.DenseOf(control.dataHandle);
            if (row != next)
            {
                Log.Error($"'{control.name}' ({control.GetType().Name}) is row {row}, pre-order puts it at {next}");
                return false;
            }

            next = row + 1;
            foreach (Entity e in control.children)
                if (e is Control child && !VerifyRange(child, ref next, nodes, pool))
                    return false;

            if (next != row + nodes[row].count)
            {
                Log.Error($"'{control.name}' ({control.GetType().Name}) counts {nodes[row].count} rows, its subtree ends at {next}");
                return false;
            }
            return true;
        }
    }
}
