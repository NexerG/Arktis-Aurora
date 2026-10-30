using ArctisAurora.Core.Filing.Serialization;
using System.Numerics;

namespace ArctisAurora.Core.Filing
{
    public enum FontStyle
    {
        Regular,
        Bold,
        Italic,
        BoldItalic
    }

    // One face's measurements for one character.
    [@Serializable]
    public struct GlyphMetrics
    {
        // ink box, font units
        public short xMin, yMin, xMax, yMax;

        // cell and horizontal metrics, in em
        public float glyphWidth;
        public float glyphHeight;
        public float advanceWidth;
        public float leftSideOffset;
        public float tsb;
    }

    [@Serializable]
    public class Glyph
    {
        // the first face's metrics; an atlas keeps the other faces' in AtlasMetaData.faceMetrics
        public GlyphMetrics regular;

        // position in the atlas's glyphs, set on load
        [@NonSerializable]
        public int index;

        [@NonSerializable]
        public List<List<Edge>> edgeContours = new List<List<Edge>>();

        [NonSerializable]
        public List<Bezier> contours = new List<Bezier>();

        [@NonSerializable]
        public bool evenOdd;

        [@NonSerializable]
        public List<StrokeRun> strokes = new List<StrokeRun>();

        public Glyph()
        {
            regular.glyphWidth = 1;
            regular.glyphHeight = 1;
        }

        public void BuildEdges()
        {
            edgeContours.Clear();

            for (int c = 0; c < contours.Count; c++)
            {
                Bezier bezier = contours[c];
                List<Edge> edges = new List<Edge>();
                List<Bezier.Point> pts = bezier.points;
                int count = pts.Count;
                if (count == 0) continue;

                // Expand first: TrueType leaves the on-curve point between two consecutive control
                // points implied, at their midpoint. Inserting those up front into a copy means the
                // edge walk below never has to mutate the list it is iterating — the old in-place
                // Insert(i + 2) ran off the end whenever such a pair straddled the contour's wrap.
                List<Bezier.Point> expanded = new List<Bezier.Point>(count + 4);
                for (int i = 0; i < count; i++)
                {
                    Bezier.Point current = pts[i];
                    Bezier.Point next = pts[(i + 1) % count];
                    expanded.Add(current);
                    if (!current.isAnchor && !next.isAnchor && !current.isCubicControl)
                        expanded.Add(new Bezier.Point((current.pos + next.pos) * 0.5f, true));
                }

                int total = expanded.Count;
                for (int i = 0; i < total; i++)
                {
                    Bezier.Point current = expanded[i];
                    if (!current.isAnchor) continue;   // control points are consumed by their edge

                    Bezier.Point next = expanded[(i + 1) % total];
                    Edge e = new Edge();
                    e.p0 = current.pos;

                    // Already cubic: both controls are given, so nothing is elevated.
                    if (next.isCubicControl)
                    {
                        e.c0 = next.pos;
                        e.c1 = expanded[(i + 2) % total].pos;
                        e.p1 = expanded[(i + 3) % total].pos;
                        edges.Add(e);
                        continue;
                    }

                    Vector2 control;
                    if (next.isAnchor)
                    {
                        // Straight run: a quadratic whose control sits on the line is the line.
                        control = (current.pos + next.pos) * 0.5f;
                        e.p1 = next.pos;
                    }
                    else
                    {
                        // No two control points are adjacent after expansion, so this is on-curve.
                        control = next.pos;
                        e.p1 = expanded[(i + 2) % total].pos;
                    }
                    // TrueType is quadratic, the generator is cubic; degree elevation is exact.
                    e.c0 = e.p0 + (control - e.p0) * (2f / 3f);
                    e.c1 = e.p1 + (control - e.p1) * (2f / 3f);
                    edges.Add(e);
                }
                edgeContours.Add(edges);
            }
        }

        public void SetParams(short xMin, short xMax, short yMin, short yMax, float unitsPerEm)
        {
            regular.xMin = xMin;
            regular.xMax = xMax;
            regular.yMin = yMin;
            regular.yMax = yMax;

            regular.glyphWidth = (xMax - xMin) / unitsPerEm;
            regular.glyphHeight = (yMax - yMin) / unitsPerEm;
        }
    }
}