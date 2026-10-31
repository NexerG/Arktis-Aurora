using ArctisAurora.Core.Security;
using ArctisAurora.Core.UI.Document;

namespace ArctisAurora.Core.UI.Notes
{
    // A note's headings by text, for #heading links.
    public static class NoteHeadings
    {
        public static bool IsHeading(TextStyleType styling) => styling >= TextStyleType.Heading1 && styling <= TextStyleType.Heading6;

        // The headings in reading order: the open document's when a session holds it, else the file's.
        public static string[] Of(string path)
        {
            if (NoteSessions.Find(path) is DocumentEditSession session) return FromBlocks(session.document.Blocks());
            if (!File.Exists(path) || !RichTextDocument.Opens(path) || EncryptedFile.IsEncrypted(path)) return Array.Empty<string>();

            return FromBlocks(RichTextDocument.Load(path).Blocks());
        }

        private static string[] FromBlocks(List<NoteBlock> blocks)
        {
            int count = 0;
            for (int i = 0; i < blocks.Count; i++)
                if (IsHeading(blocks[i].stylingType)) count++;

            string[] headings = new string[count];
            int at = 0;
            for (int i = 0; i < blocks.Count; i++)
                if (IsHeading(blocks[i].stylingType)) headings[at++] = (blocks[i].run.text ?? string.Empty).Trim();
            return headings;
        }
    }
}
