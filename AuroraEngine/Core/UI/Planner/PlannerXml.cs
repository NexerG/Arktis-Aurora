using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Security;
using ArctisAurora.Core.Users;
using System.Globalization;
using System.Xml.Linq;

namespace ArctisAurora.Core.UI.Planner
{
    // <Planner Name NameWidth><Category Id Name Color Width/><Ticket Id Name Category Color Start End AllDay TimeZone Creator Created Order Follows><Description/><Assignee User/><Attachment Kind Side Minutes/></Ticket></Planner>
    public static class PlannerXml
    {
        private const string dayFormat = "yyyy-MM-dd";
        private const string timeFormat = "yyyy-MM-ddTHH:mm";

        public static PlannerDocument Load(string path, PasswordKey? key = null)
        {
            PlannerDocument document = Parse(EncryptedFile.LoadXml(path, LoadOptions.PreserveWhitespace, ref key).Root ?? throw new Exception($"Planner '{path}' is empty."));
            document.encryption = key;
            return document;
        }

        public static void Save(PlannerDocument document, string path)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            VaultFiles.SaveXml(new XDocument(new XDeclaration("1.0", "utf-8", null), ToXml(document)), path, document.encryption, null);
        }

        #region ---- read ----
        public static PlannerDocument Parse(XElement root)
        {
            PlannerDocument document = new PlannerDocument { name = (string?)root.Attribute("Name") };
            if (float.TryParse((string?)root.Attribute("NameWidth"), NumberStyles.Float, CultureInfo.InvariantCulture, out float nameWidth))
                document.nameWidth = nameWidth;

            foreach (XElement element in root.Elements())
            {
                switch (element.Name.LocalName)
                {
                    case "Category":
                        document.categories.Add(new PlannerCategory
                        {
                            id = Id(element, "Id") ?? Guid.NewGuid(),
                            name = (string?)element.Attribute("Name") ?? "",
                            colorHex = Color(element) ?? PlannerDocument.defaultColor,
                            width = float.TryParse((string?)element.Attribute("Width"), NumberStyles.Float, CultureInfo.InvariantCulture, out float width)
                                ? width : PlannerCategory.defaultWidth
                        });
                        break;
                    case "Ticket":
                        if (ReadTicket(element) is PlannerTicket ticket) document.tickets.Add(ticket);
                        else document.extra.Add(new XElement(element));
                        break;
                    default:
                        document.extra.Add(new XElement(element));
                        break;
                }
            }

            if (document.categories.Count == 0)
                document.categories.Add(new PlannerCategory { name = "General" });
            return document;
        }

        // Null when the ticket has no readable start and end.
        private static PlannerTicket? ReadTicket(XElement element)
        {
            if (Time(element, "Start") is not DateTime start || Time(element, "End") is not DateTime end) return null;

            PlannerTicket ticket = new PlannerTicket
            {
                id = Id(element, "Id") ?? Guid.NewGuid(),
                name = (string?)element.Attribute("Name") ?? "",
                categoryId = Id(element, "Category") ?? Guid.Empty,
                colorHex = Color(element),
                time = new TimeRange(start, end < start ? start : end, (string?)element.Attribute("AllDay") == "true", (string?)element.Attribute("TimeZone")),
                creator = (string?)element.Attribute("Creator") is string creator ? User.Named(creator) : null,
                created = Time(element, "Created"),
                order = int.TryParse((string?)element.Attribute("Order"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int order) ? order : 0,
                follows = Id(element, "Follows")
            };

            foreach (XElement child in element.Elements())
            {
                if (child.Name.LocalName == "Description")
                    ticket.description = child.Value;
                else if (child.Name.LocalName == "Assignee" && (string?)child.Attribute("User") is string user && user.Length > 0)
                    ticket.assignees.Add(User.Named(user));
                else if (child.Name.LocalName == "Attachment" && ReadAttachment(child) is PlannerAttachment attachment)
                    ticket.attachments.Add(attachment);
                else ticket.extra.Add(new XElement(child));
            }
            return ticket;
        }

        // Null without a kind and a readable length.
        private static PlannerAttachment? ReadAttachment(XElement element)
        {
            if ((string?)element.Attribute("Kind") is not { Length: > 0 } kind) return null;
            if (!int.TryParse((string?)element.Attribute("Minutes"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes) || minutes <= 0) return null;
            return new PlannerAttachment(kind, (string?)element.Attribute("Side") != "After", TimeSpan.FromMinutes(minutes));
        }

        private static Guid? Id(XElement element, string attribute) =>
            Guid.TryParse((string?)element.Attribute(attribute), out Guid id) ? id : null;

        private static DateTime? Time(XElement element, string attribute) =>
            DateTime.TryParse((string?)element.Attribute(attribute), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime time) ? time : null;

        // A colour as #RRGGBB or RRGGBB; anything else is dropped.
        private static string? Color(XElement element)
        {
            string? color = (string?)element.Attribute("Color");
            if (color == null) return null;
            string digits = color.StartsWith('#') ? color[1..] : color;
            return digits.Length == 6 && digits.All(char.IsAsciiHexDigit) ? color : null;
        }
        #endregion

        #region ---- write ----
        public static XElement ToXml(PlannerDocument document)
        {
            XElement root = new XElement("Planner");
            if (document.name != null) root.SetAttributeValue("Name", document.name);
            if (document.nameWidth != PlannerDocument.defaultNameWidth)
                root.SetAttributeValue("NameWidth", document.nameWidth.ToString(CultureInfo.InvariantCulture));

            foreach (PlannerCategory category in document.categories)
            {
                XElement element = new XElement("Category",
                    new XAttribute("Id", category.id),
                    new XAttribute("Name", category.name),
                    new XAttribute("Color", category.colorHex));
                if (category.width != PlannerCategory.defaultWidth)
                    element.SetAttributeValue("Width", category.width.ToString(CultureInfo.InvariantCulture));
                root.Add(element);
            }

            foreach (PlannerTicket ticket in document.tickets)
            {
                string format = ticket.time.allDay ? dayFormat : timeFormat;
                XElement element = new XElement("Ticket",
                    new XAttribute("Id", ticket.id),
                    new XAttribute("Name", ticket.name),
                    new XAttribute("Category", ticket.categoryId));
                element.SetAttributeValue("Color", ticket.colorHex);
                element.SetAttributeValue("Start", ticket.time.start.ToString(format, CultureInfo.InvariantCulture));
                element.SetAttributeValue("End", ticket.time.end.ToString(format, CultureInfo.InvariantCulture));
                if (ticket.time.allDay) element.SetAttributeValue("AllDay", "true");
                element.SetAttributeValue("TimeZone", ticket.time.timeZone);
                element.SetAttributeValue("Creator", ticket.creator?.name);
                element.SetAttributeValue("Created", ticket.created?.ToString("s", CultureInfo.InvariantCulture));
                if (ticket.order != 0) element.SetAttributeValue("Order", ticket.order.ToString(CultureInfo.InvariantCulture));
                element.SetAttributeValue("Follows", ticket.follows);

                if (ticket.description.Length > 0) element.Add(new XElement("Description", ticket.description));
                foreach (User assignee in ticket.assignees)
                    element.Add(new XElement("Assignee", new XAttribute("User", assignee.name)));
                foreach (PlannerAttachment attachment in ticket.attachments)
                    element.Add(new XElement("Attachment",
                        new XAttribute("Kind", attachment.kind),
                        new XAttribute("Side", attachment.before ? "Before" : "After"),
                        new XAttribute("Minutes", ((int)attachment.duration.TotalMinutes).ToString(CultureInfo.InvariantCulture))));
                element.Add(ticket.extra.Select(e => new XElement(e)));
                root.Add(element);
            }

            root.Add(document.extra.Select(e => new XElement(e)));
            return root;
        }
        #endregion
    }
}
