using System.Numerics;

namespace ArctisAurora.Core.UI.Graph
{
    // A force-directed layout: Barnes–Hut repulsion, springs along edges and a pull to the origin, cooling until settled.
    public sealed class GraphLayout
    {
        // forces
        private const float Repulsion = 900f;
        private const float Spring = 0.08f;
        private const float SpringLength = 40f;
        private const float Centring = 0.02f;
        private const float Theta = 0.9f;
        private const float MinDistance = 1f;

        // cooling
        private const float Damping = 0.6f;
        private const float AlphaDecay = 0.0228f;
        private const float AlphaMin = 0.001f;
        private const float ReheatAlpha = 0.3f;
        private const int MaxDepth = 24;

        private struct Quad
        {
            public Vector2 centre;
            public float half;
            public Vector2 sum;
            public int mass;
            public int body;
            public int child;
        }

        public readonly int count;
        public readonly Vector2[] position;
        private readonly Vector2[] velocity;
        private readonly Vector2[] force;
        public readonly bool[] pinned;
        private readonly (int a, int b)[] edges;

        private Quad[] quads;
        private int quadCount;
        private int[] stack;

        private float alpha = 1f;
        public bool settled => alpha < AlphaMin;

        public GraphLayout(int count, (int a, int b)[] edges)
        {
            this.count = count;
            position = new Vector2[count];
            velocity = new Vector2[count];
            force = new Vector2[count];
            pinned = new bool[count];
            this.edges = edges;
            quads = new Quad[Math.Max(16, count * 8)];
            stack = new int[quads.Length];

            float golden = MathF.PI * (3f - MathF.Sqrt(5f));
            for (int i = 0; i < count; i++)
            {
                float radius = 10f * MathF.Sqrt(0.5f + i);
                position[i] = new Vector2(radius * MathF.Cos(i * golden), radius * MathF.Sin(i * golden));
            }
        }

        // Places a node where it stays until unpinned.
        public void Pin(int node, in Vector2 at)
        {
            position[node] = at;
            velocity[node] = Vector2.Zero;
            pinned[node] = true;
        }

        public void Unpin(int node) => pinned[node] = false;

        public void Reheat() => alpha = MathF.Max(alpha, ReheatAlpha);

        // One tick of the simulation.
        public void Step()
        {
            if (count == 0) { alpha = 0f; return; }

            Vector2[] position = this.position;
            Vector2[] velocity = this.velocity;
            Vector2[] force = this.force;
            bool[] pinned = this.pinned;
            (int a, int b)[] edges = this.edges;

            BuildTree();
            for (int i = 0; i < position.Length; i++)
                force[i] = Repel(i) - position[i] * Centring;

            for (int e = 0; e < edges.Length; e++)
            {
                (int a, int b) = edges[e];
                Vector2 d = position[b] - position[a];
                float length = MathF.Max(d.Length(), MinDistance);
                Vector2 pull = d * (Spring * (length - SpringLength) / length);
                force[a] += pull;
                force[b] -= pull;
            }

            float heat = alpha;
            for (int i = 0; i < position.Length; i++)
            {
                if (pinned[i]) { velocity[i] = Vector2.Zero; continue; }
                velocity[i] = (velocity[i] + force[i] * heat) * Damping;
                position[i] += velocity[i];
            }
            alpha = heat - heat * AlphaDecay;
        }

        #region ---- Barnes–Hut ----
        private void BuildTree()
        {
            Vector2[] position = this.position;
            Vector2 min = position[0], max = position[0];
            for (int i = 1; i < position.Length; i++)
            {
                min = Vector2.Min(min, position[i]);
                max = Vector2.Max(max, position[i]);
            }
            Vector2 size = max - min;
            quadCount = 0;
            NewQuad((min + max) * 0.5f, MathF.Max(MathF.Max(size.X, size.Y) * 0.5f, MinDistance) + MinDistance);

            for (int i = 0; i < position.Length; i++)
                Insert(i);
        }

        private int NewQuad(Vector2 centre, float half)
        {
            if (quadCount == quads.Length)
            {
                Array.Resize(ref quads, quads.Length * 2);
                Array.Resize(ref stack, quads.Length);
            }
            quads[quadCount] = new Quad { centre = centre, half = half, body = -1, child = -1 };
            return quadCount++;
        }

        private void Insert(int body)
        {
            Vector2 p = position[body];
            int q = 0;
            for (int depth = 0; ; depth++)
            {
                ref Quad quad = ref quads[q];
                quad.sum += p;
                quad.mass++;
                if (quad.mass == 1) { quad.body = body; return; }
                if (quad.child < 0)
                {
                    if (depth >= MaxDepth) { quad.body = -2; return; }
                    int held = quad.body;
                    Vector2 centre = quad.centre;
                    float half = quad.half * 0.5f;
                    int first = NewQuad(centre + new Vector2(-half, -half), half);
                    NewQuad(centre + new Vector2(half, -half), half);
                    NewQuad(centre + new Vector2(-half, half), half);
                    NewQuad(centre + new Vector2(half, half), half);
                    quads[q].child = first;
                    quads[q].body = -1;

                    ref Quad moved = ref quads[ChildFor(q, in position[held])];
                    moved.sum = position[held];
                    moved.mass = 1;
                    moved.body = held;
                }
                q = ChildFor(q, in p);
            }
        }

        private int ChildFor(int q, in Vector2 p) =>
            quads[q].child + (p.X >= quads[q].centre.X ? 1 : 0) + (p.Y >= quads[q].centre.Y ? 2 : 0);

        private Vector2 Repel(int body)
        {
            Vector2 p = position[body];
            Vector2 total = Vector2.Zero;
            int top = 0;
            stack[top++] = 0;
            while (top > 0)
            {
                ref Quad quad = ref quads[stack[--top]];
                if (quad.mass == 0 || quad.body == body) continue;

                Vector2 d = p - quad.sum / quad.mass;
                float distance2 = d.LengthSquared();
                float width = quad.half * 2f;
                if (quad.child >= 0 && width * width >= Theta * Theta * distance2)
                {
                    for (int c = 0; c < 4; c++)
                        stack[top++] = quad.child + c;
                    continue;
                }
                if (distance2 < MinDistance) d = new Vector2(body % 2 == 0 ? MinDistance : -MinDistance, 0f);
                total += d * (Repulsion * quad.mass / MathF.Max(distance2, MinDistance));
            }
            return total;
        }
        #endregion
    }
}
