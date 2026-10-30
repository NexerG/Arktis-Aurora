using ArctisAurora.Core.UI.Containers;

namespace ArctisAurora.Core.UI.Controls
{
    // A translucent wash over a region — a drop indicator, the ground under a context menu. Whether
    // it takes the pointer is the caller's business, not the wash's.
    public class HintControl : PanelControl
    {
        public HintControl()
        {
            role = PaletteRole.Accent;
            alpha = 0.35f;
        }
    }
}
