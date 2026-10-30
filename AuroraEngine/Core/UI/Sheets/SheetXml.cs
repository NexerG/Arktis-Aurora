using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Security;
using System.Globalization;
using System.Xml.Linq;

namespace ArctisAurora.Core.UI.Sheets
{
    // <Sheet Name Fixed><Page Name Rows Columns><Column At Width/><Row At Height/><Format At Bold Fill Number/><Layer Name Visible><Cell At>text</Cell></Layer></Page></Sheet>
    public static class SheetXml
    {
        public static SheetDocument Load(string path, PasswordKey? key = null)
        {
            SheetDocument document = Parse(EncryptedFile.LoadXml(path, LoadOptions.PreserveWhitespace, ref key).Root ?? throw new Exception($"Sheet '{path}' is empty."));
            document.encryption = key;
            return document;
        }

        public static void Save(SheetDocument document, string path)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            VaultFiles.SaveXml(new XDocument(new XDeclaration("1.0", "utf-8", null), ToXml(document)), path, document.encryption, null);
        }

        #region ---- read ----
        public static SheetDocument Parse(XElement root)
        {
            SheetDocument document = new SheetDocument
            {
                name = (string?)root.Attribute("Name"),
                fixedSize = (string?)root.Attribute("Fixed") == "true"
            };

            foreach (XElement element in root.Elements())
            {
                if (element.Name.LocalName == "Page") document.pages.Add(ReadPage(element, document.fixedSize));
                else document.extra.Add(new XElement(element));
            }

            if (document.pages.Count == 0) document.pages.Add(SheetPage.Blank("Sheet 1"));
            return document;
        }

        private static SheetPage ReadPage(XElement element, bool fixedSize)
        {
            SheetPage page = new SheetPage { name = (string?)element.Attribute("Name") ?? "" };
            if (fixedSize)
            {
                page.rows = Count(element, "Rows") ?? SheetPage.defaultSize;
                page.columns = Count(element, "Columns") ?? SheetPage.defaultSize;
            }

            foreach (XElement child in element.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case "Column":
                        if (SheetDocument.TryParseAddress((string?)child.Attribute("At") ?? "", out _, out int column)
                            && column >= 0 && Number(child, "Width") is float width)
                            page.columnWidths[column] = width;
                        else page.extra.Add(new XElement(child));
                        break;
                    case "Row":
                        if (SheetDocument.TryParseAddress((string?)child.Attribute("At") ?? "", out int row, out _)
                            && row >= 0 && Number(child, "Height") is float height)
                            page.rowHeights[row] = height;
                        else page.extra.Add(new XElement(child));
                        break;
                    case "Format":
                        if (SheetDocument.TryParseAddress((string?)child.Attribute("At") ?? "", out int formatRow, out int formatColumn)
                            && formatRow >= 0 && formatColumn >= 0)
                            page.SetFormat(formatRow, formatColumn, new SheetFormat(
                                (string?)child.Attribute("Bold") == "true",
                                Fill(child),
                                (string?)child.Attribute("Number")));
                        else page.extra.Add(new XElement(child));
                        break;
                    case "Layer":
                        page.layers.Add(ReadLayer(child));
                        break;
                    default:
                        page.extra.Add(new XElement(child));
                        break;
                }
            }

            if (page.layers.Count == 0) page.layers.Add(new SheetLayer { name = "Layer 1" });
            (int rows, int columns) used = page.Used();
            page.Extend(used.rows, used.columns);
            return page;
        }

        private static SheetLayer ReadLayer(XElement element)
        {
            SheetLayer layer = new SheetLayer
            {
                name = (string?)element.Attribute("Name") ?? "",
                visible = (string?)element.Attribute("Visible") != "false"
            };

            foreach (XElement child in element.Elements())
            {
                if (child.Name.LocalName == "Cell"
                    && SheetDocument.TryParseAddress((string?)child.Attribute("At") ?? "", out int row, out int column)
                    && row >= 0 && column >= 0)
                    layer.Set(row, column, child.Value);
                else layer.extra.Add(new XElement(child));
            }

            return layer;
        }

        private static float? Number(XElement element, string attribute) =>
            float.TryParse((string?)element.Attribute(attribute), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && value > 0f
                ? value
                : null;

        private static int? Count(XElement element, string attribute) =>
            int.TryParse((string?)element.Attribute(attribute), NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0
                ? value
                : null;

        // A fill as #RRGGBB or RRGGBB; anything else is dropped.
        private static string? Fill(XElement element)
        {
            string? fill = (string?)element.Attribute("Fill");
            if (fill == null) return null;
            string digits = fill.StartsWith('#') ? fill[1..] : fill;
            return digits.Length == 6 && digits.All(char.IsAsciiHexDigit) ? fill : null;
        }
        #endregion

        #region ---- write ----
        // Cells in row-major order.
        public static XElement ToXml(SheetDocument document)
        {
            XElement root = new XElement("Sheet");
            if (document.name != null) root.SetAttributeValue("Name", document.name);
            if (document.fixedSize) root.SetAttributeValue("Fixed", "true");

            foreach (SheetPage page in document.pages)
            {
                XElement pageElement = new XElement("Page", new XAttribute("Name", page.name));
                if (document.fixedSize)
                {
                    pageElement.SetAttributeValue("Rows", page.rows.ToString(CultureInfo.InvariantCulture));
                    pageElement.SetAttributeValue("Columns", page.columns.ToString(CultureInfo.InvariantCulture));
                }

                foreach (KeyValuePair<int, float> column in page.columnWidths.OrderBy(c => c.Key))
                    pageElement.Add(new XElement("Column",
                        new XAttribute("At", SheetDocument.ColumnName(column.Key)),
                        new XAttribute("Width", column.Value.ToString(CultureInfo.InvariantCulture))));

                foreach (KeyValuePair<int, float> row in page.rowHeights.OrderBy(r => r.Key))
                    pageElement.Add(new XElement("Row",
                        new XAttribute("At", (row.Key + 1).ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("Height", row.Value.ToString(CultureInfo.InvariantCulture))));

                foreach (KeyValuePair<long, SheetFormat> format in page.formats.OrderBy(f => f.Key))
                {
                    XElement formatElement = new XElement("Format",
                        new XAttribute("At", SheetDocument.Address(SheetDocument.RowOf(format.Key), SheetDocument.ColumnOf(format.Key))));
                    if (format.Value.bold) formatElement.SetAttributeValue("Bold", "true");
                    formatElement.SetAttributeValue("Fill", format.Value.fill);
                    formatElement.SetAttributeValue("Number", format.Value.number);
                    pageElement.Add(formatElement);
                }

                foreach (SheetLayer layer in page.layers)
                {
                    XElement layerElement = new XElement("Layer", new XAttribute("Name", layer.name));
                    if (!layer.visible) layerElement.SetAttributeValue("Visible", "false");

                    foreach (KeyValuePair<long, SheetCell> cell in layer.cells.OrderBy(c => c.Key))
                        layerElement.Add(new XElement("Cell",
                            new XAttribute("At", SheetDocument.Address(SheetDocument.RowOf(cell.Key), SheetDocument.ColumnOf(cell.Key))),
                            cell.Value.raw));

                    layerElement.Add(layer.extra.Select(e => new XElement(e)));
                    pageElement.Add(layerElement);
                }

                pageElement.Add(page.extra.Select(e => new XElement(e)));
                root.Add(pageElement);
            }

            root.Add(document.extra.Select(e => new XElement(e)));
            return root;
        }
        #endregion
    }
}
