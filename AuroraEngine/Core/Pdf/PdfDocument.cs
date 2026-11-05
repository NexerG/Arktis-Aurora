using System.Globalization;
using System.Text;

namespace ArctisAurora.Core.Pdf
{
    // A PDF file built object by object: numbered objects, Flate streams, xref and trailer.
    public sealed class PdfDocument
    {
        private readonly List<byte[]?> objects = new List<byte[]?>();

        // An object number to fill later, so objects can refer to each other in any order.
        public int Reserve()
        {
            objects.Add(null);
            return objects.Count;
        }

        public int Add(string body)
        {
            int id = Reserve();
            Set(id, body);
            return id;
        }

        public int AddStream(string dictionary, byte[] data, bool compress = true)
        {
            int id = Reserve();
            SetStream(id, dictionary, data, compress);
            return id;
        }

        public void Set(int id, string body) => objects[id - 1] = Encoding.Latin1.GetBytes(body);

        // dictionary is the entries without the brackets; Length and Filter are added here
        public void SetStream(int id, string dictionary, byte[] data, bool compress = true)
        {
            byte[] payload = compress ? Deflate(data) : data;
            string head = $"<< {dictionary}{(compress ? " /Filter /FlateDecode" : "")} /Length {payload.Length} >>\nstream\n";
            using MemoryStream body = new MemoryStream();
            body.Write(Encoding.Latin1.GetBytes(head));
            body.Write(payload);
            body.Write(Encoding.Latin1.GetBytes("\nendstream"));
            objects[id - 1] = body.ToArray();
        }

        public void Save(string path, int catalog, int info)
        {
            using MemoryStream file = new MemoryStream();
            Write(file, "%PDF-1.7\n%âãÏÓ\n");

            long[] offsets = new long[objects.Count];
            for (int i = 0; i < objects.Count; i++)
            {
                offsets[i] = file.Position;
                Write(file, $"{i + 1} 0 obj\n");
                file.Write(objects[i] ?? Encoding.Latin1.GetBytes("null"));
                Write(file, "\nendobj\n");
            }

            long xref = file.Position;
            StringBuilder table = new StringBuilder();
            table.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
            foreach (long offset in offsets)
                table.Append($"{offset:D10} 00000 n \n");
            table.Append($"trailer\n<< /Size {objects.Count + 1} /Root {catalog} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            Write(file, table.ToString());

            File.WriteAllBytes(path, file.ToArray());
        }

        // A number as PDF writes it: invariant, at most three decimals.
        public static string Num(float value) => MathF.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);

        // A text string, UTF-16BE with its byte order mark.
        public static string Text(string text)
        {
            StringBuilder hex = new StringBuilder("<FEFF");
            foreach (char c in text) hex.Append(((int)c).ToString("X4"));
            return hex.Append('>').ToString();
        }

        public static byte[] Deflate(byte[] data)
        {
            using MemoryStream packed = new MemoryStream();
            Arctis.Imaging.Deflater.Deflate(data, packed);
            return packed.ToArray();
        }

        private static void Write(Stream stream, string text) => stream.Write(Encoding.Latin1.GetBytes(text));
    }
}
