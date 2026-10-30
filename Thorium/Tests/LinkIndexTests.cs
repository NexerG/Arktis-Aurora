using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Security;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Sheets;
using System.Xml.Linq;
using Thorium.Editor;

namespace Thorium.Tests
{
    internal static class LinkIndexTests
    {
        private static string? savedFolder;

        [A_XSDActionDependency("LinkIndex.Extract", "Test")]
        private static IEnumerator<int> Extract(TestContext t)
        {
            string vault = TempVault();
            LinkIndex index = Open(vault, out _);
            index.Refresh();

            t.Check(index.reads == 4, $"every note and sheet is read once, the manifest not: {index.reads}");
            t.Check(Kinds(index, vault, "Plan.md") == "Embed,Link,Link,Link,Math,Property", $"a markdown note's links: {Kinds(index, vault, "Plan.md")}");
            t.Check(Kinds(index, vault, "Note.axml") == "Embed,Link,Math,Property", $"an axml note's links: {Kinds(index, vault, "Note.axml")}");
            t.Check(Names(index.Outgoing(Path.Combine(vault, "Summary" + SheetDocument.extension)).Select(e => e.target)) == "Budget,Gone",
                "a sheet's formulas name the other files they read, and same-file addresses add nothing");

            Done(vault);
            yield break;
        }

        [A_XSDActionDependency("LinkIndex.Resolve", "Test")]
        private static IEnumerator<int> Resolve(TestContext t)
        {
            string vault = TempVault();
            LinkIndex index = Open(vault, out _);
            index.Refresh();

            string budget = Path.Combine(vault, "Budget" + SheetDocument.extension);
            t.Check(Names(index.Incoming(budget).Select(e => Path.GetFileName(e.source))) == "Note.axml,Plan.md,Summary.sheet.axml",
                "embeds, math and formulas all resolve to the sheet");
            t.Check(index.Incoming(Path.Combine(vault, "Note.axml")).Count(e => e.source.EndsWith("Plan.md")) == 3,
                "a property, an aliased link and a heading link all resolve to the note");
            t.Check(Names(index.Unresolved().Select(e => e.target)) == "Gone,Missing", "links to files that aren't there stay unresolved");

            SheetDocument.Blank("Gone").Save(Path.Combine(vault, "Gone" + SheetDocument.extension));
            index.Refresh();
            t.Check(index.reads == 1, $"a new file is the only one read: {index.reads}");
            t.Check(Names(index.Unresolved().Select(e => e.target)) == "Missing", "and a link naming it resolves");

            Done(vault);
            yield break;
        }

        [A_XSDActionDependency("LinkIndex.Incremental", "Test")]
        private static IEnumerator<int> Incremental(TestContext t)
        {
            string vault = TempVault();
            LinkIndex index = Open(vault, out _);
            index.Refresh();

            string plan = Path.Combine(vault, "Plan.md");
            File.AppendAllText(plan, "\n[[Note]]\n");
            index.Refresh();
            t.Check(index.reads == 1, $"an edit re-reads only that file: {index.reads}");
            t.Check(index.Outgoing(plan).Any(e => e.target == "Note" && e.kind == LinkKind.Link && e.resolved != null), "and picks up its new link");

            string note = Path.Combine(vault, "Note.axml");
            File.Delete(note);
            index.Refresh();
            t.Check(index.Outgoing(note).Count == 0 && !index.Incoming(plan).Any(), "a deleted file's links are dropped");
            t.Check(index.Unresolved().Any(e => e.target == "Note"), "and links to it become unresolved");

            Done(vault);
            yield break;
        }

        [A_XSDActionDependency("LinkIndex.Cache", "Test")]
        private static IEnumerator<int> Cache(TestContext t)
        {
            string vault = TempVault();
            LinkIndex index = Open(vault, out string id);
            index.Refresh();
            string budget = Path.Combine(vault, "Budget" + SheetDocument.extension);
            int incoming = index.Incoming(budget).Count();
            t.Check(File.Exists(Path.Combine(LinkIndex.cacheFolder!, id + ".links.axml")), "a refresh writes the cache under the vault's id");

            LinkIndex.Close(vault);
            LinkIndex again = LinkIndex.For(vault, id);
            again.Refresh();
            t.Check(again.reads == 0, $"a fresh index loads the cache and re-reads nothing: {again.reads}");
            t.Check(again.Incoming(budget).Count() == incoming, "and finds the same links");
            Done(vault);

            string locked = TempVault();
            Directory.CreateDirectory(Path.GetDirectoryName(VaultLock.LockPath(locked))!);
            File.WriteAllText(VaultLock.LockPath(locked), "<VaultLock Version=\"1\" Id=\"0123456789abcdef0123456789abcdef\" State=\"Locked\"/>\n");
            LinkIndex lockedIndex = Open(locked, out string lockedId);
            lockedIndex.Refresh();
            t.Check(lockedIndex.Incoming(Path.Combine(locked, "Budget" + SheetDocument.extension)).Any(), "a locked vault is indexed");
            t.Check(!File.Exists(Path.Combine(LinkIndex.cacheFolder!, lockedId + ".links.axml")), "but its index never reaches the disk");
            Done(locked);
            yield break;
        }

        [A_XSDActionDependency("LinkIndex.Sealed", "Test")]
        private static IEnumerator<int> Sealed(TestContext t)
        {
            string vault = TempVault();
            string secret = Path.Combine(vault, "Secret.axml");
            using (PasswordKey key = PasswordKey.Derive("pw", 1024, 1, 1, new byte[16]))
            {
                RichTextDocument document = DocumentXml.Parse(XElement.Parse("<Document><Block><Run Text=\"see [[Plan]]\"/></Block></Document>"));
                document.encryption = key;
                document.Save(secret);
            }

            LinkIndex index = Open(vault, out _);
            index.Refresh();
            t.Check(index.IsSealed(secret) && index.Outgoing(secret).Count == 0, "a password-encrypted note is recorded with no links");
            t.Check(!index.IsSealed(Path.Combine(vault, "Plan.md")), "and a plain one is not sealed");

            Done(vault);
            yield break;
        }

        private static LinkIndex Open(string vault, out string id)
        {
            SettingsRegistry.Get<KnownVaults>().Remember(vault);
            savedFolder = LinkIndex.cacheFolder;
            LinkIndex.cacheFolder = vault + "-cache";
            id = VaultManifest.IdOf(vault);
            return LinkIndex.For(vault, id);
        }

        private static void Done(string vault)
        {
            LinkIndex.Close(vault);
            LinkIndex.cacheFolder = savedFolder;
            SettingsRegistry.Get<KnownVaults>().vaults.RemoveAll(v => KnownVaults.SamePath(v.path, vault));
            Directory.Delete(vault, true);
            if (Directory.Exists(vault + "-cache")) Directory.Delete(vault + "-cache", true);
        }

        private static string Kinds(LinkIndex index, string vault, string file) =>
            string.Join(",", index.Outgoing(Path.Combine(vault, file)).Select(e => e.kind.ToString()).OrderBy(k => k, StringComparer.Ordinal));

        private static string Names(IEnumerable<string> names) =>
            string.Join(",", names.Distinct().OrderBy(n => n, StringComparer.Ordinal));

        private static string TempVault()
        {
            string vault = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"aurora-links-vault-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(vault);

            SheetDocument budget = SheetDocument.Blank("Budget");
            budget.pages[0].name = "Data";
            budget.Save(Path.Combine(vault, "Budget" + SheetDocument.extension));
            SheetDocument summary = SheetDocument.Blank("Summary");
            summary.pages[0].layers[0].Set(0, 0, "=[Budget]Data!A1+A2");
            summary.pages[0].layers[0].Set(1, 0, "=SUM([Gone]Data!A1:A2)");
            summary.Save(Path.Combine(vault, "Summary" + SheetDocument.extension));

            File.WriteAllText(Path.Combine(vault, "Plan.md"),
                "---\nup: \"[[Note]]\"\n---\nTotal ![[Budget.sheet.axml#Data!A1]] and [[Note|alias]], [[Missing]] and ![[Note#Heading]]\n\n$x = \\sheet{Budget.sheet.axml#Data!B1}$\n");
            File.WriteAllText(Path.Combine(vault, "Note.axml"),
                "<Document Frontmatter=\"---&#10;rel: '[[Plan]]'&#10;---\"><Block><Run Text=\"see [[Plan]]\"/>"
                + "<Run Sheet=\"Budget.sheet.axml#Data!A1\"/><Run Math=\"\\sheet{Budget.sheet.axml#Data!A2}\"/></Block></Document>\n");
            return vault;
        }
    }
}
