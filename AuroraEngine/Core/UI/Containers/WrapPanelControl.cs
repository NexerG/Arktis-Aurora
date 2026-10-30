using ArctisAurora.Core.Registry;

namespace ArctisAurora.Core.UI.Containers
{
    // Children left to right at their desired size, starting a new line when the next one does not fit.
    [A_XSDType("WrapPanel", "UI")]
    public class WrapPanelControl : ContainerControl
    {
        [A_XSDElementProperty("Spacing", "UI", "Space between children and between lines in pixels.")]
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
    }
}
