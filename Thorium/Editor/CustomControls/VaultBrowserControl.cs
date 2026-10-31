using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Security;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Containers.Workspace;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Files;
using ArctisAurora.Core.UI.Graph;
using ArctisAurora.Core.UI.Menus;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Planner;
using ArctisAurora.Core.UI.Sheets;
using ArctisAurora.Core.UI.Tex;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.Core.UI.Windows;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;
using Microsoft.VisualBasic.FileIO;
using System.Numerics;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace Thorium.Editor.CustomControls
{
    // Lists the vault as an openable tree and opens the clicked note in the document editor.
    [A_XSDType("VaultBrowser", "UI")]
    public class VaultBrowserControl : FileTreeControl
    {
        private static readonly LogChannel Log = LogChannel.For("Vault");

        // control names in UI.ui.axml
        private const string filterName = "VaultFilter";

        // context declared in Contexts/Thorium.contexts.axml
        private const string tabsContext = "ActiveTabViewer";

        // menu documents registered in ThoriumAssets.assets.axml
        private const string vaultMenu = "vault";
        private const string folderMenu = "vault-folder";
        private const string noteMenu = "vault-note";
        private const string csvMenu = "vault-csv";
        private const string noteXmlMenu = "vault-note-xml";
        private const string asNoteMenu = "vault-as-note";
        private const string asSheetMenu = "vault-as-sheet";
        private const string asPlannerMenu = "vault-as-planner";

        // the shown workspace's kind, which picks the files listed
        private WorkspaceKind filtered;

        // absolute folder of the vault this browser lists
        public string vault = "";

        // the pane whose + was pressed, which the next file made opens in
        private static TabViewControl? pendingPane;
        private const string paneNewMenu = "pane-new";

        // a row's "Add to sticky note" submenu and the window a sticky opens as
        private const string stickySource = "vault-stickies";
        private const string stickyDocument = "sticky-window";

        static VaultBrowserControl() => ContextMenus.RegisterSource(stickySource, StickyEntries);

        public VaultBrowserControl()
        {
            contextMenu = vaultMenu;
            Rebuild();
            Context.changed += OnContextChanged;
            TabViewControl.activeChanged += OnActiveTabChanged;
            WorkspaceControl.changed += OnWorkspaceChanged;
        }

        public override void OnDestroy()
        {
            Context.changed -= OnContextChanged;
            TabViewControl.activeChanged -= OnActiveTabChanged;
            WorkspaceControl.changed -= OnWorkspaceChanged;
            base.OnDestroy();
        }

        #region ---- workspace filter ----
        private void OnWorkspaceChanged(WorkspaceControl workspace)
        {            Control? root = UIEngine.WindowOf(this)?.ui?.uiRoot;
            if (root == null || !ReferenceEquals(workspace, WorkspaceControl.In(root))) return;

            Control top = this;
            while (top.parent is Control up) top = up;
            if (!ReferenceEquals(top, root)) return;

            WorkspaceKind kind = workspace.shown?.kind ?? WorkspaceKind.General;
            if (kind == filtered) return;

            filtered = kind;
            if (root.FindByName(filterName) is LabelControl label) label.text = FilterName(kind);
            Rebuild();
        }

        private static bool Shows(WorkspaceKind kind, string path)
        {
            bool sheet = SheetDocument.IsSheet(path) || SheetCsv.IsCsv(path);
            bool planner = PlannerDocument.IsPlanner(path);
            bool tex = FileTypes.KindOf(path) == FileKind.Tex;
            return kind switch
            {
                WorkspaceKind.Docs => !sheet && !planner && !tex,
                WorkspaceKind.Sheets => sheet,
                WorkspaceKind.LaTeX => tex,
                WorkspaceKind.Manager or WorkspaceKind.Calendar => planner,
                WorkspaceKind.Xml => IsXml(path),
                _ => true
            };
        }

        private static bool IsXml(string path) => IsAuroraXml(path) || FileTypes.KindOf(path) == FileKind.Xml;

        private static bool IsAuroraXml(string path) => FileTypes.KindOf(path) is FileKind.Note or FileKind.Sheet or FileKind.Planner or FileKind.Graph;

        private static string FilterName(WorkspaceKind kind) => kind switch
        {
            WorkspaceKind.Docs => "Notes",
            WorkspaceKind.Sheets => "Sheets",
            WorkspaceKind.LaTeX => "LaTeX",
            WorkspaceKind.Manager or WorkspaceKind.Calendar => "Plans",
            WorkspaceKind.Xml => "XML",
            _ => "All files"
        };
        #endregion

        #region ---- current note ----
        private void OnContextChanged(string name, object? value)
        {
            if (name == tabsContext) FollowFocusedTab();
        }

        private void OnActiveTabChanged(TabViewControl view)
        {
            RenderWindow? window = UIEngine.WindowOf(this);
            TabViewControl focused = window == null ? null : FocusedTabs(window);
            if (focused == null ? window != null && UIEngine.WindowOf(view) == window : ReferenceEquals(view, focused))
                SetCurrent(view.activeItem?.name);
        }

        private void FollowFocusedTab() =>
            SetCurrent(UIEngine.WindowOf(this) is RenderWindow window ? FocusedTabs(window)?.activeItem?.name : null);
        #endregion

        protected override string RootPath => vault;

        protected override bool AcceptsFolder(FileObject folder) => !folder.name.StartsWith('.');

        // A locked vault lists nothing but the way in.
        protected override void PopulateRows()
        {
            if (!VaultLock.HasLock(RootPath) || VaultLock.IsOpen(RootPath))
            {
                base.PopulateRows();
                return;
            }

            ButtonControl unlock = new ButtonControl
            {
                preferredHeight = 26,
                padding = new Thickness(12f, 0f),
                role = PaletteRole.Chrome,
                cornerRole = CornerRole.Control,
                horizontalAlignment = HorizontalAlignment.Left
            };
            unlock.AddChild(new LabelControl { text = "Vault locked - Unlock", fontSize = 14, role = PaletteRole.MutedInk });
            unlock.RegisterOnRelease(_ => { Engine.Post(() => UnlockVault(vault)); return true; });
            rows.AddChild(unlock);
        }

        protected override bool Accepts(FileObject file) =>
            (FileTypes.Of(file.path) != null || SheetCsv.IsCsv(file.path))
            && Shows(filtered, file.path);

        protected override string DisplayName(FileObject file) =>
            file.type == FileObject.FileType.Directory ? file.name
            : SheetCsv.IsCsv(file.path) ? Path.GetFileName(file.path)
            : BaseName(file.path);

        protected override void Activate(FileObject file)
        {
            pendingPane = null;
            Open(file.path);
        }

        protected override (string icon, PaletteRole role)? RowIcon(FileObject file) =>
            file.type == FileObject.FileType.Directory ? ("folder", PaletteRole.MutedInk)
            : SheetCsv.IsCsv(file.path) ? ("sheet", PaletteRole.Sheet)
            : FileTypes.KindOf(file.path) switch
            {
                FileKind.Note or FileKind.Markdown or FileKind.PlainText => ("doc", PaletteRole.Doc),
                FileKind.Sheet => ("sheet", PaletteRole.Sheet),
                FileKind.Planner => ("planner", PaletteRole.Planner),
                _ => ("file", PaletteRole.MutedInk)
            };

        protected override void Rename(FileObject file, string newName) => RenameNote(file.path, newName);

        protected override string RowContextMenu(FileObject file) =>
            file.type == FileObject.FileType.Directory ? folderMenu
            : SheetCsv.IsCsv(file.path) ? csvMenu
            : !IsXml(file.path) ? noteMenu
            : filtered != WorkspaceKind.Xml ? noteXmlMenu
            : SheetDocument.IsSheet(file.path) ? asSheetMenu
            : PlannerDocument.IsPlanner(file.path) ? asPlannerMenu
            : asNoteMenu;

        #region ---- note operations ----
        [A_XSDActionDependency("Notes.New", "UI", "Creates a note at the vault root and opens it")]
        public static void New()
        {
            VaultBrowserControl browser = Browser();
            browser?.NewNote(browser.RootPath);
        }

        [A_XSDActionDependency("Notes.NewHere", "UI", "Creates a note beside the entry the menu was opened on")]
        public static void NewHere()
        {
            FileRowControl row = MenuRow();
            if (row == null) { New(); return; }

            Browser()?.NewNote(row.file.type == FileObject.FileType.Directory ? row.file.path : row.file.parent.path);
        }

        [A_XSDActionDependency("Sheets.New", "UI", "Creates an unfixed sheet at the vault root and opens it")]
        public static void NewSheet() => NewSheetAtRoot(false);

        [A_XSDActionDependency("Sheets.NewHere", "UI", "Creates an unfixed sheet beside the entry the menu was opened on")]
        public static void NewSheetHere() => NewSheetBeside(false);

        [A_XSDActionDependency("Sheets.NewFixed", "UI", "Creates a fixed-size sheet at the vault root and opens it")]
        public static void NewFixedSheet() => NewSheetAtRoot(true);

        [A_XSDActionDependency("Sheets.NewFixedHere", "UI", "Creates a fixed-size sheet beside the entry the menu was opened on")]
        public static void NewFixedSheetHere() => NewSheetBeside(true);

        private static void NewSheetAtRoot(bool fixedSize)
        {
            VaultBrowserControl browser = Browser();
            browser?.NewSheet(browser.RootPath, fixedSize);
        }

        private static void NewSheetBeside(bool fixedSize)
        {
            FileRowControl row = MenuRow();
            if (row == null) { NewSheetAtRoot(fixedSize); return; }

            Browser()?.NewSheet(row.file.type == FileObject.FileType.Directory ? row.file.path : row.file.parent.path, fixedSize);
        }

        [A_XSDActionDependency("Planners.New", "UI", "Creates a planner at the vault root and opens it")]
        public static void NewPlanner()
        {
            VaultBrowserControl browser = Browser();
            browser?.NewPlanner(browser.RootPath);
        }

        [A_XSDActionDependency("Tex.New", "UI", "Creates a LaTeX document at the vault root and opens it")]
        public static void NewTex()
        {
            VaultBrowserControl browser = Browser();
            browser?.NewTex(browser.RootPath);
        }

        [A_XSDActionDependency("Planners.NewHere", "UI", "Creates a planner beside the entry the menu was opened on")]
        public static void NewPlannerHere()
        {
            FileRowControl row = MenuRow();
            if (row == null) { NewPlanner(); return; }

            Browser()?.NewPlanner(row.file.type == FileObject.FileType.Directory ? row.file.path : row.file.parent.path);
        }

        [A_XSDActionDependency("Graphs.New", "UI", "Creates a graph view at the vault root and opens it")]
        public static void NewGraph()
        {
            VaultBrowserControl browser = Browser();
            browser?.NewGraph(browser.RootPath);
        }

        [A_XSDActionDependency("Graphs.NewHere", "UI", "Creates a graph view beside the entry the menu was opened on")]
        public static void NewGraphHere()
        {
            FileRowControl row = MenuRow();
            if (row == null) { NewGraph(); return; }

            Browser()?.NewGraph(row.file.type == FileObject.FileType.Directory ? row.file.path : row.file.parent.path);
        }

        [A_XSDActionDependency("Folders.New", "UI", "Creates a folder at the vault root")]
        public static void NewFolder()
        {
            VaultBrowserControl browser = Browser();
            browser?.NewFolder(browser.RootPath);
        }

        [A_XSDActionDependency("Folders.NewHere", "UI", "Creates a folder inside the folder the menu was opened on, or beside the file")]
        public static void NewFolderHere()
        {
            FileRowControl row = MenuRow();
            if (row == null) { NewFolder(); return; }

            Browser()?.NewFolder(row.file.type == FileObject.FileType.Directory ? row.file.path : row.file.parent.path);
        }

        [A_XSDActionDependency("Sheets.FromCsv", "UI", "Writes the CSV the menu was opened on as a sheet beside it, keeps the CSV and opens the sheet")]
        public static void FromCsv()
        {
            FileRowControl row = MenuRow();
            if (row != null && SheetCsv.IsCsv(row.file.path)) Browser()?.SheetFromCsv(row.file.path, false);
        }

        [A_XSDActionDependency("Sheets.ConvertCsv", "UI", "Turns the CSV the menu was opened on into a sheet, sends the CSV to the recycle bin and opens the sheet")]
        public static void ConvertCsv()
        {
            FileRowControl row = MenuRow();
            if (row != null && SheetCsv.IsCsv(row.file.path)) Browser()?.SheetFromCsv(row.file.path, true);
        }

        [A_XSDActionDependency("Sheets.ConvertOpenCsv", "UI", "Turns the CSV in the focused tab into a sheet, sends the CSV to the recycle bin and opens the sheet")]
        public static void ConvertOpenCsv()
        {
            TabItemControl? item = TabViewControl.focused?.activeItem;
            if (item != null && TabViewControl.FileEditorOf(item) is SheetEditorControl { document.isCsv: true, path: string path })
                Browser()?.SheetFromCsv(path, true);
        }

        [A_XSDActionDependency("Notes.Rename", "UI", "Turns the name of the note the menu was opened on into a field")]
        public static void RenameEntry()
        {
            FileRowControl row = MenuRow();
            if (row != null) Browser()?.BeginRename(row.file);
        }

        [A_XSDActionDependency("Notes.Duplicate", "UI", "Copies the note the menu was opened on and opens the copy")]
        public static void Duplicate()
        {
            FileRowControl row = MenuRow();
            if (row != null) Browser()?.DuplicateNote(row.file);
        }

        [A_XSDActionDependency("Notes.Delete", "UI", "Asks, then sends the note the menu was opened on to the recycle bin")]
        public static void Delete()
        {
            FileRowControl row = MenuRow();
            if (row != null) Browser()?.DeleteNote(row.file);
        }

        [A_XSDActionDependency("Notes.OpenAsXml", "UI", "Opens the file the menu was opened on as XML source")]
        public static void OpenAsXml()
        {
            FileRowControl row = MenuRow();
            if (row != null && IsXml(row.file.path)) Open(row.file.path, true);
        }

        [A_XSDActionDependency("Notes.Encrypt", "UI", "Asks for a new password and encrypts the note, sheet or planner the menu was opened on")]
        public static void Encrypt()
        {
            FileRowControl row = MenuRow();
            if (row == null || !IsAuroraXml(row.file.path) || EncryptedFile.IsEncrypted(row.file.path)) return;

            string path = row.file.path;
            PasswordWindow.Ask(UIActions.Invoking(), $"Encrypt {Path.GetFileName(path)}", true, null,
                password => Task.Run(() => PasswordKey.Create(password)).ContinueWith(task => Engine.Post(() => SetEncryption(path, task.Result, null))), null);
        }

        [A_XSDActionDependency("Notes.Decrypt", "UI", "Asks for the password and stores the file the menu was opened on unencrypted")]
        public static void Decrypt()
        {
            FileRowControl row = MenuRow();
            if (row != null && EncryptedFile.IsEncrypted(row.file.path)) AskToDecrypt(UIActions.Invoking(), row.file.path, null);
        }

        private static void AskToDecrypt(RenderWindow source, string path, string? error) =>
            PasswordWindow.Ask(source, $"Remove encryption from {Path.GetFileName(path)}", false, error,
                password => Task.Run(() => EncryptedFile.Unlock(path, password)).ContinueWith(task => Engine.Post(() =>
                {
                    if (task.Result is PasswordKey opener) SetEncryption(path, null, opener);
                    else AskToDecrypt(source, path, "Wrong password");
                })), null);

        // Re-saves the file under key, or plain when key is null; opener reads it while it is still encrypted.
        private static void SetEncryption(string path, PasswordKey? key, PasswordKey? opener)
        {
            FileWriteQueue.Drain();
            PasswordKey? old;
            if (SheetDocument.IsSheet(path))
            {
                SheetDocument sheet = SheetBook.Get(path, opener);
                old = sheet.encryption;
                sheet.encryption = key;
                sheet.Save(path);
            }
            else if (PlannerDocument.IsPlanner(path))
            {
                PlannerDocument planner = PlannerBook.Open(path, opener);
                old = planner.encryption;
                planner.encryption = key;
                planner.Save(path);
                PlannerBook.Close(path, planner);
            }
            else
            {
                DocumentEditSession session = NoteSessions.Open(path, opener);
                old = session.document.encryption;
                session.document.encryption = key;
                session.Save();
                NoteSessions.Close(session);
            }

            if (old != null && !FileKeys.IsRemembered(old)) old.Dispose();
            if (opener != null && opener != old) opener.Dispose();
        }

        #region ---- vault lock ----
        // The vault of the row or header the menu was opened on, else of the focused tab's file, else the window's first.
        internal static string CurrentVault()
        {
            for (Control? c = ContextMenus.target; c != null; c = c.parent as Control)
            {
                if (c is VaultBrowserControl browser) return browser.vault;
                if (c.contextMenu == vaultMenu && BrowsersIn(c).LastOrDefault() is VaultBrowserControl lowest) return lowest.vault;
                if (c.parent is StackPanelControl group && group.children.OfType<VaultBrowserControl>().FirstOrDefault() is VaultBrowserControl beside)
                    return beside.vault;
            }

            RenderWindow home = Home();
            if (TabViewControl.focused?.activeItem is TabItemControl item && UIEngine.WindowOf(item) == home
                && TabViewControl.FileEditorOf(item)?.path is string path && VaultWindows.KnownVaultOf(path) is string owner
                && home.roots.Any(root => KnownVaults.SamePath(root, owner)))
                return owner;

            return home.roots.FirstOrDefault() ?? KnownVaults.Resolve(SettingsRegistry.Get<ThoriumSettings>().vault.path);
        }

        // The known vault holding a file, deepest first; a file in none falls back to the vault setting.
        private static string VaultOf(string? home) =>
            (home != null ? VaultWindows.KnownVaultOf(home) : null) ?? KnownVaults.Resolve(SettingsRegistry.Get<ThoriumSettings>().vault.path);

        // The editor window to act in: the given one when it lists vaults, else the first shown one.
        private static RenderWindow VaultWindow(RenderWindow? from)
        {
            if (from != null && Browsers(from).Any()) return from;
            return VaultWindows.Editors().FirstOrDefault(window => !window.hidden && Browsers(window).Any()) ?? Engine.primary;
        }

        internal static RenderWindow Home() => VaultWindow(UIActions.Invoking());

        internal static IEnumerable<VaultBrowserControl> Browsers(RenderWindow window) => BrowsersIn(window.ui?.uiRoot);

        private static IEnumerable<VaultBrowserControl> BrowsersIn(Control? control)
        {
            if (control == null) yield break;
            if (control is VaultBrowserControl browser) { yield return browser; yield break; }
            foreach (Entity child in control.children)
                if (child is Control childControl)
                    foreach (VaultBrowserControl found in BrowsersIn(childControl))
                        yield return found;
        }

        [A_XSDActionDependency("Vault.Unlock", "UI", "Asks for the password or recovery key of the locked vault and opens it")]
        public static void UnlockVault() => UnlockVault(CurrentVault());

        private static void UnlockVault(string vault)
        {
            if (!VaultLock.HasLock(vault) || VaultLock.IsOpen(vault)) return;

            AskVaultKey(vault, "Unlock vault", null, master =>
            {
                VaultLock.Use(vault, master);
                VaultWindows.RebuildBrowsers(vault);
                VaultWindows.Revive(vault);
            }, null);
        }

        // Asks for a locked vault's password or recovery key, again after a wrong one; opened takes its master key.
        internal static void AskVaultKey(string vault, string title, string? error, Action<byte[]> opened, Action? cancelled) =>
            PasswordWindow.Ask(Home(), $"{title}: password or recovery key", false, error,
                secret => Task.Run(() => VaultLock.Open(vault, secret)).ContinueWith(task =>
                {
                    byte[]? master = task.IsCompletedSuccessfully ? task.Result : null;
                    Engine.Post(() =>
                    {
                        if (master == null) AskVaultKey(vault, title, "Wrong password or recovery key", opened, cancelled);
                        else opened(master);
                    });
                }), cancelled,
                VaultLock.HelloOf(vault) is (string credential, string id) ? ("Use Windows Hello", () => HelloKey(vault, title, credential, id, opened, cancelled)) : null);

        // Asks Windows Hello for the vault's signature; back to the password prompt when it does not open the vault.
        private static void HelloKey(string vault, string title, string credential, string id, Action<byte[]> opened, Action? cancelled) =>
            Task.Run(() => WindowsHello.Sign(credential, id, false)).ContinueWith(task =>
            {
                byte[]? signature = task.IsCompletedSuccessfully ? task.Result : null;
                Engine.Post(() =>
                {
                    byte[]? master = signature != null ? VaultLock.OpenWithHello(vault, signature) : null;
                    if (master == null) AskVaultKey(vault, title, "Windows Hello did not open the vault", opened, cancelled);
                    else opened(master);
                });
            });

        [A_XSDActionDependency("Vault.EnableHello", "UI", "Adds Windows Hello as a way to unlock the open encrypted vault")]
        public static void EnableHello()
        {
            string vault = CurrentVault();
            if (!VaultLock.IsOpen(vault)) return;

            string credential = VaultLock.HelloCredential(vault);
            string id = VaultLock.IdOf(vault);
            Task.Run(async () => await WindowsHello.IsAvailable() ? await WindowsHello.Sign(credential, id, true) : null).ContinueWith(task =>
            {
                byte[]? signature = task.IsCompletedSuccessfully ? task.Result : null;
                Engine.Post(() =>
                {
                    if (signature == null) Log.Warn($"Windows Hello is not set up or was cancelled; the vault keeps its other ways in.");
                    else VaultLock.AddHello(vault, credential, signature);
                });
            });
        }

        [A_XSDActionDependency("Vault.Lock", "UI", "Asks for a password, shows a recovery key, then encrypts every file of the vault")]
        public static void LockVault()
        {
            string vault = CurrentVault();
            if (VaultLock.HasLock(vault)) return;

            RenderWindow home = Home();
            PasswordWindow.Ask(home, "Lock vault", true, null, password =>
                Task.Run(() => PasswordKey.Create(password)).ContinueWith(task => Engine.Post(() =>
                {
                    PasswordKey key = task.Result;
                    byte[] recovery = VaultLock.NewRecovery();
                    void Forget()
                    {
                        key.Dispose();
                        CryptographicOperations.ZeroMemory(recovery);
                    }

                    RecoveryKeyWindow.Show(home, VaultLock.FormatRecovery(recovery), () =>
                    {
                        VaultLock.Create(vault, key, recovery);
                        Forget();
                        Browser()?.Rebuild();
                    }, Forget);
                })), null);
        }

        [A_XSDActionDependency("Vault.ChangePassword", "UI", "Asks for a new password for the open encrypted vault")]
        public static void ChangeVaultPassword()
        {
            string vault = CurrentVault();
            if (!VaultLock.IsOpen(vault)) return;

            PasswordWindow.Ask(Home(), "New vault password", true, null, password =>
                Task.Run(() => PasswordKey.Create(password)).ContinueWith(task => Engine.Post(() =>
                {
                    using PasswordKey key = task.Result;
                    VaultLock.ChangePassword(vault, key);
                })), null);
        }

        [A_XSDActionDependency("Vault.RemoveLock", "UI", "Asks for the password or recovery key, then decrypts every file of the vault")]
        public static void RemoveVaultLock()
        {
            string vault = CurrentVault();
            if (!VaultLock.IsOpen(vault)) return;

            AskVaultKey(vault, "Remove vault lock", null, master =>
            {
                CryptographicOperations.ZeroMemory(master);
                VaultLock.Remove(vault);
            }, null);
        }
        #endregion

        [A_XSDActionDependency("Notes.OpenNormally", "UI", "Opens the file the menu was opened on as a note, sheet or planner")]
        public static void OpenNormally()
        {
            FileRowControl row = MenuRow();
            if (row != null) Open(row.file.path, false);
        }

        // "Add to sticky note": a new sticky, then every open sticky.
        private static List<ContextMenuEntry> StickyEntries()
        {
            List<ContextMenuEntry> entries = new List<ContextMenuEntry>();
            if (MenuRow() is not FileRowControl row) return entries;

            string path = row.file.path;
            bool asXml = ShownKind(Home()) == WorkspaceKind.Xml && IsXml(path);
            RenderWindow source = UIActions.Invoking();
            entries.Add(new ContextMenuButton("New sticky", () => TabViewControl.OpenStickyAtPointer(BuildTab(path, asXml), stickyDocument, source)));
            foreach (RenderWindow window in Engine.windows.Values)
            {
                if (window.closeRequested || window.uiDocument != stickyDocument) continue;
                if (TabViewControl.TabViews(window.ui.uiRoot).FirstOrDefault() is not TabViewControl view) continue;

                entries.Add(new ContextMenuButton(view.activeItem?.header ?? "Sticky", () =>
                {
                    TabItemControl tab = BuildTab(path, asXml);
                    view.AddChild(tab);
                    view.SetActive(tab);
                }));
            }
            return entries;
        }

        // The row the open menu was opened on, or null when it was opened on the browser's ground.
        private static FileRowControl MenuRow()
        {
            for (Control c = ContextMenus.target; c != null; c = c.parent as Control)
                if (c is FileRowControl row) return row;

            return null;
        }

        private static VaultBrowserControl? Browser()
        {
            string vault = CurrentVault();
            List<VaultBrowserControl> browsers = Browsers(Home()).ToList();
            return browsers.FirstOrDefault(browser => KnownVaults.SamePath(browser.vault, vault)) ?? browsers.FirstOrDefault();
        }

        private void NewNote(string folder, string? extension = null) =>
            NoteNameWindow.Ask(UIEngine.WindowOf(this), "Untitled",
                name => CreateNote(folder, name, extension ?? SettingsRegistry.Get<ThoriumSettings>().newNotes.Extension), null);

        // A note needs a block holding a run before it can be typed into — the editor places its
        // caret on a run and builds neither.
        private void CreateNote(string folder, string name, string extension)
        {
            string path = FreePath(folder, name, extension);

            NoteBlock block = new NoteBlock();
            block.AppendRun(new Run());
            RichTextDocument document = new RichTextDocument { name = Path.GetFileNameWithoutExtension(path), blocks = [block] };
            document.Save(path);

            Expand(folder);
            Rebuild();
            Open(path);
        }

        private void NewSheet(string folder, bool fixedSize)
        {
            if (fixedSize)
                NoteNameWindow.AskSheet(UIEngine.WindowOf(this), "Untitled", SheetPage.defaultSize, SheetPage.defaultSize,
                    (name, rows, columns) => CreateSheet(folder, name, true, rows, columns), null);
            else
                NoteNameWindow.Ask(UIEngine.WindowOf(this), "Untitled",
                    name => CreateSheet(folder, name, false, SheetPage.defaultSize, SheetPage.defaultSize), null);
        }

        private void CreateSheet(string folder, string name, bool fixedSize, int rows, int columns)
        {
            string path = FreePath(folder, name, SheetDocument.extension);
            SheetDocument sheet = SheetDocument.Blank(BaseName(path), fixedSize);
            sheet.pages[0].rows = rows;
            sheet.pages[0].columns = columns;
            sheet.Save(path);
            SheetBook.Created();

            Expand(folder);
            Rebuild();
            Open(path);
        }

        private void NewTex(string folder) =>
            NoteNameWindow.Ask(UIEngine.WindowOf(this), "Untitled", name => CreateTex(folder, name), null);

        private void CreateTex(string folder, string name)
        {
            string path = FreePath(folder, name, ".tex");
            VaultFiles.WriteText(path, "\\documentclass{article}\n\n\\begin{document}\n\n\\end{document}\n");

            Expand(folder);
            Rebuild();
            Open(path);
        }

        private void NewPlanner(string folder) =>
            NoteNameWindow.Ask(UIEngine.WindowOf(this), "Untitled", name => CreatePlanner(folder, name), null);

        private void CreatePlanner(string folder, string name)
        {
            string path = FreePath(folder, name, PlannerDocument.extension);
            PlannerDocument.Blank(BaseName(path)).Save(path);

            Expand(folder);
            Rebuild();
            Open(path);
        }

        private void NewGraph(string folder) =>
            NoteNameWindow.Ask(UIEngine.WindowOf(this), "Graph", name => CreateGraph(folder, name), null);

        private void CreateGraph(string folder, string name)
        {
            string path = FreePath(folder, name, GraphDocument.Extension);
            GraphDocument.Blank(BaseName(path)).Save(path);

            Expand(folder);
            Rebuild();
            Open(path);
        }

        private void NewFolder(string folder) =>
            NoteNameWindow.Ask(UIEngine.WindowOf(this), "New folder", name => CreateFolder(folder, name), null);

        private void CreateFolder(string folder, string name)
        {
            string path = Path.Combine(folder, name);
            for (int i = 2; Directory.Exists(path) || File.Exists(path); i++)
                path = Path.Combine(folder, $"{name} {i}");
            Directory.CreateDirectory(path);

            Expand(folder);
            Rebuild();
        }

        // The loaded copy, so edits not yet saved to the CSV come along; replacing deletes the CSV and its tab.
        private void SheetFromCsv(string csv, bool replace)
        {
            string path = FreePath(Path.GetDirectoryName(csv)!, BaseName(csv), SheetDocument.extension);
            SheetBook.Get(csv).Save(path);
            WriteName(path, BaseName(path));
            if (replace)
            {
                SheetBook.Renamed(csv, path, VaultSheets(path));
                SheetLinks.Renamed(csv, path, VaultNotes(path));
                DeleteFile(csv);
            }
            SheetBook.Created();

            Rebuild();
            Open(path);
        }

        private void DuplicateNote(FileObject file)
        {
            string path = FreePath(file.parent.path, BaseName(file.path) + " copy", Extension(file.path));

            File.Copy(file.path, path);
            WriteName(path, BaseName(path));
            if (SheetDocument.IsSheet(path)) SheetBook.Created();

            Rebuild();
            Open(path);
        }

        private void DeleteNote(FileObject file) =>
            ConfirmWindow.Ask(UIEngine.WindowOf(this), $"Delete \"{DisplayName(file)}\"?",
                () => DeleteFile(file.path), null);

        // The tab goes first and goes unwritten, or closing it would put the note back on disk.
        private void DeleteFile(string path)
        {
            TabItemControl open = TabViewControl.FindOpenDocument(path, out TabViewControl owner);
            if (open != null) owner.FinishClose(open);

            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            if (SheetDocument.IsSheet(path) || SheetCsv.IsCsv(path)) SheetBook.Deleted(path);
            Rebuild();
        }
        #endregion

        // The name lives in three places: the file, the Name inside it, and every editor already
        // holding the note open. Static, because a tab renames through here too and has no row.
        internal static void RenameNote(string path, string newName)
        {
            string name = newName?.Trim();
            if (SheetCsv.IsCsv(path) && name != null && SheetCsv.IsCsv(name)) name = name[..^SheetCsv.extension.Length];
            if (string.IsNullOrEmpty(name) || name == BaseName(path)) return;

            string target = FreePath(Path.GetDirectoryName(path)!, name, Extension(path));
            FileWriteQueue.Drain();
            File.Move(path, target);
            FileKeys.Renamed(path, target);
            WriteName(target, name);

            foreach ((TabItemControl item, TabViewControl view) in TabViewControl.FindOpenDocuments(path))
            {
                IFileEditor editor = TabViewControl.FileEditorOf(item);
                editor.Repath(target, name);
                item.name = target;
                view.Retitle(item, editor is XmlEditorControl ? Path.GetFileName(target) : name);
            }

            if (SheetDocument.IsSheet(target) || SheetCsv.IsCsv(target))
            {
                SheetBook.Renamed(path, target, SheetBook.Linked(target, VaultSheets));
                SheetLinks.Renamed(path, target, SheetBook.Linked(target, VaultNotes));
            }
            else NoteLinks.Renamed(path, target, VaultNotes(target));
            if (PlannerDocument.IsPlanner(target)) PlannerBook.Renamed(Path.GetFullPath(path), target);

            VaultBrowserControl? browser = Browser();
            browser?.Rebuild();
            browser?.FollowFocusedTab();
        }

        // Focuses the note wherever it is on screen, and only opens a tab when it is not. A note open
        // in a hidden workspace gets a second view in the shown one.
        internal static void Open(string notePath) => Open(notePath, ShownKind(Home()) == WorkspaceKind.Xml && IsXml(notePath));

        private static void Open(string notePath, bool asXml)
        {
            RenderWindow home = Home();
            foreach ((TabItemControl already, TabViewControl owner) in TabViewControl.FindOpenDocuments(notePath))
            {
                if (!Shown(owner) || TabViewControl.FileEditorOf(already) is XmlEditorControl != asXml) continue;
                owner.SetActive(already);
                UIEngine.WindowOf(owner)?.Focus();
                return;
            }

            TabViewControl? pending = pendingPane;
            pendingPane = null;
            TabViewControl tabs = (pending?.parent != null ? pending : null) ?? FocusedTabs(home) ?? ShownTabs(home);
            if (tabs == null) return;

            TabItemControl tab = BuildTab(notePath, asXml);
            if (ShownKind(home) == WorkspaceKind.Calendar && TabViewControl.FileEditorOf(tab) is PlannerEditorControl planner)
                planner.ShowView(PlannerView.Calendar);
            tabs.AddChild(tab);
            tabs.SetActive(tab);
        }

        // A pane's +: a file of its workspace's kind, or a menu of kinds in a General workspace.
        public static void NewInPane(TabViewControl view, Control button)
        {
            pendingPane = view;
            switch (WorkspacePageControl.Of(view)?.kind ?? WorkspaceKind.General)
            {
                case WorkspaceKind.Docs: New(); break;
                case WorkspaceKind.Xml:
                    VaultBrowserControl? browser = Browser();
                    browser?.NewNote(browser.RootPath, ".axml");
                    break;
                case WorkspaceKind.Sheets: NewSheet(); break;
                case WorkspaceKind.LaTeX: NewTex(); break;
                case WorkspaceKind.Manager:
                case WorkspaceKind.Calendar: NewPlanner(); break;
                default:
                    if (ContextMenus.Get(paneNewMenu) is ContextMenu menu)
                        ContextMenus.Open(menu.entries, button, new Vector2(button.arrangedRect.x, button.arrangedRect.Bottom));
                    break;
            }
        }

        // A sheet reference's file part: the first sheet whose name, or path ending, matches it.
        public static string? FindSheet(string? home, string file)
        {
            if (!Redirect(ref home, ref file)) return null;
            string name = file.Trim().Replace('\\', '/');
            if (SheetCsv.IsCsv(name))
            {
                string root = VaultOf(home);
                if (!Directory.Exists(root)) return null;
                foreach (string path in Directory.EnumerateFiles(root, "*" + SheetCsv.extension, System.IO.SearchOption.AllDirectories))
                    if (path.Replace('\\', '/').EndsWith("/" + name, StringComparison.OrdinalIgnoreCase)) return path;
                return null;
            }
            if (name.EndsWith(SheetDocument.extension, StringComparison.OrdinalIgnoreCase))
                name = name[..^SheetDocument.extension.Length];

            foreach (string path in VaultSheets(home))
            {
                string stem = path.Replace('\\', '/')[..^SheetDocument.extension.Length];
                if (stem.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase)) return path;
            }
            return null;
        }

        internal static IEnumerable<string> VaultSheets(string? home)
        {
            string root = VaultOf(home);
            return Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "*" + SheetDocument.extension, System.IO.SearchOption.AllDirectories)
                : Enumerable.Empty<string>();
        }

        // An "@Alias/" target moves home into the vault the alias names; false when that vault isn't on this machine.
        private static bool Redirect(ref string? home, ref string name)
        {
            (bool aliased, _, string? vault) = ReferencedVault(home, name, out string rest);
            if (!aliased) return true;
            if (vault == null) return false;

            home = Path.Combine(vault, "_");
            name = rest;
            return true;
        }

        // Whether name starts "@Alias/", the id the alias names, and that vault's folder when it is on this machine.
        private static (bool aliased, string? id, string? vault) ReferencedVault(string? home, string name, out string rest)
        {
            rest = name;
            string trimmed = name.Trim();
            int slash = trimmed.IndexOfAny(['/', '\\']);
            if (!trimmed.StartsWith('@') || slash < 2) return (false, null, null);

            rest = trimmed[(slash + 1)..];
            string? id = VaultManifest.AliasTarget(VaultOf(home), trimmed[1..slash]);
            string? vault = id == null ? null : SettingsRegistry.Get<KnownVaults>().PathOf(id);
            return (true, id, vault != null && Directory.Exists(vault) ? vault : null);
        }

        // A reference into a vault the home vault names but this machine doesn't have.
        public static bool VaultMissing(string? home, string file) => ReferencedVault(home, file, out _) is (true, not null, null);

        // The file part without its "@Alias/" when, written in home, it points into target's vault; null otherwise.
        public static string? VaultPart(string? home, string file, string target)
        {
            (bool aliased, _, string? vault) = ReferencedVault(home, file, out string rest);
            string? into = aliased ? vault : VaultOf(home);
            return into != null && KnownVaults.SamePath(into, VaultOf(target)) ? (aliased ? rest : file) : null;
        }

        // Homes inside the open vaults whose manifests reference home's vault.
        public static IEnumerable<string> ReferencingVaults(string? home)
        {
            string vault = VaultOf(home);
            string id = VaultManifest.IdOf(vault);
            if (id.Length == 0) return Enumerable.Empty<string>();
            return SettingsRegistry.Get<KnownVaults>().vaults
                .Where(known => !KnownVaults.SamePath(known.path, vault) && VaultWindows.IsOpen(known.path)
                                && VaultManifest.Depends(known.path).Any(d => d.id == id))
                .Select(known => Path.Combine(known.path, "_"))
                .ToList();
        }

        // "@Alias/" for a link from a file in one vault to a sheet in another, adding the reference when there is none yet.
        public static string? FilePrefix(string? destination, string source)
        {
            string? from = destination == null ? null : VaultWindows.KnownVaultOf(destination);
            string? into = VaultWindows.KnownVaultOf(source);
            if (from == null || into == null || KnownVaults.SamePath(from, into)) return null;

            string id = VaultManifest.IdOf(into);
            string? alias = AliasOf(from, id);
            if (alias == null && VaultWindows.AddReference(from, into)) alias = AliasOf(from, id);
            return alias == null ? null : "@" + alias + "/";
        }

        private static string? AliasOf(string vault, string id) =>
            VaultManifest.Depends(vault).FirstOrDefault(d => d.id == id).alias is { Length: > 0 } alias ? alias : null;

        // The note names a [[ in a note at home can complete to, each once.
        public static string[] NoteNames(string? home)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> names = new List<string>(256);
            foreach (string path in VaultNotes(home))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (seen.Add(name)) names.Add(name);
            }
            return names.ToArray();
        }

        // Notes that can hold a sheet link: .md and .axml, sheets and planners left out.
        internal static IEnumerable<string> VaultNotes(string? home)
        {
            string root = VaultOf(home);
            if (!Directory.Exists(root)) return Enumerable.Empty<string>();
            return Directory.EnumerateFiles(root, "*.md", System.IO.SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(root, "*.axml", System.IO.SearchOption.AllDirectories)
                    .Where(path => !SheetDocument.IsSheet(path) && !PlannerDocument.IsPlanner(path) && !GraphDocument.IsGraph(path)))
                .Where(path => !InDotFolder(root, path));
        }

        // Whether a file sits under a folder the browser hides.
        internal static bool InDotFolder(string root, string path) =>
            Path.GetRelativePath(root, Path.GetDirectoryName(path)!)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part.StartsWith('.') && part != ".");

        // A [[note]] by name, or a web address handed to the system browser.
        public static bool OpenLink(string? home, string link)
        {
            if (link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link) { UseShellExecute = true });
                return true;
            }

            if (!link.StartsWith("[[") || !link.EndsWith("]]")) return false;

            string target = link[2..^2].Split('|')[0];
            int hash = target.IndexOf('#');
            string name = hash < 0 ? target : target[..hash];
            string? note = name.Trim().Length == 0 ? home : FindNote(home, name);
            if (note == null) return false;

            Open(note);
            if (hash >= 0 && !target[(hash + 1)..].StartsWith('^')) ShowHeading(note, target[(hash + 1)..]);
            return true;
        }

        // Shows a heading in the note's open document view.
        private static void ShowHeading(string note, string heading)
        {
            foreach ((TabItemControl item, TabViewControl _) in TabViewControl.FindOpenDocuments(note))
                if (TabViewControl.FileEditorOf(item) is DocumentEditorControl editor && editor.ShowHeading(heading)) return;
        }

        // The link index of the open vault holding a file; null for a file in none.
        internal static LinkIndex? IndexOf(string path) =>
            VaultWindows.KnownVaultOf(path) is string vault && !VaultWindows.Closed(path) ? LinkIndex.For(vault, VaultManifest.IdOf(vault)) : null;

        // The first file whose name, or path ending, matches a link target, in the vault of home.
        public static string? FindNote(string? home, string name)
        {
            if (!Redirect(ref home, ref name)) return null;
            string root = VaultOf(home);
            if (!Directory.Exists(root)) return null;

            name = name.Trim().Replace('\\', '/');
            foreach (string path in Directory.EnumerateFiles(root, "*", System.IO.SearchOption.AllDirectories))
            {
                string withoutExtension = Path.ChangeExtension(path, null).Replace('\\', '/');
                if ((withoutExtension.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase)
                     || path.Replace('\\', '/').EndsWith("/" + name, StringComparison.OrdinalIgnoreCase))
                    && FileTypes.Of(path) != null && !SheetCsv.IsCsv(path) && !InDotFolder(root, path))
                    return path;
            }
            return null;
        }

        // The split pane last clicked in, so a note opens where the work is. Only this window counts —
        // the browser has no business opening notes in one that was torn off.
        private static TabViewControl FocusedTabs(RenderWindow window)
        {
            TabViewControl view = Context.Get<TabViewControl>(tabsContext);
            return view != null && UIEngine.WindowOf(view) == window && Shown(view) ? view : null;
        }

        // The first pane of the window's shown workspace.
        private static TabViewControl? ShownTabs(RenderWindow window) =>
            WorkspaceControl.In(window.ui.uiRoot)?.shown is WorkspacePageControl page
                ? TabViewControl.TabViews(page).FirstOrDefault()
                : null;

        private static WorkspaceKind ShownKind(RenderWindow window) =>
            WorkspaceControl.In(window.ui.uiRoot)?.shown?.kind ?? WorkspaceKind.General;

        // Whether a view is in the workspace on screen in its window.
        private static bool Shown(TabViewControl view) =>
            WorkspacePageControl.Of(view) is not WorkspacePageControl page || page.isShown;

        // One tab holding one note, for whichever view is going to take it. Loaded before the tab is
        // built, so the caption can come from the note's own name.
        internal static TabItemControl BuildTab(string notePath, bool asXml)
        {
            if (VaultWindows.Closed(notePath) && VaultWindows.KnownVaultOf(notePath) is string vault) return BuildClosedTab(notePath, vault, asXml);
            if (EncryptedFile.IsEncrypted(notePath))
            {
                asXml = false;
                if (Locked(notePath)) return BuildLockedTab(notePath);
            }
            if (asXml) return BuildXmlTab(notePath);
            if (SheetDocument.IsSheet(notePath) || SheetCsv.IsCsv(notePath)) return BuildSheetTab(notePath);
            if (PlannerDocument.IsPlanner(notePath)) return BuildPlannerTab(notePath);
            if (GraphDocument.IsGraph(notePath)) return BuildGraphTab(notePath);
            if (FileTypes.KindOf(notePath) == FileKind.Tex) return BuildTexTab(notePath);

            DocumentEditorControl editor = new DocumentEditorControl { contextMenu = "note" };
            editor.LoadPath(notePath);

            TabItemControl tab = new TabItemControl
            {
                name = notePath,
                header = editor.session?.document?.name ?? Path.GetFileNameWithoutExtension(notePath),
                onRename = name => RenameNote(editor.session.path, name)
            };
            tab.AddChild(editor);
            return tab;
        }

        private static TabItemControl BuildSheetTab(string sheetPath)
        {
            SheetEditorControl editor = new SheetEditorControl { contextMenu = "sheet" };
            editor.LoadPath(sheetPath);

            TabItemControl tab = new TabItemControl
            {
                name = sheetPath,
                header = editor.document.name ?? BaseName(sheetPath),
                onRename = name => RenameNote(editor.path, name)
            };
            tab.AddChild(editor);
            return tab;
        }

        private static TabItemControl BuildPlannerTab(string plannerPath)
        {
            PlannerEditorControl editor = new PlannerEditorControl();
            editor.LoadPath(plannerPath);

            TabItemControl tab = new TabItemControl
            {
                name = plannerPath,
                header = editor.document.name ?? BaseName(plannerPath),
                onRename = name => RenameNote(editor.path, name)
            };
            tab.AddChild(editor);
            return tab;
        }

        private static TabItemControl BuildGraphTab(string graphPath)
        {
            GraphEditorControl editor = new GraphEditorControl();
            editor.LoadPath(graphPath);

            TabItemControl tab = new TabItemControl
            {
                name = graphPath,
                header = editor.document.name ?? BaseName(graphPath),
                onRename = name => RenameNote(editor.path, name)
            };
            tab.AddChild(editor);
            return tab;
        }

        private static TabItemControl BuildTexTab(string texPath)
        {
            TexEditorControl editor = new TexEditorControl();
            editor.LoadPath(texPath);

            TabItemControl tab = new TabItemControl
            {
                name = texPath,
                header = editor.source.session?.document?.name ?? Path.GetFileNameWithoutExtension(texPath),
                onRename = name => RenameNote(editor.path, name)
            };
            tab.AddChild(editor);
            return tab;
        }

        // An encrypted file with no key to hand and no unlocked tab already holding it.
        private static bool Locked(string path)
        {
            if (FileKeys.Has(path)) return false;
            foreach ((TabItemControl open, TabViewControl _) in TabViewControl.FindOpenDocuments(path))
                if (TabViewControl.FileEditorOf(open) is not LockedFileControl) return false;
            return true;
        }

        private static TabItemControl BuildLockedTab(string path)
        {
            LockedFileControl locked = new LockedFileControl(path, false);
            TabItemControl tab = new TabItemControl { name = path, header = BaseName(path) };
            tab.AddChild(locked);
            return tab;
        }

        private static TabItemControl BuildClosedTab(string path, string vault, bool asXml)
        {
            TabItemControl tab = new TabItemControl { name = path, header = BaseName(path) };
            tab.AddChild(new ClosedVaultControl(path, vault, asXml));
            return tab;
        }

        private static TabItemControl BuildXmlTab(string xmlPath)
        {
            XmlEditorControl editor = new XmlEditorControl();
            editor.LoadPath(xmlPath);

            TabItemControl tab = new TabItemControl
            {
                name = xmlPath,
                header = Path.GetFileName(xmlPath),
                onRename = name => RenameNote(editor.path, name)
            };
            tab.AddChild(editor);
            return tab;
        }

        // A file's name without its extension; a sheet's ".sheet.axml" and a planner's ".planner.axml" count as one.
        private static string BaseName(string path) => Path.GetFileName(path)[..^Extension(path).Length];

        private static string Extension(string path) =>
            SheetDocument.IsSheet(path) ? SheetDocument.extension
            : PlannerDocument.IsPlanner(path) ? PlannerDocument.extension
            : GraphDocument.IsGraph(path) ? GraphDocument.Extension
            : Path.GetExtension(path);

        // "Name", then "Name 2", "Name 3" — a name already taken is never written over.
        private static string FreePath(string folder, string baseName, string extension)
        {
            string path = Path.Combine(folder, baseName + extension);
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(folder, $"{baseName} {i}{extension}");

            return path;
        }

        // Only .axml carries a name of its own; other formats are named by their file.
        private static void WriteName(string path, string name)
        {
            if (!IsAuroraXml(path) || EncryptedFile.IsEncrypted(path)) return;

            PasswordKey? none = null;
            XDocument xml = EncryptedFile.LoadXml(path, LoadOptions.None, ref none);
            xml.Root!.SetAttributeValue("Name", name);
            VaultFiles.SaveXml(xml, path, null, null);
        }
    }
}
