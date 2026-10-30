using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Registry;
using System.Xml.Linq;

namespace ArctisAurora.Core.UI.Planner
{
    [A_XSDType("AttachmentKind", "UI")]
    public class PlannerAttachmentKind
    {
        [A_XSDElementProperty("Name", "UI", "Name a ticket's attachment references this kind by.")]
        public string name = "";

        [A_XSDElementProperty("Color", "UI", "Colour of the kind's blocks, as a hex code.")]
        public string colorHex = "#8E8E93";

        [A_XSDElementProperty("Minutes", "UI", "Length an attachment of this kind gets when none is given.")]
        public int minutes = 30;
    }

    [A_XSDType("AttachmentKinds", "UI", typeof(PlannerAttachmentKind), Description = "Root container for planner attachment kinds")]
    public class PlannerAttachmentKindMap { }

    // Time attached to one side of a ticket, such as travel before it.
    public readonly record struct PlannerAttachment(string kind, bool before, TimeSpan duration);

    // The attachment kinds: the engine's, then the host's.
    public static class PlannerAttachmentKinds
    {
        private static readonly Diagnostics.LogChannel Log = Diagnostics.LogChannel.For("UI");

        public static readonly List<PlannerAttachmentKind> all = new List<PlannerAttachmentKind>();

        public static PlannerAttachmentKind? Find(string name) =>
            all.Find(kind => string.Equals(kind.name, name, StringComparison.OrdinalIgnoreCase));

        [A_XSDActionDependency("PlannerAttachmentKinds.Load", "Bootstrap")]
        public static bool Load()
        {
            all.Clear();
            if (VirtualFileSystem.TryResolveFile("XML/Documents/Engine.attachments.axml", out string engine))
                LoadFile(engine);
            if (VirtualFileSystem.TryResolveFile("XML/Documents/Attachments.attachments.axml", out string path))
                LoadFile(path);
            else Log.Debug($"no Attachments.attachments.axml found — only the engine's attachment kinds loaded.");
            return true;
        }

        // A later kind of the same name replaces the earlier one.
        private static void LoadFile(string path)
        {
            foreach (XElement element in DataFiles.LoadXml(path).Elements())
            {
                PlannerAttachmentKind kind = new PlannerAttachmentKind
                {
                    name = (string?)element.Attribute("Name") ?? "",
                    colorHex = (string?)element.Attribute("Color") ?? "#8E8E93",
                    minutes = int.TryParse((string?)element.Attribute("Minutes"), out int minutes) ? minutes : 30
                };
                if (kind.name.Length == 0) continue;
                all.RemoveAll(k => string.Equals(k.name, kind.name, StringComparison.OrdinalIgnoreCase));
                all.Add(kind);
            }
        }
    }
}
