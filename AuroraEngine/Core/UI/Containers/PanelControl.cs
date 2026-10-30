using ArctisAurora.Core.Registry;

namespace ArctisAurora.Core.UI.Containers
{
    // A drawn box that holds at most one child.
    [A_XSDType("Panel", "UI")]
    public class PanelControl : Control
    {
        public PanelControl()
        {
            role = PaletteRole.Clear;
        }
    }
}
