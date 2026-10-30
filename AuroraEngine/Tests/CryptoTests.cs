using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Security;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Planner;
using ArctisAurora.Core.UI.Sheets;
using Konscious.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace ArctisAurora.Tests
{
    internal static class CryptoTests
    {
        private static readonly byte[] message = Encoding.UTF8.GetBytes("<Document><Block>secret</Block></Document>");

        [A_XSDActionDependency("Crypto.RoundTrip", "Test")]
        private static IEnumerator<int> RoundTrip(TestContext t)
        {
            byte[] key = Cipher.NewKey();
            byte[] blob = Cipher.Encrypt(key, message);
            t.Check(blob.Length == message.Length + Cipher.overhead, $"a blob is the input plus {Cipher.overhead} bytes: {blob.Length}");
            t.Check(Cipher.TryDecrypt(key, blob, out byte[] back) && back.AsSpan().SequenceEqual(message), "the same key decrypts the input back");
            t.Check(!blob.AsSpan().SequenceEqual(Cipher.Encrypt(key, message)), "two encryptions of one input differ");
            t.Check(Cipher.TryDecrypt(key, Cipher.Encrypt(key, ReadOnlySpan<byte>.Empty), out byte[] empty) && empty.Length == 0, "an empty input round-trips");
            yield break;
        }

        [A_XSDActionDependency("Crypto.Rejects", "Test")]
        private static IEnumerator<int> Rejects(TestContext t)
        {
            byte[] key = Cipher.NewKey();
            byte[] blob = Cipher.Encrypt(key, message, "note"u8);
            t.Check(!Cipher.TryDecrypt(Cipher.NewKey(), blob, out _, "note"u8), "another key fails");
            t.Check(!Cipher.TryDecrypt(key, blob, out _, "sheet"u8), "other associated data fails");

            for (int i = 0; i < blob.Length; i += 7)
            {
                byte[] flipped = (byte[])blob.Clone();
                flipped[i] ^= 0x01;
                t.Check(!Cipher.TryDecrypt(key, flipped, out _, "note"u8), $"a flipped bit at byte {i} fails");
            }
            t.Check(!Cipher.TryDecrypt(key, blob.AsSpan(0, blob.Length - 1), out _, "note"u8), "a truncated blob fails");
            yield break;
        }

        [A_XSDActionDependency("Crypto.Argon2idVector", "Test")]
        private static IEnumerator<int> Argon2idVector(TestContext t)
        {
            using Argon2id argon = new Argon2id(Filled(32, 0x01))
            {
                Salt = Filled(16, 0x02),
                KnownSecret = Filled(8, 0x03),
                AssociatedData = Filled(12, 0x04),
                MemorySize = 32,
                Iterations = 3,
                DegreeOfParallelism = 4
            };
            string tag = Convert.ToHexString(argon.GetBytes(32));
            t.Check(tag == "0D640DF58D78766C08C037A34A8B53C9D01EF0452D75B65EB52520E96B01E659", $"RFC 9106 Argon2id vector: {tag}");
            yield break;
        }

        [A_XSDActionDependency("Crypto.PasswordKey", "Test")]
        private static IEnumerator<int> PasswordKeyTest(TestContext t)
        {
            using PasswordKey made = PasswordKey.Derive("correct horse", 1024, 1, 1, Filled(16, 0x07));
            XElement envelope = new XElement("Encrypted", made.Attributes());

            using PasswordKey? again = PasswordKey.FromAttributes(envelope, "correct horse");
            t.Check(again != null && again.key.AsSpan().SequenceEqual(made.key), "the same password and attributes derive the same key");

            using PasswordKey? wrong = PasswordKey.FromAttributes(envelope, "correct horse!");
            t.Check(wrong != null && !wrong.key.AsSpan().SequenceEqual(made.key), "another password derives another key");
            t.Check(PasswordKey.FromAttributes(new XElement("Encrypted"), "x") == null, "an element without parameters gives no key");

            byte[] blob = Cipher.Encrypt(made.key, message);
            t.Check(!Cipher.TryDecrypt(wrong!.key, blob, out _), "a wrong password does not open the blob");
            yield break;
        }

        [A_XSDActionDependency("Crypto.EncryptedFiles", "Test")]
        private static IEnumerator<int> EncryptedFiles(TestContext t)
        {
            string folder = Path.Combine(Path.GetTempPath(), $"aurora-crypto-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            using PasswordKey key = PasswordKey.Derive("pw", 1024, 1, 1, Filled(16, 0x09));
            try
            {
                string note = Path.Combine(folder, "secret.axml");
                RichTextDocument document = DocumentXml.Parse(XElement.Parse("<Document><Block><Run Text=\"plaintext marker\"/></Block></Document>"));
                document.encryption = key;
                document.Save(note);
                t.Check(EncryptedFile.IsEncrypted(note) && !File.ReadAllText(note).Contains("plaintext marker"), "an encrypted note holds no plaintext on disk");
                t.Check(Locked(() => RichTextDocument.Load(note)), "opening it with no key is refused as locked");
                RichTextDocument back = RichTextDocument.Load(note, key);
                t.Check(DocumentXml.ToXml(back).ToString().Contains("plaintext marker") && back.encryption == key, "the key opens it and stays with the note");
                t.Check(EncryptedFile.Unlock(note, "wrong") == null, "a wrong password does not unlock it");

                PasswordKey? opened = EncryptedFile.Unlock(note, "pw");
                t.Check(opened != null, "the right password unlocks it");
                FileKeys.Use(note, opened!, false);
                t.Check(RichTextDocument.Load(note).encryption == opened, "a key handed over once opens the next load");
                t.Check(Locked(() => RichTextDocument.Load(note)), "and only that one");
                opened!.Dispose();

                string sheetPath = Path.Combine(folder, "secret" + SheetDocument.extension);
                SheetDocument sheet = SheetDocument.Blank("Hidden sheet");
                sheet.encryption = key;
                sheet.Save(sheetPath);
                t.Check(!File.ReadAllText(sheetPath).Contains("Hidden sheet"), "an encrypted sheet holds no plaintext on disk");
                t.Check(SheetDocument.Load(sheetPath, key).name == "Hidden sheet", "and reads back with its key");

                string plannerPath = Path.Combine(folder, "secret" + PlannerDocument.extension);
                PlannerDocument planner = PlannerDocument.Blank("Hidden planner");
                planner.encryption = key;
                planner.Save(plannerPath);
                t.Check(!File.ReadAllText(plannerPath).Contains("Hidden planner"), "an encrypted planner holds no plaintext on disk");
                t.Check(PlannerDocument.Load(plannerPath, key).name == "Hidden planner", "and reads back with its key");
                t.Check(Locked(() => PlannerDocument.Load(plannerPath)), "and is locked without it");
            }
            finally
            {
                Directory.Delete(folder, true);
            }
            yield break;
        }

        [A_XSDActionDependency("Crypto.SealedBlocks", "Test")]
        private static IEnumerator<int> SealedBlocks(TestContext t)
        {
            string folder = Path.Combine(Path.GetTempPath(), $"aurora-sealed-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "sealed.axml");
            try
            {
                RichTextDocument document = DocumentXml.Parse(XElement.Parse(
                    "<Document><Block><Run Text=\"plain one\"/></Block><Block><Run Text=\"secret two\"/></Block><Block><Run Text=\"plain three\"/></Block></Document>"));
                document.SetSeals(1, [new NoteSeal(PasswordKey.Derive("pw", 1024, 1, 1, Filled(16, 0x05)))]);
                document.Save(path);
                string raw = File.ReadAllText(path);
                t.Check(raw.Contains("plain one") && raw.Contains("<Sealed") && !raw.Contains("secret"), "a sealed block is written encrypted beside plain ones");

                RichTextDocument back = RichTextDocument.Load(path);
                t.Check(back.blocks.Length == 3 && back.blocks[1] is NoteSealed && back.Blocks().Count == 2, "it reads back as one locked entry with no blocks of its own");
                NoteSealed locked = (NoteSealed)back.blocks[1];
                t.Check(NoteSeals.Unlock(locked.element, "wrong", out _) == null, "a wrong password does not open it");

                PasswordKey? key = NoteSeals.Unlock(locked.element, "pw", out byte[] plain);
                t.Check(key != null, "the right password opens it");
                if (key == null) yield break;
                NoteNode[] entries = NoteSeals.Entries(plain, key);
                t.Check(entries.Length == 1 && entries[0] is NoteBlock { run.text: "secret two" } && entries[0].seal != null, "its entries come back sealed");

                DocumentEditSession session = NoteSessions.Open(path);
                session.document.ReplaceNodes(1, 1, NoteSeals.Entries(plain, PasswordKey.Derive("pw", 1024, 1, 1, Filled(16, 0x05))));
                session.document.SplitBlockAt(new DocumentAddress(1, 6));
                session.document.InsertText(new DocumentAddress(2, 0), "more ");
                t.Check(session.document.blocks[2].seal != null && session.document.blocks[2].seal == session.document.blocks[1].seal, "a block split inside a seal stays in it");
                session.MarkDirty();
                session.Save();
                FileWriteQueue.Drain();
                raw = File.ReadAllText(path);
                t.Check(!raw.Contains("secret") && !raw.Contains("more") && raw.Contains("plain three"), "a segment write keeps the edited seal encrypted");
                NoteSessions.Close(session);

                RichTextDocument again = RichTextDocument.Load(path);
                PasswordKey? reopened = again.blocks[1] is NoteSealed lockedAgain ? NoteSeals.Unlock(lockedAgain.element, "pw", out plain) : null;
                t.Check(reopened != null && NoteSeals.Entries(plain, reopened).OfType<NoteBlock>().Select(b => b.run.text).SequenceEqual(["secret", "more  two"]),
                    "and holds the edit");
                key.Dispose();
                reopened?.Dispose();
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        private static bool Locked(Action load)
        {
            try
            {
                load();
                return false;
            }
            catch (LockedFileException)
            {
                return true;
            }
        }

        private static byte[] Filled(int length, byte value)
        {
            byte[] bytes = new byte[length];
            Array.Fill(bytes, value);
            return bytes;
        }
    }
}
