using System.Numerics;

namespace Glance;

internal enum Kind { Point, Line, Circle, Curve, Plane, Cylinder, Surface }

/// <summary>Something the user can click to measure: a corner, an edge or a face. Everything is in model space.</summary>
internal sealed class Entity
{
    public Kind Kind;
    public Vector3 Pick;            // where it was clicked
    public Vector3 A, B;            // line end points
    public Vector3 Center, Axis;    // circle / cylinder (Axis is unit length); plane normal lives in Axis too
    public float Radius;
    public bool Full;               // a whole circle or a whole cylinder, not just an arc
    public double Length, Area;
    public Vector3[]? Path;         // edge polyline, for drawing
    public uint[]? Tris;            // face triangles as vertex indices, for drawing
    public int Chain = -1, Region = -1;

    public string Name => Kind switch
    {
        Kind.Point => "Corner", Kind.Line => "Edge", Kind.Circle => Full ? "Circle" : "Arc", Kind.Curve => "Curved edge",
        Kind.Plane => "Flat face", Kind.Cylinder => Full ? "Cylinder" : "Rounded face", _ => "Curved face",
    };

    public bool SameAs(Entity o) => Kind == o.Kind && Chain == o.Chain && Region == o.Region
        && (Kind != Kind.Point || (Pick - o.Pick).LengthSquared() < 1e-12f);
}

/// <summary>One measurement between two entities, ready to draw.</summary>
internal sealed class Measurement
{
    public string Title = "";                    // "Distance", "Angle", …
    public double Value; public bool IsAngle;    // headline number
    public Vector3 P1, P2; public bool HasSpan;  // the span to draw a dimension line along
    public readonly List<(string Label, double Value, bool Angle)> Extra = new();
}

internal static class Inspect
{
    // ───────────────────────── Picking ─────────────────────────

    /// <summary>
    /// What is under the cursor: a corner or edge if one is within a few pixels, otherwise the face that was hit.
    /// <paramref name="mvp"/> maps model space to clip space; cursor and viewport are in pixels.
    /// </summary>
    public static Entity? PickAt(Mesh m, Vector3 rayO, Vector3 rayD, Matrix4x4 mvp, float mx, float my, int w, int h,
        float snapPx, Vector4? clip)
    {
        if (m.TriAdj == null) return null;
        bool hit = Analysis.Raycast(m, rayO, rayD, clip, out Vector3 hp, out int tri, out float hd);
        float tol = (m.Max - m.Min).Length() * 0.004f;

        int[]? seg = m.SegVerts;
        if (seg != null && m.SegChain != null)
        {
            float[] pos = m.Pos;
            float best = snapPx * snapPx; int bestSeg = -1; float bestT = 0, bestDepth = float.MaxValue;
            var cur = new Vector2(mx, my);
            for (int s = 0; s < seg.Length / 2; s++)
            {
                int ia = seg[s * 2] * 3, ib = seg[s * 2 + 1] * 3;
                var a = new Vector3(pos[ia], pos[ia + 1], pos[ia + 2]); var b = new Vector3(pos[ib], pos[ib + 1], pos[ib + 2]);
                if (!Project(a, mvp, w, h, out Vector2 sa) || !Project(b, mvp, w, h, out Vector2 sb)) continue;
                Vector2 ab = sb - sa; float l2 = ab.LengthSquared();
                float t = l2 > 1e-6f ? Math.Clamp(Vector2.Dot(cur - sa, ab) / l2, 0, 1) : 0;
                float d2 = (sa + ab * t - cur).LengthSquared();
                if (d2 >= snapPx * snapPx) continue;
                Vector3 q = a + (b - a) * t;
                float depth = Vector3.Dot(q - rayO, rayD);
                // Closest to the cursor wins; when two edges are as good as each other, the one in front does.
                bool better = d2 < best - 2f || (d2 < best + 2f && depth < bestDepth - tol * 0.05f);
                if (!better) continue;
                if (clip is Vector4 cp && Vector3.Dot(q, new Vector3(cp.X, cp.Y, cp.Z)) + cp.W < -tol * 0.1f) continue;
                if (hit && depth > hd + tol) continue;      // hidden behind the surface
                best = MathF.Min(best, d2); bestSeg = s; bestT = t; bestDepth = depth;
            }
            if (bestSeg >= 0)
            {
                int chain = m.SegChain[bestSeg];
                Entity e = FromChain(m, chain);
                Vector3 a = m.P(seg[bestSeg * 2]), b = m.P(seg[bestSeg * 2 + 1]);
                e.Pick = a + (b - a) * bestT;

                // Near the end of an edge the corner itself is what the user means (the nearer end, if both qualify).
                int c0 = m.ChainStart![chain], c1 = m.ChainStart[chain + 1] - 1;
                if (!m.ChainClosed![chain])
                {
                    int corner = -1; float cd = float.MaxValue;
                    foreach (int v in (ReadOnlySpan<int>)[m.ChainVerts![c0], m.ChainVerts[c1]])
                    {
                        if (!Project(m.P(v), mvp, w, h, out Vector2 sv) || (sv - cur).Length() >= snapPx * 1.2f) continue;
                        float dv = Vector3.Dot(m.P(v) - rayO, rayD);
                        if (dv < cd) { cd = dv; corner = v; }
                    }
                    if (corner >= 0) return new Entity { Kind = Kind.Point, Pick = m.P(corner), Center = m.P(corner) };
                }
                return e;
            }
        }
        if (!hit) return null;
        Entity face = FromTriangle(m, tri);
        return new Entity
        {
            Kind = face.Kind, Pick = hp, Center = face.Center, Axis = face.Axis, Radius = face.Radius, Full = face.Full,
            Area = face.Area, Tris = face.Tris, Region = face.Region,
        };
    }

    public static bool Project(Vector3 p, Matrix4x4 mvp, int w, int h, out Vector2 s)
    {
        Vector4 c = Vector4.Transform(new Vector4(p, 1), mvp);
        if (c.W <= 1e-6f) { s = default; return false; }
        s = new Vector2((c.X / c.W * 0.5f + 0.5f) * w, (0.5f - c.Y / c.W * 0.5f) * h);
        return true;
    }

    // ───────────────────────── Edges ─────────────────────────

    static Entity FromChain(Mesh m, int chain)
    {
        int c0 = m.ChainStart![chain], c1 = m.ChainStart[chain + 1];
        var pts = new Vector3[c1 - c0];
        for (int i = 0; i < pts.Length; i++) pts[i] = m.P(m.ChainVerts![c0 + i]);
        bool closed = m.ChainClosed![chain];
        double len = 0;
        for (int i = 1; i < pts.Length; i++) len += (pts[i] - pts[i - 1]).Length();
        float diag = (m.Max - m.Min).Length();
        var e = new Entity { Chain = chain, Path = pts, Length = len, A = pts[0], B = pts[^1], Kind = Kind.Curve };

        if (!closed)
        {
            Vector3 ab = e.B - e.A; float l = ab.Length(); float dev = 0;
            if (l > 0)
            {
                Vector3 dir = ab / l;
                foreach (var p in pts) dev = MathF.Max(dev, Vector3.Cross(p - e.A, dir).Length());
                if (dev <= MathF.Max(l * 1e-3f, diag * 1e-5f)) { e.Kind = Kind.Line; e.Axis = dir; e.Center = (e.A + e.B) * 0.5f; return e; }
            }
        }

        int n = closed ? pts.Length - 1 : pts.Length;      // a closed chain repeats its first point
        if (n >= 4 && FitCircle(pts.AsSpan(0, n), null, out Vector3 center, out Vector3 normal, out float r, out float resid, out float sweep, closed)
            && resid < r * 0.012f + diag * 1e-5f && sweep > 0.45f)
        {
            e.Kind = Kind.Circle; e.Center = center; e.Axis = normal; e.Radius = r; e.Full = closed || sweep > 6.2f;
        }
        return e;
    }

    /// <summary>Least-squares circle through 3D points. With <paramref name="axis"/> the plane is given; otherwise it is found.</summary>
    internal static bool FitCircle(ReadOnlySpan<Vector3> pts, Vector3? axis, out Vector3 center, out Vector3 normal, out float radius,
        out float residual, out float sweep, bool closed)
    {
        center = default; normal = default; radius = 0; residual = float.MaxValue; sweep = 0;
        int n = pts.Length;
        Vector3 c = Vector3.Zero;
        foreach (var p in pts) c += p;
        c /= n;
        if (axis is Vector3 ax) normal = ax;
        else
        {
            Vector3 nn = Vector3.Zero;                      // Newell's method
            for (int i = 0; i < n; i++)
            {
                Vector3 a = pts[i] - c, b = pts[(i + 1) % n] - c;
                nn += Vector3.Cross(a, b);
            }
            if (nn.Length() < 1e-20f) return false;
            normal = Vector3.Normalize(nn);
        }
        Vector3 u = Vector3.Normalize(Vector3.Cross(normal, MathF.Abs(normal.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY));
        Vector3 v = Vector3.Cross(normal, u);

        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0, sxz = 0, syz = 0, sz = 0, planar = 0;
        foreach (var p in pts)
        {
            Vector3 d = p - c;
            double x = Vector3.Dot(d, u), y = Vector3.Dot(d, v), z = x * x + y * y;
            sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y; sxz += x * z; syz += y * z; sz += z;
            if (axis == null) planar = Math.Max(planar, Math.Abs(Vector3.Dot(d, normal)));
        }
        // Solve [sxx sxy sx; sxy syy sy; sx sy n] [a b c]ᵀ = [sxz syz sz]ᵀ
        double det = sxx * (syy * n - sy * sy) - sxy * (sxy * n - sy * sx) + sx * (sxy * sy - syy * sx);
        if (Math.Abs(det) < 1e-30) return false;
        double a2 = (sxz * (syy * n - sy * sy) - sxy * (syz * n - sy * sz) + sx * (syz * sy - syy * sz)) / det;
        double b2 = (sxx * (syz * n - sy * sz) - sxz * (sxy * n - sy * sx) + sx * (sxy * sz - syz * sx)) / det;
        double c2 = (sxx * (syy * sz - sy * syz) - sxy * (sxy * sz - syz * sx) + sxz * (sxy * sy - syy * sx)) / det;
        double cx = a2 / 2, cy = b2 / 2, rr = c2 + cx * cx + cy * cy;
        if (rr <= 0) return false;
        radius = (float)Math.Sqrt(rr);
        center = c + u * (float)cx + v * (float)cy;

        float worst = (float)planar; double prev = 0, total = 0; bool have = false;
        Span<bool> bins = stackalloc bool[72];
        foreach (var p in pts)
        {
            Vector3 d = p - center;
            double x = Vector3.Dot(d, u), y = Vector3.Dot(d, v);
            worst = MathF.Max(worst, MathF.Abs((float)Math.Sqrt(x * x + y * y) - radius));
            double ang = Math.Atan2(y, x);
            bins[Math.Clamp((int)((ang + Math.PI) / (2 * Math.PI) * 72), 0, 71)] = true;
            if (have) { double da = ang - prev; while (da > Math.PI) da -= 2 * Math.PI; while (da < -Math.PI) da += 2 * Math.PI; total += da; }
            prev = ang; have = true;
        }
        residual = worst;
        if (axis != null) { int filled = 0; foreach (bool b in bins) if (b) filled++; sweep = filled / 72f * MathF.Tau; }
        else sweep = closed ? MathF.Tau : (float)Math.Abs(total);
        return true;
    }

    // ───────────────────────── Faces ─────────────────────────

    /// <summary>The face region containing a triangle: flood-filled across smooth edges, then classified. Cached per mesh.</summary>
    static Entity FromTriangle(Mesh m, int seed)
    {
        int T = m.TriCount;
        m.TriRegion ??= new int[T];
        int id = m.TriRegion[seed];
        if (id > 0) return m.Regions[id - 1];

        id = m.Regions.Count + 1;
        int[] adj = m.TriAdj!; int[] region = m.TriRegion;
        var stack = new Stack<int>(); var tris = new List<int>();
        stack.Push(seed); region[seed] = id;
        while (stack.Count > 0)
        {
            int t = stack.Pop(); tris.Add(t);
            for (int k = 0; k < 3; k++)
            {
                int o = adj[t * 3 + k];
                if (o >= 0 && region[o] == 0) { region[o] = id; stack.Push(o); }
            }
        }

        var e = new Entity { Region = id, Kind = Kind.Surface, Tris = new uint[tris.Count * 3] };
        double area = 0; Vector3 nsum = Vector3.Zero, csum = Vector3.Zero;
        double m00 = 0, m01 = 0, m02 = 0, m11 = 0, m12 = 0, m22 = 0;
        for (int i = 0; i < tris.Count; i++)
        {
            int t = tris[i];
            int a = m.Corner(t, 0), b = m.Corner(t, 1), c = m.Corner(t, 2);
            e.Tris[i * 3] = (uint)a; e.Tris[i * 3 + 1] = (uint)b; e.Tris[i * 3 + 2] = (uint)c;
            Vector3 p0 = m.P(a), p1 = m.P(b), p2 = m.P(c);
            Vector3 nn = Vector3.Cross(p1 - p0, p2 - p0);
            float w = nn.Length() * 0.5f;
            if (w <= 0) continue;
            Vector3 n = nn / (w * 2);
            area += w; nsum += n * w; csum += (p0 + p1 + p2) * (w / 3);
            m00 += w * n.X * n.X; m01 += w * n.X * n.Y; m02 += w * n.X * n.Z; m11 += w * n.Y * n.Y; m12 += w * n.Y * n.Z; m22 += w * n.Z * n.Z;
        }
        e.Area = area;
        m.Regions.Add(e);
        if (area <= 0) return e;
        Vector3 centroid = csum / (float)area;
        e.Center = centroid;

        if (nsum.Length() / area > 0.9995)
        {
            e.Kind = Kind.Plane; e.Axis = Vector3.Normalize(nsum);
            return e;
        }

        // A cylinder's normals all lie in one plane: the direction they share nothing with is its axis.
        Eigen(m00 / area, m01 / area, m02 / area, m11 / area, m12 / area, m22 / area, out Vector3 axis, out double l0, out double l1);
        if (l0 < 2e-3 && l1 > 0.01)
        {
            var seen = new HashSet<int>(); var pts = new List<Vector3>();
            foreach (uint v in e.Tris) if (seen.Add(m.Weld![v])) pts.Add(m.P((int)v));
            float diag = (m.Max - m.Min).Length();
            if (pts.Count >= 6 && FitCircle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pts), axis, out Vector3 c, out _, out float r, out float resid, out float sweep, false)
                && resid < r * 0.012f + diag * 1e-5f)
            {
                e.Kind = Kind.Cylinder; e.Axis = axis; e.Radius = r; e.Full = sweep > 6.0f;
                e.Center = c + axis * Vector3.Dot(centroid - c, axis);
            }
        }
        return e;
    }

    /// <summary>Smallest eigenvector of a symmetric 3×3 matrix (Jacobi), plus the two smallest eigenvalues.</summary>
    internal static void Eigen(double a00, double a01, double a02, double a11, double a12, double a22, out Vector3 smallest, out double l0, out double l1)
    {
        double[,] a = { { a00, a01, a02 }, { a01, a11, a12 }, { a02, a12, a22 } };
        double[,] v = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        for (int sweep = 0; sweep < 24; sweep++)
        {
            double off = Math.Abs(a[0, 1]) + Math.Abs(a[0, 2]) + Math.Abs(a[1, 2]);
            if (off < 1e-14) break;
            for (int p = 0; p < 2; p++)
                for (int q = p + 1; q < 3; q++)
                {
                    if (Math.Abs(a[p, q]) < 1e-18) continue;
                    double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0) t = 1;
                    double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                    for (int k = 0; k < 3; k++)
                    {
                        double akp = a[k, p], akq = a[k, q];
                        a[k, p] = c * akp - s * akq; a[k, q] = s * akp + c * akq;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        double apk = a[p, k], aqk = a[q, k];
                        a[p, k] = c * apk - s * aqk; a[q, k] = s * apk + c * aqk;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq; v[k, q] = s * vkp + c * vkq;
                    }
                }
        }
        int i0 = 0;
        for (int i = 1; i < 3; i++) if (a[i, i] < a[i0, i0]) i0 = i;
        int i1 = -1;
        for (int i = 0; i < 3; i++) if (i != i0 && (i1 < 0 || a[i, i] < a[i1, i1])) i1 = i;
        l0 = a[i0, i0]; l1 = a[i1, i1];
        smallest = Vector3.Normalize(new Vector3((float)v[0, i0], (float)v[1, i0], (float)v[2, i0]));
    }

    // ───────────────────────── Measuring ─────────────────────────

    enum Cat { Point, Line, Axis, Plane }

    static Cat CatOf(Entity e) => e.Kind switch
    {
        Kind.Line => Cat.Line, Kind.Circle or Kind.Cylinder => Cat.Axis, Kind.Plane => Cat.Plane, _ => Cat.Point,
    };

    static Vector3 PointOf(Entity e) => e.Kind is Kind.Point or Kind.Circle ? e.Center : e.Pick;

    static bool Parallel3(Vector3 a, Vector3 b) => MathF.Abs(Vector3.Dot(a, b)) > 0.99985f;       // within 1°

    static double AngleBetween(Vector3 a, Vector3 b, bool fold)
    {
        double d = Math.Clamp(Vector3.Dot(a, b), -1, 1);
        return Math.Acos(fold ? Math.Abs(d) : d) * 180 / Math.PI;
    }

    static Vector3 ClosestOnLine(Vector3 p, Vector3 a, Vector3 dir) => a + dir * Vector3.Dot(p - a, dir);

    /// <summary>The natural measurement between two picked things.</summary>
    public static Measurement Between(Entity first, Entity second)
    {
        Entity a = first, b = second;
        if (CatOf(a) < CatOf(b)) (a, b) = (b, a);         // higher category first: Plane > Axis > Line > Point
        Cat ca = CatOf(a), cb = CatOf(b);
        var r = new Measurement { Title = "Distance", HasSpan = true };

        if (ca == Cat.Plane && cb == Cat.Plane)
        {
            if (Parallel3(a.Axis, b.Axis))
            {
                float d = Vector3.Dot(b.Pick - a.Pick, a.Axis);
                r.P1 = b.Pick - a.Axis * d; r.P2 = b.Pick;
            }
            else return Angle(r, a.Axis, b.Axis, a.Pick, b.Pick, "between faces");
        }
        else if (ca == Cat.Plane)
        {
            if (cb == Cat.Line && !Parallel3Perp(b.Axis, a.Axis))
            {
                double ang = 90 - AngleBetween(b.Axis, a.Axis, true);
                r.Title = "Angle"; r.IsAngle = true; r.Value = ang; r.P1 = a.Pick; r.P2 = b.Center; r.HasSpan = false;
                return r;
            }
            Vector3 p = cb == Cat.Axis ? b.Center : cb == Cat.Line ? b.Center : PointOf(b);
            float d = Vector3.Dot(p - a.Pick, a.Axis);
            r.P1 = p - a.Axis * d; r.P2 = p;
        }
        else if (ca == Cat.Axis && cb == Cat.Axis)
        {
            if (Parallel3(a.Axis, b.Axis))
            {
                Vector3 d = b.Center - a.Center, along = a.Axis * Vector3.Dot(d, a.Axis), across = d - along;
                if (across.Length() > MathF.Max(a.Radius, b.Radius) * 1e-3f)
                {
                    r.Title = "Center distance"; r.P1 = a.Center + along; r.P2 = b.Center;      // between the two axes
                }
                else { r.Title = "Distance along axis"; r.P1 = a.Center; r.P2 = b.Center; }
            }
            else
            {
                r.P1 = a.Center; r.P2 = b.Center;
                r.Extra.Add(("Angle between axes", AngleBetween(a.Axis, b.Axis, true), true));
                r.Title = "Center distance";
            }
        }
        else if (ca == Cat.Axis)
        {
            Vector3 p = cb == Cat.Line ? b.Center : PointOf(b);
            if (a.Kind == Kind.Cylinder || (cb == Cat.Line && Parallel3(a.Axis, b.Axis)))
            {
                r.Title = "Distance to axis"; r.P1 = ClosestOnLine(p, a.Center, a.Axis); r.P2 = p;
            }
            else { r.Title = "Distance to center"; r.P1 = a.Center; r.P2 = p; }
        }
        else if (ca == Cat.Line && cb == Cat.Line)
        {
            if (!Parallel3(a.Axis, b.Axis)) return Angle(r, a.Axis, b.Axis, a.Center, b.Center, "between edges");
            r.P2 = b.Center; r.P1 = ClosestOnLine(b.Center, a.A, a.Axis);
        }
        else if (ca == Cat.Line)
        {
            Vector3 p = PointOf(b);
            r.P1 = ClosestOnLine(p, a.A, a.Axis); r.P2 = p;
        }
        else { r.P1 = PointOf(first); r.P2 = PointOf(second); }

        Vector3 span = r.P2 - r.P1;
        r.Value = span.Length();
        r.Extra.Add(("ΔX", MathF.Abs(span.X), false)); r.Extra.Add(("ΔY", MathF.Abs(span.Y), false)); r.Extra.Add(("ΔZ", MathF.Abs(span.Z), false));
        return r;
    }

    static bool Parallel3Perp(Vector3 dir, Vector3 normal) => MathF.Abs(Vector3.Dot(dir, normal)) < 0.0175f;   // line lies in / parallel to the plane

    static Measurement Angle(Measurement r, Vector3 da, Vector3 db, Vector3 pa, Vector3 pb, string what)
    {
        double ang = AngleBetween(da, db, true);
        r.Title = "Angle"; r.IsAngle = true; r.Value = ang; r.P1 = pa; r.P2 = pb; r.HasSpan = false;
        if (ang > 0.05 && ang < 89.95) r.Extra.Add(("or", 180 - ang, true));
        return r;
    }
}
