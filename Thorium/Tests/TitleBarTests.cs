using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Menus;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;
using System.Numerics;

namespace Thorium.Tests
{
    internal static class TitleBarTests
    {
        [A_XSDActionDependency("TitleBar.ThoriumMenu", "Test")]
        private static IEnumerator<int> ThoriumMenu(TestContext t)
        {
            WindowRoot root = (WindowRoot)Control.ParseXML("main");
            Engine.primary.ui.uiRoot = root;
            yield return 3;

            foreach ((string menu, string entry, string window) in new[] { ("file", "Vaults", "vaults"), ("thorium", "Settings", "settings") })
            {
                MenuButtonControl? button = Find<MenuButtonControl>(root).FirstOrDefault(b => b.contextMenu == menu);
                t.Check(button != null, $"the title bar has the {menu} menu button");
                if (button == null) continue;

                yield return t.Click(button, Centre(button.arrangedRect));
                yield return 40;
                ContextMenuControl.Row? row = Find<ContextMenuControl.Row>(root).FirstOrDefault(r => (r.entry as ContextMenuButton)?.text == entry);
                t.Check(row != null, $"the {menu} menu opens with {entry}");
                if (row == null) continue;

                yield return t.Click(row, Centre(row.arrangedRect));
                yield return 3;
                RenderWindow? opened = Engine.windows.GetValueOrDefault(window);
                t.Check(opened != null && !opened.closeRequested, $"{menu} > {entry} opens the {window} window");
                t.Check(!Find<ContextMenuControl>(root).Any(), $"and closes the {menu} menu");

                if (opened != null) Engine.CloseWindow(opened);
                yield return 3;
            }

            t.Show(new StackPanelControl());
            yield return 2;
        }

        [A_XSDActionDependency("TitleBar.HiddenWindowIgnored", "Test")]
        private static IEnumerator<int> HiddenWindowIgnored(TestContext t)
        {
            WindowRoot root = (WindowRoot)Control.ParseXML("main");
            Engine.primary.ui.uiRoot = root;
            RenderWindow hidden = Engine.OpenMenuWindow("titlebar-hidden", 400, 300);
            hidden.ui.uiRoot = new WindowRoot();
            Background.HideWindow(hidden);
            yield return 3;

            MenuButtonControl? button = Find<MenuButtonControl>(root).FirstOrDefault(b => b.contextMenu == "file");
            if (button != null) yield return t.Click(button, Centre(button.arrangedRect));
            yield return 40;
            ContextMenuControl.Row? row = Find<ContextMenuControl.Row>(root).FirstOrDefault(r => (r.entry as ContextMenuButton)?.text == "Vaults");
            t.Check(row != null, "the file menu opens with Vaults");

            if (row != null)
            {
                yield return t.MoveTo(row);
                hidden.isInWindow = true;
                yield return t.Key(Keys.MouseLeft);
                yield return 3;
            }
            RenderWindow? vaults = Engine.windows.GetValueOrDefault("vaults");
            t.Check(vaults != null && !vaults.closeRequested, "a hidden window still marked under the pointer does not swallow the click");

            if (vaults != null) Engine.CloseWindow(vaults);
            Engine.CloseWindow(hidden);
            t.Show(new StackPanelControl());
            yield return 3;
        }

        private static Vector2 Centre(LayoutRect r) => new Vector2(r.x + r.width * 0.5f, r.y + r.height * 0.5f);

        // Every descendant of a type, depth first.
        private static IEnumerable<T> Find<T>(Control root) where T : Control
        {
            foreach (Entity child in root.children)
            {
                if (child is not Control control) continue;
                if (control is T match) yield return match;
                foreach (T deeper in Find<T>(control)) yield return deeper;
            }
        }
    }
}
