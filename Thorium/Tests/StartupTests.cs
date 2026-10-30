using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Containers.Workspace;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;
using System.Numerics;

namespace Thorium.Tests
{
    internal static class StartupTests
    {
        private const string stickyDocument = "sticky-window";

        [A_XSDActionDependency("Startup.FreeStickyKeepsScope", "Test")]
        private static IEnumerator<int> FreeStickyKeepsScope(TestContext t)
        {
            string path = TempNote();
            string before = SessionLayout.scope;
            SessionLayout.scope = "startup-a";
            t.Show(new StackPanelControl());

            RenderWindow? free = TabViewControl.OpenSticky(SessionLayout.tabFactory(path, false), stickyDocument, 100, 100, Startup.freeScope);
            yield return 3;
            t.Check(free != null && free.sessionScope == Startup.freeScope, "a free sticky is tagged with the stickies scope");

            SessionLayout.ChangeScope("startup-b");
            yield return 3;
            t.Check(free != null && !free.closeRequested, "changing vault leaves the free sticky open");

            SessionLayout.Capture();
            SessionScope? recorded = SettingsRegistry.Get<SessionLayout>().scopes.FirstOrDefault(s => s.key == Startup.freeScope);
            t.Check(recorded?.windows.Count == 1 && recorded.windows[0].document == stickyDocument,
                "the free sticky is recorded under the stickies scope");
            SessionScope? vault = SettingsRegistry.Get<SessionLayout>().scopes.FirstOrDefault(s => s.key == "startup-b");
            t.Check(vault != null && vault.windows.All(w => w.document != stickyDocument), "and not under the vault's");

            if (free != null) Engine.CloseWindow(free);
            SessionLayout.scope = before;
            yield return 3;
            File.Delete(path);
        }

        [A_XSDActionDependency("Startup.TrayHidesVault", "Test")]
        private static IEnumerator<int> TrayHidesVault(TestContext t)
        {
            string path = TempNote();
            string other = TempNote();
            t.Show(new StackPanelControl());
            RenderWindow? free = TabViewControl.OpenSticky(SessionLayout.tabFactory(path, false), stickyDocument, 100, 100, Startup.freeScope);
            RenderWindow? vault = TabViewControl.OpenSticky(SessionLayout.tabFactory(other, false), stickyDocument, 420, 100, null);
            yield return 3;

            bool canTray = Background.canTray;
            Startup.tray = true;
            Startup.Place(false);
            Startup.tray = false;
            yield return 2;

            t.Check(Engine.primary.hidden == canTray, "--tray hides the main window when there is a tray");
            t.Check(vault?.hidden == canTray, "and the vault's sticky");
            t.Check(free?.hidden == false, "but shows the free sticky");

            Background.Show();
            yield return 2;
            t.Check(!Engine.primary.hidden && vault?.hidden == false, "opening from the tray shows every hidden window");

            if (free != null) Engine.CloseWindow(free);
            if (vault != null) Engine.CloseWindow(vault);
            yield return 3;
            File.Delete(path);
            File.Delete(other);
        }

        [A_XSDActionDependency("Startup.LoginWithoutStickies", "Test")]
        private static IEnumerator<int> LoginWithoutStickies(TestContext t)
        {
            string path = TempNote();
            t.Show(new StackPanelControl());
            RenderWindow? free = TabViewControl.OpenSticky(SessionLayout.tabFactory(path, false), stickyDocument, 100, 100, Startup.freeScope);
            yield return 3;

            StartupSetting settings = SettingsRegistry.Get<ThoriumSettings>().startup;
            settings.stickies = false;
            Startup.login = true;
            Startup.Place(false);
            Startup.login = false;
            settings.stickies = true;
            yield return 2;

            t.Check(!Engine.primary.hidden, "a login without InTray keeps the main window");
            t.Check(free?.hidden == Background.canTray, "and hides the free stickies when Stickies is off");

            Background.Show();
            if (free != null) Engine.CloseWindow(free);
            yield return 3;
            File.Delete(path);
        }

        [A_XSDActionDependency("Startup.ToggleSwitch", "Test")]
        private static IEnumerator<int> ToggleSwitch(TestContext t)
        {
            bool? picked = null;
            ToggleSwitchControl toggle = new ToggleSwitchControl { onText = "Yes", offText = "Script" };
            toggle.onChanged = on => picked = on;
            StackPanelControl host = new StackPanelControl { padding = new Thickness(12f) };
            host.AddChild(toggle);
            t.Show(host);
            yield return 2;
            LabelControl[] captions = toggle.children.OfType<LabelControl>().ToArray();
            t.Check(captions.Count(c => !c.hidden) == 1 && captions.Single(c => !c.hidden).text == "Script", "off shows only the off caption");
            yield return t.Golden("Off", toggle);

            yield return t.Click(toggle);
            yield return 20;
            t.Check(toggle.on && picked == true, "a click turns the switch on and reports it");
            t.Check(((Control)toggle.children[0].children[0]).alpha > 0.99f, "the accent layer has faded in");            t.Check(captions.Count(c => !c.hidden) == 1 && captions.Single(c => !c.hidden).text == "Yes", "on shows only the on caption");
            yield return t.Golden("On", toggle);

            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Startup.Switch", "Test")]
        private static IEnumerator<int> Switch(TestContext t)
        {
            int? picked = null;
            SwitchControl choice = new SwitchControl { optionList = "Axml, Markdown, Plain text" };
            choice.onChanged = index => picked = index;
            StackPanelControl host = new StackPanelControl { padding = new Thickness(12f) };
            host.AddChild(choice);
            t.Show(host);
            yield return 2;
            t.Check(choice.options.Count == 3 && choice.selected == 0, "Options splits into three captions, the first chosen");
            yield return t.Golden("First", choice);

            LayoutRect last = choice.children.OfType<LabelControl>().Last().arrangedRect;
            yield return t.Click(choice, new Vector2(last.x + last.width * 0.5f, last.y + last.height * 0.5f));
            yield return 20;
            t.Check(choice.selected == 2 && picked == 2, "a click on the last caption picks it and reports its index");
            yield return t.Golden("Last", choice);

            picked = null;
            yield return t.Click(choice, new Vector2(last.x + last.width * 0.5f, last.y + last.height * 0.5f));
            yield return 2;
            t.Check(picked == null, "clicking the chosen option reports nothing");

            SwitchControl rows = new SwitchControl { optionList = "None, Bullets, Numbers, Tasks, Sorted", columns = 3 };
            rows.onChanged = index => picked = index;
            StackPanelControl rowsHost = new StackPanelControl { padding = new Thickness(12f) };
            rowsHost.AddChild(rows);
            t.Show(rowsHost);
            yield return 2;
            PanelControl[] tracks = rows.children.OfType<PanelControl>().Take(2).ToArray();
            t.Check(tracks.Length == 2 && tracks[1].arrangedRect.y > tracks[0].arrangedRect.y
                && tracks[0].arrangedRect.width == tracks[1].arrangedRect.width, "Columns 3 lays five options out as two pills of one width");

            LayoutRect sorted = rows.children.OfType<LabelControl>().Last().arrangedRect;
            yield return t.Click(rows, new Vector2(sorted.x + sorted.width * 0.5f, sorted.y + sorted.height * 0.5f));
            yield return 20;
            t.Check(rows.selected == 4 && picked == 4, "a click on the second row picks its option");
            yield return t.Golden("Rows", rows);

            t.Show(new StackPanelControl());
        }

        private static string TempNote()
        {
            string path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"aurora-startup-{Guid.NewGuid():N}.axml"));
            File.WriteAllText(path, "<Document><Block><Run Text=\"sticky text\"/></Block></Document>\n");
            return path;
        }
    }
}
