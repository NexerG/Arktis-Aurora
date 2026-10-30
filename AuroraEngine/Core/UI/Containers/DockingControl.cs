using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using System.Numerics;

namespace ArctisAurora.Core.UI.Containers
{
    // Docks children to the edges in declaration order, the fill child taking what is left.
    [A_XSDType("Dock", "UI")]
    public class DockingControl : ContainerControl
    {
        [A_XSDElementProperty("LastChildFill", "UI", "If true, the last child fills remaining space regardless of its DockMode.")]
        public bool lastChildFill = true;

        // the child that took each edge, resolved during Arrange
        public Control? top;
        public Control? bottom;
        public Control? left;
        public Control? right;
        public Control? center;

        protected override Vector2 MeasureCore(Vector2 availableSize)
        {
            float w = preferredWidth > 0 ? preferredWidth : availableSize.X;
            float h = preferredHeight > 0 ? preferredHeight : availableSize.Y;

            LayoutRect remaining = new LayoutRect(0, 0, w, h).Shrink(padding);

            // edge carve-offs, and the fill child's own ask
            float usedLeft = 0f;
            float usedRight = 0f;
            float usedTop = 0f;
            float usedBottom = 0f;
            float fillW = 0f;
            float fillH = 0f;

            for (int i = 0; i < children.Count; i++)
            {
                if (children[i] is not Control child) continue;

                DockMode mode = ResolveDockMode(child, i);
                Vector2 desired = child.Measure(new Vector2(
                    MathF.Max(0, remaining.width),
                    MathF.Max(0, remaining.height)));

                switch (mode)
                {
                    case DockMode.Left:
                        usedLeft += desired.X + child.margin.totalHorizontal;
                        remaining = new LayoutRect(
                            remaining.x + desired.X + child.margin.totalHorizontal,
                            remaining.y,
                            MathF.Max(0, remaining.width - desired.X - child.margin.totalHorizontal),
                            remaining.height);
                        break;

                    case DockMode.Right:
                        usedRight += desired.X + child.margin.totalHorizontal;
                        remaining = new LayoutRect(
                            remaining.x,
                            remaining.y,
                            MathF.Max(0, remaining.width - desired.X - child.margin.totalHorizontal),
                            remaining.height);
                        break;

                    case DockMode.Top:
                        usedTop += desired.Y + child.margin.totalVertical;
                        remaining = new LayoutRect(
                            remaining.x,
                            remaining.y + desired.Y + child.margin.totalVertical,
                            remaining.width,
                            MathF.Max(0, remaining.height - desired.Y - child.margin.totalVertical));
                        break;

                    case DockMode.Bottom:
                        usedBottom += desired.Y + child.margin.totalVertical;
                        remaining = new LayoutRect(
                            remaining.x,
                            remaining.y,
                            remaining.width,
                            MathF.Max(0, remaining.height - desired.Y - child.margin.totalVertical));
                        break;

                    case DockMode.Fill:
                        fillW = desired.X + child.margin.totalHorizontal;
                        fillH = desired.Y + child.margin.totalVertical;
                        break;
                }
            }

            float contentW = usedLeft + usedRight + MathF.Max(fillW, remaining.width) + padding.totalHorizontal;
            float contentH = usedTop + usedBottom + MathF.Max(fillH, remaining.height) + padding.totalVertical;

            arrange.desired = new Vector2(
                preferredWidth > 0 ? MathF.Max(contentW, preferredWidth) : contentW,
                preferredHeight > 0 ? MathF.Max(contentH, preferredHeight) : contentH);
            SetFlag(ArrangeFlags.MeasureDirty, false);
            return arrange.desired;
        }

        protected override void ArrangeCore(LayoutRect finalRect)
        {
            WriteArranged(finalRect);

            top = null;
            bottom = null;
            left = null;
            right = null;
            center = null;

            LayoutRect remaining = finalRect.Shrink(padding);

            for (int i = 0; i < children.Count; i++)
            {
                if (children[i] is not Control child) continue;

                DockMode mode = ResolveDockMode(child, i);
                LayoutRect childRect;

                switch (mode)
                {
                    case DockMode.Left:
                        {
                            float sliceW = child.DesiredSize.X + child.margin.totalHorizontal;
                            childRect = AlignVertically(child,
                                new LayoutRect(remaining.x, remaining.y, sliceW, remaining.height).Shrink(child.margin));

                            remaining = new LayoutRect(
                                remaining.x + sliceW,
                                remaining.y,
                                MathF.Max(0, remaining.width - sliceW),
                                remaining.height);
                            left ??= child;
                            break;
                        }

                    case DockMode.Right:
                        {
                            float sliceW = child.DesiredSize.X + child.margin.totalHorizontal;
                            float sliceX = remaining.x + remaining.width - sliceW;
                            childRect = AlignVertically(child,
                                new LayoutRect(sliceX, remaining.y, sliceW, remaining.height).Shrink(child.margin));

                            remaining = new LayoutRect(
                                remaining.x,
                                remaining.y,
                                MathF.Max(0, remaining.width - sliceW),
                                remaining.height);
                            right ??= child;
                            break;
                        }

                    case DockMode.Top:
                        {
                            float sliceH = child.DesiredSize.Y + child.margin.totalVertical;
                            childRect = AlignHorizontally(child,
                                new LayoutRect(remaining.x, remaining.y, remaining.width, sliceH).Shrink(child.margin));

                            remaining = new LayoutRect(
                                remaining.x,
                                remaining.y + sliceH,
                                remaining.width,
                                MathF.Max(0, remaining.height - sliceH));
                            top ??= child;
                            break;
                        }

                    case DockMode.Bottom:
                        {
                            float sliceH = child.DesiredSize.Y + child.margin.totalVertical;
                            float sliceY = remaining.y + remaining.height - sliceH;
                            childRect = AlignHorizontally(child,
                                new LayoutRect(remaining.x, sliceY, remaining.width, sliceH).Shrink(child.margin));

                            remaining = new LayoutRect(
                                remaining.x,
                                remaining.y,
                                remaining.width,
                                MathF.Max(0, remaining.height - sliceH));
                            bottom ??= child;
                            break;
                        }

                    default:
                        {
                            childRect = remaining.Shrink(child.margin);
                            center ??= child;
                            remaining = LayoutRect.Empty;
                            break;
                        }
                }

                child.Arrange(childRect);
            }

            SetFlag(ArrangeFlags.ArrangeDirty, false);
        }

        private DockMode ResolveDockMode(Control child, int index)
        {
            if (lastChildFill && IsLastControl(index)) return DockMode.Fill;
            if (child.dockMode == DockMode.Unknown) return DockMode.Fill;

            return child.dockMode;
        }

        private bool IsLastControl(int fromIndex)
        {
            for (int i = fromIndex + 1; i < children.Count; i++)
                if (children[i] is Control) return false;

            return true;
        }

        // Vertical alignment inside a left or right slice.
        private static LayoutRect AlignVertically(Control child, LayoutRect slot)
        {
            if (child.verticalAlignment == VerticalAlignment.Stretch) return slot;

            float childH = MathF.Min(child.DesiredSize.Y, slot.height);
            float oy = child.verticalAlignment switch
            {
                VerticalAlignment.Center => (slot.height - childH) * 0.5f,
                VerticalAlignment.Bottom => slot.height - childH,
                _ => 0f
            };
            return new LayoutRect(slot.x, slot.y + oy, slot.width, childH);
        }

        // Horizontal alignment inside a top or bottom slice.
        private static LayoutRect AlignHorizontally(Control child, LayoutRect slot)
        {
            if (child.horizontalAlignment == HorizontalAlignment.Stretch) return slot;

            float childW = MathF.Min(child.DesiredSize.X, slot.width);
            float ox = child.horizontalAlignment switch
            {
                HorizontalAlignment.Center => (slot.width - childW) * 0.5f,
                HorizontalAlignment.Right => slot.width - childW,
                _ => 0f
            };
            return new LayoutRect(slot.x + ox, slot.y, childW, slot.height);
        }
    }
}
