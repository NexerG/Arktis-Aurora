using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Menus;

namespace ArctisAurora.Core.UI
{
    // Splitting a view from the keyboard. The drag-to-edge path lives in TabViewControl; this is the
    // same operation under a name XML can bind.
    public static class ViewActions
    {
        // The new stack's view: the first above the menu's target, the active control or the hovered
        // one, in that order.
        private static TabViewControl? Acting() =>
            ViewAbove(ContextMenus.target) ?? ViewAbove(UIEngine.activeControl) ?? ViewAbove(UIEngine.hovering);

        private static TabViewControl? ViewAbove(Control? control)
        {
            for (Control? c = control; c != null; c = c.parent as Control)
                if (c is TabViewControl view) return view;

            return null;
        }

        [A_XSDActionDependency("View.SplitRight", "Any", "Moves the focused view's active tab into a new pane beside it")]
        public static void SplitRight() => Split(SplitViewControl.SplitEdge.Right);

        [A_XSDActionDependency("View.SplitDown", "Any", "Moves the focused view's active tab into a new pane below it")]
        public static void SplitDown() => Split(SplitViewControl.SplitEdge.Bottom);

        // The view's own menu and the keybinds have no tab under the pointer, so they act on the
        // active one; a right click on a tab goes through TabActions instead.
        private static void Split(SplitViewControl.SplitEdge nextEdge)
        {
            TabViewControl? next = Acting();
            next?.SplitOff(next.activeItem, nextEdge);
        }
    }
}
