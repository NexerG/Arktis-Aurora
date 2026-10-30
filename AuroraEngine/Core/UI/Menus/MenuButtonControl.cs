using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Controls;
using System.Numerics;

namespace ArctisAurora.Core.UI.Menus
{
    // A menu bar entry: a left press drops the menu it names under it, and the active control stays
    // where it was so the entries act on it.
    [A_XSDType("MenuButton", "UI")]
    public class MenuButtonControl : ButtonControl
    {
        public override bool takesActiveControl => false;

        public override bool OnPointerPress(PointerEvent e)
        {
            base.OnPointerPress(e);
            if (e.button != PointerEvent.leftButton || contextMenu == null || ContextMenus.ClosedOn(this)) return true;

            ContextMenu? menu = ContextMenus.Get(contextMenu);
            if (menu != null)
                ContextMenus.Open(menu.entries, this,
                    new Vector2(arrangedRect.x, arrangedRect.y + arrangedRect.height));
            return true;
        }
    }
}
