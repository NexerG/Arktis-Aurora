using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Menus;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;
using Thorium.Editor.CustomControls;
using static Thorium.StartupSetting;

namespace Thorium
{
    // What a launch at login or with --tray opens, and the stickies that belong to no vault.
    internal static class Startup
    {
        private const string loginFlag = "--login";
        private const string trayFlag = "--tray";

        // free stickies
        internal const string freeScope = "stickies";
        private const string stickyDocument = "sticky-window";
        private const uint stickySize = 300;

        internal static bool login;
        internal static bool tray;

        internal static string StickiesFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Arktis", "Thorium", "Stickies");

        private static StartupSetting Settings() => SettingsRegistry.Get<ThoriumSettings>().startup;

        [A_XSDActionDependency("Startup.Apply", "Settings", "Adds Thorium to the programs Windows starts at sign-in, or removes it")]
        public static void Apply() => Background.SetLoginCommand(Settings().atLogin ? loginFlag : null);

        // Reads the launch flags and tells the engine whether the main window starts hidden.
        internal static void Read(string[] args)
        {
            login = args.Contains(loginFlag);
            tray = args.Contains(trayFlag);
            Background.startHidden = () => tray || (login && Settings().inTray);
            Background.hidePrimary = () => Settings().opens == StartupView.VaultBrowser;
            Background.trayEntries.Add(new ContextMenuButton("New sticky", NewSticky));
        }

        // Hides what this launch keeps in the tray; a locked vault shows its prompt.
        internal static void Place(bool locked)
        {
            if (!login && !tray) return;

            StartupSetting settings = Settings();
            if (locked && Engine.primary.hidden) Background.Show();
            if (!Background.canTray) return;

            bool hideVault = !locked && (tray || settings.inTray);
            bool hideFree = !settings.stickies;

            foreach (RenderWindow window in Engine.windows.Values.ToList())
            {
                if (window.closeRequested || string.IsNullOrEmpty(window.uiDocument) || window.hidden) continue;
                if (window.sessionScope == freeScope ? hideFree : hideVault) Background.HideWindow(window);
            }
        }

        // A new empty note in the stickies folder, in a sticky of its own in the middle of the screen.
        internal static void NewSticky()
        {
            Directory.CreateDirectory(StickiesFolder);
            string path = Path.Combine(StickiesFolder, $"Sticky {DateTime.Now:yyyy-MM-dd HHmmss}{SettingsRegistry.Get<ThoriumSettings>().newNotes.Extension}");

            NoteBlock block = new NoteBlock();
            block.AppendRun(new Run());
            new RichTextDocument { name = Path.GetFileNameWithoutExtension(path), blocks = [block] }.Save(path);

            int size = (int)UIScaling.ToPixels(Engine.primary, stickySize);
            (int x, int y) = Engine.CenterOnPrimary(size, size);
            TabViewControl.OpenSticky(VaultBrowserControl.BuildTab(path, false), stickyDocument, x, y, freeScope);
        }
    }
}
