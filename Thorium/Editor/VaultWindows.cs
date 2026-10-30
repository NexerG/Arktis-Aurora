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
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Sheets;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;
using Thorium.Editor.CustomControls;

namespace Thorium.Editor
{
    // Editor windows and the vaults each one lists. A vault is open while some editor window lists it.
    public static class VaultWindows
    {
        private static readonly LogChannel Log = LogChannel.For("Vault");

        // the one session partition every editor window and tear-off is recorded in
        internal const string workspaceScope = "workspace";

        // UI documents and control names in UI.ui.axml
        private const string editorDocument = "main";
        private const string stickyDocument = "sticky-window";
        private const string browsersName = "Browsers";
        private const string trayButtonName = "TrayButton";
        private const string referencedName = "Referenced";
        private const string hostTitle = "Thorium";

        // menu document registered in ThoriumAssets.assets.axml
        private const string vaultMenu = "vault";

        // layout
        private const int headerHeight = 22;
        private const int cascade = 30;

        private static int _windows;

        // vaults the layout being restored lists, so a tab built before its window counts its vault open
        private static HashSet<string>? restoring;

        // vaults each window shows because its vaults reference them
        private static readonly Dictionary<RenderWindow, List<string>> referenced = new Dictionary<RenderWindow, List<string>>();

        #region ---- queries ----
        internal static IEnumerable<RenderWindow> Editors() =>
            Engine.windows.Values.Where(window => !window.closeRequested && window.uiDocument == editorDocument);

        private static bool IsSticky(RenderWindow window) => window.sessionScope == Startup.freeScope;

        internal static bool IsOpen(string vault) =>
            restoring?.Contains(Path.TrimEndingDirectorySeparator(vault)) == true
            || Editors().Any(window => window.roots.Any(root => KnownVaults.SamePath(root, vault))
                                       || referenced.TryGetValue(window, out List<string>? shown) && shown.Any(other => KnownVaults.SamePath(other, vault)));

        internal static string NameOf(string vault) => Path.GetFileName(Path.TrimEndingDirectorySeparator(vault));

        // The deepest known vault holding a file; null for a file in none.
        internal static string? KnownVaultOf(string path)
        {
            string full = Path.GetFullPath(path);
            string? best = null;
            foreach (KnownVault known in SettingsRegistry.Get<KnownVaults>().vaults)
            {
                string prefix = Path.TrimEndingDirectorySeparator(known.path) + Path.DirectorySeparatorChar;
                if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (best == null || known.path.Length > best.Length))
                    best = known.path;
            }
            return best;
        }

        // A file whose vault no window has open, or whose vault is still locked.
        internal static bool Closed(string path)
        {
            if (Background.standalone || KnownVaultOf(path) is not string vault) return false;
            return (VaultLock.HasLock(vault) && !VaultLock.IsOpen(vault)) || !IsOpen(vault);
        }

        private static bool Inside(string vault, string path) => KnownVaultOf(path) is string owner && KnownVaults.SamePath(owner, vault);

        private static IEnumerable<(TabItemControl item, TabViewControl view)> Tabs(RenderWindow window)
        {
            if (window.ui?.uiRoot == null) yield break;
            foreach (TabViewControl view in TabViewControl.TabViews(window.ui.uiRoot).ToList())
                foreach (TabItemControl item in view.Items.ToList())
                    yield return (item, view);
        }
        #endregion

        #region ---- opening ----
        // Adds the vault to the window, asking first for a locked one's key; a window already listing it is raised.
        internal static void OpenHere(RenderWindow window, string path)
        {
            string vault = KnownVaults.Resolve(path);
            SettingsRegistry.Get<KnownVaults>().Remember(vault);
            SettingsRegistry.Commit();

            WithKey(vault, () =>
            {
                bool wasOpen = IsOpen(vault);
                if (!window.roots.Any(root => KnownVaults.SamePath(root, vault)))
                {
                    window.roots.Add(vault);
                    AddBrowser(window, vault);
                    Sync(window);
                    Retitle(window);
                }
                else RebuildBrowsers(vault);

                Raise(window);
                if (!wasOpen || VaultLock.HasLock(vault)) Revive(vault);
                Log.Info($"opened '{vault}' in a window listing {window.roots.Count} vault(s)");
            }, null);
        }

        // Opens the vault in a window of its own: the emptied main window when there is one.
        internal static void OpenInNewWindow(string path)
        {
            string vault = KnownVaults.Resolve(path);
            WithKey(vault, () => OpenHere(EmptyPrimary() ?? NewEditor(), vault), null);
        }

        // The window already listing the vault, else the first shown editor, else a window of its own.
        internal static void OpenSomewhere(string path)
        {
            string vault = KnownVaults.Resolve(path);
            RenderWindow? window = Editors().FirstOrDefault(w => w.roots.Any(root => KnownVaults.SamePath(root, vault)))
                                   ?? Editors().FirstOrDefault(w => !w.hidden && w.roots.Count > 0);
            if (window != null) OpenHere(window, vault);
            else OpenInNewWindow(vault);
        }

        // Runs then once the vault's key is to hand; a vault with no lock needs none.
        private static void WithKey(string vault, Action then, Action? cancelled)
        {
            if (!VaultLock.HasLock(vault) || VaultLock.IsOpen(vault)) { then(); return; }

            VaultBrowserControl.AskVaultKey(vault, "Open vault", null, master =>
            {
                VaultLock.Use(vault, master);
                then();
            }, cancelled);
        }

        private static RenderWindow? EmptyPrimary() =>
            Engine.primary.hidden && Engine.primary.roots.Count == 0 ? Engine.primary : null;

        private static RenderWindow NewEditor()
        {
            GraphicsSettings settings = SettingsRegistry.Get<GraphicsSettings>();
            int width = (int)UIScaling.ToPixels(Engine.primary, settings.window.width);
            int height = (int)UIScaling.ToPixels(Engine.primary, settings.window.height);
            (int x, int y) = Engine.CenterOnPrimary(width, height);
            int offset = ++_windows % 8 * cascade;

            RenderWindow window = Engine.OpenWindow($"vault-{_windows}", (uint)width, (uint)height, x + offset, y + offset);
            window.uiDocument = editorDocument;
            window.ui.uiRoot = (WindowRoot)Control.ParseXML(editorDocument);
            WorkspaceControl.In(window.ui.uiRoot)?.LoadEmpty();
            Wire(window);
            return window;
        }

        private static void Raise(RenderWindow window)
        {
            if (window.hidden) Background.ShowWindow(window);
            window.Focus();
        }
        #endregion

        #region ---- closing ----
        [A_XSDActionDependency("Vault.Close", "UI", "Closes the vault the menu was opened on in this window")]
        public static void CloseMenuVault()
        {
            RenderWindow window = VaultBrowserControl.Home();
            string vault = VaultBrowserControl.CurrentVault();
            if (window.roots.Any(root => KnownVaults.SamePath(root, vault))) CloseVault(window, vault);
        }

        // Takes the vault out of one window, closing its tabs there.
        internal static void CloseVault(RenderWindow window, string vault)
        {
            window.roots.RemoveAll(root => KnownVaults.SamePath(root, vault));
            RemoveBrowser(window, vault);
            Sync(window);
            foreach ((TabItemControl item, TabViewControl view) in Tabs(window))
                if (TabViewControl.FileEditorOf(item)?.path is string path && Inside(vault, path)) view.CloseTab(item);

            Retitle(window);
            if (!IsOpen(vault)) Settle(vault);
        }

        // An editor window's close: the last shown one asks Close / Tray / Cancel; the main window is emptied and hidden.
        internal static void CloseWindow(RenderWindow window)
        {
            if (!Editors().Any(other => other != window && !other.hidden))
            {
                Background.Close(window);
                return;
            }

            NoteActions.SaveEdited();
            List<string> vaults = window.roots.ToList();
            if (referenced.Remove(window, out List<string>? shown)) vaults.AddRange(shown);
            window.roots.Clear();
            if (window == Engine.primary) Empty(window);
            else Engine.CloseWindow(window);

            foreach (string vault in vaults)
                if (!IsOpen(vault)) Settle(vault);
        }

        private static void Empty(RenderWindow window)
        {
            RemoveBrowsers(window);
            if (WorkspaceControl.In(window.ui.uiRoot) is WorkspaceControl workspace)
            {
                foreach (Entity child in workspace.children.ToList()) child.Destroy();
                workspace.LoadEmpty();
            }
            Retitle(window);
            Background.HideWindow(window);
        }

        // A vault no window lists: its tabs close, stickies keep a closed placeholder, and its key goes.
        private static void Settle(string vault)
        {
            foreach (RenderWindow window in Engine.windows.Values.ToList())
            {
                if (window.closeRequested) continue;
                bool sticky = IsSticky(window);
                foreach ((TabItemControl item, TabViewControl view) in Tabs(window))
                {
                    if (TabViewControl.FileEditorOf(item) is not IFileEditor editor || editor.path is not string path || !Inside(vault, path)) continue;
                    if (!sticky) view.CloseTab(item);
                    else if (editor is not ClosedVaultControl) ToClosed(item, editor, path, vault);
                }
            }

            if (VaultLock.HasLock(vault)) SheetBook.Clear();
            VaultLock.Close(vault);
            LinkIndex.Close(vault);
            Log.Info($"'{vault}' is open in no window");
        }

        private static void ToClosed(TabItemControl item, IFileEditor editor, string path, string vault)
        {
            if (editor.isDirty) editor.Save();
            SessionTab view = editor.ViewState();
            Control control = (Control)editor;
            item.RemoveChild(control);
            control.Destroy();

            ClosedVaultControl closed = new ClosedVaultControl(path, vault, view.asXml);
            closed.RestoreView(view);
            item.AddChild(closed);
        }

        // Every placeholder of the vault's files swapped back for its editor.
        internal static void Revive(string vault)
        {
            foreach (RenderWindow window in Engine.windows.Values.ToList())
            {
                if (window.closeRequested) continue;
                foreach ((TabItemControl item, TabViewControl _) in Tabs(window))
                    if (TabViewControl.FileEditorOf(item) is ClosedVaultControl closed && closed.path is string path && Inside(vault, path))
                        closed.Reopen();
            }
        }
        #endregion

        #region ---- browsers ----
        // An editor window's chrome, close, title and one browser per listed vault.
        internal static void Wire(RenderWindow window)
        {
            window.anchor = true;
            window.onClose = CloseWindow;
            if (!Background.canTray && window.ui.uiRoot?.FindByName(trayButtonName) is ButtonControl tray) tray.enabled = false;

            RemoveBrowsers(window);
            foreach (string vault in window.roots) AddBrowser(window, vault);
            Sync(window);
            Retitle(window);
        }

        private static void Retitle(RenderWindow window) =>
            Engine.SetTitle(window, window.roots.Count == 0 ? hostTitle : string.Join(", ", window.roots.Select(NameOf)));

        private static void AddBrowser(RenderWindow window, string vault, string? via = null)
        {
            if (window.ui.uiRoot?.FindByName(browsersName) is not StackPanelControl browsers) return;

            StackPanelControl header = Header(NameOf(vault), via);
            VaultBrowserControl browser = new VaultBrowserControl { vault = vault, fitContent = true, stopsContextMenu = true, padding = new Thickness(8, 0, 8, 0) };
            StackPanelControl group = new StackPanelControl { orientation = StackPanelControl.Orientation.Vertical, alpha = 0f };
            if (via != null) group.name = referencedName;
            group.AddChild(header);
            group.AddChild(browser);
            browsers.AddChild(group);
            Divide(browsers);
            browser.Rebuild();
            browsers.InvalidateLayout();
        }

        // A referenced vault this machine doesn't have: its name and a way to point at its folder.
        private static void AddMissing(RenderWindow window, string id, string alias, string via)
        {
            if (window.ui.uiRoot?.FindByName(browsersName) is not StackPanelControl browsers) return;

            StackPanelControl header = Header(alias, via);
            header.contextMenu = null;
            LabelControl state = new LabelControl { text = "not on this machine", fontSize = 12, preferredHeight = headerHeight, verticalPosition = 0.5f, margin = new Thickness(0, 0, 0, 8), role = PaletteRole.MutedInk };
            header.AddChild(state);

            ButtonControl locate = new ButtonControl
            {
                preferredHeight = 26,
                padding = new Thickness(12f, 0f),
                margin = new Thickness(4, 0, 0, 8),
                role = PaletteRole.Chrome,
                cornerRole = CornerRole.Control,
                horizontalAlignment = HorizontalAlignment.Left
            };
            locate.AddChild(new LabelControl { text = "Locate...", fontSize = 14, role = PaletteRole.MutedInk });
            locate.RegisterOnRelease(_ =>
            {
                Engine.Post(() => FolderPicker.Pick(window, Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(via)) ?? via, picked =>
                {
                    if (picked != null && !Locate(id, picked)) state.text = "that folder is a different vault";
                }));
                return true;
            });

            StackPanelControl group = new StackPanelControl { orientation = StackPanelControl.Orientation.Vertical, alpha = 0f, name = referencedName };
            group.AddChild(header);
            group.AddChild(locate);
            browsers.AddChild(group);
            Divide(browsers);
            browsers.InvalidateLayout();
        }

        private static StackPanelControl Header(string title, string? via)
        {
            StackPanelControl header = new StackPanelControl
            {
                orientation = StackPanelControl.Orientation.Horizontal,
                preferredHeight = headerHeight,
                padding = new Thickness(0, 10, 0, 10),
                contextMenu = vaultMenu,
                stopsContextMenu = true
            };
            header.PaintOr(null, PaletteRole.Chrome);
            header.AddChild(new PanelControl
            {
                preferredWidth = 3,
                preferredHeight = 12,
                margin = new Thickness(0, 7, 0, 0),
                verticalAlignment = VerticalAlignment.Center,
                role = PaletteRole.Accent,
                hitTestable = false
            });
            header.AddChild(new LabelControl
            {
                text = title,
                fontSize = 12,
                preferredHeight = headerHeight,
                verticalPosition = 0.5f,
                role = PaletteRole.Ink,
                style = FontStyle.Bold
            });
            if (via != null)
                header.AddChild(new LabelControl
                {
                    text = "via " + NameOf(via),
                    fontSize = 12,
                    preferredHeight = headerHeight,
                    verticalPosition = 0.5f,
                    margin = new Thickness(0, 0, 0, 8),
                    role = PaletteRole.MutedInk
                });
            return header;
        }

        // Rebuilds the groups of vaults the window's vaults reference, and theirs in turn; settles the ones it stopped showing.
        private static void Sync(RenderWindow window)
        {
            if (window.ui.uiRoot?.FindByName(browsersName) is not StackPanelControl browsers) return;
            foreach (Entity child in browsers.children.Where(child => child.name == referencedName).ToList()) child.Destroy();

            KnownVaults known = SettingsRegistry.Get<KnownVaults>();
            HashSet<string> seen = new HashSet<string>(window.roots.Select(root => Path.TrimEndingDirectorySeparator(root)), StringComparer.OrdinalIgnoreCase);
            HashSet<string> missing = new HashSet<string>();
            List<string> shown = new List<string>();
            Queue<string> pending = new Queue<string>(window.roots);
            while (pending.Count > 0)
            {
                string from = pending.Dequeue();
                foreach ((string id, string alias) in VaultManifest.Depends(from))
                {
                    string? vault = known.PathOf(id);
                    if (vault == null || !Directory.Exists(vault))
                    {
                        if (missing.Add(id)) AddMissing(window, id, alias, from);
                        continue;
                    }
                    if (!seen.Add(Path.TrimEndingDirectorySeparator(vault))) continue;

                    shown.Add(vault);
                    AddBrowser(window, vault, from);
                    pending.Enqueue(vault);
                }
            }

            List<string> before = referenced.GetValueOrDefault(window) ?? new List<string>();
            referenced[window] = shown;
            Divide(browsers);
            foreach (string vault in before)
                if (!IsOpen(vault)) Settle(vault);
        }

        // Re-syncs every editor window's referenced vaults.
        private static void SyncAll()
        {
            foreach (RenderWindow window in Editors().ToList()) Sync(window);
        }

        [A_XSDActionDependency("Vault.AddReference", "UI", "Picks a folder and adds it as a vault the menu's vault references")]
        public static void AddMenuReference()
        {
            RenderWindow window = VaultBrowserControl.Home();
            string vault = VaultBrowserControl.CurrentVault();
            FolderPicker.Pick(window, Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(vault)) ?? vault, picked =>
            {
                if (picked != null) AddReference(vault, picked);
            });
        }

        // Adds the picked folder as a vault this one references, under an alias from its folder name.
        internal static bool AddReference(string vault, string picked)
        {
            if (KnownVaults.SamePath(vault, picked)) return false;
            SettingsRegistry.Get<KnownVaults>().Remember(picked);
            SettingsRegistry.Commit();
            string id = VaultManifest.IdOf(picked);
            if (id.Length == 0) return false;

            string name = new string(NameOf(picked).Where(c => "#[]!/@|".IndexOf(c) < 0).ToArray()).Trim();
            if (name.Length == 0) name = "Vault";
            string alias = name;
            for (int n = 2; VaultManifest.AliasTarget(vault, alias) != null; n++) alias = $"{name} {n}";
            if (!VaultManifest.AddDependency(vault, id, alias)) return false;

            Log.Info($"'{vault}' references '{picked}' as @{alias}");
            SyncAll();
            return true;
        }

        // Takes the picked folder as the referenced vault the id names, when its manifest carries that id.
        internal static bool Locate(string id, string picked)
        {
            if (!File.Exists(VaultManifest.PathOf(picked)) || VaultManifest.IdOf(picked) != id)
            {
                Log.Warn($"'{picked}' is not the referenced vault {id}");
                return false;
            }

            KnownVaults known = SettingsRegistry.Get<KnownVaults>();
            known.vaults.RemoveAll(v => v.id == id && !Directory.Exists(v.path));
            known.Remember(picked);
            SettingsRegistry.Commit();
            SyncAll();
            return true;
        }

        private static void RemoveBrowser(RenderWindow window, string vault)
        {
            foreach (VaultBrowserControl browser in VaultBrowserControl.Browsers(window).ToList())
                if (KnownVaults.SamePath(browser.vault, vault)) (browser.parent as Control)?.Destroy();
            if (window.ui.uiRoot?.FindByName(browsersName) is StackPanelControl browsers) Divide(browsers);
        }

        // A rule above every vault group but the first.
        private static void Divide(StackPanelControl browsers)
        {
            for (int i = 0; i < browsers.children.Count; i++)
                if (browsers.children[i].children.FirstOrDefault() is Control header)
                {
                    header.edgeThickness = i == 0 ? Thickness.Zero : new Thickness(1, 0, 0, 0);
                    header.edgeRole = PaletteRole.Line;
                }
        }

        private static void RemoveBrowsers(RenderWindow window)
        {
            if (window.ui.uiRoot?.FindByName(browsersName) is not StackPanelControl browsers) return;
            foreach (Entity child in browsers.children.ToList()) child.Destroy();
        }

        internal static void RebuildBrowsers(string vault)
        {
            foreach (RenderWindow window in Editors())
                foreach (VaultBrowserControl browser in VaultBrowserControl.Browsers(window))
                    if (KnownVaults.SamePath(browser.vault, vault)) browser.Rebuild();
        }
        #endregion

        #region ---- launch ----
        // Seeds the global layout from the last vault's record and moves every sticky into the free one; once.
        internal static bool Migrate()
        {
            SessionLayout layout = SettingsRegistry.Get<SessionLayout>();
            if (layout.scopes.Any(s => s.key == workspaceScope)) return false;

            string vault = KnownVaults.Resolve(SettingsRegistry.Get<ThoriumSettings>().vault.path);
            SessionScope workspace = new SessionScope { key = workspaceScope };
            if (layout.scopes.FirstOrDefault(s => KnownVaults.SamePath(s.key, vault)) is SessionScope last)
                foreach (SessionWindow record in last.windows.Where(w => w.document != stickyDocument))
                {
                    if (record.document == editorDocument && record.roots.Count == 0) record.roots.Add(new SessionRoot { path = vault });
                    workspace.windows.Add(record);
                }

            SessionScope? free = layout.scopes.FirstOrDefault(s => s.key == Startup.freeScope);
            if (free == null) layout.scopes.Add(free = new SessionScope { key = Startup.freeScope });
            foreach (SessionScope scope in layout.scopes.Where(s => s != free))
            {
                free.windows.AddRange(scope.windows.Where(w => w.document == stickyDocument && !w.primary));
                scope.windows.RemoveAll(w => w.document == stickyDocument);
            }

            layout.scopes.Add(workspace);
            Log.Info($"moved the session of '{vault}' to the global layout");
            return true;
        }

        // Rebuilds every editor window as it was left; with none listing a vault, the main window hides and the picker opens.
        internal static void Restore(bool migrated)
        {
            string fallback = KnownVaults.Resolve(SettingsRegistry.Get<ThoriumSettings>().vault.path);
            SessionScope? recorded = SettingsRegistry.Get<SessionLayout>().scopes.FirstOrDefault(s => s.key == workspaceScope);
            restoring = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SessionRoot root in recorded?.windows.SelectMany(w => w.roots) ?? [])
                restoring.Add(Path.TrimEndingDirectorySeparator(root.path));
            if (migrated) restoring.Add(Path.TrimEndingDirectorySeparator(fallback));

            SessionLayout.Restore();
            restoring = null;
            if (migrated && Engine.primary.roots.Count == 0) Engine.primary.roots.Add(fallback);

            foreach (RenderWindow window in Editors().ToList()) Wire(window);

            if (Engine.primary.roots.Count > 0) return;
            if (Editors().Any(window => window.roots.Count > 0)) Background.HideWindow(Engine.primary);
            else StartEmpty();
        }

        // The main window empty and hidden, with the picker over it.
        internal static void StartEmpty()
        {
            Engine.primary.roots.Clear();
            WorkspaceControl.In(Engine.primary.ui.uiRoot)?.LoadEmpty();
            Wire(Engine.primary);
            if (!Engine.primary.hidden) Background.HideWindow(Engine.primary);
            if (!Background.startsHidden) Engine.Post(() => VaultsWindow.Open(Engine.primary));
        }

        internal static bool AnyLocked() =>
            Editors().SelectMany(window => window.roots).Any(vault => VaultLock.HasLock(vault) && !VaultLock.IsOpen(vault));

        // Asks for each listed locked vault's key in turn; a cancelled prompt leaves its unlock row.
        internal static void UnlockAtLaunch()
        {
            List<string> locked = Editors().SelectMany(window => window.roots)
                .Where(vault => VaultLock.HasLock(vault) && !VaultLock.IsOpen(vault))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            UnlockNext(locked, 0);
        }

        private static void UnlockNext(List<string> vaults, int index)
        {
            if (index >= vaults.Count) return;
            string vault = vaults[index];
            VaultBrowserControl.AskVaultKey(vault, "Unlock vault", null, master =>
            {
                VaultLock.Use(vault, master);
                RebuildBrowsers(vault);
                Revive(vault);
                UnlockNext(vaults, index + 1);
            }, () => UnlockNext(vaults, index + 1));
        }

        // The tray's Open: the picker when no window lists a vault, otherwise every hidden window but the emptied main one.
        internal static void TrayOpen()
        {
            bool anyVault = Editors().Any(window => window.roots.Count > 0);
            foreach (RenderWindow window in Engine.windows.Values.ToList())
            {
                if (!window.hidden || window.closeRequested) continue;
                if (window == Engine.primary && window.roots.Count == 0) continue;
                if (!anyVault && !IsSticky(window)) continue;
                Background.ShowWindow(window);
            }

            if (!anyVault)
            {
                VaultsWindow.Open(Engine.primary);
                return;
            }
            Editors().FirstOrDefault(window => !window.hidden)?.Focus();
        }
        #endregion
    }
}
