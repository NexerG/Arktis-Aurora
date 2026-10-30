using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Menus;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;
using Silk.NET.GLFW;

namespace ArctisAurora.Core.UI
{
    // Window chrome for a window GLFW created undecorated: an application draws its own title bar
    // out of ordinary controls and its buttons name these.
    public static unsafe class WindowActions
    {
        // These are bound from XML as zero-argument delegates, so the window comes from the control
        // that fired them: the menu's owner when one is open, otherwise whatever the pointer is on.
        private static RenderWindow Acting() =>
            UIEngine.WindowOf(ContextMenus.target ?? UIEngine.hovering);

        [A_XSDActionDependency("Window.Minimize", "UI", "Iconifies the window")]
        public static void Minimize()
        {
            RenderWindow window = Acting();
            if (window == null) return;

            AGlfwWindow._glfw.IconifyWindow(window.os.handle);
        }

        [A_XSDActionDependency("Window.MinimizeToTray", "UI", "Hides the main window to the tray")]
        public static void MinimizeToTray()
        {
            if (Background.canTray) Background.Hide(Acting() ?? Engine.primary);
        }

        [A_XSDActionDependency("Window.MaximizeRestore", "UI","Maximizes the window, or restores it when it already is")]
        public static void MaximizeRestore()
        {
            RenderWindow window = Acting();
            if (window == null) return;

            MaximizeRestore(window);
        }

        internal static void MaximizeRestore(RenderWindow window)
        {
            WindowHandle* handle = window.os.handle;
            if (AGlfwWindow._glfw.GetWindowAttrib(handle, WindowAttributeGetter.Maximized))
                AGlfwWindow._glfw.RestoreWindow(handle);
            else
                AGlfwWindow._glfw.MaximizeWindow(handle);
        }

        [A_XSDActionDependency("Window.TogglePin", "UI", "Keeps the window above every other window, or stops keeping it there")]
        public static void TogglePin()
        {
            RenderWindow window = Acting();
            if (window == null) return;

            Pin(window, !window.pinned);
        }

        // Sets the on-top state and lights the control named "pin" when there is one.
        internal static void Pin(RenderWindow window, bool pinned)
        {
            window.pinned = pinned;
            window.os.SetFloating(pinned);
            if (window.ui.uiRoot?.FindByName("pin") is Control pin)
                pin.role = pinned ? PaletteRole.Accent : PaletteRole.MutedInk;
        }

        // The main window closing is the application closing, so it goes through the shutdown
        // sequence; any other window settles its own notes and goes on its own.
        [A_XSDActionDependency("Window.Close", "UI", "Closes the window, or ends the application when it is the main one")]
        public static void Close()
        {
            RenderWindow window = Acting();
            if (window == null) return;

            Close(window);
        }

        internal static void Close(RenderWindow window)
        {
            if (window.onClose != null)
            {
                window.onClose(window);
                return;
            }

            if (window == Engine.primary)
            {
                Background.Close();
                return;
            }

            NoteActions.SettleWindow(window, () => Engine.CloseWindow(window));
        }
    }
}
