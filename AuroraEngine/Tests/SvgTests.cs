using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Generators;
using ArctisAurora.Core.Testing;
using System.Numerics;
using Image = Arctis.Imaging.Image;

namespace ArctisAurora.Tests
{
    internal static class SvgTests
    {
        [A_XSDActionDependency("Svg.InheritedFill", "Test")]
        private static IEnumerator<int> InheritedFill(TestContext t)
        {
            t.Check(!Load("<svg viewBox='0 0 100 100' xmlns='http://www.w3.org/2000/svg' fill='none'><path d='M10 10H90V90Z'/></svg>",
                out _, out string reason) && reason.Contains("no shape"), "fill='none' on <svg> is inherited by the path");
            t.Check(!Load("<svg viewBox='0 0 100 100' xmlns='http://www.w3.org/2000/svg'><g style='fill: none'><path d='M10 10H90V90Z'/></g></svg>",
                out _, out _), "fill:none in a <g> style is inherited by the path");
            yield break;
        }

        [A_XSDActionDependency("Svg.Unrendered", "Test")]
        private static IEnumerator<int> Unrendered(TestContext t)
        {
            bool loaded = Load("<svg viewBox='0 0 100 100' xmlns='http://www.w3.org/2000/svg'>"
                + "<defs><path d='M0 0H100V100H0Z'/></defs><clipPath><path d='M0 0H50V50Z'/></clipPath>"
                + "<path d='M10 10H20V20H10Z'/></svg>", out Glyph glyph, out string reason);
            t.Check(loaded, $"loads ({reason})");
            if (!loaded) yield break;
            t.Check(glyph.contours.Count == 1, $"only the rendered path makes a contour (got {glyph.contours.Count})");
            t.Check(Bounds(glyph, 10, 10, 20, 20), "the contour is the rendered path");
        }

        [A_XSDActionDependency("Svg.Transform", "Test")]
        private static IEnumerator<int> Transform(TestContext t)
        {
            bool loaded = Load("<svg viewBox='0 0 100 100' xmlns='http://www.w3.org/2000/svg'><g transform='translate(10 20)'>"
                + "<path d='M0 0H10V10H0Z' transform='scale(2)'/></g></svg>", out Glyph glyph, out string reason);
            t.Check(loaded && Bounds(glyph, 10, 20, 30, 40), $"the path's scale applies before the group's translate ({reason})");

            loaded = Load("<svg viewBox='0 0 100 100' xmlns='http://www.w3.org/2000/svg'>"
                + "<path d='M60 50H70V60H60Z' transform='rotate(90 50 50)'/></svg>", out glyph, out reason);
            t.Check(loaded && Bounds(glyph, 40, 60, 50, 70), $"rotate turns clockwise about its centre ({reason})");

            loaded = Load("<svg viewBox='0 0 100 100' xmlns='http://www.w3.org/2000/svg'>"
                + "<path d='M0 0H10V10H0Z' transform='matrix(1 0 0 1 5 6) scale(3,2)'/></svg>", out glyph, out reason);
            t.Check(loaded && Bounds(glyph, 5, 6, 35, 26), $"a list applies right to left ({reason})");

            t.Check(!Load("<svg viewBox='0 0 100 100' xmlns='http://www.w3.org/2000/svg'>"
                + "<path d='M0 0H10V10Z' transform='perspective(2)'/></svg>", out _, out _), "an unknown transform is refused");
            yield break;
        }

        [A_XSDActionDependency("Svg.Malformed", "Test")]
        private static IEnumerator<int> Malformed(TestContext t)
        {
            t.Check(!Load(Path("M0 0H10V10Z 5 5"), out _, out _), "a number after Z fails instead of hanging");
            t.Check(!Load(Path("M0 0L10 10 #"), out _, out _), "an unknown character fails instead of hanging");
            yield break;
        }

        [A_XSDActionDependency("Svg.FillRule", "Test")]
        private static IEnumerator<int> FillRule(TestContext t)
        {
            bool loaded = Load("<svg viewBox='0 0 100 100' xmlns='http://www.w3.org/2000/svg' fill-rule='evenodd'>"
                + "<path d='M10 10H90V90H10Z M30 30H70V70H30Z'/></svg>", out Glyph glyph, out string reason);
            t.Check(loaded && glyph.evenOdd, $"an inherited evenodd marks the glyph ({reason})");

            loaded = Load(Path("M10 10H90V90H10Z"), out glyph, out reason);
            t.Check(loaded && !glyph.evenOdd, $"nonzero is the default ({reason})");

            t.Check(!Load("<svg viewBox='0 0 100 100' xmlns='http://www.w3.org/2000/svg'><path d='M10 10H20V20Z'/>"
                + "<path d='M30 30H40V40Z' fill-rule='evenodd'/></svg>", out _, out _), "mixed fill rules are refused");
            yield break;
        }

        [A_XSDActionDependency("Svg.Arc", "Test")]
        private static IEnumerator<int> Arc(TestContext t)
        {
            bool loaded = Load(Path("M10 50A40 40 0 0 1 90 50Z"), out Glyph glyph, out string reason);
            t.Check(loaded && Bounds(glyph, 10, 10, 90, 50), $"a sweep-1 half circle passes over the top ({reason})");
            t.Check(loaded && Segments(glyph, 0) == 3, "a half circle is two quarter cubics plus the closing line");

            loaded = Load(Path("M10 50a40 40 0 0180 0z"), out glyph, out reason);
            t.Check(loaded && Bounds(glyph, 10, 10, 90, 50), $"flags written without separators parse ({reason})");

            loaded = Load(Path("M10 50A40 40 0 0 0 90 50Z"), out glyph, out reason);
            t.Check(loaded && Bounds(glyph, 10, 50, 90, 90), $"a sweep-0 half circle passes under ({reason})");

            loaded = Load(Path("M10 50A1 1 0 0 1 90 50Z"), out glyph, out reason);
            t.Check(loaded && Bounds(glyph, 10, 10, 90, 50), $"radii too small to reach are scaled up ({reason})");

            loaded = Load(Path("M50 10A40 40 0 1 1 10 50Z"), out glyph, out reason);
            t.Check(loaded && Bounds(glyph, 10, 10, 90, 90) && Segments(glyph, 0) == 4,
                $"a large three-quarter arc covers the circle in three cubics ({reason})");
            yield break;
        }

        [A_XSDActionDependency("Svg.Shapes", "Test")]
        private static IEnumerator<int> Shapes(TestContext t)
        {
            bool loaded = Load(Shape("<rect x='10' y='10' width='80' height='60' rx='10'/>"), out Glyph glyph, out string reason);
            t.Check(loaded && Bounds(glyph, 10, 10, 90, 70) && Segments(glyph, 0) == 8, $"a rounded rect is four lines and four corners ({reason})");

            loaded = Load(Shape("<rect x='10' y='10' width='80' height='80' rx='50'/>"), out glyph, out reason);
            t.Check(loaded && Bounds(glyph, 10, 10, 90, 90) && Segments(glyph, 0) == 4, $"rx clamps to half the side and drops the lines ({reason})");

            loaded = Load(Shape("<rect x='10' y='20' width='30' height='40'/>"), out glyph, out reason);
            t.Check(loaded && Bounds(glyph, 10, 20, 40, 60) && Segments(glyph, 0) == 4, $"a square rect is four lines ({reason})");

            loaded = Load(Shape("<circle cx='50' cy='50' r='40'/>"), out glyph, out reason);
            t.Check(loaded && Bounds(glyph, 10, 10, 90, 90) && Segments(glyph, 0) == 4, $"a circle is four quarter cubics ({reason})");

            loaded = Load(Shape("<ellipse cx='50' cy='50' rx='40' ry='20'/>"), out glyph, out reason);
            t.Check(loaded && Bounds(glyph, 10, 30, 90, 70), $"an ellipse has its own radii ({reason})");

            loaded = Load(Shape("<polygon points='10,10 90,10 50,90'/><polyline points='0 0 5 0 5 5'/><line x1='0' y1='0' x2='9' y2='9'/>"),
                out glyph, out reason);
            t.Check(loaded && glyph.contours.Count == 2 && Segments(glyph, 0) == 3 && Segments(glyph, 1) == 3,
                $"polygon and polyline close, a line encloses nothing ({reason})");

            t.Check(!Load(Shape("<rect width='0' height='10'/><circle r='0'/>"), out _, out _), "zero-size shapes make no contour");
            yield break;
        }

        [A_XSDActionDependency("Svg.StrokeRuns", "Test")]
        private static IEnumerator<int> StrokeRuns(TestContext t)
        {
            bool loaded = Load("<svg viewBox='0 0 100 100' xmlns='http://www.w3.org/2000/svg' fill='none' stroke='currentColor' stroke-width='2'"
                + " stroke-linecap='round' stroke-linejoin='bevel'><path d='M2 2H10V10M14 14H20V20Z'/></svg>", out Glyph glyph, out string reason);
            t.Check(loaded && glyph.contours.Count == 0 && glyph.strokes.Count == 2, $"fill:none with a stroke loads as two runs ({reason})");
            if (!loaded || glyph.strokes.Count != 2) yield break;
            StrokeRun open = glyph.strokes[0], closed = glyph.strokes[1];
            t.Check(!open.closed && open.edges.Count == 2, "a subpath without Z is open and keeps its own segments");
            t.Check(closed.closed && closed.edges.Count == 3, "Z closes the run and adds the closing line");
            t.Check(open.halfWidth == 1f && open.cap == StrokeCap.Round && open.join == StrokeJoin.Bevel && open.miterLimit == 4f,
                "width, cap, join and miter limit are inherited from <svg>");
            Vector2 mapped = Vector2.Transform(open.edges[0].p0, open.toGlyph);
            t.Check(MathF.Abs(mapped.X - 0.02f) < 1e-5f && MathF.Abs(mapped.Y - 0.98f) < 1e-5f, $"toGlyph maps into the normalized Y-up glyph ({mapped})");

            loaded = Load(Shape("<g stroke-width='3' stroke-miterlimit='0.5'><rect x='10' y='10' width='20' height='20' stroke='black'"
                + " transform='translate(5 0)'/></g>"), out glyph, out reason);
            t.Check(loaded && glyph.contours.Count == 1 && glyph.strokes.Count == 1 && glyph.strokes[0].closed
                && glyph.strokes[0].edges.Count == 4, $"a filled and stroked rect makes both ({reason})");
            t.Check(loaded && glyph.strokes.Count == 1 && glyph.strokes[0].halfWidth == 1.5f && glyph.strokes[0].miterLimit == 1f
                && glyph.strokes[0].join == StrokeJoin.Miter && glyph.strokes[0].cap == StrokeCap.Butt,
                "group stroke-width inherits, miter limit clamps to 1, SVG defaults otherwise");
            t.Check(loaded && glyph.strokes.Count == 1 && MathF.Abs(Vector2.Transform(glyph.strokes[0].edges[0].p0, glyph.strokes[0].toGlyph).X - 0.15f) < 1e-5f,
                "the element transform is in toGlyph, edges stay local");

            loaded = Load(Shape("<path d='M5 5Z M9 9' stroke='black' fill='none' stroke-linecap='round'/>"), out glyph, out reason);
            t.Check(loaded && glyph.strokes.Count == 1 && !glyph.strokes[0].closed && glyph.strokes[0].edges.Count == 1,
                $"a zero-length closed subpath is one dot run; a lone moveto is nothing ({reason})");

            t.Check(!Load(Shape("<path d='M0 0H10' stroke='black' vector-effect='non-scaling-stroke'/>"), out _, out reason)
                && reason.Contains("non-scaling"), "non-scaling-stroke is refused");
            t.Check(!Load(Shape("<path d='M0 0H10V10Z' fill='none' stroke='black' stroke-width='0'/>"), out _, out _),
                "a zero-width stroke on an unfilled shape draws nothing");
        }

        [A_XSDActionDependency("Svg.StrokeBake", "Test")]
        private static IEnumerator<int> StrokeBake(TestContext t)
        {
            const string line = "<path d='M30 50H70' fill='none' stroke='black' stroke-width='20' stroke-linecap='{0}'/>";
            Inside(t, Shape(string.Format(line, "butt")), (50, 50, true), (74, 50, false));
            Inside(t, Shape(string.Format(line, "round")), (74, 50, true), (79, 59, false));
            Inside(t, Shape(string.Format(line, "square")), (78, 58, true), (83, 50, false));

            const string peak = "<path d='M20 80L50 30 80 80' fill='none' stroke='black' stroke-width='20' stroke-linejoin='{0}' stroke-miterlimit='{1}'/>";
            Inside(t, Shape(string.Format(peak, "miter", 4)), (50, 14, true), (50, 8, false));
            Inside(t, Shape(string.Format(peak, "round", 4)), (50, 22, true), (50, 14, false));
            Inside(t, Shape(string.Format(peak, "bevel", 4)), (50, 27, true), (50, 22, false));
            Inside(t, Shape(string.Format(peak, "miter", 1.5)), (50, 27, true), (50, 22, false));
            foreach (string join in new[] { "miter", "round", "bevel" })
                Inside(t, Shape(string.Format(peak, join, 4)), (47.86f, 28.71f, true), (45.71f, 27.43f, true));

            Inside(t, Shape("<rect x='30' y='30' width='40' height='40' fill='none' stroke='black' stroke-width='20'/>"),
                (22, 22, true), (50, 50, false), (78, 78, true));
            Inside(t, Shape("<path d='M50 10a40 40 0 1 0 0 80a40 40 0 1 0 0-80zM50 30a20 20 0 1 0 0 40a20 20 0 1 0 0-40z' fill-rule='evenodd'/>"
                + "<path d='M30 50H70' fill='none' stroke='black' stroke-width='6'/>"), (50, 50, true), (50, 40, false), (50, 20, true));
            Inside(t, Shape("<circle r='20' fill='none' stroke='black' stroke-width='10' transform='translate(50 50) scale(2 1)'/>"),
                (96, 50, true), (50, 37, false), (50, 30, true));
            Inside(t, Shape("<path d='M50 50Z' stroke='black' stroke-width='20' stroke-linecap='round'/>"), (50, 55, true), (50, 62, false));
            Inside(t, Shape("<path d='M50 50Z' stroke='black' stroke-width='20'/>"), (50, 50, false));
            yield break;
        }

        private const int bakeCell = 130;

        // Bakes one cell and checks the median channel at viewBox points.
        private static void Inside(TestContext t, string svg, params (float x, float y, bool inside)[] samples)
        {
            if (!Load(svg, out Glyph glyph, out string reason)) { t.Check(false, $"loads: {svg} ({reason})"); return; }
            MTSDFGen.ColorEdges(glyph);
            Image cell = new Image(bakeCell, bakeCell);
            MTSDFGen.GenerateCell(glyph, cell, 0, 0, bakeCell, MTSDFGen.PxRange);

            int inner = bakeCell - 2;
            float spread = (inner / 8) / (float)inner;
            foreach ((float x, float y, bool inside) in samples)
            {
                int tx = (int)MathF.Round((x / 100f + spread) / (1f + 2f * spread) * inner - 0.5f) + 1;
                int ty = (int)MathF.Round(((100f - y) / 100f + spread) / (1f + 2f * spread) * inner - 0.5f) + 1;
                int at = (ty * bakeCell + tx) * 4;
                byte r = cell.Pixels[at], g = cell.Pixels[at + 1], b = cell.Pixels[at + 2];
                float median = MathF.Max(MathF.Min(r, g), MathF.Min(MathF.Max(r, g), b)) / 255f;
                t.Check(inside ? median > 0.75f : median < 0.25f, $"({x},{y}) {(inside ? "inside" : "outside")}, median {median:0.00}: {svg}");
            }
        }

        private static string Shape(string elements) =>
            $"<svg viewBox='0 0 100 100' xmlns='http://www.w3.org/2000/svg'>{elements}</svg>";

        private static string Path(string data) =>
            $"<svg viewBox='0 0 100 100' xmlns='http://www.w3.org/2000/svg'><path d='{data}'/></svg>";

        private static bool Load(string svg, out Glyph glyph, out string reason)
        {
            string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AuroraSvgTest.svg");
            File.WriteAllText(file, svg);
            try { return SvgPath.TryLoad(file, out glyph, out reason); }
            finally { File.Delete(file); }
        }

        // Anchor bounds, mapped back from the normalized Y-up glyph into the 100x100 viewBox.
        private static bool Bounds(Glyph glyph, float minX, float minY, float maxX, float maxY)
        {
            IEnumerable<Bezier.Point> anchors = glyph.contours.SelectMany(c => c.points).Where(p => p.isAnchor);
            float x0 = anchors.Min(p => p.pos.X) * 100f, x1 = anchors.Max(p => p.pos.X) * 100f;
            float y0 = 100f - anchors.Max(p => p.pos.Y) * 100f, y1 = 100f - anchors.Min(p => p.pos.Y) * 100f;
            const float e = 1e-2f;
            return MathF.Abs(x0 - minX) < e && MathF.Abs(y0 - minY) < e && MathF.Abs(x1 - maxX) < e && MathF.Abs(y1 - maxY) < e;
        }

        private static int Segments(Glyph glyph, int contour) => glyph.contours[contour].points.Count / 3;
    }
}
