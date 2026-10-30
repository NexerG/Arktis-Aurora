using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Pdf;
using ArctisAurora.Core.Registry.Assets;
using ArctisAurora.Core.UI.Notes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Numerics;
using System.Text;
using System.Xml.Linq;

namespace ArctisAurora.Core.UI.Document
{
    // Lays a document out off screen and writes what it draws as a PDF, one page per sheet.
    public static class PdfExport
    {
        private static readonly LogChannel Log = LogChannel.For("Pdf");

        // layout zoom, and layout pixels to PDF points at that zoom
        public const float zoom = 4f;
        private const float ptPerPx = 72f / 96f / zoom;

        // Writes the tree as a PDF at target; false when it has no pages.
        public static bool Export(XElement tree, string target, string title)
        {
            (List<DrawItem> items, List<LayoutRect> pages, float footTop) = Record(tree);
            if (pages.Count == 0) return false;

            PdfDocument pdf = new PdfDocument();
            int pagesId = pdf.Reserve();
            int resourcesId = pdf.Reserve();
            Writer writer = new Writer(pdf);

            List<DrawItem>[] perPage = new List<DrawItem>[pages.Count];
            for (int i = 0; i < pages.Count; i++) perPage[i] = new List<DrawItem>();
            foreach (DrawItem item in items)
            {
                Vector2 at = item.kind == DrawKind.Glyph ? new Vector2(item.x, item.y) : new Vector2(item.x + item.width * 0.5f, item.y + item.height * 0.5f);
                int page = pages.FindIndex(p => at.Y >= p.y && at.Y < p.Bottom);
                if (page >= 0) perPage[page].Add(item);
            }
            for (int i = 0; i < pages.Count; i++)
            {
                float foot = pages[i].Bottom - footTop;
                perPage[i] = perPage[i].Where(item => item.kind != DrawKind.Glyph || item.y <= foot)
                                       .Concat(perPage[i].Where(item => item.kind == DrawKind.Glyph && item.y > foot)).ToList();
            }

            StringBuilder kids = new StringBuilder();
            for (int i = 0; i < pages.Count; i++)
            {
                LayoutRect p = pages[i];
                byte[] content = Encoding.Latin1.GetBytes(writer.Page(perPage[i], p));
                int contents = pdf.AddStream("", content);
                int page = pdf.Add($"<< /Type /Page /Parent {pagesId} 0 R /MediaBox [0 0 {PdfDocument.Num(p.width * ptPerPx)} {PdfDocument.Num(p.height * ptPerPx)}] " +
                                   $"/Resources {resourcesId} 0 R /Contents {contents} 0 R >>");
                kids.Append($"{page} 0 R ");
            }

            pdf.Set(pagesId, $"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>");
            pdf.Set(resourcesId, writer.Resources());
            int catalog = pdf.Add($"<< /Type /Catalog /Pages {pagesId} 0 R >>");
            int info = pdf.Add($"<< /Title {PdfDocument.Text(title)} /Producer {PdfDocument.Text("Aurora")} >>");
            pdf.Save(target, catalog, info);
            return true;
        }

        // Lays the tree out at the export zoom in the print palette and records what it draws, with the bottom margin's height.
        private static (List<DrawItem> items, List<LayoutRect> pages, float footTop) Record(XElement tree)
        {
            RichTextDocument document = DocumentXml.Parse(tree);
            document.readOnly = true;
            DocumentControl view = new DocumentControl
            {
                blockSpacing = document.layout.blockSpacing,
                page = document.layout.Page,
                zoom = zoom,
                document = document,
                plainText = true,
                readOnly = true,
                paletteName = "print",
                alpha = 0f
            };
            foreach (NoteNode entry in document.blocks)
            {
                if (entry is NoteTable model)
                {
                    TableControl table = new TableControl(model);
                    table.ApplyLayout(document.layout);
                    view.AddChild(table.Hosted());
                    continue;
                }
                if (entry is NoteSealed locked)
                {
                    view.AddChild(new SealedControl(locked));
                    continue;
                }
                BlockControl block = new BlockControl((NoteBlock)entry);
                block.StartEffects();
                block.ApplyLayout(document.layout);
                view.AddChild(block);
            }

            DrawRecorder recorder = new DrawRecorder();
            try
            {
                Vector2 size = view.Measure(new Vector2(float.MaxValue, float.MaxValue));
                view.Arrange(new LayoutRect(0f, 0f, size.X, size.Y));
                UIEngine.recorder = recorder;
                UIEngine.Collect(view, Control.rootDepth);
                return (recorder.items, view.PageRects().ToList(), view.page.marginBottom * PageLayout.PxPerMm * zoom);
            }
            finally
            {
                UIEngine.recorder = null;
                view.Destroy();
            }
        }

        // One file's fonts, pictures and page content.
        private sealed class Writer
        {
            private readonly PdfDocument pdf;
            private readonly Dictionary<(FontAsset, int), PdfType3Font?> faces = new Dictionary<(FontAsset, int), PdfType3Font?>();
            private readonly Dictionary<string, int> pictures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<int> fonts = new HashSet<int>();

            public Writer(PdfDocument pdf) => this.pdf = pdf;

            // One page's content stream, items in painter order.
            public string Page(List<DrawItem> items, LayoutRect page)
            {
                StringBuilder s = new StringBuilder();
                Vector4 clip = new Vector4(float.NaN);
                bool clipped = false, inText = false;
                int font = -1;
                float size = -1f;
                Vector3 color = new Vector3(-1f);

                foreach (DrawItem item in items)
                {
                    Vector4 c = new Vector4(MathF.Max(item.clip.X, page.x), MathF.Max(item.clip.Y, page.y),
                                            MathF.Min(item.clip.Z, page.Right), MathF.Min(item.clip.W, page.Bottom));
                    if (c.X >= c.Z || c.Y >= c.W) continue;

                    (int id, byte code) glyph = default;
                    if (item.kind == DrawKind.Glyph)
                    {
                        PdfType3Font? face = Face(item.font!, item.face);
                        if (face == null) continue;
                        glyph = face.Code(item.character);
                    }

                    if (c != clip)
                    {
                        if (inText) s.Append("ET\n");
                        if (clipped) s.Append("Q\n");
                        s.Append($"q {X(c.X, page)} {Y(c.W, page)} {N((c.Z - c.X) * ptPerPx)} {N((c.W - c.Y) * ptPerPx)} re W n\n");
                        clip = c;
                        clipped = true;
                        inText = false;
                        font = -1;
                        color = new Vector3(-1f);
                    }

                    if (item.kind != DrawKind.Glyph && inText)
                    {
                        s.Append("ET\n");
                        inText = false;
                    }
                    if (item.kind != DrawKind.Image && item.color != color)
                    {
                        s.Append($"{N(item.color.X)} {N(item.color.Y)} {N(item.color.Z)} rg\n");
                        color = item.color;
                    }

                    switch (item.kind)
                    {
                        case DrawKind.Glyph:
                            if (!inText)
                            {
                                s.Append("BT\n");
                                inText = true;
                                font = -1;
                            }
                            if (glyph.id != font || item.size != size)
                            {
                                s.Append($"/F{glyph.id} {N(item.size * ptPerPx)} Tf\n");
                                font = glyph.id;
                                size = item.size;
                                fonts.Add(glyph.id);
                            }
                            s.Append($"1 0 0 1 {X(item.x, page)} {Y(item.y, page)} Tm <{glyph.code:X2}> Tj\n");
                            break;
                        case DrawKind.Rect:
                            s.Append($"{X(item.x, page)} {Y(item.y + item.height, page)} {N(item.width * ptPerPx)} {N(item.height * ptPerPx)} re f\n");
                            break;
                        case DrawKind.Image:
                            int picture = Picture(item.source!);
                            if (picture == 0) break;
                            s.Append($"q {Placement(item, page)} cm /Im{picture} Do Q\n");
                            break;
                        case DrawKind.Icon:
                            s.Append(IconPath(item, page));
                            break;
                    }
                }

                if (inText) s.Append("ET\n");
                if (clipped) s.Append("Q\n");
                return s.ToString();
            }

            // Writes every font and names them with the pictures in one resource dictionary.
            public string Resources()
            {
                foreach (PdfType3Font? face in faces.Values) face?.Write();
                StringBuilder r = new StringBuilder("<< /ProcSet [/PDF /Text /ImageB /ImageC] /Font << ");
                foreach (int id in fonts) r.Append($"/F{id} {id} 0 R ");
                r.Append(">> /XObject << ");
                foreach (int id in pictures.Values.Where(id => id != 0)) r.Append($"/Im{id} {id} 0 R ");
                return r.Append(">> >>").ToString();
            }

            // The source file of one atlas face, the stamp's faces in block order, or null when it cannot be found.
            private PdfType3Font? Face(FontAsset font, int atlasFace)
            {
                if (faces.TryGetValue((font, atlasFace), out PdfType3Font? known)) return known;

                PdfType3Font? face = null;
                string stampPath = Paths.Font(font.folder, font.folder + ".import.axml");
                if (DataFiles.Exists(stampPath))
                {
                    XElement stamp = DataFiles.LoadXml(stampPath);
                    XElement? entry = null;
                    int at = 0;
                    foreach (XElement listed in stamp.Elements("Face"))
                        if (at++ == atlasFace)
                        {
                            entry = listed;
                            break;
                        }
                    string file = (string?)entry?.Attribute("Source") ?? string.Empty;
                    int index = (int?)entry?.Attribute("Index") ?? 0;
                    string shipped = Paths.Font(font.folder, file);
                    string? path = file.Length == 0 ? null : DataFiles.Exists(shipped) ? shipped : AssetImporter.ResolveSystemFont(file) ?? Beside(stamp, file);
                    if (path != null) face = new PdfType3Font(pdf, path, index);
                    else Log.Warn($"font '{font.folder}' face {atlasFace}: source '{file}' not found, its text is left out.");
                }
                else Log.Warn($"font '{font.folder}' has no import stamp, its text is left out.");

                faces.Add((font, atlasFace), face);
                return face;
            }

            // A face file in the folder of the stamp's regular source, when that source is a full path.
            private static string? Beside(XElement stamp, string file)
            {
                string source = (string?)stamp.Attribute("Source") ?? string.Empty;
                if (!Path.IsPathRooted(source)) return null;
                string path = Path.Combine(Path.GetDirectoryName(source)!, file);
                return File.Exists(path) ? path : null;
            }

            // A picture's image object, JPEG passed through, anything else decoded with its alpha as a soft mask; 0 when unreadable.
            private int Picture(string path)
            {
                if (pictures.TryGetValue(path, out int known)) return known;

                int id = 0;
                try
                {
                    string extension = Path.GetExtension(path).ToLowerInvariant();
                    byte[] bytes = VaultFiles.ReadBytes(path);
                    ImageInfo info = SixLabors.ImageSharp.Image.Identify(bytes);
                    int bits = info.PixelType.BitsPerPixel;
                    if (extension is ".jpg" or ".jpeg" && bits is 24 or 8)
                        id = pdf.AddStream($"/Type /XObject /Subtype /Image /Width {info.Width} /Height {info.Height} " +
                                           $"/ColorSpace /{(bits == 8 ? "DeviceGray" : "DeviceRGB")} /BitsPerComponent 8 /Filter /DCTDecode",
                                           bytes, false);
                    else
                        id = Decoded(bytes);
                }
                catch (Exception e)
                {
                    Log.Warn($"picture '{path}' could not be written: {e.Message}");
                }

                pictures.Add(path, id);
                return id;
            }

            private int Decoded(byte[] bytes)
            {
                using Image<Rgba32> image = SixLabors.ImageSharp.Image.Load<Rgba32>(bytes);
                int width = image.Width, height = image.Height;
                byte[] rgb = new byte[width * height * 3];
                byte[] alpha = new byte[width * height];
                bool translucent = false;
                image.ProcessPixelRows(rows =>
                {
                    for (int y = 0; y < rows.Height; y++)
                    {
                        Span<Rgba32> row = rows.GetRowSpan(y);
                        for (int x = 0; x < row.Length; x++)
                        {
                            int i = y * width + x;
                            rgb[i * 3] = row[x].R;
                            rgb[i * 3 + 1] = row[x].G;
                            rgb[i * 3 + 2] = row[x].B;
                            alpha[i] = row[x].A;
                            translucent |= row[x].A != 255;
                        }
                    }
                });

                string mask = translucent
                    ? $" /SMask {pdf.AddStream($"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceGray /BitsPerComponent 8", alpha)} 0 R"
                    : "";
                return pdf.AddStream($"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceRGB /BitsPerComponent 8{mask}", rgb);
            }

            // The unit square onto the picture's box, turned about its centre.
            private static string Placement(DrawItem item, LayoutRect page)
            {
                float turn = -2f * MathF.Atan2(item.rotation.Z, item.rotation.W);
                float cos = MathF.Cos(turn), sin = MathF.Sin(turn);
                float w = item.width * ptPerPx, h = item.height * ptPerPx;
                float cx = (item.x + item.width * 0.5f - page.x) * ptPerPx;
                float cy = (page.Bottom - item.y - item.height * 0.5f) * ptPerPx;
                float a = cos * w, b = sin * w, c = -sin * h, d = cos * h;
                return $"{N(a)} {N(b)} {N(c)} {N(d)} {N(cx - 0.5f * (a + c))} {N(cy - 0.5f * (b + d))}";
            }

            // A bullet icon as a filled path, from the default set's bullet-*.svg geometry in its 24-unit box; null for any other icon.
            private static string? IconPath(DrawItem item, LayoutRect page)
            {
                float u = item.width / 24f, v = item.height / 24f;
                string P(float x, float y) => $"{X(item.x + x * u, page)} {Y(item.y + y * v, page)}";
                string Polygon(params float[] p)
                {
                    StringBuilder path = new StringBuilder($"{P(p[0], p[1])} m ");
                    for (int i = 2; i < p.Length; i += 2) path.Append($"{P(p[i], p[i + 1])} l ");
                    return path.Append("h ").ToString();
                }
                string Circle(float r)
                {
                    float k = 0.5523f * r;
                    return $"{P(12f + r, 12f)} m {P(12f + r, 12f + k)} {P(12f + k, 12f + r)} {P(12f, 12f + r)} c "
                         + $"{P(12f - k, 12f + r)} {P(12f - r, 12f + k)} {P(12f - r, 12f)} c "
                         + $"{P(12f - r, 12f - k)} {P(12f - k, 12f - r)} {P(12f, 12f - r)} c "
                         + $"{P(12f + k, 12f - r)} {P(12f + r, 12f - k)} {P(12f + r, 12f)} c h ";
                }

                return item.source switch
                {
                    "bullet-disc" => Circle(10f) + "f\n",
                    "bullet-circle" => Circle(10f) + Circle(6f) + "f*\n",
                    "bullet-square" => Polygon(3f, 3f, 21f, 3f, 21f, 21f, 3f, 21f) + "f\n",
                    "bullet-square-outline" => Polygon(3f, 3f, 21f, 3f, 21f, 21f, 3f, 21f) + Polygon(7.5f, 7.5f, 16.5f, 7.5f, 16.5f, 16.5f, 7.5f, 16.5f) + "f*\n",
                    "bullet-triangle" => Polygon(12f, 2f, 22.4f, 20f, 1.6f, 20f) + "f\n",
                    "bullet-triangle-outline" => Polygon(12f, 2f, 22.4f, 20f, 1.6f, 20f) + Polygon(12f, 9.5f, 16.2f, 16.5f, 7.8f, 16.5f) + "f*\n",
                    _ => null
                };
            }

            private static string X(float x, LayoutRect page) => N((x - page.x) * ptPerPx);
            private static string Y(float y, LayoutRect page) => N((page.Bottom - y) * ptPerPx);
            private static string N(float value) => PdfDocument.Num(value);
        }
    }
}
