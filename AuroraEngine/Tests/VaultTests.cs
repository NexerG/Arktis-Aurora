using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Security;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Document;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Xml.Linq;

namespace ArctisAurora.Tests
{
    internal static class VaultTests
    {
        private const int convertTicks = 600;
        private const string noteMarker = "marker note";
        private const string markdownMarker = "marker markdown";
        private const string textMarker = "marker text";

        [A_XSDActionDependency("Vault.LockRoundTrip", "Test")]
        private static IEnumerator<int> LockRoundTrip(TestContext t)
        {
            string vault = MakeVault(out Dictionary<string, byte[]> originals);
            byte[] recovery = VaultLock.NewRecovery();
            using (PasswordKey password = Password()) VaultLock.Create(vault, password, recovery);
            for (int i = 0; i < convertTicks && VaultLock.isConverting; i++) yield return 1;

            t.Check(!VaultLock.isConverting && (string?)XElement.Load(VaultLock.LockPath(vault)).Attribute("State") == "Locked", "locking encrypts the whole vault");
            t.Check(VaultFilesOf(vault).All(f => EncryptedFile.IsVaultEnvelope(File.ReadAllBytes(f))), "every file is an envelope");
            t.Check(VaultFilesOf(vault).All(f => !Contains(f, "marker")), "no file holds plaintext");

            string note = Path.Combine(vault, "note.axml");
            t.Check(DocumentXml.ToXml(RichTextDocument.Load(note)).ToString().Contains(noteMarker), "a note reads through the vault key");
            t.Check(VaultFiles.ReadText(Path.Combine(vault, "page.md")).Contains(markdownMarker), "Markdown reads through it");
            t.Check(VaultFiles.ReadBytes(Path.Combine(vault, "pic.png")).AsSpan().SequenceEqual(originals["pic.png"]), "a picture reads back byte for byte");

            RichTextDocument edited = RichTextDocument.Load(note);
            edited.InsertText(new DocumentAddress(0, 0), "saved ");
            edited.Save(note);
            t.Check(EncryptedFile.IsVaultEnvelope(File.ReadAllBytes(note)) && !Contains(note, "saved"), "a save stays sealed");

            VaultLock.Close();
            t.Check(Locked(() => RichTextDocument.Load(note)) && Locked(() => VaultFiles.ReadBytes(Path.Combine(vault, "page.md"))), "a closed vault reads nothing");
            t.Check(VaultLock.Refuses(note) && Locked(() => edited.Save(note)) && !Contains(note, "saved"), "and writes nothing in plaintext");

            byte[]? master = VaultLock.Open(vault, "pw");
            t.Check(master != null, "the password opens it");
            if (master == null) yield break;
            VaultLock.Use(vault, master);
            VaultLock.Remove(vault);
            for (int i = 0; i < convertTicks && VaultLock.isConverting; i++) yield return 1;

            t.Check(!VaultLock.HasLock(vault) && !Directory.Exists(Path.Combine(vault, VaultLock.folderName)), "removing the lock drops it");
            t.Check(originals.Where(o => o.Key != "note.axml").All(o => File.ReadAllBytes(Path.Combine(vault, o.Key)).AsSpan().SequenceEqual(o.Value)),
                "and every file is back as it was");
            t.Check(Contains(note, "saved " + noteMarker), "the note keeps the edit made while locked");
            Directory.Delete(vault, true);
        }

        [A_XSDActionDependency("Vault.TwoOpen", "Test")]
        private static IEnumerator<int> TwoOpen(TestContext t)
        {
            string a = MakeVault(out _);
            string b = MakeVault(out _);
            using (PasswordKey password = Password()) VaultLock.Create(a, password, VaultLock.NewRecovery());
            for (int i = 0; i < convertTicks && VaultLock.isConverting; i++) yield return 1;
            using (PasswordKey password = Password()) VaultLock.Create(b, password, VaultLock.NewRecovery());
            for (int i = 0; i < convertTicks && VaultLock.isConverting; i++) yield return 1;

            t.Check(VaultLock.IsOpen(a) && VaultLock.IsOpen(b), "opening a second vault keeps the first open");
            byte[]? keyA = VaultLock.KeyFor(Path.Combine(a, "note.axml"));
            byte[]? keyB = VaultLock.KeyFor(Path.Combine(b, "note.axml"));
            t.Check(keyA != null && keyB != null && !keyA.AsSpan().SequenceEqual(keyB), "each file gets its own vault's key");
            t.Check(DocumentXml.ToXml(RichTextDocument.Load(Path.Combine(a, "note.axml"))).ToString().Contains(noteMarker)
                && DocumentXml.ToXml(RichTextDocument.Load(Path.Combine(b, "note.axml"))).ToString().Contains(noteMarker), "both vaults read");

            VaultLock.Close(a);
            t.Check(!VaultLock.IsOpen(a) && VaultLock.IsOpen(b), "closing one vault leaves the other open");
            t.Check(Locked(() => RichTextDocument.Load(Path.Combine(a, "note.axml"))) && VaultLock.Refuses(Path.Combine(a, "note.axml")), "the closed one reads and writes nothing");
            t.Check(VaultFiles.ReadText(Path.Combine(b, "page.md")).Contains(markdownMarker), "the open one still reads");

            VaultLock.Close();
            Directory.Delete(a, true);
            Directory.Delete(b, true);
        }

        [A_XSDActionDependency("Vault.WrongPassword", "Test")]
        private static IEnumerator<int> WrongPassword(TestContext t)
        {
            string vault = MakeVault(out _);
            byte[] recovery = VaultLock.NewRecovery();
            using (PasswordKey password = Password()) VaultLock.Create(vault, password, recovery);
            VaultLock.Close();

            byte[]? byPassword = VaultLock.Open(vault, "pw");
            string text = VaultLock.FormatRecovery(recovery);
            t.Check(text.Length == 13 * 4 + 12 && text.Split('-').All(g => g.Length == 4), $"a recovery key is 13 groups of four: {text}");
            byte[]? byRecovery = VaultLock.Open(vault, text.Replace("-", " ").ToLowerInvariant());
            t.Check(byPassword != null && byRecovery != null && byPassword.AsSpan().SequenceEqual(byRecovery), "the password and the recovery key open the same master key");
            t.Check(VaultLock.Open(vault, "nope") == null, "a wrong password opens nothing");

            byte[] other = VaultLock.NewRecovery();
            t.Check(VaultLock.Open(vault, VaultLock.FormatRecovery(other)) == null, "another recovery key opens nothing");
            t.Check(VaultLock.ParseRecovery("ABCD-EFGH") == null && VaultLock.ParseRecovery(text)!.AsSpan().SequenceEqual(recovery), "only a whole key parses");
            Directory.Delete(vault, true);
            yield break;
        }

        [A_XSDActionDependency("Vault.ConvertResumes", "Test")]
        private static IEnumerator<int> ConvertResumes(TestContext t)
        {
            string vault = MakeVault(out _);
            using (PasswordKey password = Password()) VaultLock.Create(vault, password, VaultLock.NewRecovery());
            VaultLock.Close();
            yield return 2;

            t.Check((string?)XElement.Load(VaultLock.LockPath(vault)).Attribute("State") == "Encrypting", "a cut-short lock is remembered as encrypting");
            t.Check(VaultFiles.ReadText(Path.Combine(vault, "sub", "notes.txt")).Contains(textMarker), "its unconverted files still read");

            VaultLock.Use(vault, VaultLock.Open(vault, "pw")!);
            for (int i = 0; i < convertTicks && VaultLock.isConverting; i++) yield return 1;
            t.Check(VaultFilesOf(vault).All(f => !Contains(f, "marker")) && (string?)XElement.Load(VaultLock.LockPath(vault)).Attribute("State") == "Locked",
                "the next unlock finishes it");

            VaultLock.Close();
            Directory.Delete(vault, true);
        }

        [A_XSDActionDependency("Vault.HelloSlot", "Test")]
        private static IEnumerator<int> HelloSlot(TestContext t)
        {
            string vault = MakeVault(out _);
            using (PasswordKey password = Password()) VaultLock.Create(vault, password, VaultLock.NewRecovery());
            for (int i = 0; i < convertTicks && VaultLock.isConverting; i++) yield return 1;

            byte[] signature = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            string credential = VaultLock.HelloCredential(vault);
            VaultLock.AddHello(vault, credential, signature);
            VaultLock.Close();

            byte[]? byPassword = VaultLock.Open(vault, "pw");
            byte[]? byHello = VaultLock.OpenWithHello(vault, signature);
            t.Check(VaultLock.HelloOf(vault) is (string named, string id) && named == credential && credential.EndsWith(id), "the vault names its Hello credential");
            t.Check(byPassword != null && byHello != null && byPassword.AsSpan().SequenceEqual(byHello), "the Hello signature opens the same master key");
            signature[0] ^= 1;
            t.Check(VaultLock.OpenWithHello(vault, signature) == null, "another signature opens nothing");
            Directory.Delete(vault, true);
        }

        private static string MakeVault(out Dictionary<string, byte[]> originals)
        {
            string vault = Path.Combine(Path.GetTempPath(), $"aurora-vault-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(vault, "sub"));
            File.WriteAllText(Path.Combine(vault, "note.axml"), $"<Document><Block><Run Text=\"{noteMarker}\"/></Block></Document>\n");
            File.WriteAllText(Path.Combine(vault, "page.md"), $"# Title\n\n{markdownMarker}\n");
            File.WriteAllText(Path.Combine(vault, "sub", "notes.txt"), textMarker + "\n");
            using (Image<Rgba32> image = new Image<Rgba32>(2, 2, new Rgba32(10, 20, 30, 255))) image.SaveAsPng(Path.Combine(vault, "pic.png"));

            originals = Directory.EnumerateFiles(vault, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetRelativePath(vault, f), File.ReadAllBytes);
            return vault;
        }

        private static PasswordKey Password() => PasswordKey.Derive("pw", 1024, 1, 1, new byte[16]);

        private static IEnumerable<string> VaultFilesOf(string vault) =>
            Directory.EnumerateFiles(vault, "*", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + VaultLock.folderName + Path.DirectorySeparatorChar));

        private static bool Contains(string path, string text) => File.ReadAllText(path).Contains(text);

        private static bool Locked(Action action)
        {
            try
            {
                action();
                return false;
            }
            catch (LockedFileException)
            {
                return true;
            }
        }
    }
}
