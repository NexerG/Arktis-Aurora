using ArctisAurora.Core.Filing;
using Silk.NET.Maths;
using System.Buffers;
using System.Numerics;
using Image = Arctis.Imaging.Image;

namespace ArctisAurora.Core.Generators
{
    public static class MTSDFGen
    {
        // Width of the distance range in atlas texels. The UI fragment shader hardcodes the same
        // number; an atlas baked at any other range renders with the wrong antialiasing.
        public const float PxRange = 4f;

        // Assigns the three MSDF channel masks around each contour, breaking at corners.
        public static void ColorEdges(Glyph glyph)
        {
            int colorIndex = 0;
            for (int i = 0; i < glyph.edgeContours.Count; i++)
            {
                List<Edge> edges = glyph.edgeContours[i];
                if (edges.Count == 0) continue;

                for (int j = 0; j < edges.Count; j++)
                {
                    Edge prev = edges[(j - 1 + edges.Count) % edges.Count];
                    Edge current = edges[j];
                    // Entry direction of current edge: B'(0) = 3(C0 - P0)
                    Vector2 dirIn = current.c0 - current.p0;
                    // Exit direction of previous edge: B'(1) = 3(P1 - C1)
                    Vector2 dirOut = prev.p1 - prev.c1;

                    float lenOut = Vector2.Distance(dirOut, Vector2.Zero);
                    float lenIn = Vector2.Distance(dirIn, Vector2.Zero);
                    if (lenOut > 1e-6f && lenIn > 1e-6f)
                    {
                        float directionDot = Vector2.Dot(dirOut / lenOut, dirIn / lenIn);
                        if (directionDot < 0.5f)
                            colorIndex++;
                    }

                    switch (colorIndex % 3)
                    {
                        case 0:
                            edges[j].color = new Vector3D<int>(1, 1, 0);
                            break;
                        case 1:
                            edges[j].color = new Vector3D<int>(0, 1, 1);
                            break;
                        case 2:
                            edges[j].color = new Vector3D<int>(1, 0, 1);
                            break;
                    }
                }
                colorIndex++;
                bool isOfColor = edges[0].color == edges[edges.Count - 1].color;
                if(isOfColor)
                {
                    if(edges[edges.Count - 1].color == new Vector3D<int>(1, 1, 0))
                        edges[edges.Count - 1].color = new Vector3D<int>(0, 1, 1);

                    else if(edges[edges.Count - 1].color == new Vector3D<int>(0, 1, 1))
                        edges[edges.Count - 1].color = new Vector3D<int>(1, 0, 1);

                    else if(edges[edges.Count - 1].color == new Vector3D<int>(1, 0, 1))
                        edges[edges.Count - 1].color = new Vector3D<int>(0, 1, 1);
                }
            }
        }

        // cell padding in texels, and the distance spread per side of what is inside it
        private const int cellPad = 1;
        private static int Spread(int innerSize) => innerSize / 8;

        // Per-side fraction of a cell's sampled quad between its edge and the ink box.
        public static float InkMargin(int cellSize)
        {
            int innerSize = cellSize - cellPad * 2;
            int spreadPx = Spread(innerSize);
            return spreadPx / (float)(innerSize + 2 * spreadPx);
        }

        // Rasterizes one shape into a square cell of the atlas.
        public static void GenerateCell(Glyph glyph, Image image, int startX, int startY, int cellSize, float pxRange)
        {
            float scale = MathF.Max(glyph.regular.xMax - glyph.regular.xMin, glyph.regular.yMax - glyph.regular.yMin);
            float normW = (glyph.regular.xMax - glyph.regular.xMin) / scale;
            float normH = (glyph.regular.yMax - glyph.regular.yMin) / scale;

            int pad = cellPad;
            int innerSize = cellSize - pad * 2;
            int spreadPx = Spread(innerSize);
            float spreadU = (spreadPx / (float)innerSize) * normW;
            float spreadV = (spreadPx / (float)innerSize) * normH;

            // Contours are normalized against max(w, h), so the long axis always spans 1 and the
            // texel size below it is the one the shader's pxRange is measured in.
            float distanceFactor = innerSize / ((pxRange * 0.5f) * (1f + 2f * spreadPx / (float)innerSize));

            // distance band and edge culling
            float clampDist = 1.001f / distanceFactor;
            const float boundsSlack = 1e-4f;

            int edgeCount = 0;
            for (int c = 0; c < glyph.edgeContours.Count; c++)
                edgeCount += glyph.edgeContours[c].Count;
            float[] bounds = ArrayPool<float>.Shared.Rent(edgeCount * 4);
            float[] crossX = ArrayPool<float>.Shared.Rent(edgeCount * 3);
            int[] crossSign = ArrayPool<int>.Shared.Rent(edgeCount * 3);
            FillBounds(glyph, bounds);

            // stroke runs: glyph-to-local maps, local-to-glyph distance scale, glyph-space reach boxes
            int runCount = glyph.strokes.Count;
            Matrix3x2[] toLocal = runCount == 0 ? Array.Empty<Matrix3x2>() : new Matrix3x2[runCount];
            float[] runScale = runCount == 0 ? Array.Empty<float>() : new float[runCount];
            float[] runBounds = runCount == 0 ? Array.Empty<float>() : new float[runCount * 4];
            for (int r = 0; r < runCount; r++)
            {
                StrokeRun run = glyph.strokes[r];
                if (Matrix3x2.Invert(run.toGlyph, out toLocal[r]))
                    runScale[r] = MathF.Sqrt(MathF.Abs(run.toGlyph.GetDeterminant()));
                RunBounds(run, runBounds, r * 4);
            }

            byte[] pixels = image.Pixels;
            int atlasWidth = image.Width;
            for (int y = 0; y < innerSize; y++)
            {
                float py = ((y + 0.5f) / innerSize) * (normH + 2 * spreadV) - spreadV;
                int crossings = glyph.edgeContours.Count != 0 ? RowCrossings(py, glyph, crossX, crossSign) : 0;

                for (int x = 0; x < innerSize; x++)
                {
                    float px = ((x + 0.5f) / innerSize) * (normW + 2 * spreadU) - spreadU;
                    Vector2 p = new Vector2(px, py);

                    float minR = -1, minG = -1, minB = -1, minAll = -1;
                    if (glyph.edgeContours.Count != 0)
                    {
                        minR = clampDist;
                        minG = clampDist;
                        minB = clampDist;
                        int k = 0;
                        for (int contour = 0; contour < glyph.edgeContours.Count; contour++)
                        {
                            List<Edge> edges = glyph.edgeContours[contour];
                            for (int j = 0; j < edges.Count; j++, k++)
                            {
                                Vector3D<int> color = edges[j].color;
                                if (color.X == 0 && color.Y == 0 && color.Z == 0) continue;

                                float reach = MathF.Max(color.X != 0 ? minR : 0f,
                                    MathF.Max(color.Y != 0 ? minG : 0f, color.Z != 0 ? minB : 0f)) + boundsSlack;
                                int b = k * 4;
                                float dx = MathF.Max(MathF.Max(bounds[b] - px, px - bounds[b + 2]), 0f);
                                float dy = MathF.Max(MathF.Max(bounds[b + 1] - py, py - bounds[b + 3]), 0f);
                                if (dx * dx + dy * dy >= reach * reach) continue;

                                float dist = ClosestTOnBezier(p, edges[j]);
                                if (color.X != 0 && dist < minR) minR = dist;
                                if (color.Y != 0 && dist < minG) minG = dist;
                                if (color.Z != 0 && dist < minB) minB = dist;
                            }
                        }
                        minAll = MathF.Min(minR, MathF.Min(minG, minB));

                        int winding = 0;
                        for (int i = 0; i < crossings; i++)
                            if (crossX[i] > px) winding += crossSign[i];

                        if (glyph.evenOdd ? (winding & 1) == 0 : winding == 0)
                        {
                            minR = -minR;
                            minG = -minG;
                            minB = -minB;
                            minAll = -minAll;
                        }
                    }

                    if (runCount != 0)
                    {
                        float stroke = float.NegativeInfinity;
                        for (int r = 0; r < runCount; r++)
                        {
                            int rb = r * 4;
                            if (runScale[r] == 0f || px < runBounds[rb] - clampDist || py < runBounds[rb + 1] - clampDist
                                || px > runBounds[rb + 2] + clampDist || py > runBounds[rb + 3] + clampDist) continue;
                            stroke = MathF.Max(stroke, StrokeDistance(Vector2.Transform(p, toLocal[r]), glyph.strokes[r]) * runScale[r]);
                        }
                        minR = MathF.Max(minR, stroke);
                        minG = MathF.Max(minG, stroke);
                        minB = MathF.Max(minB, stroke);
                        minAll = MathF.Max(minAll, stroke);
                    }

                    float redDist = Math.Clamp(minR * distanceFactor, -1, 1);
                    float greenDist = Math.Clamp(minG * distanceFactor, -1, 1);
                    float blueDist = Math.Clamp(minB * distanceFactor, -1, 1);
                    float trueDist = Math.Clamp(minAll * distanceFactor, -1, 1);

                    redDist = redDist * 0.5f + 0.5f;
                    greenDist = greenDist * 0.5f + 0.5f;
                    blueDist = blueDist * 0.5f + 0.5f;
                    trueDist = trueDist * 0.5f + 0.5f;

                    int at = ((startY + pad + y) * atlasWidth + startX + pad + x) * 4;
                    pixels[at] = ToByte(redDist);
                    pixels[at + 1] = ToByte(greenDist);
                    pixels[at + 2] = ToByte(blueDist);
                    pixels[at + 3] = ToByte(trueDist);
                }
            }

            ArrayPool<float>.Shared.Return(bounds);
            ArrayPool<float>.Shared.Return(crossX);
            ArrayPool<int>.Shared.Return(crossSign);
        }

        // A [0, 1] channel to a byte, rounded half up and clamped.
        private static byte ToByte(float v) => (byte)Math.Clamp(v * 255f + 0.5f, 0f, 255f);

        // Control-polygon box of every edge, four floats each, in contour order.
        private static void FillBounds(Glyph glyph, float[] bounds)
        {
            int b = 0;
            for (int c = 0; c < glyph.edgeContours.Count; c++)
            {
                List<Edge> edges = glyph.edgeContours[c];
                for (int e = 0; e < edges.Count; e++, b += 4)
                {
                    Edge edge = edges[e];
                    bounds[b] = MathF.Min(MathF.Min(edge.p0.X, edge.c0.X), MathF.Min(edge.c1.X, edge.p1.X));
                    bounds[b + 1] = MathF.Min(MathF.Min(edge.p0.Y, edge.c0.Y), MathF.Min(edge.c1.Y, edge.p1.Y));
                    bounds[b + 2] = MathF.Max(MathF.Max(edge.p0.X, edge.c0.X), MathF.Max(edge.c1.X, edge.p1.X));
                    bounds[b + 3] = MathF.Max(MathF.Max(edge.p0.Y, edge.c0.Y), MathF.Max(edge.c1.Y, edge.p1.Y));
                }
            }
        }

        // A run's glyph-space box, widened by the furthest a cap or join reaches.
        private static void RunBounds(StrokeRun run, float[] bounds, int at)
        {
            float reach = run.halfWidth * (run.join == StrokeJoin.Miter ? MathF.Max(run.miterLimit, 1.5f) : 1.5f);
            Vector2 min = new Vector2(float.MaxValue), max = new Vector2(float.MinValue);
            foreach (Edge e in run.edges)
            {
                min = Vector2.Min(min, Vector2.Min(Vector2.Min(e.p0, e.c0), Vector2.Min(e.c1, e.p1)));
                max = Vector2.Max(max, Vector2.Max(Vector2.Max(e.p0, e.c0), Vector2.Max(e.c1, e.p1)));
            }
            min -= new Vector2(reach);
            max += new Vector2(reach);

            Vector2 a = Vector2.Transform(min, run.toGlyph), b = Vector2.Transform(max, run.toGlyph);
            Vector2 c = Vector2.Transform(new Vector2(min.X, max.Y), run.toGlyph), d = Vector2.Transform(new Vector2(max.X, min.Y), run.toGlyph);
            Vector2 lo = Vector2.Min(Vector2.Min(a, b), Vector2.Min(c, d)), hi = Vector2.Max(Vector2.Max(a, b), Vector2.Max(c, d));
            bounds[at] = lo.X;
            bounds[at + 1] = lo.Y;
            bounds[at + 2] = hi.X;
            bounds[at + 3] = hi.Y;
        }

        // Signed distance to one stroked subpath in its own units, positive inside.
        private static float StrokeDistance(Vector2 p, StrokeRun run)
        {
            List<Edge> edges = run.edges;
            Vector2 startDir = Vector2.Zero, endDir = Vector2.Zero;
            for (int e = 0; e < edges.Count && startDir == Vector2.Zero; e++) startDir = Direction(edges[e], true);
            for (int e = edges.Count - 1; e >= 0 && endDir == Vector2.Zero; e--) endDir = Direction(edges[e], false);
            if (startDir == Vector2.Zero)
            {
                if (run.cap == StrokeCap.Butt) return float.NegativeInfinity;
                startDir = endDir = Vector2.UnitX;
            }

            float nearest = float.MaxValue;
            for (int e = 0; e < edges.Count; e++)
            {
                Edge edge = edges[e];
                float dx = MathF.Max(MathF.Max(MathF.Min(MathF.Min(edge.p0.X, edge.c0.X), MathF.Min(edge.c1.X, edge.p1.X)) - p.X,
                    p.X - MathF.Max(MathF.Max(edge.p0.X, edge.c0.X), MathF.Max(edge.c1.X, edge.p1.X))), 0f);
                float dy = MathF.Max(MathF.Max(MathF.Min(MathF.Min(edge.p0.Y, edge.c0.Y), MathF.Min(edge.c1.Y, edge.p1.Y)) - p.Y,
                    p.Y - MathF.Max(MathF.Max(edge.p0.Y, edge.c0.Y), MathF.Max(edge.c1.Y, edge.p1.Y))), 0f);
                if (dx * dx + dy * dy >= nearest * nearest) continue;
                nearest = MathF.Min(nearest, ClosestTOnBezier(p, edge));
            }
            float s = run.halfWidth - nearest;

            if (!run.closed)
            {
                s = Cap(s, p, edges[0].p0, -startDir, run);
                s = Cap(s, p, edges[^1].p1, endDir, run);
            }
            int joints = run.closed ? edges.Count : edges.Count - 1;
            for (int j = 0; j < joints; j++)
                s = Join(s, p, edges[j], edges[(j + 1) % edges.Count], run);
            return s;
        }

        // Butt cuts the round end back to the end line; square adds a half-width box past it.
        private static float Cap(float s, Vector2 p, Vector2 end, Vector2 outward, StrokeRun run)
        {
            float hw = run.halfWidth;
            Vector2 d = p - end;
            float along = Vector2.Dot(d, outward);
            return run.cap switch
            {
                StrokeCap.Butt => MathF.Min(s, MathF.Max(-along, d.Length() - 1.5f * hw)),
                StrokeCap.Square => MathF.Max(s, -Box(along - hw / 2f, outward.X * d.Y - outward.Y * d.X, hw / 2f, hw)),
                _ => s,
            };
        }

        // Bevel cuts the round join back to the bevel line; miter adds the kite out to the tip.
        private static float Join(float s, Vector2 p, Edge into, Edge from, StrokeRun run)
        {
            if (run.join == StrokeJoin.Round) return s;
            Vector2 ta = Direction(into, false), tb = Direction(from, true);
            if (ta == Vector2.Zero || tb == Vector2.Zero) return s;
            float cross = ta.X * tb.Y - ta.Y * tb.X;
            float turn = MathF.Atan2(MathF.Abs(cross), Vector2.Dot(ta, tb));
            if (turn < 0.01f) return s;

            float hw = run.halfWidth;
            float side = cross > 0f ? -hw : hw;
            Vector2 nA = new Vector2(-ta.Y, ta.X) * side;
            Vector2 nB = new Vector2(-tb.Y, tb.X) * side;
            Vector2 sum = nA + nB;
            Vector2 outward = sum.LengthSquared() > 1e-12f * hw * hw ? Vector2.Normalize(sum) : ta;
            float cosHalf = MathF.Cos(turn / 2f);
            Vector2 v = into.p1;

            if (run.join == StrokeJoin.Miter && cosHalf * run.miterLimit >= 1f)
            {
                Span<Vector2> kite = stackalloc Vector2[] { v, v + nA, v + outward * (hw / cosHalf), v + nB };
                return MathF.Max(s, -Polygon(p, kite));
            }
            return MathF.Min(s, MathF.Max(hw * cosHalf - Vector2.Dot(p - v, outward), Vector2.Distance(p, v) - 1.5f * hw));
        }

        // Unit tangent at an edge's start or end, skipping coincident controls; zero for a point.
        private static Vector2 Direction(Edge edge, bool atStart)
        {
            Vector2 d = atStart ? edge.c0 - edge.p0 : edge.p1 - edge.c1;
            if (d.LengthSquared() < 1e-12f) d = atStart ? edge.c1 - edge.p0 : edge.p1 - edge.c0;
            if (d.LengthSquared() < 1e-12f) d = edge.p1 - edge.p0;
            return d.LengthSquared() < 1e-12f ? Vector2.Zero : Vector2.Normalize(d);
        }

        // Box distance in the box's own axes, outside positive.
        private static float Box(float x, float y, float halfX, float halfY)
        {
            float qx = MathF.Abs(x) - halfX, qy = MathF.Abs(y) - halfY;
            return new Vector2(MathF.Max(qx, 0f), MathF.Max(qy, 0f)).Length() + MathF.Min(MathF.Max(qx, qy), 0f);
        }

        // Polygon distance, outside positive.
        private static float Polygon(Vector2 p, ReadOnlySpan<Vector2> v)
        {
            float d = Vector2.DistanceSquared(p, v[0]);
            float sign = 1f;
            for (int i = 0, j = v.Length - 1; i < v.Length; j = i, i++)
            {
                Vector2 e = v[j] - v[i], w = p - v[i];
                Vector2 b = w - e * Math.Clamp(Vector2.Dot(w, e) / Vector2.Dot(e, e), 0f, 1f);
                d = MathF.Min(d, b.LengthSquared());
                bool c1 = p.Y >= v[i].Y, c2 = p.Y < v[j].Y, c3 = e.X * w.Y > e.Y * w.X;
                if ((c1 && c2 && c3) || (!c1 && !c2 && !c3)) sign = -sign;
            }
            return sign * MathF.Sqrt(d);
        }

        private static float ClosestTOnBezier(Vector2 p, Edge edge)
        {
            // Phase 1: coarse sample to find bracket
            int samples = 32;
            float bestT = 0f;
            float bestDist = Vector2.DistanceSquared(p, edge.p0);

            float d1Sq = Vector2.DistanceSquared(p, edge.p1);
            if (d1Sq < bestDist) { bestDist = d1Sq; bestT = 1f; }

            for (int i = 1; i < samples; i++)
            {
                float t = (float)i / samples;
                float omt = 1f - t;
                Vector2 pt = omt * omt * omt * edge.p0 + 3f * omt * omt * t * edge.c0
                    + 3f * omt * t * t * edge.c1 + t * t * t * edge.p1;
                float dSq = Vector2.DistanceSquared(p, pt);
                if (dSq < bestDist) { bestDist = dSq; bestT = t; }
            }

            // Phase 2: Newton refinement (minimize |B(t) - p|^2)
            // f(t)  = dot(B(t)-p, B'(t))
            // f'(t) = dot(B'(t), B'(t)) + dot(B(t)-p, B''(t))
            float t2 = bestT;
            for (int iter = 0; iter < 4; iter++)
            {
                float omt = 1f - t2;

                // B(t)
                Vector2 bt = omt * omt * omt * edge.p0 + 3f * omt * omt * t2 * edge.c0
                    + 3f * omt * t2 * t2 * edge.c1 + t2 * t2 * t2 * edge.p1;
                // B'(t) = 3(1-t)^2(C0-P0) + 6(1-t)t(C1-C0) + 3t^2(P1-C1)
                Vector2 bt1 = 3f * omt * omt * (edge.c0 - edge.p0) + 6f * omt * t2 * (edge.c1 - edge.c0)
                    + 3f * t2 * t2 * (edge.p1 - edge.c1);
                // B''(t) = 6(1-t)(C1 - 2C0 + P0) + 6t(P1 - 2C1 + C0)
                Vector2 bt2 = 6f * omt * (edge.c1 - 2f * edge.c0 + edge.p0)
                    + 6f * t2 * (edge.p1 - 2f * edge.c1 + edge.c0);

                Vector2 diff = bt - p;
                float f = Vector2.Dot(diff, bt1);
                float fPrime = Vector2.Dot(bt1, bt1) + Vector2.Dot(diff, bt2);

                if (MathF.Abs(fPrime) < 1e-10f) break;

                float step = f / fPrime;
                t2 -= step;
                t2 = Math.Clamp(t2, 0f, 1f);

                if (MathF.Abs(step) < 1e-6f) break;
            }

            // Compare refined result with best sample
            float omt2 = 1f - t2;
            Vector2 refined = omt2 * omt2 * omt2 * edge.p0 + 3f * omt2 * omt2 * t2 * edge.c0
                + 3f * omt2 * t2 * t2 * edge.c1 + t2 * t2 * t2 * edge.p1;
            float refinedDist = Vector2.DistanceSquared(p, refined);
            if (refinedDist < bestDist) { bestDist = refinedDist; }

            return MathF.Sqrt(bestDist);
        }

        private static int SolveCubic(float a, float b, float c, float d, Span<float> roots)
        {
            // Handle degenerate cases
            if (MathF.Abs(a) < 1e-6f)
            {
                return SolveQuadratic(b, c, d, roots);
            }

            // Normalize
            float invA = 1f / a;
            b *= invA;
            c *= invA;
            d *= invA;

            // Depressed cubic: t^3 + pt + q = 0  (substitute t = x - b/3)
            float b2 = b * b;
            float p = c - b2 / 3f;
            float q = d - b * c / 3f + 2f * b2 * b / 27f;
            float shift = b / 3f;

            float disc = q * q / 4f + p * p * p / 27f;

            if (disc > 1e-6f)
            {
                // One real root
                float sqrtDisc = MathF.Sqrt(disc);
                float u = MathF.Cbrt(-q / 2f + sqrtDisc);
                float v = MathF.Cbrt(-q / 2f - sqrtDisc);
                roots[0] = u + v - shift;
                return 1;
            }
            else if (MathF.Abs(disc) <= 1e-6f)
            {
                // Two real roots (one double)
                float u = MathF.Cbrt(-q / 2f);
                roots[0] = 2f * u - shift;
                roots[1] = -u - shift;
                return 2;
            }
            else
            {
                // Three real roots (Vieta's trigonometric method)
                float r = MathF.Sqrt(-p * p * p / 27f);
                float theta = MathF.Acos(Math.Clamp(-q / (2f * r), -1f, 1f));
                float m = 2f * MathF.Cbrt(r);

                roots[0] = m * MathF.Cos(theta / 3f) - shift;
                roots[1] = m * MathF.Cos((theta + 2f * MathF.PI) / 3f) - shift;
                roots[2] = m * MathF.Cos((theta + 4f * MathF.PI) / 3f) - shift;
                return 3;
            }
        }

        private static int SolveQuadratic(float a, float b, float c, Span<float> roots)
        {
            if (MathF.Abs(a) < 1e-6f)
            {
                if (MathF.Abs(b) < 1e-6f) return 0;
                roots[0] = -c / b;
                return 1;
            }

            float disc = b * b - 4f * a * c;
            if (disc < 0) return 0;

            float sqrtDisc = MathF.Sqrt(disc);
            float inv2a = 1f / (2f * a);
            roots[0] = (-b + sqrtDisc) * inv2a;
            roots[1] = (-b - sqrtDisc) * inv2a;
            return 2;
        }

        // Every edge's crossing of the horizontal line at py: x and winding sign.
        private static int RowCrossings(float py, Glyph glyph, float[] crossX, int[] crossSign)
        {
            int count = 0;
            Span<float> roots = stackalloc float[3];

            for (int c = 0; c < glyph.edgeContours.Count; c++)
            {
                List<Edge> edges = glyph.edgeContours[c];
                for (int e = 0; e < edges.Count; e++)
                {
                    Edge edge = edges[e];

                    // Cubic bezier: B(t) = (1-t)^3*P0 + 3(1-t)^2t*C0 + 3(1-t)t^2*C1 + t^3*P1
                    // Solve B_y(t) = p.Y for t, as ay*t^3 + by*t^2 + cy*t + dy0 = 0

                    float ay = -edge.p0.Y + 3f * edge.c0.Y - 3f * edge.c1.Y + edge.p1.Y;
                    float by = 3f * edge.p0.Y - 6f * edge.c0.Y + 3f * edge.c1.Y;
                    float cy = -3f * edge.p0.Y + 3f * edge.c0.Y;
                    float dy0 = edge.p0.Y - py;

                    int rootCount = SolveCubic(ay, by, cy, dy0, roots);

                    for (int i = 0; i < rootCount; i++)
                    {
                        float t = roots[i];
                        if (t < 0f || t >= 1f) continue;

                        // X position of curve at this t
                        float omt = 1f - t;
                        float bx = omt * omt * omt * edge.p0.X + 3f * omt * omt * t * edge.c0.X
                            + 3f * omt * t * t * edge.c1.X + t * t * t * edge.p1.X;

                        // Curve's Y derivative at t: B'_y(t) = 3ay*t^2 + 2by*t + cy
                        float dy = 3f * ay * t * t + 2f * by * t + cy;

                        if (dy == 0f) continue;
                        crossX[count] = bx;
                        crossSign[count] = dy > 0f ? 1 : -1;
                        count++;
                    }
                }
            }
            return count;
        }
    }
}
