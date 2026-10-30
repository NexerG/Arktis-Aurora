using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using System.Numerics;

namespace ArctisAurora.Core.UI.Containers
{
    [A_XSDType("ScrollDirection", "UI")]
    public enum ScrollDirection
    {
        Vertical, Horizontal, Both
    }

    [A_XSDType("Scrollable", "UI", minChildren: 0, maxChildren: 1)]
    public class ScrollableControl : ContainerControl
    {
        #region properties
        [A_XSDElementProperty("ScrollDirection", "UI", "Which axes can scroll. Default: Vertical.")]
        public ScrollDirection scrollDirection = ScrollDirection.Vertical;

        [A_XSDElementProperty("ScrollSensitivity", "UI", "Pixels per scroll wheel tick.")]
        public float scrollSensitivity = 30f;

        [A_XSDElementProperty("Overscroll", "UI", "Extra scroll past the end, as a fraction of viewport height.")]
        public float overscroll = 0f;

        // asks for its content's height instead of the offer, so it never scrolls vertically itself
        public bool fitContent;

        // thumb palette
        [A_XSDElementProperty("ThumbColorHex", "UI", "Ground of a scroll thumb at rest.")]
        public string? thumbColorHex
        {
            get => field;
            set
            {
                field = value;
                verticalThumb?.PaintOr(value, PaletteRole.Line);
                horizontalThumb?.PaintOr(value, PaletteRole.Line);
            }
        }

        [A_XSDElementProperty("ThumbHoverColorHex", "UI", "Ground of a hovered scroll thumb.")]
        public string? thumbHoverColorHex
        {
            get => field;
            set
            {
                field = value;
                if (verticalThumb != null) verticalThumb.hoverColorHex = value;
                if (horizontalThumb != null) horizontalThumb.hoverColorHex = value;
            }
        }

        [A_XSDElementProperty("ThumbPressColorHex", "UI", "Ground of a held scroll thumb.")]
        public string? thumbPressColorHex
        {
            get => field;
            set
            {
                field = value;
                if (verticalThumb != null) verticalThumb.pressColorHex = value;
                if (horizontalThumb != null) horizontalThumb.pressColorHex = value;
            }
        }
        #endregion

        #region state
        // scroll state, in pixels; positive Y = content scrolled upward
        private Vector2 scrollOffset;
        private Vector2 contentSize;
        private Vector2 viewportSize;

        // scrollbar metrics
        private const float barWidth = 8f;
        private const float barInset = 2f;
        private const float minThumbLength = 24f;

        private ScrollThumbControl verticalThumb;
        private ScrollThumbControl horizontalThumb;

        // Anything at or past this came from a float.MaxValue offer, not from real content.
        private const float unmeasurable = float.MaxValue * 0.5f;
        #endregion

        public bool CanScrollHorizontal => scrollDirection == ScrollDirection.Horizontal || scrollDirection == ScrollDirection.Both;
        public bool CanScrollVertical => scrollDirection == ScrollDirection.Vertical || scrollDirection == ScrollDirection.Both;

        // How far each thumb can slide, in pixels. Zero on an axis whose content fits.
        public Vector2 ThumbTravel { get; private set; }

        // The single content child. The thumbs also live in children and are never it, so this is
        // read off the list rather than cached — a subclass may clear children to swap its content.
        private Control Content
        {
            get
            {
                foreach (Entity e in children)
                    if (e is Control control && control is not ScrollThumbControl) return control;
                return null;
            }
        }

        // How far the content can scroll on each axis, overscroll included. Zero if content fits.
        public Vector2 MaxScrollOffset
        {
            get
            {
                float overflowY = MathF.Max(0, contentSize.Y - viewportSize.Y);
                return new Vector2(
                    MathF.Max(0, contentSize.X - viewportSize.X),
                    overflowY > 0 ? overflowY + viewportSize.Y * overscroll : 0);
            }
        }

        public ScrollableControl()
        {
            clipOutOfBounds = true;
        }

        #region layout
        protected override Vector2 MeasureCore(Vector2 availableSize)
        {
            ref ArrangeData a = ref arrange;
            float w = a.preferredWidth > 0 ? a.preferredWidth : MathF.Max(a.minWidth, availableSize.X);
            float h = a.preferredHeight > 0 ? a.preferredHeight : MathF.Max(a.minHeight, availableSize.Y);

            float innerW = MathF.Max(0, w - a.padding.totalHorizontal);
            float innerH = MathF.Max(0, h - a.padding.totalVertical);

            if (Content is Control child)
            {
                // The viewport, not infinity. A container that sums past it is what turns scrolling
                // on; the viewport's own desired size never grows with it.
                Vector2 desired = child.Measure(new Vector2(innerW, innerH));
                contentSize = new Vector2(Usable(desired.X, innerW), Usable(desired.Y, innerH));
            }
            else contentSize = Vector2.Zero;

            if (fitContent) h = contentSize.Y + a.padding.totalVertical;
            arrange.desired = new Vector2(w, h);
            SetFlag(ArrangeFlags.MeasureDirty, false);
            return arrange.desired;
        }

        // A child that cannot size itself measures the whole offer and reports it back — an unsized
        // child of a stack comes back at float.MaxValue. Falls back to the viewport, so the range is
        // zero rather than nonsense.
        private static float Usable(float desired, float viewport) =>
            float.IsFinite(desired) && desired < unmeasurable ? desired : viewport;

        protected override void ArrangeCore(LayoutRect finalRect)
        {
            WriteArranged(finalRect);

            LayoutRect inner = finalRect.Shrink(arrange.padding);
            viewportSize = new Vector2(inner.width, inner.height);

            ClampScrollOffset();

            if (Content is Control child)
            {
                float childW = CanScrollHorizontal ? MathF.Max(contentSize.X, inner.width) : inner.width;
                float childH = CanScrollVertical ? MathF.Max(contentSize.Y, inner.height) : inner.height;

                child.Arrange(new LayoutRect(inner.x - scrollOffset.X, inner.y - scrollOffset.Y, childW, childH));
            }

            ArrangeThumbs(finalRect, inner);

            SetFlag(ArrangeFlags.ArrangeDirty, false);
        }

        // Each thumb overlays the content's edge and is hidden while its axis fits, which takes it out of
        // the draw and out of the hit-test.
        private void ArrangeThumbs(LayoutRect finalRect, LayoutRect inner)
        {
            EnsureThumbs();

            Vector2 max = MaxScrollOffset;
            float travelX = 0f;
            float travelY = 0f;

            if (CanScrollVertical && max.Y > 0f && inner.height > 0f)
            {
                float length = MathF.Min(inner.height,
                    MathF.Max(minThumbLength, inner.height * inner.height / (inner.height + max.Y)));
                travelY = inner.height - length;

                verticalThumb.Show();
                verticalThumb.Arrange(new LayoutRect(
                    finalRect.Right - barWidth - barInset,
                    inner.y + travelY * scrollOffset.Y / max.Y,
                    barWidth,
                    length));
            }
            else
            {
                verticalThumb.Arrange(LayoutRect.Empty);
                verticalThumb.Hide();
            }

            if (CanScrollHorizontal && max.X > 0f && inner.width > 0f)
            {
                float length = MathF.Min(inner.width,
                    MathF.Max(minThumbLength, inner.width * inner.width / (inner.width + max.X)));
                travelX = inner.width - length;

                horizontalThumb.Show();
                horizontalThumb.Arrange(new LayoutRect(
                    inner.x + travelX * scrollOffset.X / max.X,
                    finalRect.Bottom - barWidth - barInset,
                    length,
                    barWidth));
            }
            else
            {
                horizontalThumb.Arrange(LayoutRect.Empty);
                horizontalThumb.Hide();
            }

            ThumbTravel = new Vector2(travelX, travelY);
        }

        // Appended, not inserted at the head: the hit-test walks last to first and takes the first
        // match, so a thumb has to follow the content to win a press inside it. Rebuilt when one goes
        // missing, because a subclass may clear children to swap its content.
        private void EnsureThumbs()
        {
            bool added = false;

            if (verticalThumb == null || !children.Contains(verticalThumb))
            {
                verticalThumb = NewThumb(true);
                added = true;
            }
            if (horizontalThumb == null || !children.Contains(horizontalThumb))
            {
                horizontalThumb = NewThumb(false);
                added = true;
            }

            if (added) MarkTreeOrderDirty();
        }

        private ScrollThumbControl NewThumb(bool vertical)
        {
            ScrollThumbControl thumb = new ScrollThumbControl(this, vertical)
            {
                parent = this,
                hoverColorHex = thumbHoverColorHex,
                pressColorHex = thumbPressColorHex
            };
            thumb.PaintOr(thumbColorHex, PaletteRole.Line);
            children.Add(thumb);
            return thumb;
        }
        #endregion

        #region scrolling
        // Scrolls by wheel ticks.
        public void OnScrollInput(float deltaX, float deltaY)
        {
            if (CanScrollHorizontal) scrollOffset.X += deltaX * scrollSensitivity;
            if (CanScrollVertical) scrollOffset.Y += deltaY * scrollSensitivity;

            ClampScrollOffset();
            InvalidateArrange();
        }

        public override bool OnPointerScroll(PointerEvent e)
        {
            if (onScroll != null) return onScroll.Invoke(e);

            // A wheel's horizontal axis drives X and its vertical axis Y, never the other. GLFW
            // reports +Y for a wheel pushed away, which moves the content up, so the offset falls.
            if (Moved(-e.delta.X, 0f)) return true;
            if (Moved(0f, -e.delta.Y)) return true;
            return false;
        }

        // Whether the offset actually moved, so an exhausted axis lets the wheel bubble to an outer
        // viewport instead of being swallowed here.
        private bool Moved(float deltaX, float deltaY)
        {
            if (deltaX == 0f && deltaY == 0f) return false;

            Vector2 before = scrollOffset;
            OnScrollInput(deltaX, deltaY);
            return scrollOffset != before;
        }

        public void SetScrollOffset(Vector2 offset)
        {
            scrollOffset = offset;
            ClampScrollOffset();
            InvalidateArrange();
        }

        public Vector2 GetScrollOffset() => scrollOffset;

        // Scrolls a child rect into the viewport — "scroll to selection" in a list or an editor.
        public void ScrollIntoView(LayoutRect targetRect)
        {
            LayoutRect inner = arrangedRect.Shrink(arrange.padding);

            if (CanScrollVertical)
            {
                if (targetRect.y < inner.y) scrollOffset.Y -= inner.y - targetRect.y;
                else if (targetRect.Bottom > inner.Bottom) scrollOffset.Y += targetRect.Bottom - inner.Bottom;
            }

            if (CanScrollHorizontal)
            {
                if (targetRect.x < inner.x) scrollOffset.X -= inner.x - targetRect.x;
                else if (targetRect.Right > inner.Right) scrollOffset.X += targetRect.Right - inner.Right;
            }

            ClampScrollOffset();
            InvalidateArrange();
        }

        private void ClampScrollOffset()
        {
            Vector2 max = MaxScrollOffset;
            scrollOffset.X = MathF.Max(0, MathF.Min(scrollOffset.X, max.X));
            scrollOffset.Y = MathF.Max(0, MathF.Min(scrollOffset.Y, max.Y));
        }
        #endregion

        public override void AddChild(Entity entity)
        {
            if (entity is not Control)
                throw new Exception("Child entity must be a Control");
            if (Content != null)
                throw new Exception("ScrollableControl supports only one child. Wrap multiple children in a container.");

            base.AddChild(entity);
            scrollOffset = Vector2.Zero;
        }
    }
}
