using System.Numerics;

namespace Glance;

/// <summary>
/// Background pass after a model is on screen: welds vertices, finds feature edges and face adjacency
/// (the topology the measuring tools work on), smooth normals for STL, surface area and volume.
/// </summary>
internal static class Analysis
{
    const int MaxTris = 6_000_000;

    public static void Run(Mesh m)
    {
        try { Compute(m); } catch { }
        m.Analyzed = true;
    }

    static void Compute(Mesh m)
    {
        int V = m.VertexCount, T = m.TriCount;
        float[] pos = m.Pos; uint[]? idx = m.Idx; uint[]? faceId = m.FaceId;
        if (T == 0 || T > MaxTris) return;

        // 1. Weld vertices that share a position (quantised to a millionth of the model size).
        float diag = (m.Max - m.Min).Length();
        double inv = diag > 0 ? 1.0 / (diag * 2e-6) : 1.0;
        Vector3 min = m.Min;
        int cap = 1; while (cap < V * 2) cap <<= 1;
        int[] slots = new int[cap];
        int[] remap = new int[V];
        int mask = cap - 1;
        for (int i = 0; i < V; i++)
        {
            int qx = (int)Math.Round((pos[i * 3] - min.X) * inv), qy = (int)Math.Round((pos[i * 3 + 1] - min.Y) * inv), qz = (int)Math.Round((pos[i * 3 + 2] - min.Z) * inv);
            uint h = (uint)qx * 73856093u ^ (uint)qy * 19349663u ^ (uint)qz * 83492791u;
            h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12;
            int s = (int)(h & (uint)mask);
            while (true)
            {
                int e = slots[s];
                if (e == 0) { slots[s] = i + 1; remap[i] = i; break; }
                int j = e - 1;
                if ((int)Math.Round((pos[j * 3] - min.X) * inv) == qx && (int)Math.Round((pos[j * 3 + 1] - min.Y) * inv) == qy && (int)Math.Round((pos[j * 3 + 2] - min.Z) * inv) == qz)
                { remap[i] = j; break; }
                s = (s + 1) & mask;
            }
        }
        slots = null!;

        // 1b. Meshes without normals (STL) get crease-aware smooth shading; sharp corners stay sharp.
        if (m.Nrm == null && idx == null && T <= 4_000_000)
        {
            float[] sn = new float[pos.Length];
            Loaders.SmoothNormals(pos, remap, T * 3, V, sn);
            m.SmoothNrm = sn;
        }

        // 2. Edge table: the (up to two) faces on each edge, and whether the edge is a feature.
        //    With CAD face ids a feature is a boundary between two CAD faces; otherwise it is a crease.
        int ecap = 1; while (ecap < T * 4) ecap <<= 1;
        ulong[] keys = new ulong[ecap];
        int[] first = new int[ecap], second = new int[ecap];
        byte[] info = new byte[ecap];        // low bits: face count (saturating at 3); 0x40: first face ran lo→hi; 0x80: feature
        int emask = ecap - 1;
        const float cosCrease = 0.866f;      // 30°
        double area = 0, vol = 0;
        Span<int> c = stackalloc int[3];
        Span<int> o = stackalloc int[3];

        for (int t = 0; t < T; t++)
        {
            Corners(idx, remap, t, c);
            if (c[0] == c[1] || c[1] == c[2] || c[0] == c[2]) continue;
            Vector3 n = Normal(pos, c[0], c[1], c[2], out Vector3 p0, out Vector3 p1, out Vector3 p2);
            float nl = n.Length();
            area += nl * 0.5;
            vol += Vector3.Dot(p0 - min, Vector3.Cross(p1 - min, p2 - min)) / 6.0;
            if (nl <= 0) continue;

            for (int e = 0; e < 3; e++)
            {
                int a = c[e], b = c[(e + 1) % 3];
                bool fwd = a < b;
                int lo = fwd ? a : b, hi = fwd ? b : a;
                ulong key = ((ulong)(uint)(lo + 1) << 32) | (uint)(hi + 1);
                ulong hh = key * 0x9E3779B97F4A7C15ul;
                int s = (int)(hh >> 34) & emask;
                while (true)
                {
                    ulong k = keys[s];
                    if (k == 0) { keys[s] = key; first[s] = t; second[s] = -1; info[s] = (byte)(1 | (fwd ? 0x40 : 0)); break; }
                    if (k == key)
                    {
                        byte inf = info[s];
                        int count = inf & 3;
                        if (count == 1)
                        {
                            second[s] = t;
                            if (faceId != null) { if (faceId[first[s]] != faceId[t]) inf |= 0x80; }
                            else
                            {
                                Corners(idx, remap, first[s], o);
                                Vector3 n1 = Normal(pos, o[0], o[1], o[2], out _, out _, out _);
                                float d = Vector3.Dot(n1, n) / (n1.Length() * nl);
                                if (((inf & 0x40) != 0) == fwd) d = -d;     // the two faces are wound opposite ways
                                if (d < cosCrease) inf |= 0x80;
                            }
                        }
                        if (count < 3) inf = (byte)((inf & 0xC0) | (count + 1));
                        info[s] = inf;
                        break;
                    }
                    s = (s + 1) & emask;
                }
            }
        }

        // 3. Face adjacency across smooth edges, and the list of feature segments.
        int[] adj = new int[T * 3];
        Array.Fill(adj, -1);
        bool closed = true; int lines = 0, total = 0, open = 0;
        for (int s = 0; s < ecap; s++)
        {
            ulong k = keys[s];
            if (k == 0) continue;
            total++;
            int count = info[s] & 3;
            if (count != 2) { closed = false; if (count == 1) open++; }
            if (count != 2 || (info[s] & 0x80) != 0) { lines++; continue; }
            int lo = (int)(k >> 32) - 1, hi = (int)(k & 0xFFFFFFFF) - 1;
            Link(idx, remap, adj, first[s], lo, hi, second[s], c);
            Link(idx, remap, adj, second[s], lo, hi, first[s], c);
        }
        m.Area = area; m.Closed = closed && total > 0; m.Volume = Math.Abs(vol); m.OpenEdges = open;
        m.Weld = remap; m.TriAdj = adj;

        // A noisy scan would turn into a hairball; only keep edges when they outline the shape.
        if (lines == 0 || lines > 3_000_000 || (total > 20000 && lines > total * 0.6)) return;
        float[] el = new float[lines * 6];
        int[] sv = new int[lines * 2];
        int w = 0, q = 0;
        for (int s = 0; s < ecap; s++)
        {
            ulong k = keys[s];
            if (k == 0) continue;
            int count = info[s] & 3;
            if (count == 2 && (info[s] & 0x80) == 0) continue;
            int lo = (int)(k >> 32) - 1, hi = (int)(k & 0xFFFFFFFF) - 1;
            sv[q++] = lo; sv[q++] = hi;
            el[w++] = pos[lo * 3]; el[w++] = pos[lo * 3 + 1]; el[w++] = pos[lo * 3 + 2];
            el[w++] = pos[hi * 3]; el[w++] = pos[hi * 3 + 1]; el[w++] = pos[hi * 3 + 2];
        }
        keys = null!; first = second = null!; info = null!;
        m.SegVerts = sv;
        BuildChains(m, sv, pos);
        m.EdgeLines = el;
    }

    static void Link(uint[]? idx, int[] remap, int[] adj, int tri, int lo, int hi, int other, Span<int> c)
    {
        Corners(idx, remap, tri, c);
        for (int e = 0; e < 3; e++)
        {
            int a = c[e], b = c[(e + 1) % 3];
            if ((a == lo && b == hi) || (a == hi && b == lo)) { adj[tri * 3 + e] = other; return; }
        }
    }

    /// <summary>Strings feature segments into chains, breaking at junctions and at corners sharper than 40°.</summary>
    static void BuildChains(Mesh m, int[] seg, float[] pos)
    {
        int S = seg.Length / 2;
        int[] segChain = new int[S];
        if (S > 700_000)
        {
            // Too many to be worth tracing: every segment stands on its own.
            int[] st = new int[S + 1], cv = new int[S * 2];
            for (int i = 0; i < S; i++) { segChain[i] = i; st[i] = i * 2; cv[i * 2] = seg[i * 2]; cv[i * 2 + 1] = seg[i * 2 + 1]; }
            st[S] = S * 2;
            m.SegChain = segChain; m.ChainStart = st; m.ChainVerts = cv; m.ChainClosed = new bool[S];
            return;
        }

        var ends = new Dictionary<int, (int A, int B, int N)>(S);
        for (int s = 0; s < S; s++)
            for (int k = 0; k < 2; k++)
            {
                int v = seg[s * 2 + k];
                ends.TryGetValue(v, out var e);
                if (e.N == 0) e.A = s; else if (e.N == 1) e.B = s;
                e.N++;
                ends[v] = e;
            }

        int Other(int s, int v) => seg[s * 2] == v ? seg[s * 2 + 1] : seg[s * 2];
        Vector3 P(int v) => new(pos[v * 3], pos[v * 3 + 1], pos[v * 3 + 2]);
        bool Break(int v)
        {
            var e = ends[v];
            if (e.N != 2) return true;
            Vector3 p = P(v), da = P(Other(e.A, v)) - p, db = P(Other(e.B, v)) - p;
            float l = da.Length() * db.Length();
            return l <= 0 || Vector3.Dot(da, db) / l > -0.766f;
        }
        int Next(int v, int s) { var e = ends[v]; return e.A == s ? e.B : e.A; }

        bool[] visited = new bool[S];
        var starts = new List<int>(); var verts = new List<int>(S + 16); var closedList = new List<bool>();
        for (int s0 = 0; s0 < S; s0++)
        {
            if (visited[s0]) continue;
            int sv = seg[s0 * 2], sc = s0; bool loop = false; int guard = 0;
            while (!Break(sv))
            {
                int ns = Next(sv, sc);
                sv = Other(ns, sv); sc = ns;
                if (sc == s0) { loop = true; break; }
                if (++guard > S) break;
            }
            if (loop) { sc = s0; sv = seg[s0 * 2]; }

            int id = starts.Count;
            starts.Add(verts.Count); verts.Add(sv);
            int cv = sv, cs = sc;
            while (true)
            {
                visited[cs] = true; segChain[cs] = id;
                cv = Other(cs, cv); verts.Add(cv);
                if (!loop && Break(cv)) break;
                int ns = Next(cv, cs);
                if (visited[ns]) break;
                cs = ns;
            }
            closedList.Add(loop);
        }
        starts.Add(verts.Count);
        m.SegChain = segChain; m.ChainStart = starts.ToArray(); m.ChainVerts = verts.ToArray(); m.ChainClosed = closedList.ToArray();
    }

    static void Corners(uint[]? idx, int[] remap, int t, Span<int> c)
    {
        if (idx != null) { c[0] = remap[idx[t * 3]]; c[1] = remap[idx[t * 3 + 1]]; c[2] = remap[idx[t * 3 + 2]]; }
        else { c[0] = remap[t * 3]; c[1] = remap[t * 3 + 1]; c[2] = remap[t * 3 + 2]; }
    }

    static Vector3 Normal(float[] pos, int a, int b, int c, out Vector3 p0, out Vector3 p1, out Vector3 p2)
    {
        p0 = new Vector3(pos[a * 3], pos[a * 3 + 1], pos[a * 3 + 2]);
        p1 = new Vector3(pos[b * 3], pos[b * 3 + 1], pos[b * 3 + 2]);
        p2 = new Vector3(pos[c * 3], pos[c * 3 + 1], pos[c * 3 + 2]);
        return Vector3.Cross(p1 - p0, p2 - p0);
    }

    /// <summary>
    /// Nearest hit of a ray against the mesh (model space). <paramref name="clip"/> is the section plane
    /// (xyz·p + w ≥ 0 is visible); hits on the cut-away side are ignored.
    /// </summary>
    public static bool Raycast(Mesh m, Vector3 o, Vector3 d, Vector4? clip, out Vector3 hit, out int tri, out float distance)
    {
        int T = m.TriCount;
        float best = float.MaxValue; int bestTri = -1;
        if (T > 200_000)
        {
            object gate = new();
            Parallel.For(0, (T + 65535) / 65536, chunk =>
            {
                int from = chunk * 65536, to = Math.Min(T, from + 65536);
                Scan(m, o, d, clip, from, to, out float b, out int bt);
                if (bt >= 0) lock (gate) { if (b < best) { best = b; bestTri = bt; } }
            });
        }
        else Scan(m, o, d, clip, 0, T, out best, out bestTri);
        hit = o + d * best; tri = bestTri; distance = best;
        return bestTri >= 0;
    }

    static void Scan(Mesh m, Vector3 o, Vector3 d, Vector4? clip, int from, int to, out float best, out int bestTri)
    {
        float[] pos = m.Pos; uint[]? idx = m.Idx;
        best = float.MaxValue; bestTri = -1;
        bool clipped = clip.HasValue; Vector4 cp = clip ?? default;
        var cn = new Vector3(cp.X, cp.Y, cp.Z);
        for (int t = from; t < to; t++)
        {
            int a, b, c;
            if (idx != null) { a = (int)idx[t * 3] * 3; b = (int)idx[t * 3 + 1] * 3; c = (int)idx[t * 3 + 2] * 3; }
            else { a = t * 9; b = a + 3; c = a + 6; }
            var p0 = new Vector3(pos[a], pos[a + 1], pos[a + 2]);
            var e1 = new Vector3(pos[b], pos[b + 1], pos[b + 2]) - p0;
            var e2 = new Vector3(pos[c], pos[c + 1], pos[c + 2]) - p0;
            var pv = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, pv);
            if (det > -1e-12f && det < 1e-12f) continue;
            float invDet = 1f / det;
            var tv = o - p0;
            float u = Vector3.Dot(tv, pv) * invDet;
            if (u < 0 || u > 1) continue;
            var qv = Vector3.Cross(tv, e1);
            float v = Vector3.Dot(d, qv) * invDet;
            if (v < 0 || u + v > 1) continue;
            float dist = Vector3.Dot(e2, qv) * invDet;
            if (dist <= 0 || dist >= best) continue;
            if (clipped && Vector3.Dot(o + d * dist, cn) + cp.W < 0) continue;
            best = dist; bestTri = t;
        }
    }
}
