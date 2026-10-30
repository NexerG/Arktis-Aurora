using ArctisAurora.Core.Security;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Containers.Workspace;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;

namespace Thorium.Editor.CustomControls
{
    // A tab whose file is in a vault no window has open, or one still locked; swaps in the real editor when it opens.
    public class ClosedVaultControl : PanelControl, IFileEditor
    {
        public string? path { get; private set; }
        public bool isDirty => false;

        private readonly string vault;
        private readonly bool asXml;
        private readonly LabelControl fileLabel;
        private SessionTab? pendingView;

        public ClosedVaultControl(string path, string vault, bool asXml)
        {
            this.path = path;
            this.vault = vault;
            this.asXml = asXml;
            role = PaletteRole.Ground;
            horizontalAlignment = HorizontalAlignment.Stretch;
            verticalAlignment = VerticalAlignment.Stretch;

            StackPanelControl column = new StackPanelControl
            {
                orientation = StackPanelControl.Orientation.Vertical,
                alpha = 0f,
                preferredWidth = 320,
                horizontalAlignment = HorizontalAlignment.Center,
                verticalAlignment = VerticalAlignment.Center,
                Spacing = 10
            };

            string state = VaultLock.HasLock(vault) && !VaultLock.IsOpen(vault) ? "locked" : "closed";
            column.AddChild(new LabelControl { text = $"{VaultWindows.NameOf(vault)} is {state}", fontSize = 20, preferredHeight = 26, horizontalPosition = 0f });
            fileLabel = new LabelControl { text = Path.GetFileName(path), fontSize = 13, preferredHeight = 18, horizontalPosition = 0f, role = PaletteRole.MutedInk };
            column.AddChild(fileLabel);

            ButtonControl open = new ButtonControl
            {
                preferredHeight = 26,
                padding = new Thickness(12f, 0f),
                role = PaletteRole.Chrome,
                cornerRole = CornerRole.Control,
                horizontalAlignment = HorizontalAlignment.Left
            };
            open.AddChild(new LabelControl { text = "Open vault", fontSize = 14, role = PaletteRole.MutedInk });
            open.RegisterOnRelease(_ => { Engine.Post(() => VaultWindows.OpenSomewhere(this.vault)); return true; });
            column.AddChild(open);

            AddChild(column);
        }

        // Moves a freshly built editor for the file into this tab, unless its vault is still closed.
        internal void Reopen()
        {
            if (parent is not TabItemControl tab || path == null || SessionLayout.tabFactory?.Invoke(path, asXml) is not TabItemControl fresh) return;
            if (fresh.children.Count == 0 || fresh.children[0] is not Control editor || editor is ClosedVaultControl)
            {
                fresh.Destroy();
                return;
            }

            fresh.RemoveChild(editor);
            tab.onRename = fresh.onRename;
            string header = fresh.header;
            fresh.Destroy();

            tab.RemoveChild(this);
            tab.AddChild(editor);
            if (pendingView != null) (editor as IFileEditor)?.RestoreView(pendingView);
            (tab.parent as TabViewControl)?.Retitle(tab, header);
            Destroy();
        }

        public void Save() { }

        public void Repath(string newPath, string name)
        {
            path = newPath;
            fileLabel.text = Path.GetFileName(newPath);
        }

        public SessionTab ViewState() => pendingView ?? new SessionTab { asXml = asXml };

        public void RestoreView(SessionTab view) => pendingView = view;
    }
}
