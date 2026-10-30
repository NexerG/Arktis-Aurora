using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Menus;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;

namespace ArctisAurora.Core.UI
{
    public class UIActions
    {
        private static readonly Diagnostics.LogChannel Log = Diagnostics.LogChannel.For("UI");

        // The window a bound action was invoked from — the menu's owner when one is open, otherwise
        // wherever focus or the pointer last landed. Zero-argument actions have no other way to tell
        // which window asked, and falling back to the primary is what a keybind with no tree means.
        public static RenderWindow Invoking()
        {
            return UIEngine.WindowOf(ContextMenus.target ?? UIEngine.activeControl ?? UIEngine.hovering)
                ?? Engine.primary;
        }

        [A_XSDActionDependency("TestAction", "UI")]
        public static void TestAction()
        {
            Log.Debug($"TestAction executed");
        }
    }
}