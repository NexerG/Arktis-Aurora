using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Containers.Workspace;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Sheets;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;
using Thorium.Editor;
using Thorium.Editor.CustomControls;

namespace Thorium
{
    internal class Thorium
    {
        static void Main(string[] args)
        {
            Engine engine = new Engine();
            XSDGenerator.GenerateXSD();

            SettingsRegistry.SetWriteRoot(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Arktis", "Thorium", "Settings"));

            string? file = args.Length > 0 && !args[0].StartsWith('-') && File.Exists(args[0]) ? Path.GetFullPath(args[0]) : null;
            Background.standalone = file != null;
            if (file == null) Startup.Read(args);
            FileAssociations.enabled = true;

            engine.Init(false);
            InputHandler.SetActiveKeybindGroup("InputMap");

            // The vault booted into is one that has been opened, so the browser lists it.
            KnownVaults known = SettingsRegistry.Get<KnownVaults>();
            known.Prune();
            known.Remember(SettingsRegistry.Get<ThoriumSettings>().vault.path);

            // One layout for every editor window; stickies apart.
            SessionLayout.scope = VaultWindows.workspaceScope;
            SessionLayout.freeScope = Startup.freeScope;
            bool migrated = VaultWindows.Migrate();
            SessionLayout.tabFactory = VaultBrowserControl.BuildTab;
            NotePropertiesControl.openLink = VaultBrowserControl.OpenLink;
            SheetBook.findSheet = VaultBrowserControl.FindSheet;
            SheetBook.vaultSheets = VaultBrowserControl.VaultSheets;
            SheetBook.vaultNotes = VaultBrowserControl.VaultNotes;
            SheetBook.vaultMissing = VaultBrowserControl.VaultMissing;
            SheetBook.vaultPart = VaultBrowserControl.VaultPart;
            SheetBook.referencingVaults = VaultBrowserControl.ReferencingVaults;
            SheetBook.filePrefix = VaultBrowserControl.FilePrefix;
            LinkIndex.findNote = VaultBrowserControl.FindNote;
            LinkIndex.cacheFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arktis", "Thorium", "Links");
            LinkIndex.indexOf = VaultBrowserControl.IndexOf;
            LinkIndex.openFile = VaultBrowserControl.Open;
            TabViewControl.newPressed += VaultBrowserControl.NewInPane;
            // prepare level

            // One-shot atlas bake — this is the set currently in Data/Fonts/arial.
            //AssetImporter.ImportFont(
            //    " !\"#$%&'()*+,-./0123456789:;<=>?@" +
            //    "ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`" +
            //    "abcdefghijklmnopqrstuvwxyz{|}~" +
            //    "ĄČĘĖĮŠŲŪŽąčęėįšųūž",
            //    "arial.ttf");

            if (file != null)
            {
                OpenStandalone(file);
                engine.Run();
                return;
            }

            Engine.primary.uiDocument = "main";
            Engine.primary.ui.uiRoot = (WindowRoot)Control.ParseXML("main");
            Background.open = VaultWindows.TrayOpen;
            bool picker = SettingsRegistry.Get<ThoriumSettings>().startup.opens == StartupSetting.StartupView.VaultBrowser && Background.primaryStartsHidden;
            if (picker) VaultWindows.StartEmpty();
            else VaultWindows.Restore(migrated);
            SessionLayout.RestoreFree();
            bool locked = VaultWindows.AnyLocked();
            Startup.Place(locked);
            if (locked) VaultWindows.UnlockAtLaunch();

            engine.Run();
        }

        // Shows one file in a bare tab window, leaving the vault's layout unrecorded.
        internal static void OpenStandalone(string path)
        {
            SessionLayout.paused = true;
            Engine.primary.uiDocument = "tab-window";
            WindowRoot root = (WindowRoot)Control.ParseXML("tab-window");
            Engine.primary.ui.uiRoot = root;
            WorkspaceControl.In(root)?.LoadDefault();

            if (root.FindByName("Tabs") is not TabViewControl tabs) return;
            TabItemControl tab = VaultBrowserControl.BuildTab(path, false);
            tabs.AddChild(tab);
            tabs.SetActive(tab);
        }
    }
}