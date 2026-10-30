using ArctisAurora.Core.Filing.Serialization;
using System.Buffers.Binary;

namespace ArctisAurora.Core.Filing
{
    // A face's pair kerning among a charset: GPOS's kern feature for the Latin script's default language, else the kern table.
    public static class FontKerning
    {
        // Appends the face's non-zero pairs among chars: key a << 16 | b, value in em.
        public static void Read(string fontPath, int face, char[] chars, List<uint> keys, List<float> values)
        {
            byte[] data = File.ReadAllBytes(fontPath);
            int gpos = 0, kern = 0, head = 0, cmap = 0;
            ushort[] glyphOf = new ushort[chars.Length];
            using (BinaryReader reader = new BinaryReader(new MemoryStream(data)))
            {
                int directory = (int)AssetImporter.FaceOffset(reader, face);
                int tableCount = U16(data, directory + 4);
                for (int i = 0; i < tableCount; i++)
                {
                    int record = directory + 12 + 16 * i;
                    int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record + 8));
                    switch (System.Text.Encoding.ASCII.GetString(data, record, 4))
                    {
                        case "GPOS": gpos = offset; break;
                        case "kern": kern = offset; break;
                        case "head": head = offset; break;
                        case "cmap": cmap = offset; break;
                    }
                }
                if (head == 0 || cmap == 0) return;

                AuroraFont.TableEntry cmapTable = new AuroraFont.TableEntry { offset = (uint)cmap };
                for (int i = 0; i < chars.Length; i++)
                    glyphOf[i] = AuroraFont.GetGlyphIndex(chars[i], reader, cmapTable);
            }

            float em = U16(data, head + 18);
            int[] subtables = Array.Empty<int>(), lookupStarts = Array.Empty<int>();
            bool positioned = gpos != 0 && KernSubtables(data, gpos, out subtables, out lookupStarts);
            if (!positioned && kern == 0) return;

            for (int a = 0; a < chars.Length; a++)
            {
                if (glyphOf[a] == 0 || char.IsWhiteSpace(chars[a])) continue;
                for (int b = 0; b < chars.Length; b++)
                {
                    if (glyphOf[b] == 0 || char.IsWhiteSpace(chars[b])) continue;
                    int value = positioned ? GposValue(data, subtables, lookupStarts, glyphOf[a], glyphOf[b]) : KernTableValue(data, kern, glyphOf[a], glyphOf[b]);
                    if (value == 0) continue;
                    keys.Add((uint)chars[a] << 16 | chars[b]);
                    values.Add(value / em);
                }
            }
        }

        #region ---- GPOS ----
        // The pair-adjustment subtables of the kern feature's lookups, flat, lookup l's at subtables[lookupStarts[l]..lookupStarts[l + 1]].
        private static bool KernSubtables(byte[] d, int gpos, out int[] subtables, out int[] lookupStarts)
        {
            subtables = Array.Empty<int>();
            lookupStarts = Array.Empty<int>();
            int scriptList = gpos + U16(d, gpos + 4), featureList = gpos + U16(d, gpos + 6), lookupList = gpos + U16(d, gpos + 8);

            int script = 0;
            for (int i = 0, count = U16(d, scriptList); i < count; i++)
            {
                string tag = System.Text.Encoding.ASCII.GetString(d, scriptList + 2 + 6 * i, 4);
                if (tag == "latn") { script = scriptList + U16(d, scriptList + 6 + 6 * i); break; }
                if (tag == "DFLT") script = scriptList + U16(d, scriptList + 6 + 6 * i);
            }
            if (script == 0 || U16(d, script) == 0) return false;
            int langSys = script + U16(d, script);

            List<int> lookups = new List<int>(8);
            int required = U16(d, langSys + 2);
            int featureCount = U16(d, langSys + 4);
            for (int i = -1; i < featureCount; i++)
            {
                int index = i < 0 ? required : U16(d, langSys + 6 + 2 * i);
                if (index == 0xFFFF || System.Text.Encoding.ASCII.GetString(d, featureList + 2 + 6 * index, 4) != "kern") continue;
                int feature = featureList + U16(d, featureList + 6 + 6 * index);
                for (int k = 0, count = U16(d, feature + 2); k < count; k++)
                {
                    int lookup = U16(d, feature + 4 + 2 * k);
                    if (!lookups.Contains(lookup)) lookups.Add(lookup);
                }
            }
            if (lookups.Count == 0) return false;
            lookups.Sort();

            List<int> found = new List<int>(lookups.Count * 4);
            lookupStarts = new int[lookups.Count + 1];
            for (int l = 0; l < lookups.Count; l++)
            {
                lookupStarts[l] = found.Count;
                int lookup = lookupList + U16(d, lookupList + 2 + 2 * lookups[l]);
                int type = U16(d, lookup);
                for (int s = 0, count = U16(d, lookup + 4); s < count; s++)
                {
                    int subtable = lookup + U16(d, lookup + 6 + 2 * s);
                    if (type == 9)
                    {
                        if (U16(d, subtable + 2) != 2) continue;
                        subtable += (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(subtable + 4));
                    }
                    else if (type != 2) continue;
                    found.Add(subtable);
                }
            }
            lookupStarts[lookups.Count] = found.Count;
            subtables = found.ToArray();
            return subtables.Length > 0;
        }

        // The first glyph's x-advance adjustment, summed over lookups; within one lookup the first subtable that applies wins.
        private static int GposValue(byte[] d, int[] subtables, int[] lookupStarts, ushort first, ushort second)
        {
            int total = 0;
            for (int l = 0; l < lookupStarts.Length - 1; l++)
                for (int s = lookupStarts[l]; s < lookupStarts[l + 1]; s++)
                    if (PairAdjustment(d, subtables[s], first, second, out int value))
                    {
                        total += value;
                        break;
                    }
            return total;
        }

        // PairPos format 1 (pair sets) or 2 (class pairs); false when the subtable does not apply to the pair.
        private static bool PairAdjustment(byte[] d, int sub, ushort first, ushort second, out int value)
        {
            value = 0;
            int format = U16(d, sub);
            int coverage = Coverage(d, sub + U16(d, sub + 2), first);
            if (coverage < 0) return false;
            int format1 = U16(d, sub + 4), format2 = U16(d, sub + 6);
            int size1 = ValueSize(format1), size2 = ValueSize(format2);

            if (format == 1)
            {
                int set = sub + U16(d, sub + 10 + 2 * coverage);
                int record = 2 + size1 + size2;
                int lo = 0, hi = U16(d, set) - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    int at = set + 2 + mid * record;
                    int glyph = U16(d, at);
                    if (glyph == second)
                    {
                        value = XAdvance(d, at + 2, format1);
                        return true;
                    }
                    if (glyph < second) lo = mid + 1;
                    else hi = mid - 1;
                }
                return false;
            }
            if (format != 2) return false;

            int class1 = ClassOf(d, sub + U16(d, sub + 8), first), class2 = ClassOf(d, sub + U16(d, sub + 10), second);
            int class1Count = U16(d, sub + 12), class2Count = U16(d, sub + 14);
            if (class1 >= class1Count || class2 >= class2Count) return false;
            value = XAdvance(d, sub + 16 + (class1 * class2Count + class2) * (size1 + size2), format1);
            return true;
        }

        // A glyph's coverage index, -1 when not covered.
        private static int Coverage(byte[] d, int table, ushort glyph)
        {
            int format = U16(d, table), count = U16(d, table + 2);
            int lo = 0, hi = count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (format == 1)
                {
                    int g = U16(d, table + 4 + 2 * mid);
                    if (g == glyph) return mid;
                    if (g < glyph) lo = mid + 1;
                    else hi = mid - 1;
                }
                else
                {
                    int range = table + 4 + 6 * mid;
                    if (glyph < U16(d, range)) hi = mid - 1;
                    else if (glyph > U16(d, range + 2)) lo = mid + 1;
                    else return U16(d, range + 4) + glyph - U16(d, range);
                }
            }
            return -1;
        }

        // A glyph's class, 0 when the definition does not name it.
        private static int ClassOf(byte[] d, int table, ushort glyph)
        {
            if (U16(d, table) == 1)
            {
                int start = U16(d, table + 2), count = U16(d, table + 4);
                return glyph >= start && glyph < start + count ? U16(d, table + 6 + 2 * (glyph - start)) : 0;
            }
            int lo = 0, hi = U16(d, table + 2) - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int range = table + 4 + 6 * mid;
                if (glyph < U16(d, range)) hi = mid - 1;
                else if (glyph > U16(d, range + 2)) lo = mid + 1;
                else return U16(d, range + 4);
            }
            return 0;
        }

        private static int ValueSize(int format) => 2 * System.Numerics.BitOperations.PopCount((uint)format & 0xFF);

        private static int XAdvance(byte[] d, int record, int format) =>
            (format & 4) == 0 ? 0 : (short)U16(d, record + 2 * System.Numerics.BitOperations.PopCount((uint)format & 3));
        #endregion

        #region ---- kern table ----
        // The horizontal format-0 subtables' value for the pair, summed.
        private static int KernTableValue(byte[] d, int kern, ushort first, ushort second)
        {
            if (U16(d, kern) != 0) return 0;
            uint key = (uint)first << 16 | second;
            int total = 0;
            int sub = kern + 4;
            for (int t = 0, count = U16(d, kern + 2); t < count; t++)
            {
                int length = U16(d, sub + 2), coverage = U16(d, sub + 4);
                if (coverage >> 8 == 0 && (coverage & 7) == 1)
                {
                    int lo = 0, hi = U16(d, sub + 6) - 1;
                    while (lo <= hi)
                    {
                        int mid = (lo + hi) >> 1;
                        int at = sub + 14 + 6 * mid;
                        uint pair = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(at));
                        if (pair == key)
                        {
                            total += (short)U16(d, at + 4);
                            break;
                        }
                        if (pair < key) lo = mid + 1;
                        else hi = mid - 1;
                    }
                }
                sub += length;
            }
            return total;
        }
        #endregion

        private static int U16(byte[] d, int at) => BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(at));
    }
}
