using ArctisAurora.Core.UI.Controls;
using System.Numerics;

namespace ArctisAurora.Core.UI.Containers
{
    // The draggable thumb of a ScrollableControl's scrollbar, one per axis. Created and
    // positioned by the viewport, never authored in XML.
    public class ScrollThumbControl : ButtonControl
    {
        private readonly ScrollableControl viewport;
        private readonly bool vertical;
        private float grab;
        private float grabOffset;

        public ScrollThumbControl(ScrollableControl viewport, bool vertical)
        {
            this.viewport = viewport;
            this.vertical = vertical;
        }

        public override bool OnPointerPress(PointerEvent e)
        {
            grab = vertical ? e.point.Y : e.point.X;
            Vector2 offset = viewport.GetScrollOffset();
            grabOffset = vertical ? offset.Y : offset.X;
            StartDrag();
            return base.OnPointerPress(e);
        }

        // Pointer travel along the track maps onto the scroll range by the ratio between the two.
        public override void OnDrag(PointerEvent e)
        {
            float travel = vertical ? viewport.ThumbTravel.Y : viewport.ThumbTravel.X;
            if (travel > 0f)
            {
                float moved = (vertical ? e.point.Y : e.point.X) - grab;
                Vector2 max = viewport.MaxScrollOffset;
                Vector2 now = viewport.GetScrollOffset();
                float wanted = grabOffset + moved * (vertical ? max.Y : max.X) / travel;

                viewport.SetScrollOffset(vertical
                    ? new Vector2(now.X, wanted)
                    : new Vector2(wanted, now.Y));
            }
            base.OnDrag(e);
        }
    }
}
