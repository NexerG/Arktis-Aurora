using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Security;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.Core.UI.Windows;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Registry;
using ArctisAurora.EngineWork.Rendering;

namespace Thorium.Editor
{
    // Every vault that has been opened, as one screen. The shell is Vaults.ui.axml; only the rows are
    // built here, because the list is data. Built per open and closed by its own title bar, the same
    // as the settings screen.
    public static class VaultsWindow
    {
        private const string windowName = "vaults";
        private const string document = "vaults";
        private const uint windowWidth = 520;
        private const uint windowHeight = 400;

        // layout
        private const int rowHeight = 44;
        private const int nameHeight = 18;
        private const int pathHeight = 14;
        private const int newWindowWidth = 44;

        private static StackPanelControl _rows = null!;

        // the window a row click opens its vault in
        private static RenderWindow _source = null!;

        // One screen at a time; a second ask raises the one already up and returns no root.
        public static void Open(RenderWindow source)
        {
            WindowRoot root = MenuScreen.Open(windowName, document, windowWidth, windowHeight, source);
            if (root == null) return;

            _source = source;
            _rows = (StackPanelControl)root.FindByName("Rows");
            Fill();
        }

        [A_XSDActionDependency("Vaults.Open", "UI", "Opens the vault browser over the window that asked")]
        public static void Open() => Open(UIActions.Invoking());

        [A_XSDActionDependency("Vaults.Add", "UI", "Picks a folder and opens it as a vault")]
        public static void Add()
        {
            RenderWindow source = Engine.windows.GetValueOrDefault(windowName) ?? Engine.primary;
            FolderPicker.Pick(source, KnownVaults.Resolve(SettingsRegistry.Get<ThoriumSettings>().vault.path), picked =>
            {
                if (picked == null) return;

                SettingsRegistry.Get<KnownVaults>().Remember(picked);
                Choose(picked, false);
            });
        }

        #region ---- rows ----
        // Pruning happens here rather than on a timer — the list is only ever looked at from this
        // screen, and a vault that was moved should not be offered once it is on screen.
        private static void Fill()
        {
            KnownVaults known = SettingsRegistry.Get<KnownVaults>();
            known.Prune();

            foreach (Entity child in _rows.children.ToArray())
                child.Destroy();

            foreach (KnownVault vault in known.vaults)
                _rows.AddChild(Row(vault.path));

            _rows.InvalidateLayout();
        }

        private static Control Row(string path)
        {
            StackPanelControl content = new StackPanelControl
            {
                orientation = StackPanelControl.Orientation.Vertical,
                alpha = 0f,
                hitTestable = false,
                horizontalPosition = 0f
            };

            content.AddChild(new LabelControl
            {
                text = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
                fontSize = 14,
                preferredHeight = nameHeight,
                horizontalPosition = 0f
            });

            content.AddChild(new LabelControl
            {
                text = VaultLock.HasLock(KnownVaults.Resolve(path)) ? path + "  (encrypted)" : path,
                fontSize = 11,
                role = PaletteRole.MutedInk,
                preferredHeight = pathHeight,
                horizontalPosition = 0f
            });

            ButtonControl row = new ButtonControl
            {
                preferredHeight = rowHeight,
                widthStar = 1,
                padding = new Thickness(0, 0, 0, 10),
                role = PaletteRole.Chrome,
                cornerRadius = new CornerRadii(4)
            };
            row.AddChild(content);

            ButtonControl newWindow = new ButtonControl
            {
                preferredWidth = newWindowWidth,
                preferredHeight = rowHeight,
                role = PaletteRole.Chrome,
                cornerRadius = new CornerRadii(4)
            };
            newWindow.AddChild(new IconControl { setName = "default", iconName = "maximize", preferredWidth = 14, preferredHeight = 14, hitTestable = false });

            // Posted, so the tree this click is still bubbling through is not torn down under it.
            row.RegisterOnRelease(_ => { Engine.Post(() => Choose(path, false)); return true; });
            newWindow.RegisterOnRelease(_ => { Engine.Post(() => Choose(path, true)); return true; });

            StackPanelControl line = new StackPanelControl
            {
                orientation = StackPanelControl.Orientation.Horizontal,
                alpha = 0f,
                preferredHeight = rowHeight,
                horizontalAlignment = HorizontalAlignment.Stretch,
                Spacing = 6
            };
            line.AddChild(row);
            line.AddChild(newWindow);
            return line;
        }
        #endregion

        #region ---- opening ----
        // Opens the vault in the window the screen was opened over, or in a window of its own.
        private static void Choose(string path, bool newWindow)
        {
            if (Engine.windows.TryGetValue(windowName, out RenderWindow window)) Engine.CloseWindow(window);

            RenderWindow source = _source != null && !_source.closeRequested && _source.uiDocument == "main" ? _source : Engine.primary;
            if (newWindow) VaultWindows.OpenInNewWindow(path);
            else VaultWindows.OpenHere(source, path);
        }
        #endregion
    }
}
