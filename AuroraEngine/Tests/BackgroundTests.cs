using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Menus;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.Core.UI.Windows;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;
using Silk.NET.GLFW;

namespace ArctisAurora.Tests
{
    internal static unsafe class BackgroundTests
    {
        [A_XSDActionDependency("Background.HideShow", "Test")]
        private static IEnumerator<int> HideShow(TestContext t)
        {
            t.Show(new StackPanelControl());
            yield return 2;

            Background.Hide();
            yield return 10;
            t.Check(Engine.primary.hidden && !Visible(Engine.primary), "hiding takes the main window off screen");

            Background.Show();
            yield return 2;
            t.Check(!Engine.primary.hidden && Visible(Engine.primary), "showing brings it back");
        }

        [A_XSDActionDependency("Background.MenuClamp", "Test")]
        private static IEnumerator<int> MenuClamp(TestContext t)
        {
            t.Show(new StackPanelControl());
            yield return 2;

            (int wx, int wy, _, _) = Bounds(Engine.primary);
            (int ax, int ay, int aw, int ah) = AGlfwWindow.WorkAreaAt(wx, wy);
            List<ContextMenuEntry> entries = new List<ContextMenuEntry>
            {
                new ContextMenuButton("First", () => { }),
                new ContextMenuButton("Second", () => { })
            };
            ContextMenus.OpenAtScreen(entries, ax + aw - 1, ay + ah - 1, Engine.primary);
            yield return 20;

            RenderWindow? menu = Engine.windows.Values.FirstOrDefault(w => !w.closeRequested && w.ui?.uiRoot?.children.FirstOrDefault() is ContextMenuControl);
            t.Check(menu != null, "the menu opens in a window of its own");
            if (menu == null) yield break;

            (int mx, int my, int mw, int mh) = Bounds(menu);
            t.Check(mx >= ax && my >= ay && mx + mw <= ax + aw && my + mh <= ay + ah,
                $"a menu opened at the work area's corner stays inside it: {mx},{my} {mw}x{mh} in {ax},{ay} {aw}x{ah}");

            ContextMenus.Close();
            yield return 2;
        }

        [A_XSDActionDependency("Background.ClosePrompt", "Test")]
        private static IEnumerator<int> ClosePrompt(TestContext t)
        {
            t.Show(new StackPanelControl());
            yield return 2;

            TraySetting tray = SettingsRegistry.Get<GraphicsSettings>().tray;
            bool trayWas = tray.enabled;
            tray.enabled = true;
            WindowSetting setting = SettingsRegistry.Get<GraphicsSettings>().window;
            WindowSetting.CloseAction was = setting.onClose;
            setting.onClose = WindowSetting.CloseAction.Ask;

            WindowActions.Close(Engine.primary);
            yield return 4;
            t.Check(ConfirmWindow.isOpen, "closing the main window asks");

            ButtonControl? minimize = ConfirmButton("Minimize");
            t.Check(minimize != null && ConfirmButton("Cancel") != null && ConfirmButton("Close") != null,
                "the prompt offers Cancel, Minimize and Close");

            if (!Background.canTray)
            {
                t.Check(minimize != null && !minimize.enabled, "without a tray, Minimize is greyed out");
                if (minimize != null) yield return t.Click(minimize);
                yield return 4;
                t.Check(ConfirmWindow.isOpen && !Engine.primary.hidden && Visible(Engine.primary), "a greyed Minimize ignores the click");

                ButtonControl? cancel = ConfirmButton("Cancel");
                if (cancel != null) yield return t.Click(cancel);
                yield return 4;
                t.Check(!ConfirmWindow.isOpen, "Cancel closes the prompt");

                setting.onClose = was;
                tray.enabled = trayWas;
                yield break;
            }

            if (minimize != null) yield return t.Click(minimize);
            yield return 10;

            t.Check(!ConfirmWindow.isOpen && Engine.primary.hidden && !Visible(Engine.primary), "Minimize hides the main window");
            t.Check(setting.onClose == WindowSetting.CloseAction.Ask, "an unticked check leaves the setting asking");

            setting.onClose = was;
            tray.enabled = trayWas;
            Background.Show();
            yield return 2;
        }

        private static ButtonControl? ConfirmButton(string caption)
        {
            if (!Engine.windows.TryGetValue("confirm", out RenderWindow? window) || window.ui?.uiRoot == null) return null;

            Stack<Entity> open = new Stack<Entity>();
            open.Push(window.ui.uiRoot);
            while (open.Count > 0)
            {
                Entity entity = open.Pop();
                if (entity is ButtonControl button && button.children.OfType<LabelControl>().Any(l => l.text == caption)) return button;
                foreach (Entity child in entity.children) open.Push(child);
            }
            return null;
        }

        private static bool Visible(RenderWindow window) =>
            AGlfwWindow._glfw.GetWindowAttrib(window.os.handle, WindowAttributeGetter.Visible);

        private static (int x, int y, int width, int height) Bounds(RenderWindow window)
        {
            AGlfwWindow._glfw.GetWindowPos(window.os.handle, out int x, out int y);
            AGlfwWindow._glfw.GetWindowSize(window.os.handle, out int width, out int height);
            return (x, y, width, height);
        }
    }
}
