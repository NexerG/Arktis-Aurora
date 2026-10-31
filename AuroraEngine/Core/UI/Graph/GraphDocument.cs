using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Security;
using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace ArctisAurora.Core.UI.Graph
{
    // A graph view's settings: <Graph Version Name Orphans Unresolved><Pin Path X Y/></Graph>, pins keyed by vault-relative path.
    public sealed class GraphDocument
    {
        public const string Extension = ".graph.axml";

        public string? name;
        public bool showOrphans = true;
        public bool showUnresolved = true;
        public readonly Dictionary<string, Vector2> pins = new Dictionary<string, Vector2>(StringComparer.OrdinalIgnoreCase);
        public bool unsaved;

        public static bool IsGraph(string path) => path.EndsWith(Extension,StringComparison.OrdinalIgnoreCase);

        public static GraphDocument Blank(string? name) => new GraphDocument { name = name };

        public static GraphDocument Load(string path)
        {
            PasswordKey? key = null;
            XElement root = EncryptedFile.LoadXml(path, LoadOptions.None, ref key).Root ?? throw new Exception($"Graph '{path}' is empty.");
            GraphDocument document = new GraphDocument
            {
                name = (string?)root.Attribute("Name"),
                showOrphans = (bool?)root.Attribute("Orphans") ?? true,
                showUnresolved = (bool?)root.Attribute("Unresolved") ?? true
            };
            foreach (XElement pin in root.Elements("Pin"))
                if ((string?)pin.Attribute("Path") is string file
                    && float.TryParse((string?)pin.Attribute("X"), NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    && float.TryParse((string?)pin.Attribute("Y"), NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
                    document.pins[file] = new Vector2(x, y);
            return document;
        }

        public void Save(string path)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            XElement root = new XElement("Graph",
                new XAttribute("Version", 1),
                new XAttribute("Orphans", showOrphans),
                new XAttribute("Unresolved", showUnresolved));
            if (name != null) root.Add(new XAttribute("Name", name));
            foreach ((string file, Vector2 at) in pins)
                root.Add(new XElement("Pin",
                    new XAttribute("Path", file),
                    new XAttribute("X", at.X.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("Y", at.Y.ToString(CultureInfo.InvariantCulture))));
            VaultFiles.SaveXml(new XDocument(new XDeclaration("1.0", "utf-8", null), root), path, null, null);
        }
    }
}
