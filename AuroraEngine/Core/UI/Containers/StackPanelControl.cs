using ArctisAurora.Core.Registry;

namespace ArctisAurora.Core.UI.Containers
{
    [A_XSDType("StackPanel", "UI")]
    public class StackPanelControl : ContainerControl
    {
        #region enums
        [A_XSDType("Orientation", "UI")]
        public enum Orientation
        {
            Horizontal,
            Vertical
        }
        #endregion

        #region properties
        // settings, mirrored into the row's LayoutNode
        [A_XSDElementProperty("Orientation", "UI", "")]
        public Orientation orientation
        {
            get => field;
            set
            {
                field = value;
                node.axis = (byte)value;
                InvalidateLayout();
            }
        }

        [A_XSDElementProperty("Spacing", "UI", "Space between children in pixels.")]
        public float Spacing
        {
            get => field;
            set
            {
                field = value;
                node.spacing = value;
                InvalidateLayout();
            }
        }
        #endregion

        public StackPanelControl()
        {
            orientation = Orientation.Vertical;
        }
    }
}
