using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.EngineWork;
using System.Numerics;

namespace ArctisAurora.Core.UI.Containers.Tabs
{
    // The button for one tab in a TabView's strip. It carries the item it stands for, because a
    // drop is resolved by whatever the pointer is over and that control has no other way to learn
    // which tab arrived.
    public class TabStripButtonControl : ButtonControl
    {
        private const float tearThreshold = 12f;

        internal TabItemControl item = null!;
        internal TabViewControl owner = null!;

        // False until the pointer has travelled far enough that this is a drag and not a sloppy
        // click, so a drop is refused.
        internal bool dragging { get; private set; }

        // press state
        private bool armed;
        private Vector2 grab;

        public override bool OnPointerPress(PointerEvent e)
        {
            grab = e.point;
            armed = true;
            dragging = false;
            return base.OnPointerPress(e);
        }

        // The drag is claimed past the threshold rather than on the press, because a live drag
        // swallows the release that activates the tab.
        public override bool OnPointerMove(PointerEvent e)
        {
            if (armed)
            {
                if (!InputHandler.instance.IsKeyDown(Keys.MouseLeft)) armed = false;
                else if ((e.point - grab).Length() >= tearThreshold)
                {
                    armed = false;
                    dragging = true;
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

        // A drag ends without a release, so the activation a click would have made happens here. The
        // tab that moved to another view is no longer ours and SetActive drops it.
        public override void OnDragStop(bool accepted)
        {
            DragGhost.Hide();
            dragging = false;
            owner.SetActive(item);
            base.OnDragStop(accepted);
        }

        #region ---- layout ----
        // caption first, close button second
        public override void AddChild(Entity entity)
        {
            children.Add(entity);
            entity.parent = this;
            MarkTreeOrderDirty();
            InvalidateLayout();
        }

        protected override Vector2 MeasureCore(Vector2 availableSize)
        {
            float w = preferredWidth > 0 ? preferredWidth : availableSize.X;
            float h = preferredHeight > 0 ? preferredHeight : availableSize.Y;
            Vector2 inner = new Vector2(MathF.Max(0, w - padding.totalHorizontal), MathF.Max(0, h - padding.totalVertical));

            Control caption = (Control)children[0];
            Vector2 close = ((Control)children[1]).Measure(inner);
            caption.Measure(new Vector2(MathF.Max(0, inner.X - close.X - caption.margin.totalHorizontal),
                MathF.Max(0, inner.Y - caption.margin.totalVertical)));

            arrange.desired = new Vector2(w, h);
            SetFlag(ArrangeFlags.MeasureDirty, false);
            return arrange.desired;
        }

        protected override void ArrangeCore(LayoutRect finalRect)
        {
            WriteArranged(finalRect);
            LayoutRect inner = finalRect.Shrink(padding);

            Control caption = (Control)children[0];
            Control close = (Control)children[1];
            float closeWidth = close.DesiredSize.X;
            close.Arrange(new LayoutRect(inner.Right - closeWidth, inner.y, closeWidth, inner.height));

            LayoutRect area = new LayoutRect(inner.x, inner.y, MathF.Max(0, inner.width - closeWidth), inner.height).Shrink(caption.margin);
            caption.Arrange(new LayoutRect(
                area.x + (area.width - caption.DesiredSize.X) * caption.horizontalPosition,
                area.y + (area.height - caption.DesiredSize.Y) * caption.verticalPosition,
                caption.DesiredSize.X, caption.DesiredSize.Y));
            SetFlag(ArrangeFlags.ArrangeDirty, false);
        }
        #endregion
    }
}
