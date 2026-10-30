using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Security;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Containers.Workspace;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;
using Thorium.Editor;
using Thorium.Editor.CustomControls;

namespace Thorium.Tests
{
    internal static class VaultWindowsTests
    {
        private const string stickyDocument = "sticky-window";
        private const string editorDocument = "main";
        private const int convertTicks = 600;

        [A_XSDActionDependency("VaultWindows.TwoInOneWindow", "Test")]
        private static IEnumerator<int> TwoInOneWindow(TestContext t)
        {
            string a = TempVault();
            string b = TempVault();
            t.Show(new StackPanelControl());

            VaultWindows.OpenInNewWindow(a);
            RenderWindow? window = Listing(a).FirstOrDefault();
            t.Check(window != null && window != Engine.primary, "a vault opens in a window of its own");
            if (window == null) yield break;
            VaultWindows.OpenHere(window, b);
            yield return 3;
            t.Check(window.roots.Count == 2 && VaultBrowserControl.Browsers(window).Count() == 2, "a second vault in the same window gets a browser of its own");

            string bNote = Path.Combine(b, "note.axml");
            TabViewControl? view = TabViewControl.TabViews(window.ui.uiRoot).FirstOrDefault();
            TabItemControl tab = SessionLayout.tabFactory(bNote, false);
            view?.AddChild(tab);
            yield return 2;
            t.Check(TabViewControl.FileEditorOf(tab) is not ClosedVaultControl, "a file of a listed vault opens live");

            VaultWindows.CloseVault(window, b);
            yield return 3;
            t.Check(window.roots.Count == 1 && VaultBrowserControl.Browsers(window).Count() == 1 && !VaultWindows.IsOpen(b),
                "closing one vault leaves the other listed");
            t.Check(TabViewControl.FindOpenDocument(bNote, out _) == null, "and closes its tabs");
            TabItemControl later = SessionLayout.tabFactory(bNote, false);
            t.Check(TabViewControl.FileEditorOf(later) is ClosedVaultControl, "a file of a vault no window lists builds a closed placeholder");
            later.Destroy();

            VaultWindows.CloseWindow(window);
            yield return 3;
            t.Check(window.closeRequested && !VaultWindows.IsOpen(a), "closing the window closes its vaults");
            Forget(a);
            Forget(b);
        }

        [A_XSDActionDependency("VaultWindows.KeyFollowsWindows", "Test")]
        private static IEnumerator<int> KeyFollowsWindows(TestContext t)
        {
            string vault = TempVault();
            using (PasswordKey password = PasswordKey.Derive("pw", 1024, 1, 1, new byte[16])) VaultLock.Create(vault, password, VaultLock.NewRecovery());
            for (int i = 0; i < convertTicks && VaultLock.isConverting; i++) yield return 1;
            t.Show(new StackPanelControl());

            VaultWindows.OpenInNewWindow(vault);
            VaultWindows.OpenInNewWindow(vault);
            yield return 3;
            List<RenderWindow> windows = Listing(vault).ToList();
            t.Check(windows.Count == 2, "the same vault opens in two windows");

            string note = Path.Combine(vault, "note.axml");
            RenderWindow? sticky = TabViewControl.OpenSticky(SessionLayout.tabFactory(note, false), stickyDocument, 100, 100, Startup.freeScope);
            yield return 3;
            TabViewControl? stuck = sticky != null ? TabViewControl.TabViews(sticky.ui.uiRoot).FirstOrDefault() : null;

            if (windows.Count > 0) VaultWindows.CloseWindow(windows[0]);
            yield return 3;
            t.Check(VaultLock.IsOpen(vault) && Live(stuck), "closing one of them keeps the key and the sticky's tab");

            if (windows.Count > 1) VaultWindows.CloseWindow(windows[1]);
            yield return 3;
            t.Check(!VaultLock.IsOpen(vault), "closing the last window listing it drops the key");
            t.Check(stuck?.activeItem != null && TabViewControl.FileEditorOf(stuck.activeItem) is ClosedVaultControl, "and the sticky keeps a closed placeholder");

            byte[]? master = VaultLock.Open(vault, "pw");
            if (master != null) VaultLock.Use(vault, master);
            VaultWindows.OpenInNewWindow(vault);
            yield return 3;
            t.Check(Live(stuck) && TabViewControl.FileEditorOf(stuck!.activeItem!)?.path == Path.GetFullPath(note), "reopening the vault brings the sticky's tab back");

            foreach (RenderWindow window in Listing(vault).ToList()) VaultWindows.CloseWindow(window);
            if (sticky != null) Engine.CloseWindow(sticky);
            yield return 3;
            VaultLock.Close(vault);
            Forget(vault);
        }

        [A_XSDActionDependency("VaultWindows.RootsRecorded", "Test")]
        private static IEnumerator<int> RootsRecorded(TestContext t)
        {
            string a = TempVault();
            string b = TempVault();
            t.Show(new StackPanelControl());

            VaultWindows.OpenInNewWindow(a);
            RenderWindow? window = Listing(a).FirstOrDefault();
            if (window != null) VaultWindows.OpenHere(window, b);
            yield return 3;

            SessionLayout.Capture();
            SessionScope? recorded = SettingsRegistry.Get<SessionLayout>().scopes.FirstOrDefault(s => s.key == VaultWindows.workspaceScope);
            SessionWindow? record = recorded?.windows.FirstOrDefault(w => w.roots.Any(r => KnownVaults.SamePath(r.path, a)));
            t.Check(record != null && record.roots.Count == 2 && record.roots.Any(r => KnownVaults.SamePath(r.path, b)),
                "a window's vaults are recorded in the global layout");
            t.Check(record != null && !record.primary, "and a second editor window is not recorded as the main one");

            if (window != null) VaultWindows.CloseWindow(window);
            yield return 3;
            Forget(a);
            Forget(b);
        }

        [A_XSDActionDependency("VaultWindows.Migrate", "Test")]
        private static IEnumerator<int> Migrate(TestContext t)
        {
            SessionLayout layout = SettingsRegistry.Get<SessionLayout>();
            List<SessionScope> saved = layout.scopes;
            string vault = KnownVaults.Resolve(SettingsRegistry.Get<ThoriumSettings>().vault.path);
            layout.scopes =
            [
                new SessionScope { key = vault, windows = [new SessionWindow { document = editorDocument, primary = true }, new SessionWindow { document = stickyDocument }] },
                new SessionScope { key = "migrate-other", windows = [new SessionWindow { document = stickyDocument }] }
            ];

            bool migrated = VaultWindows.Migrate();
            SessionScope? workspace = layout.scopes.FirstOrDefault(s => s.key == VaultWindows.workspaceScope);
            SessionScope? free = layout.scopes.FirstOrDefault(s => s.key == Startup.freeScope);
            t.Check(migrated && workspace?.windows.Count == 1 && workspace.windows[0].roots.Count == 1
                && KnownVaults.SamePath(workspace.windows[0].roots[0].path, vault), "the last vault's layout becomes the global one, listing that vault");
            t.Check(free?.windows.Count == 2 && layout.scopes.Where(s => s != free).All(s => s.windows.All(w => w.document != stickyDocument)),
                "every vault's stickies move to the global stickies");
            t.Check(!VaultWindows.Migrate(), "and it runs once");

            layout.scopes = saved;
            yield break;
        }

        private static IEnumerable<RenderWindow> Listing(string vault) =>
            VaultWindows.Editors().Where(w => w != Engine.primary && w.roots.Any(r => KnownVaults.SamePath(r, vault)));

        private static bool Live(TabViewControl? view) =>
            view?.activeItem != null && TabViewControl.FileEditorOf(view.activeItem) is IFileEditor editor && editor is not ClosedVaultControl;

        private static string TempVault()
        {
            string vault = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"aurora-windows-vault-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(vault);
            File.WriteAllText(Path.Combine(vault, "note.axml"), "<Document><Block><Run Text=\"vault windows\"/></Block></Document>\n");
            SettingsRegistry.Get<KnownVaults>().Remember(vault);
            return vault;
        }

        private static void Forget(string vault)
        {
            SettingsRegistry.Get<KnownVaults>().vaults.RemoveAll(v => KnownVaults.SamePath(v.path, vault));
            Directory.Delete(vault, true);
        }
    }
}
