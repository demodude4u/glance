using System.Numerics;
using System.Runtime.InteropServices;

namespace Glance;

/// <summary>Round holes of one diameter.</summary>
internal sealed class HoleGroup
{
    public double Diameter;
    public double Depth = double.NaN;       // set only when every hole in the group is equally deep
    public readonly List<(Vector3 Center, Vector3 Axis, float Radius, float Depth)> Holes = new();
}

/// <summary>Facts about a part that take a pass over the whole mesh: its round holes and the stock it fits in.</summary>
internal sealed class PartFacts
{
    public readonly List<HoleGroup> Holes = new();
    public int HoleCount;
    public bool HolesKnown;                 // false when the model is too large to have the topology holes are read from
    public Vector3 Stock;                   // the smallest box found, largest side first
    public Vector3[] StockBox = [];         // its eight corners in model space (bit 0, 1, 2 = far side along each of its axes)
    public bool StockTurned;                // true when that box is smaller than the one along the file's own axes
}

/// <summary>
/// Worked out on a background thread the first time the Details card asks, never when a model is opened.
/// Reads the mesh and the topology Analysis left behind; changes neither.
/// </summary>
internal static class Facts
{
    public static PartFacts Compute(Mesh m)
    {
        var f = new PartFacts();
        Vector3 lo = m.Min, hi = m.Max;
        f.Stock = Sorted(hi - lo);
        f.StockBox = new Vector3[8];
        for (int i = 0; i < 8; i++) f.StockBox[i] = new Vector3((i & 1) == 0 ? lo.X : hi.X, (i & 2) == 0 ? lo.Y : hi.Y, (i & 4) == 0 ? lo.Z : hi.Z);
        try { if (m.Weld != null) Stock(m, f); } catch { }
        try { if (m.TriAdj != null && m.Weld != null) { Holes(m, f); f.HolesKnown = true; } } catch { f.Holes.Clear(); f.HoleCount = 0; }
        return f;
    }

    static Vector3 Sorted(Vector3 v)
    {
        float a = v.X, b = v.Y, c = v.Z;
        if (a < b) (a, b) = (b, a);
        if (b < c) (b, c) = (c, b);
        if (a < b) (a, b) = (b, a);
        return new Vector3(a, b, c);
    }

    // ───────────────────────── Holes ─────────────────────────

    sealed class Wall
    {
        public Vector3 C, Axis; public float R; public double Facing;
        public List<Vector3> Pts = new();
        public int Group = -1;
    }

    /// <summary>
    /// A hole is a cylindrical face that looks inward and goes all the way round. Faces are found the way the
    /// measuring tool finds them (triangles joined across smooth edges); a hole that the CAD system saved as two
    /// half-cylinders is put back together.
    /// </summary>
    static void Holes(Mesh m, PartFacts f)
    {
        int T = m.TriCount;
        int[] adj = m.TriAdj!, weld = m.Weld!;
        float diag = (m.Max - m.Min).Length();
        if (T == 0 || diag <= 0) return;

        var seen = new bool[T];
        var stack = new Stack<int>();
        var tris = new List<int>();
        var walls = new List<Wall>();
        double signed = 0;                      // six times the volume, with its sign: negative means the mesh is inside out
        Vector3 origin = m.Min;

        for (int seed = 0; seed < T; seed++)
        {
            if (seen[seed]) continue;
            tris.Clear(); stack.Push(seed); seen[seed] = true;
            while (stack.Count > 0)
            {
                int t = stack.Pop(); tris.Add(t);
                for (int k = 0; k < 3; k++)
                {
                    int o = adj[t * 3 + k];
                    if (o >= 0 && !seen[o]) { seen[o] = true; stack.Push(o); }
                }
            }

            double area = 0; Vector3 nsum = Vector3.Zero;
            double m00 = 0, m01 = 0, m02 = 0, m11 = 0, m12 = 0, m22 = 0;
            foreach (int t in tris)
            {
                Vector3 p0 = m.P(m.Corner(t, 0)), p1 = m.P(m.Corner(t, 1)), p2 = m.P(m.Corner(t, 2));
                signed += Vector3.Dot(p0 - origin, Vector3.Cross(p1 - origin, p2 - origin));
                Vector3 nn = Vector3.Cross(p1 - p0, p2 - p0);
                float w = nn.Length() * 0.5f;
                if (w <= 0) continue;
                Vector3 n = nn / (w * 2);
                area += w; nsum += n * w;
                m00 += w * n.X * n.X; m01 += w * n.X * n.Y; m02 += w * n.X * n.Z; m11 += w * n.Y * n.Y; m12 += w * n.Y * n.Z; m22 += w * n.Z * n.Z;
            }
            if (tris.Count < 6 || area <= 0) continue;
            if (nsum.Length() / area > 0.9995) continue;                // flat

            // A cylinder's normals all lie in one plane: the direction they share nothing with is its axis.
            Inspect.Eigen(m00 / area, m01 / area, m02 / area, m11 / area, m12 / area, m22 / area, out Vector3 axis, out double l0, out double l1);
            if (!(l0 < 2e-3 && l1 > 0.01)) continue;

            var ids = new HashSet<int>(); var pts = new List<Vector3>();
            foreach (int t in tris)
                for (int k = 0; k < 3; k++)
                {
                    int v = weld[m.Corner(t, k)];
                    if (ids.Add(v)) pts.Add(m.P(v));
                }
            if (pts.Count < 6) continue;
            if (!Inspect.FitCircle(CollectionsMarshal.AsSpan(pts), axis, out Vector3 c, out _, out float r, out float resid, out _, false)) continue;
            if (!(resid < r * 0.012f + diag * 1e-5f) || r <= 0) continue;

            double facing = 0;                  // positive when the face looks away from its axis: a pin, not a hole
            foreach (int t in tris)
            {
                Vector3 p0 = m.P(m.Corner(t, 0)), p1 = m.P(m.Corner(t, 1)), p2 = m.P(m.Corner(t, 2));
                Vector3 radial = (p0 + p1 + p2) / 3 - c;
                radial -= axis * Vector3.Dot(radial, axis);
                facing += Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), radial);
            }
            walls.Add(new Wall { C = c, Axis = axis, R = r, Facing = facing, Pts = pts });
        }

        bool insideOut = signed < 0;
        walls.RemoveAll(w => w.Facing == 0 || (w.Facing > 0) != insideOut);

        // Pieces of one hole: the same radius about the same line, touching along it.
        static (float Lo, float Hi) Extent(Wall w, Vector3 c, Vector3 axis)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (Vector3 p in w.Pts) { float t = Vector3.Dot(p - c, axis); lo = MathF.Min(lo, t); hi = MathF.Max(hi, t); }
            return (lo, hi);
        }
        int groups = 0;
        for (int i = 0; i < walls.Count; i++)
        {
            Wall a = walls[i];
            if (a.Group >= 0) continue;
            a.Group = groups++;
            float tol = MathF.Max(a.R * 0.02f, diag * 2e-5f), reach = MathF.Max(a.R * 0.05f, diag * 1e-4f);
            var (lo, hi) = Extent(a, a.C, a.Axis);
            bool grew = true;
            while (grew)
            {
                grew = false;
                for (int j = i + 1; j < walls.Count; j++)
                {
                    Wall b = walls[j];
                    if (b.Group >= 0 || MathF.Abs(b.R - a.R) > tol || MathF.Abs(Vector3.Dot(a.Axis, b.Axis)) < 0.9995f) continue;
                    Vector3 d = b.C - a.C;
                    if ((d - a.Axis * Vector3.Dot(d, a.Axis)).Length() > tol * 2) continue;
                    var (blo, bhi) = Extent(b, a.C, a.Axis);
                    if (blo > hi + reach || bhi < lo - reach) continue;
                    b.Group = a.Group; lo = MathF.Min(lo, blo); hi = MathF.Max(hi, bhi); grew = true;
                }
            }
        }

        var holes = new List<(Vector3 Center, Vector3 Axis, float Radius, float Depth)>();
        var angles = new List<float>();
        for (int g = 0; g < groups; g++)
        {
            Wall? first = null; float lo = float.MaxValue, hi = float.MinValue;
            angles.Clear();
            Vector3 u = default, v = default;
            foreach (Wall w in walls)
            {
                if (w.Group != g) continue;
                if (first == null)
                {
                    first = w;
                    u = Vector3.Normalize(Vector3.Cross(w.Axis, MathF.Abs(w.Axis.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY));
                    v = Vector3.Cross(w.Axis, u);
                }
                foreach (Vector3 p in w.Pts)
                {
                    Vector3 d = p - first.C;
                    float t = Vector3.Dot(d, first.Axis);
                    lo = MathF.Min(lo, t); hi = MathF.Max(hi, t);
                    angles.Add(MathF.Atan2(Vector3.Dot(d, v), Vector3.Dot(d, u)));
                }
            }
            if (first == null || angles.Count < 6) continue;

            // All the way round: no stretch of the circle without a point on it. A fillet or a slot end leaves half of it bare.
            angles.Sort();
            float gap = angles[0] + MathF.Tau - angles[^1];
            for (int i = 1; i < angles.Count; i++) gap = MathF.Max(gap, angles[i] - angles[i - 1]);
            if (gap > MathF.PI / 4) continue;

            holes.Add((first.C + first.Axis * ((lo + hi) / 2), first.Axis, first.R, hi - lo));
        }

        holes.Sort((x, y) => x.Radius.CompareTo(y.Radius));
        HoleGroup? cur = null;
        foreach (var h in holes)
        {
            double d = h.Radius * 2.0;
            if (cur == null || d - cur.Diameter > Math.Max(0.01, cur.Diameter * 0.002))
            {
                cur = new HoleGroup { Diameter = d };
                f.Holes.Add(cur);
            }
            cur.Holes.Add(h);
        }
        foreach (HoleGroup g in f.Holes)
        {
            double sum = 0, dmin = double.MaxValue, dmax = 0;
            foreach (var h in g.Holes) { sum += h.Radius * 2.0; dmin = Math.Min(dmin, h.Depth); dmax = Math.Max(dmax, h.Depth); }
            g.Diameter = sum / g.Holes.Count;
            if (dmax - dmin <= Math.Max(0.01, dmax * 0.002)) g.Depth = (dmin + dmax) / 2;
            f.HoleCount += g.Holes.Count;
        }
    }

    // ───────────────────────── Stock size ─────────────────────────

    /// <summary>
    /// The smallest box the part fits in when it may be turned. A box is tried resting on each of the part's larger
    /// flat faces and on the file's own axes; for each, the outline seen from above is turned until its rectangle is
    /// smallest. That is how a part is laid on stock, and it is exact for the direction chosen: every vertex is
    /// measured. A part with no flat faces gets the best box about the file's axes.
    /// </summary>
    static void Stock(Mesh m, PartFacts f)
    {
        float[] pos = m.Pos; int[] weld = m.Weld!;
        int V = m.VertexCount, T = m.TriCount;
        float diag = (m.Max - m.Min).Length();
        if (V < 4 || T == 0 || diag <= 0) return;

        int n = 0;
        for (int i = 0; i < V; i++) if (weld[i] == i) n++;
        float[] p = new float[n * 3];
        for (int i = 0, w = 0; i < V; i++)
            if (weld[i] == i) { p[w++] = pos[i * 3]; p[w++] = pos[i * 3 + 1]; p[w++] = pos[i * 3 + 2]; }

        // Directions to rest the box on: the file's axes, then the flat faces that make up most of the surface.
        var dirs = new List<Vector3> { Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY };
        var faces = new Dictionary<long, (Vector3 Sum, double Area)>();
        int step = Math.Max(1, T / 400_000);
        double total = 0;
        for (int t = 0; t < T; t += step)
        {
            Vector3 p0 = m.P(m.Corner(t, 0)), p1 = m.P(m.Corner(t, 1)), p2 = m.P(m.Corner(t, 2));
            Vector3 nn = Vector3.Cross(p1 - p0, p2 - p0);
            float a = nn.Length();
            if (!(a > 0)) continue;
            Vector3 d = nn / a;
            // A face and the one opposite it rest the same box, so they count as one direction.
            const float z = 1e-4f;
            if (d.X < -z || (MathF.Abs(d.X) <= z && (d.Y < -z || (MathF.Abs(d.Y) <= z && d.Z < 0)))) d = -d;
            long key = ((long)MathF.Round(d.X * 120) + 512) | (((long)MathF.Round(d.Y * 120) + 512) << 16) | (((long)MathF.Round(d.Z * 120) + 512) << 32);
            faces.TryGetValue(key, out var e);
            faces[key] = (e.Sum + d * a, e.Area + a);
            total += a;
        }
        var ranked = new List<(Vector3 Sum, double Area)>(faces.Values);
        ranked.Sort((x, y) => y.Area.CompareTo(x.Area));
        foreach (var (sum, area) in ranked)
        {
            if (dirs.Count >= 19 || area < total * 0.01) break;
            if (sum.Length() <= 0) continue;
            Vector3 d = Vector3.Normalize(sum);
            bool known = false;
            foreach (Vector3 k in dirs) if (MathF.Abs(Vector3.Dot(k, d)) > 0.99995f) { known = true; break; }
            if (!known) dirs.Add(d);
        }

        float eps = diag * 1e-7f;
        Vector3 size0 = m.Max - m.Min;
        double best = (double)MathF.Max(size0.X, eps) * MathF.Max(size0.Y, eps) * MathF.Max(size0.Z, eps);
        double first = best;
        float[] xs = new float[n], ys = new float[n];
        const int Cols = 512;
        int[] top = new int[Cols], bot = new int[Cols];
        var outline = new List<Vector2>(Cols * 2);
        var hull = new List<Vector2>(Cols * 2);

        foreach (Vector3 nrm in dirs)
        {
            // In-plane axes: the file's own where the direction is one of them, so that "not turned" means exactly that.
            Vector3 u = nrm == Vector3.UnitZ ? Vector3.UnitX : nrm == Vector3.UnitX ? Vector3.UnitY : nrm == Vector3.UnitY ? Vector3.UnitZ
                : Vector3.Normalize(Vector3.Cross(nrm, MathF.Abs(nrm.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY));
            Vector3 v = Vector3.Cross(nrm, u);
            float xmin = float.MaxValue, xmax = float.MinValue, zmin = float.MaxValue, zmax = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                var q = new Vector3(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]);
                float x = Vector3.Dot(q, u), z = Vector3.Dot(q, nrm);
                xs[i] = x; ys[i] = Vector3.Dot(q, v);
                if (x < xmin) xmin = x; if (x > xmax) xmax = x;
                if (z < zmin) zmin = z; if (z > zmax) zmax = z;
            }

            // The outline from above, near enough to choose an angle by: the highest and lowest point in each of 512 columns.
            Array.Fill(top, -1); Array.Fill(bot, -1);
            float k = xmax > xmin ? (Cols - 1) / (xmax - xmin) : 0;
            for (int i = 0; i < n; i++)
            {
                int c = (int)((xs[i] - xmin) * k);
                if (top[c] < 0 || ys[i] > ys[top[c]]) top[c] = i;
                if (bot[c] < 0 || ys[i] < ys[bot[c]]) bot[c] = i;
            }
            outline.Clear();
            for (int c = 0; c < Cols; c++)
            {
                if (top[c] < 0) continue;
                outline.Add(new Vector2(xs[bot[c]], ys[bot[c]]));
                if (top[c] != bot[c]) outline.Add(new Vector2(xs[top[c]], ys[top[c]]));
            }
            ConvexHull(outline, hull);

            // The smallest rectangle around a convex outline has one side along one of its edges.
            Vector2 dir = Vector2.UnitX; double bestArea = double.MaxValue;
            for (int e = -1; e < hull.Count; e++)
            {
                Vector2 d;
                if (e < 0) d = Vector2.UnitX;                                   // as it lies, first: ties go to "not turned"
                else
                {
                    d = hull[(e + 1) % hull.Count] - hull[e];
                    float len = d.Length();
                    if (len <= diag * 1e-6f) continue;
                    d /= len;
                }
                float a0 = float.MaxValue, a1 = float.MinValue, b0 = float.MaxValue, b1 = float.MinValue;
                foreach (Vector2 h in hull)
                {
                    float a = h.X * d.X + h.Y * d.Y, b = h.Y * d.X - h.X * d.Y;
                    if (a < a0) a0 = a; if (a > a1) a1 = a;
                    if (b < b0) b0 = b; if (b > b1) b1 = b;
                }
                double area = (double)(a1 - a0) * (b1 - b0);
                if (area < bestArea * (1 - 1e-5)) { bestArea = area; dir = d; }
            }

            // Then exactly, over every vertex, for that angle.
            float amin = float.MaxValue, amax = float.MinValue, bmin = float.MaxValue, bmax = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                float a = xs[i] * dir.X + ys[i] * dir.Y, b = ys[i] * dir.X - xs[i] * dir.Y;
                if (a < amin) amin = a; if (a > amax) amax = a;
                if (b < bmin) bmin = b; if (b > bmax) bmax = b;
            }
            float w = amax - amin, h2 = bmax - bmin, dz = zmax - zmin;
            double vol = (double)MathF.Max(w, eps) * MathF.Max(h2, eps) * MathF.Max(dz, eps);
            if (!(vol < best * 0.999)) continue;                                // not worth turning the part for less than that

            best = vol;
            Vector3 A = u * dir.X + v * dir.Y, B = v * dir.X - u * dir.Y;
            f.Stock = Sorted(new Vector3(w, h2, dz));
            for (int i = 0; i < 8; i++)
                f.StockBox[i] = A * ((i & 1) == 0 ? amin : amax) + B * ((i & 2) == 0 ? bmin : bmax) + nrm * ((i & 4) == 0 ? zmin : zmax);
        }
        f.StockTurned = best < first * 0.999;
    }

    /// <summary>Andrew's monotone chain. The result runs counter-clockwise without repeating its first point.</summary>
    static void ConvexHull(List<Vector2> pts, List<Vector2> hull)
    {
        hull.Clear();
        pts.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        if (pts.Count < 3) { hull.AddRange(pts); return; }
        static double Cross(Vector2 o, Vector2 a, Vector2 b) => (double)(a.X - o.X) * (b.Y - o.Y) - (double)(a.Y - o.Y) * (b.X - o.X);
        for (int i = 0; i < pts.Count; i++)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], pts[i]) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(pts[i]);
        }
        int lower = hull.Count + 1;
        for (int i = pts.Count - 2; i >= 0; i--)
        {
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], pts[i]) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(pts[i]);
        }
        hull.RemoveAt(hull.Count - 1);
    }
}
