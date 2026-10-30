using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Sheets;
using Thorium.Editor.CustomControls;

namespace Thorium.Tests
{
    internal static class VaultScopeTests
    {
        [A_XSDActionDependency("VaultScope.SheetInHomeVault", "Test")]
        private static IEnumerator<int> SheetInHomeVault(TestContext t)
        {
            string a = TempVault();
            string b = TempVault();
            KnownVaults known = SettingsRegistry.Get<KnownVaults>();
            known.Remember(a);
            known.Remember(b);

            string? fromA = VaultBrowserControl.FindSheet(Path.Combine(a, "note.md"), "Budget");
            string? fromB = VaultBrowserControl.FindSheet(Path.Combine(b, "note.md"), "Budget");
            t.Check(fromA != null && fromA.StartsWith(a, StringComparison.OrdinalIgnoreCase), "a reference resolves in the vault of the file holding it");
            t.Check(fromB != null && fromB.StartsWith(b, StringComparison.OrdinalIgnoreCase), "and the same name in another vault resolves there");

            known.vaults.RemoveAll(v => KnownVaults.SamePath(v.path, a) || KnownVaults.SamePath(v.path, b));
            Directory.Delete(a, true);
            Directory.Delete(b, true);
            yield break;
        }

        private static string TempVault()
        {
            string vault = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"aurora-scope-vault-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(vault);
            File.WriteAllText(Path.Combine(vault, "Budget" + SheetDocument.extension), "<Sheet/>\n");
            return vault;
        }
    }
}
