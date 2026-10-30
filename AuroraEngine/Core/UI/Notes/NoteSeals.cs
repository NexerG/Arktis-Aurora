using ArctisAurora.Core.Security;
using ArctisAurora.Core.UI.Document;
using System.Text;
using System.Xml.Linq;

namespace ArctisAurora.Core.UI.Notes
{
    // A note's password-sealed runs: <Sealed Kdf… Salt…>base64</Sealed>, the ciphertext of a <Group> of its <Block>s and <Table>s.
    public static class NoteSeals
    {
        private static readonly byte[] associated = "Aurora.Seal"u8.ToArray();

        internal static XElement Write(XNamespace ns, NoteSeal seal, NoteNode[] nodes)
        {
            XElement group = new XElement(ns + "Group", nodes.Select(node => DocumentXml.WriteNode(ns, node)));
            byte[] plain = Encoding.UTF8.GetBytes(group.ToString(SaveOptions.DisableFormatting));
            return new XElement(ns + "Sealed", seal.key.Attributes(), Convert.ToBase64String(Cipher.Encrypt(seal.key.key, plain, associated)));
        }

        // The key and the group's bytes when the password opens the element; null when it does not. Safe off the main thread.
        public static PasswordKey? Unlock(XElement element, string password, out byte[] plain)
        {
            plain = Array.Empty<byte>();
            PasswordKey? key = PasswordKey.FromAttributes(element, password);
            if (key == null) return null;
            if (Cipher.TryDecrypt(key.key, Convert.FromBase64String(element.Value), out plain, associated)) return key;

            key.Dispose();
            return null;
        }

        // The entries a decrypted group holds, each sealed under the key.
        public static NoteNode[] Entries(byte[] plain, PasswordKey key)
        {
            NoteSeal seal = new NoteSeal(key);
            List<NoteNode> nodes = new List<NoteNode>();
            foreach (XElement element in XElement.Parse(Encoding.UTF8.GetString(plain)).Elements())
                if (DocumentXml.ReadEntry(element) is NoteNode node)
                {
                    node.seal = seal;
                    nodes.Add(node);
                }
            return nodes.ToArray();
        }
    }
}
