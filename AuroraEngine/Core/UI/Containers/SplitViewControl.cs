using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Containers.Tabs;

namespace ArctisAurora.Core.UI.Containers
{
    // Two panes with a grip between them. A stack panel in every respect — it is its own type so that
    // collapsing a split can never reach authored chrome.
    [A_XSDType("SplitView", "UI")]
    public class SplitViewControl : StackPanelControl
    {
        public enum SplitEdge
        {
            Left, Right, Top, Bottom
        }

        // grip thickness and the floor a dragged pane stops at
        private const float gripThickness = 3f;
        private const float paneMinimum = 120f;

        public SplitViewControl()
        {
            alpha = 0f;
        }

        // Puts a split where the source sits, the source on one side of the grip and a new view on
        // the other.
        public static TabViewControl Split(TabViewControl source, SplitEdge edge)
        {
            if (source.parent is not Control host) return null;

            bool vertical = edge is SplitEdge.Top or SplitEdge.Bottom;
            bool sourceFirst = edge is SplitEdge.Right or SplitEdge.Bottom;
            int slot = host.children.IndexOf(source);

            float main = vertical ? source.arrangedRect.height : source.arrangedRect.width;
            float half = MathF.Max(paneMinimum, (main - gripThickness) * 0.5f);

            SplitViewControl split = new SplitViewControl
            {
                orientation = vertical ? Orientation.Vertical : Orientation.Horizontal,
                widthStar = source.widthStar,
                heightStar = source.heightStar,
                preferredWidth = source.preferredWidth,
                preferredHeight = source.preferredHeight,
                minWidth = source.minWidth,
                minHeight = source.minHeight
            };

            TabViewControl fresh = NewPane(source);
            Control first = sourceFirst ? source : fresh;
            Control second = sourceFirst ? fresh : source;

            // the source leaves the host before the split arrives, so a single-child host is never
            // asked to hold both at once
            first.SetParent(split);
            split.AddChild(NewGrip(vertical, source));
            second.SetParent(split);
            split.SetParent(host);

            SizePane(first, vertical, half);
            SizePane(second, vertical, 0);

            host.children.Remove(split);
            host.children.Insert(slot, split);
            split.MarkTreeOrderDirty();
            host.InvalidateLayout();

            return fresh;
        }

        // A pane with nothing left in it takes the split down with it, and its neighbour moves up
        // into the split's slot.
        public static void Collapse(SplitViewControl split, Control leaving)
        {
            if (split.parent is not Control host) return;

            Control survivor = null;
            foreach (Entity e in split.children)
                if (e is Control pane && pane is not SplitterControl && !ReferenceEquals(pane, leaving))
                {
                    survivor = pane;
                    break;
                }

            int slot = host.children.IndexOf(split);
            leaving.Destroy();

            // the split leaves the host before the survivor arrives, so a single-child host is never
            // asked to hold both at once
            host.RemoveChild(split);

            if (survivor != null)
            {
                survivor.widthStar = split.widthStar;
                survivor.heightStar = split.heightStar;
                survivor.preferredWidth = split.preferredWidth;
                survivor.preferredHeight = split.preferredHeight;
                survivor.minWidth = split.minWidth;
                survivor.minHeight = split.minHeight;

                survivor.SetParent(host);
                host.children.Remove(survivor);
                host.children.Insert(slot, survivor);
            }

            split.MarkTreeOrderDirty();
            split.Destroy();
            host.InvalidateLayout();
        }

        // The new pane is the source's kind and chrome with none of its contents.
        private static TabViewControl NewPane(TabViewControl source)
        {
            TabViewControl pane = source.NewOfSameKind();

            pane.name = source.name;
            pane.tabHeight = source.tabHeight;
            pane.tabWidth = source.tabWidth;
            pane.minTabWidth = source.minTabWidth;
            pane.CopyPaint(source);
            pane.tabColorHex = source.tabColorHex;
            pane.activeTabColorHex = source.activeTabColorHex;
            pane.tabHoverColorHex = source.tabHoverColorHex;
            pane.tabInkColorHex = source.tabInkColorHex;
            pane.gripColorHex = source.gripColorHex;
            pane.gripHoverColorHex = source.gripHoverColorHex;
            pane.gripPressColorHex = source.gripPressColorHex;
            pane.tearOffDocument = source.tearOffDocument;
            pane.stickyDocument = source.stickyDocument;
            pane.tabContextMenu = source.tabContextMenu;
            pane.newButton = source.newButton;
            pane.contextMenu = source.contextMenu;

            return pane;
        }

        // Only the main axis is pinned; the cross axis stretches, and a thickness on it would leave
        // the grip a five pixel square.
        private static SplitterControl NewGrip(bool vertical, TabViewControl source)
        {
            SplitterControl grip = new SplitterControl
            {
                hoverColorHex = source.gripHoverColorHex,
                pressColorHex = source.gripPressColorHex
            };
            grip.PaintOr(source.gripColorHex, PaletteRole.Line);
            if (vertical) grip.preferredHeight = gripThickness;
            else grip.preferredWidth = gripThickness;
            return grip;
        }

        // The pane before the grip carries a size and the one after it carries the star, which is the
        // shape SplitterControl resizes.
        private static void SizePane(Control pane, bool vertical, float fixedMain)
        {
            pane.widthStar = 0f;
            pane.heightStar = 0f;
            pane.preferredWidth = 0;
            pane.preferredHeight = 0;

            if (vertical) pane.minHeight = paneMinimum;
            else pane.minWidth = paneMinimum;

            if (fixedMain > 0)
            {
                if (vertical) pane.preferredHeight = fixedMain;
                else pane.preferredWidth = fixedMain;
            }
            else if (vertical) pane.heightStar = 1f;
            else pane.widthStar = 1f;
        }
    }
}
