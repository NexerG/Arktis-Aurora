using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Generators;
using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Runtime.InteropServices;

namespace ArctisAurora.Core.Filing
{
    [@Serializable]
    public class AuroraFont : IDeserialize
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1), @Serializable]
        public struct FontMeta
        {
            public uint version;
            public ushort tableCount;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1), @Serializable]
        public struct TableEntry
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 4)]
            public string name;
            public uint checksum;
            public uint offset;
            public uint length;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1), @Serializable]
        public struct TextData
        {
            //[@Serializable]
            public int characterCount;
            //[@Serializable]
            public char[] characters;
        }

        [@Serializable]
        public FontMeta fontMeta;
        [@Serializable]
        public TableEntry[] tableEntries = null!;

        [@Serializable]
        public TextData textData;

        public void Deserialize(string path)
        {
            // meta data
            byte[] fontMetaBuffer = new byte[Marshal.SizeOf<FontMeta>()];
            using (FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                fileStream.Read(fontMetaBuffer);
                fileStream.Close();
            }

            GCHandle handleMeta = GCHandle.Alloc(fontMetaBuffer, GCHandleType.Pinned);
            fontMeta = Marshal.PtrToStructure<FontMeta>(handleMeta.AddrOfPinnedObject());
            handleMeta.Free();

            // table entries
            tableEntries = new TableEntry[fontMeta.tableCount];
            byte[] tableEntryBuffer = new byte[(Marshal.SizeOf<TableEntry>() + sizeof(int)) * fontMeta.tableCount];

            using (FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                fileStream.Seek(Marshal.SizeOf<FontMeta>() + sizeof(int), SeekOrigin.Begin);
                fileStream.Read(tableEntryBuffer, 0, tableEntryBuffer.Length);
            }
            GCHandle handleTables = GCHandle.Alloc(tableEntryBuffer, GCHandleType.Pinned);
            for (int i = 0; i < fontMeta.tableCount; i++)
            {
                IntPtr entryPtr = handleTables.AddrOfPinnedObject() + (i * (Marshal.SizeOf<TableEntry>()) + sizeof(int));
                tableEntries[i] = Marshal.PtrToStructure<TableEntry>(entryPtr);
            }
            handleTables.Free();

            // character data
            textData = new TextData();
            using (FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                long offset = Marshal.SizeOf<FontMeta>() + (Marshal.SizeOf<TableEntry>() * fontMeta.tableCount);
                fileStream.Seek(offset, SeekOrigin.Begin);

                byte[] characterCount = new byte[Marshal.SizeOf<int>()];
                fileStream.Read(characterCount, 0, characterCount.Length);

                GCHandle handleTextData = GCHandle.Alloc(characterCount, GCHandleType.Pinned);
                textData.characterCount = Marshal.PtrToStructure<int>(handleTextData.AddrOfPinnedObject());
                handleTextData.Free();

                // Read characters
                BinaryReader reader = new BinaryReader(fileStream, System.Text.Encoding.Unicode);
                textData.characters = new char[textData.characterCount];
                for (int i = 0; i < textData.characterCount; i++)
                {
                    textData.characters[i] = reader.ReadChar();
                }
            }
        }

        // faceKeys names each face in block order as variant * 4 + style, the plain regular first; variantNames
        // are joined by '\n' with the plain family's empty name first.
        internal static void GenerateGlyphAtlas(AuroraFont[] faces, string[] facePaths, int[] faceIndices, int[] faceKeys,
            string variantNames, string baseName, int perGlyphSize, string outputRoot)
        {
            // One array per face. Contours live here and never reach the .agd.
            Glyph[][] faceGlyphs = new Glyph[faces.Length][];
            Diagnostics.Profiling.Zone.Start("Atlas.ReadFaces");
            for (int f = 0; f < faces.Length; f++)
                faceGlyphs[f] = ReadFaceGlyphs(faces[f], facePaths[f]);
            Diagnostics.Profiling.Zone.End("Atlas.ReadFaces");

            AtlasMetaData glyphs = new AtlasMetaData();
            glyphs.glyphCount = faces[0].textData.characterCount;
            glyphs.chars = faces[0].textData.characters;
            glyphs.glyphs = faceGlyphs[0];
            glyphs.faceKeys = faceKeys;
            glyphs.variantNames = variantNames.ToCharArray();
            glyphs.pxRange = MTSDFGen.PxRange;
            glyphs.cellSize = perGlyphSize;

            // Every face measures its own advances and ink boxes; the first face's stay on its glyphs.
            int count = glyphs.glyphCount;
            glyphs.faceMetrics = new GlyphMetrics[(faces.Length - 1) * count];
            for (int f = 1; f < faces.Length; f++)
                for (int i = 0; i < count; i++)
                    glyphs.faceMetrics[(f - 1) * count + i] = faceGlyphs[f][i].regular;
            glyphs.ReadKerning(facePaths, faceIndices);

            string atlasDataPath = Path.Combine(outputRoot, baseName, $"{baseName}.agd"); // aurora glyph data
            Diagnostics.Profiling.Zone.Start("Atlas.WriteMeta");
            Serializer.SerializeAttributed(glyphs, atlasDataPath);
            Diagnostics.Profiling.Zone.End("Atlas.WriteMeta");

            // One cell per (character, face), laid out as consecutive per-face blocks.
            int cellCount = glyphs.glyphCount * faces.Length;
            int glyphsPerAxis = (int)Math.Ceiling(MathF.Sqrt(cellCount));
            Image<Rgba32> atlasImage = new Image<Rgba32>(perGlyphSize * glyphsPerAxis, perGlyphSize * glyphsPerAxis);
            Diagnostics.Profiling.Zone.Start("Atlas.Cells");
            for (int cell = 0; cell < cellCount; cell++)
            {
                Glyph g = faceGlyphs[cell / glyphs.glyphCount][cell % glyphs.glyphCount];
                if (g == null)
                    continue;

                int x = cell % glyphsPerAxis * perGlyphSize;
                int y = cell / glyphsPerAxis * perGlyphSize;
                MTSDFGen.GenerateCell(g, atlasImage, x, y, perGlyphSize, MTSDFGen.PxRange);
                Diagnostics.Profiling.Zone.Increment("Cell");
            }
            Diagnostics.Profiling.Zone.End("Atlas.Cells");

            Diagnostics.Profiling.Zone.Start("Atlas.SavePng");
            atlasImage.Save(Path.Combine(outputRoot, baseName, $"{baseName}_atlas.png"));
            Diagnostics.Profiling.Zone.End("Atlas.SavePng");
        }

        // Reads one face's outlines and metrics into its own glyph array.
        internal static Glyph[] ReadFaceGlyphs(AuroraFont fontData, string path)
        {
            Glyph[] faceGlyphs;
            using (BinaryReader reader = new BinaryReader(DataFiles.Open(path)))
            {
                TableEntry maxp = fontData.tableEntries.First(t => t.name == "maxp");
                reader.BaseStream.Position = maxp.offset + 4;
                uint numGlyphs = AssetImporter.ReadUInt16BE(reader);

                uint[] glyphOffsets = new uint[numGlyphs + 1];
                TableEntry headTable = fontData.tableEntries.First(t => t.name == "head"); // 'head'

                reader.BaseStream.Position = headTable.offset + 18; // Offset 18 in 'head' is unitsPerEm
                ushort unitsPerEm = AssetImporter.ReadUInt16BE(reader);

                // CFF outlines, or TrueType's loca + glyf
                TableEntry cffTable = fontData.tableEntries.FirstOrDefault(t => t.name.TrimEnd() == "CFF");
                bool cff = cffTable.name != null;
                (byte[][] charStrings, byte[][] global, byte[][] local) cffData = default;
                TableEntry glyf = default;
                if (cff) cffData = CffOutlines.Read(reader, cffTable);
                else
                {
                    reader.BaseStream.Position = headTable.offset + 50; // Offset 50 in 'head'
                    uint indexToLocFormat = AssetImporter.ReadUInt16BE(reader); // 0 = uint16, 1 = uint32

                    TableEntry locaTable = fontData.tableEntries.First(t => t.name == "loca"); // 'loca'
                    reader.BaseStream.Position = locaTable.offset;
                    for (int i = 0; i <= numGlyphs; i++)
                    {
                        if (indexToLocFormat == 0)
                            glyphOffsets[i] = (uint)(AssetImporter.ReadUInt16BE(reader) * 2); // 16-bit → scale ×2
                        else
                            glyphOffsets[i] = AssetImporter.ReadUInt32BE(reader); // 32-bit
                    }
                    glyf = fontData.tableEntries.First(t => t.name == "glyf"); // for glyph outlines
                }

                // loading glyph outlines
                TableEntry cmap = fontData.tableEntries.First(t => t.name == "cmap"); // for index

                faceGlyphs = new Glyph[fontData.textData.characterCount];
                for (int i = 0; i < fontData.textData.characterCount; i++)
                {
                    char character = fontData.textData.characters[i];
                    ushort glyphIndex = GetGlyphIndex(character, reader, cmap);
                    Glyph glyph = cff
                        ? GetCffGlyph(glyphIndex, cffData, unitsPerEm)
                        : GetGlyphOutline(glyphIndex, glyphOffsets, glyf, reader, unitsPerEm);
                    faceGlyphs[i] = glyph;
                }

                //loading distances between glyphs
                TableEntry hhea = fontData.tableEntries.First(t => t.name == "hhea");
                reader.BaseStream.Position = hhea.offset + 34;
                ushort numberOfHMetrics = AssetImporter.ReadUInt16BE(reader);

                TableEntry hmtx = fontData.tableEntries.First(t => t.name == "hmtx"); // for distances between glyphs
                ushort[] advanceWidth = new ushort[numGlyphs];
                short[] lsb = new short[numGlyphs];
                reader.BaseStream.Position = hmtx.offset;
                for (int i = 0; i < numberOfHMetrics; i++)
                {
                    advanceWidth[i] = AssetImporter.ReadUInt16BE(reader);
                    lsb[i] = AssetImporter.ReadInt16BE(reader);
                }

                for (int i = numberOfHMetrics; i < numGlyphs ; i++)
                {
                    lsb[i] = AssetImporter.ReadInt16BE(reader);
                    advanceWidth[i] = advanceWidth[numberOfHMetrics - 1];
                }

                //TableEntry vhea = fontData.tableEntries.First(t => t.name == "vhea");
                //reader.BaseStream.Position = vhea.offset + 34;
                //ushort numberOfVMetrics = AssetImporter.ReadUInt16BE(reader);
                //
                //TableEntry vmtx = fontData.tableEntries.First(t => t.name == "vmtx");
                //ushort[] bsb = new ushort[numberOfVMetrics];
                //short[] tsb = new short[numGlyphs];
                //reader.BaseStream.Position = vmtx.offset;
                //for (int i = 0; i < numGlyphs; i++)
                //{
                //    bsb[i] = AssetImporter.ReadUInt16BE(reader);
                //    tsb[i] = AssetImporter.ReadInt16BE(reader);
                //}
                //for (int i = numberOfVMetrics; i < numGlyphs; i++)
                //{
                //    tsb[i] = AssetImporter.ReadInt16BE(reader);
                //}

                reader.BaseStream.Position = hhea.offset + 4; // ascender is at offset 4
                short ascender = AssetImporter.ReadInt16BE(reader);
                reader.BaseStream.Position = hhea.offset + 6;
                short descender = AssetImporter.ReadInt16BE(reader);
                float lineHeight = 0.1f;

                for (int i = 0; i < faceGlyphs.Length; i++)
                {
                    char character = fontData.textData.characters[i];
                    ushort glyphIndex = GetGlyphIndex(character, reader, cmap);
                    faceGlyphs[i].regular.advanceWidth = (float)advanceWidth[glyphIndex] / unitsPerEm;
                    faceGlyphs[i].regular.leftSideOffset = (float)lsb[glyphIndex] / unitsPerEm;
                    if(faceGlyphs[i].regular.yMin < 0)
                    {
                        faceGlyphs[i].regular.tsb = -(faceGlyphs[i].regular.yMin) / (float)unitsPerEm;
                    }
                    if (faceGlyphs[i].contours.Count == 0)
                    {
                        faceGlyphs[i].regular.glyphHeight = lineHeight;
                        faceGlyphs[i].regular.glyphWidth = faceGlyphs[i].regular.advanceWidth;
                    }
                }
            }
            return faceGlyphs;
        }

        internal static ushort GetGlyphIndex(char character, BinaryReader reader, TableEntry cmap)
        {
            reader.BaseStream.Position = cmap.offset;

            ushort version = AssetImporter.ReadUInt16BE(reader);
            ushort numSubtables = AssetImporter.ReadUInt16BE(reader);

            // Search for Unicode BMP subtable (PlatformID=3, EncodingID=1)
            for (int i = 0; i < numSubtables; i++)
            {
                ushort platformID = AssetImporter.ReadUInt16BE(reader);
                ushort encodingID = AssetImporter.ReadUInt16BE(reader);
                uint subtableOffset = AssetImporter.ReadUInt32BE(reader);

                if (platformID == 3 && encodingID == 1) // Windows Unicode
                {
                    long savedPos = reader.BaseStream.Position;
                    reader.BaseStream.Position = cmap.offset + subtableOffset;

                    ushort format = AssetImporter.ReadUInt16BE(reader);
                    if (format == 4) // Format 4 (segmented mapping)
                    {
                        ushort length = AssetImporter.ReadUInt16BE(reader);
                        ushort language = AssetImporter.ReadUInt16BE(reader);
                        ushort segCountX2 = AssetImporter.ReadUInt16BE(reader);
                        ushort segCount = (ushort)(segCountX2 / 2);
                        ushort searchRange = AssetImporter.ReadUInt16BE(reader);
                        ushort entrySelector = AssetImporter.ReadUInt16BE(reader);
                        ushort rangeShift = AssetImporter.ReadUInt16BE(reader);

                        // Read segmentation data
                        ushort[] endCodes = new ushort[segCount];
                        for (int j = 0; j < segCount; j++) endCodes[j] = AssetImporter.ReadUInt16BE(reader);

                        ushort reservedPad = AssetImporter.ReadUInt16BE(reader);

                        ushort[] startCodes = new ushort[segCount];
                        for (int j = 0; j < segCount; j++) startCodes[j] = AssetImporter.ReadUInt16BE(reader);

                        short[] idDeltas = new short[segCount];
                        for (int j = 0; j < segCount; j++) idDeltas[j] = (short)AssetImporter.ReadUInt16BE(reader);

                        long idRangeOffsetStart = reader.BaseStream.Position;
                        ushort[] idRangeOffsets = new ushort[segCount];
                        for (int j = 0; j < segCount; j++) idRangeOffsets[j] = AssetImporter.ReadUInt16BE(reader);

                        // Find the segment that actually COVERS this character. The previous version
                        // took segment 0 unconditionally, which happens to be right for basic Latin
                        // (Arial's first segment) and wrong for everything else — every accented
                        // character resolved to a nearby but incorrect glyph.
                        for (int j = 0; j < segCount; j++)
                        {
                            if (character > endCodes[j]) continue;
                            if (character < startCodes[j]) return 0;   // gap between segments

                            if (idRangeOffsets[j] == 0)
                            {
                                return (ushort)((character + idDeltas[j]) % 65536);
                            }
                            else
                            {
                                // glyphIndexAddress = &idRangeOffset[j] + idRangeOffset[j] + 2*(c - start).
                                // idRangeOffsetStart + j*2 IS &idRangeOffset[j], so no further
                                // correction belongs here — the old "- (segCount - j)" term walked
                                // back past the array a second time and read the wrong id.
                                long glyphOffset = idRangeOffsetStart + j * 2
                                    + idRangeOffsets[j]
                                    + 2 * (character - startCodes[j]);

                                long saved = reader.BaseStream.Position;
                                reader.BaseStream.Position = glyphOffset;
                                ushort glyphIndex = AssetImporter.ReadUInt16BE(reader);
                                reader.BaseStream.Position = saved;

                                if (glyphIndex != 0)
                                    return (ushort)((glyphIndex + idDeltas[j]) % 65536);
                                else
                                    return 0;
                            }
                        }
                    }
                    reader.BaseStream.Position = savedPos;
                }
            }
            return 0;   // .notdef — a face that lacks a character must not fail the family's bake
        }

        // TrueType stores accented characters as COMPOSITE glyphs: numContours < 0, and the entry
        // references other glyphs with their own offsets and transforms. Components must therefore be
        // assembled in shared font units and normalised once against the composite's own bbox, which
        // is why reading and normalising are separate steps here.
        private const int MaxCompositeDepth = 5;

        private static float ReadF2Dot14(BinaryReader reader) => AssetImporter.ReadInt16BE(reader) / 16384f;

        // Appends a glyph's contours in FONT UNITS, mapped through [a c; b d] + (dx, dy).
        private static void AppendGlyphContours(ushort glyphIndex, uint[] glyphOffsets, TableEntry glyfTable,
            BinaryReader reader, List<Bezier> dest,
            float a, float b, float c, float d, float dx, float dy, int depth)
        {
            if (glyphIndex + 1 >= glyphOffsets.Length) return;

            uint start = glyphOffsets[glyphIndex];
            if (start == glyphOffsets[glyphIndex + 1]) return;   // no outline (space)

            reader.BaseStream.Position = glyfTable.offset + start;
            short numContours = AssetImporter.ReadInt16BE(reader);
            reader.BaseStream.Position += 8;                     // this glyph's own bbox

            if (numContours < 0)
            {
                if (depth >= MaxCompositeDepth) return;          // cyclic or malformed font
                AppendComposite(glyphOffsets, glyfTable, reader, dest, a, b, c, d, dx, dy, depth);
                return;
            }
            if (numContours == 0) return;

            ushort[] endPts = new ushort[numContours];
            for (int i = 0; i < numContours; i++)
                endPts[i] = AssetImporter.ReadUInt16BE(reader);

            int pointCount = endPts[numContours - 1] + 1;
            // Two statements on purpose: `Position += Read...()` reads Position BEFORE the
            // call advances it, which silently rewinds 2 bytes and misaligns every read after.
            ushort instructionLength = AssetImporter.ReadUInt16BE(reader);
            reader.BaseStream.Position += instructionLength;

            byte[] flags = new byte[pointCount];
            for (int i = 0; i < pointCount; i++)
            {
                flags[i] = reader.ReadByte();
                if ((flags[i] & 0x08) == 0) continue;
                byte repeat = reader.ReadByte();
                for (int j = 1; j <= repeat && i + j < pointCount; j++)
                    flags[i + j] = flags[i];
                i += repeat;
            }

            short[] xs = new short[pointCount];
            short x = 0;
            for (int i = 0; i < pointCount; i++)
            {
                if ((flags[i] & 0x02) != 0)
                {
                    byte delta = reader.ReadByte();
                    x += (flags[i] & 0x10) != 0 ? delta : (short)-delta;
                }
                else if ((flags[i] & 0x10) == 0)
                    x += AssetImporter.ReadInt16BE(reader);
                xs[i] = x;
            }

            short[] ys = new short[pointCount];
            short y = 0;
            for (int i = 0; i < pointCount; i++)
            {
                if ((flags[i] & 0x04) != 0)
                {
                    byte delta = reader.ReadByte();
                    y += (flags[i] & 0x20) != 0 ? delta : (short)-delta;
                }
                else if ((flags[i] & 0x20) == 0)
                    y += AssetImporter.ReadInt16BE(reader);
                ys[i] = y;
            }

            int p = 0;
            for (int con = 0; con < numContours; con++)
            {
                Bezier bezier = new Bezier();
                for (; p <= endPts[con] && p < pointCount; p++)
                {
                    float fx = a * xs[p] + c * ys[p] + dx;
                    float fy = b * xs[p] + d * ys[p] + dy;
                    bezier.points.Add(new Bezier.Point(new Vector2(fx, fy), (flags[p] & 0x01) != 0));
                }
                dest.Add(bezier);
            }
        }

        // Reader is positioned at the first component record.
        private static void AppendComposite(uint[] glyphOffsets, TableEntry glyfTable, BinaryReader reader,
            List<Bezier> dest, float a, float b, float c, float d, float dx, float dy, int depth)
        {
            while (true)
            {
                ushort flags = AssetImporter.ReadUInt16BE(reader);
                ushort componentIndex = AssetImporter.ReadUInt16BE(reader);

                float arg1, arg2;
                if ((flags & 0x0001) != 0)   // ARG_1_AND_2_ARE_WORDS
                {
                    arg1 = AssetImporter.ReadInt16BE(reader);
                    arg2 = AssetImporter.ReadInt16BE(reader);
                }
                else
                {
                    arg1 = (sbyte)reader.ReadByte();
                    arg2 = (sbyte)reader.ReadByte();
                }

                float ca = 1f, cb = 0f, cc = 0f, cd = 1f;
                if ((flags & 0x0008) != 0)        // WE_HAVE_A_SCALE
                    ca = cd = ReadF2Dot14(reader);
                else if ((flags & 0x0040) != 0)   // WE_HAVE_AN_X_AND_Y_SCALE
                {
                    ca = ReadF2Dot14(reader);
                    cd = ReadF2Dot14(reader);
                }
                else if ((flags & 0x0080) != 0)   // WE_HAVE_A_TWO_BY_TWO
                {
                    ca = ReadF2Dot14(reader);
                    cb = ReadF2Dot14(reader);
                    cc = ReadF2Dot14(reader);
                    cd = ReadF2Dot14(reader);
                }

                // Point-matching placement (ARGS_ARE_XY_VALUES clear) is rare and unsupported; such a
                // component lands at the origin rather than being dropped entirely.
                float cdx = 0f, cdy = 0f;
                if ((flags & 0x0002) != 0)        // ARGS_ARE_XY_VALUES
                {
                    cdx = arg1;
                    cdy = arg2;
                }

                float na = a * ca + c * cb;
                float nb = b * ca + d * cb;
                float nc = a * cc + c * cd;
                float nd = b * cc + d * cd;
                float ndx = a * cdx + c * cdy + dx;
                float ndy = b * cdx + d * cdy + dy;

                long resume = reader.BaseStream.Position;
                AppendGlyphContours(componentIndex, glyphOffsets, glyfTable, reader, dest, na, nb, nc, nd, ndx, ndy, depth + 1);
                reader.BaseStream.Position = resume;

                if ((flags & 0x0020) == 0) break;   // MORE_COMPONENTS
            }
        }

        private static Glyph GetGlyphOutline(ushort glyphIndex, uint[] glyphOffsets, TableEntry glyfTable, BinaryReader reader, float unitsPerEm)
        {
            if (glyphIndex + 1 >= glyphOffsets.Length)
                return new Glyph();

            uint start = glyphOffsets[glyphIndex];
            if (start == glyphOffsets[glyphIndex + 1])
                return new Glyph();

            reader.BaseStream.Position = glyfTable.offset + start;

            short numContours = AssetImporter.ReadInt16BE(reader);
            short xMin = AssetImporter.ReadInt16BE(reader);
            short yMin = AssetImporter.ReadInt16BE(reader);
            short xMax = AssetImporter.ReadInt16BE(reader);
            short yMax = AssetImporter.ReadInt16BE(reader);

            Glyph glyph = new Glyph();
            glyph.SetParams(xMin, xMax, yMin, yMax, unitsPerEm);

            AppendGlyphContours(glyphIndex, glyphOffsets, glyfTable, reader, glyph.contours, 1f, 0f, 0f, 1f, 0f, 0f, 0);
            Normalise(glyph, xMin, yMin, xMax, yMax);
            return glyph;
        }

        private static Glyph GetCffGlyph(ushort glyphIndex, (byte[][] charStrings, byte[][] global, byte[][] local) cff, float unitsPerEm)
        {
            if (glyphIndex >= cff.charStrings.Length)
                return new Glyph();

            List<Bezier> contours = CffOutlines.Interpret(cff.charStrings[glyphIndex], cff.global, cff.local);
            if (contours.Count == 0)
                return new Glyph();

            (float x0, float y0, float x1, float y1) = CffOutlines.Bounds(contours);
            short xMin = (short)MathF.Floor(x0), yMin = (short)MathF.Floor(y0);
            short xMax = (short)MathF.Ceiling(x1), yMax = (short)MathF.Ceiling(y1);

            Glyph glyph = new Glyph { contours = contours };
            glyph.SetParams(xMin, xMax, yMin, yMax, unitsPerEm);
            Normalise(glyph, xMin, yMin, xMax, yMax);
            return glyph;
        }

        // Contours into the unit box over the ink, then edges built and coloured for the generator.
        private static void Normalise(Glyph glyph, short xMin, short yMin, short xMax, short yMax)
        {
            float coordScale = MathF.Max(xMax - xMin, yMax - yMin);
            if (coordScale <= 0f) coordScale = 1f;
            foreach (Bezier bezier in glyph.contours)
                foreach (Bezier.Point point in bezier.points)
                {
                    point.SetX((point.pos.X - xMin) / coordScale);
                    point.SetY((point.pos.Y - yMin) / coordScale);
                }

            glyph.BuildEdges();
            MTSDFGen.ColorEdges(glyph);
        }

    }

    [@Serializable]
    public class AtlasMetaData : IDeserialize
    {
        [@Serializable]
        public int glyphCount;
        [@Serializable]
        public char[] chars = null!;
        [@Serializable]
        public Glyph[] glyphs = null!;
        [@Serializable]
        public float pxRange;
        [@Serializable]
        public int cellSize;

        // the baked faces in block order as variant * 4 + style, face 0 the plain regular; variant names joined by
        // '\n', the plain family's empty name first; faces after the first keep glyph i's metrics at (f - 1) * glyphCount + i
        [@Serializable]
        public int[] faceKeys = { 0 };
        [@Serializable]
        public char[] variantNames = Array.Empty<char>();
        [@Serializable]
        public GlyphMetrics[] faceMetrics = Array.Empty<GlyphMetrics>();

        // pair kerning per face: keys a << 16 | b and em values at kernStarts[face]..kernStarts[face + 1]; last, so an
        // older .agd reads them as empty
        [@Serializable]
        public uint[] kernKeys = Array.Empty<uint>();
        [@Serializable]
        public float[] kernValues = Array.Empty<float>();
        [@Serializable]
        public int[] kernStarts = Array.Empty<int>();

        // (face << 32 | a << 16 | b) to em, built on load
        [NonSerializable]
        private Dictionary<ulong, float> kerns = noKerns;
        private static readonly Dictionary<ulong, float> noKerns = new Dictionary<ulong, float>();

        // char to index into chars and glyphs, built on load
        [NonSerializable]
        private Dictionary<char, int> charIndex = null!;
        private static readonly CharHash charHash = new CharHash();

        // em advances of the chars below AdvanceTableSize, one row per face, built on load
        public const int AdvanceTableSize = 256;
        [NonSerializable]
        private float[] advances = null!;

        // variant * 4 + style to face, -1 where the bake found none, and the variant names, built on load
        [NonSerializable]
        private int[] slots = null!;
        [NonSerializable]
        public string[] variants = null!;

        private sealed class CharHash : IEqualityComparer<char>
        {
            public bool Equals(char a, char b) => a == b;
            public int GetHashCode(char c) => c;
        }

        public int faceCount => faceKeys.Length;

        public int cellCount => glyphCount * faceCount;

        // cell quad geometry around the ink box
        public float inkMargin => MTSDFGen.InkMargin(cellSize);
        public float cellScale => 1f / (1f - 2f * inkMargin);

        // The face a variant draws a style in: its own, else the plain family's, else its regular, else regular.
        public int Effective(int variant, FontStyle style)
        {
            int own = slots[variant * 4 + (int)style];
            if (own >= 0) return own;
            int plain = slots[(int)style];
            if (plain >= 0) return plain;
            int upright = slots[variant * 4];
            return upright >= 0 ? upright : 0;
        }

        public bool HasFace(int variant, FontStyle style) => slots[variant * 4 + (int)style] >= 0;

        // A variant by name, 0 (the plain family) for none or a name this font lacks.
        public int VariantOf(string? name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            string[] names = variants;
            for (int v = 1; v < names.Length; v++)
                if (string.Equals(names[v], name, StringComparison.OrdinalIgnoreCase)) return v;
            return 0;
        }

        public GlyphMetrics Metrics(Glyph glyph, int face) => face == 0 ? glyph.regular : faceMetrics[(face - 1) * glyphCount + glyph.index];

        public int CellIndex(int charIndex, int face) => face * glyphCount + charIndex;

        private static GlyphMetrics ReadMetrics(BinaryReader reader) => new GlyphMetrics()
        {
            xMin = reader.ReadInt16(),
            yMin = reader.ReadInt16(),
            xMax = reader.ReadInt16(),
            yMax = reader.ReadInt16(),
            glyphWidth = reader.ReadSingle(),
            glyphHeight = reader.ReadSingle(),
            advanceWidth = reader.ReadSingle(),
            leftSideOffset = reader.ReadSingle(),
            tsb = reader.ReadSingle()
        };

        public void Deserialize(string name)
        {
            string path = Paths.Font(name, $"{name}.agd");
            using (FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                BinaryReader reader = new BinaryReader(fileStream, System.Text.Encoding.Unicode);
                glyphCount = reader.ReadInt32();
                chars = new char[glyphCount];
                glyphs = new Glyph[glyphCount];

                for (int i = 0; i < glyphCount; i++)
                {
                    chars[i] = reader.ReadChar();
                }

                // COMMENTED OUT CUZ IT MIGHT HAVE PROBLEMS WITH BEZIER LIST.
                // DIDNT TEST AND THIS IS NOT PERFORMANCE CRITICAL

                //long offset = sizeof(int) + count * sizeof(char) * 2;
                //fileStream.Seek(offset, SeekOrigin.Begin);

                //byte[] buffer = new byte[Marshal.SizeOf<>()];
                //GCHandle handleMeta = GCHandle.Alloc(fontMetaBuffer, GCHandleType.Pinned);
                //fontMeta = Marshal.PtrToStructure<FontMeta>(handleMeta.AddrOfPinnedObject());
                //handleMeta.Free();

                for (int i = 0; i < glyphCount; i++)
                {
                    glyphs[i] = new Glyph();
                    glyphs[i].regular = ReadMetrics(reader);
                    ReadMetrics(reader);
                    ReadMetrics(reader);
                }

                pxRange = reader.ReadSingle();
                reader.ReadBoolean();
                reader.ReadBoolean();
            }
        }

        // Maps each char to its glyph index.
        public void BuildCharIndex()
        {
            charIndex = new Dictionary<char, int>(glyphCount, charHash);
            for (int i = 0; i < glyphCount; i++)
            {
                charIndex.TryAdd(chars[i], i);
                glyphs[i].index = i;
            }

            variants = new string(variantNames).Split('\n');
            slots = new int[variants.Length * 4];
            Array.Fill(slots, -1);
            for (int f = 0; f < faceKeys.Length; f++) slots[faceKeys[f]] = f;

            int faces = faceKeys.Length;
            advances = new float[faces * AdvanceTableSize];
            for (int c = 0; c < AdvanceTableSize; c++)
            {
                Glyph glyph = GetGlyph((char)c) ?? GetGlyph(' ');
                if (glyph == null) continue;
                for (int f = 0; f < faces; f++)
                    advances[f * AdvanceTableSize + c] = Metrics(glyph, f).advanceWidth;
            }

            kerns = kernKeys.Length == 0 ? noKerns : new Dictionary<ulong, float>(kernKeys.Length);
            for (int f = 0; f + 1 < kernStarts.Length; f++)
                for (int k = kernStarts[f]; k < kernStarts[f + 1]; k++)
                    kerns[(ulong)f << 32 | kernKeys[k]] = kernValues[k];
        }

        // Fills the kern pairs from each baked face's file.
        internal void ReadKerning(string[] facePaths, int[] faceIndices)
        {
            List<uint> keys = new List<uint>(1024);
            List<float> values = new List<float>(1024);
            kernStarts = new int[facePaths.Length + 1];
            for (int f = 0; f < facePaths.Length; f++)
            {
                kernStarts[f] = keys.Count;
                FontKerning.Read(facePaths[f], faceIndices[f], chars, keys, values);
            }
            kernStarts[facePaths.Length] = keys.Count;
            kernKeys = keys.ToArray();
            kernValues = values.ToArray();
        }

        // The em kern between two characters in one face, 0 when the font names no pair.
        public float Kern(char first, char second, int face) =>
            kerns.Count == 0 ? 0f : kerns.GetValueOrDefault((ulong)face << 32 | (uint)first << 16 | second);

        // A char's em advance in one face, unimported chars as space; character must be below AdvanceTableSize.
        public float TableAdvance(char character, int face) => advances[face * AdvanceTableSize + character];

        public Glyph GetGlyph(char character)
        {
            if (charIndex.TryGetValue(character, out int index))
            {
                return glyphs[index];
            }
            return null; // or throw an exception if preferred
        }

        public (Glyph, int) GetGlyphAndIndex(char character)
        {
            if (charIndex.TryGetValue(character, out int index))
            {
                return (glyphs[index], index);
            }
            return (null, -1); // or throw an exception if preferred
        }

        public int GetIndexOfChar(char character)
        {
            if (charIndex.TryGetValue(character, out int index))
            {
                return index;
            }
            return -1; // or throw an exception if preferred
        }
    }
}