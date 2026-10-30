using ArctisAurora.Core.Security;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Containers.Workspace;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;

namespace ArctisAurora.Core.UI.Notes
{
    // An encrypted file's tab before it is unlocked: a password field that swaps in the file's real editor.
    public class LockedFileControl : PanelControl, IFileEditor
    {
        public string? path { get; private set; }
        public bool isDirty => false;

        private readonly bool asXml;
        private readonly LabelControl fileLabel;
        private readonly LabelControl error;
        private readonly TextBoxControl field;
        private readonly CheckBoxControl remember;
        private SessionTab? pendingView;
        private bool deriving;

        public LockedFileControl(string path, bool asXml)
        {
            this.path = path;
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

            column.AddChild(new LabelControl { text = "[ENCRYPTED]", fontSize = 20, preferredHeight = 26, horizontalPosition = 0f });
            fileLabel = new LabelControl { text = Path.GetFileName(path), fontSize = 13, preferredHeight = 18, horizontalPosition = 0f, role = PaletteRole.MutedInk };
            column.AddChild(fileLabel);

            field = new TextBoxControl
            {
                masked = true,
                preferredHeight = 30,
                horizontalAlignment = HorizontalAlignment.Stretch,
                role = PaletteRole.SubField,
                fontSize = 15,
                padding = new Thickness(0, 6, 0, 8)
            };
            field.onCommit = _ => Unlock();
            column.AddChild(field);

            StackPanelControl rememberRow = new StackPanelControl
            {
                orientation = StackPanelControl.Orientation.Horizontal,
                alpha = 0f,
                preferredHeight = 20,
                Spacing = 8
            };
            remember = new CheckBoxControl();
            rememberRow.AddChild(remember);
            rememberRow.AddChild(new LabelControl { text = "Remember password for this session", fontSize = 13, preferredHeight = 18 });
            column.AddChild(rememberRow);

            error = new LabelControl { text = "Wrong password", fontSize = 13, preferredHeight = 18, horizontalPosition = 0f, role = PaletteRole.Danger };
            error.Hide();
            column.AddChild(error);

            ButtonControl unlock = new ButtonControl
            {
                preferredHeight = 26,
                padding = new Thickness(12f, 0f),
                role = PaletteRole.Chrome,
                cornerRole = CornerRole.Control,
                horizontalAlignment = HorizontalAlignment.Left
            };
            unlock.AddChild(new LabelControl { text = "Unlock", fontSize = 14, role = PaletteRole.MutedInk });
            unlock.RegisterOnRelease(_ => { Unlock(); return true; });
            column.AddChild(unlock);

            AddChild(column);
        }

        // Derives the key off the main thread, then opens the file with it or reports a wrong password.
        public void Unlock()
        {
            string password = field.text;
            if (deriving || password.Length == 0 || path == null) return;

            deriving = true;
            string target = path;
            bool keep = remember.isChecked;
            Task.Run(() => EncryptedFile.Unlock(target, password)).ContinueWith(task =>
            {
                PasswordKey? key = task.IsCompletedSuccessfully ? task.Result : null;
                Engine.Post(() => Unlocked(target, key, keep));
            });
        }

        private void Unlocked(string target, PasswordKey? key, bool keep)
        {
            deriving = false;
            if (destroyed || target != path)
            {
                key?.Dispose();
                return;
            }
            if (key == null)
            {
                error.Show();
                field.text = string.Empty;
                return;
            }

            field.text = string.Empty;
            FileKeys.Use(target, key, keep);
            Open();
        }

        // Moves a freshly built editor for the file into this tab in place of the lock.
        private void Open()
        {
            if (parent is not TabItemControl tab || path == null || SessionLayout.tabFactory?.Invoke(path, asXml) is not TabItemControl fresh) return;
            if (fresh.children.Count == 0 || fresh.children[0] is not Control editor || editor is LockedFileControl)
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
