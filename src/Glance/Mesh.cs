using System.Numerics;

namespace Glance;

internal sealed class Mesh
{
    public float[] Pos = [];
    public float[]? Nrm;      // per-vertex normals; null means flat shading from screen-space derivatives
    public uint[]? Col;       // per-vertex RGBA (alpha 0 = use the viewer material)
    public uint[]? Idx;       // null means every three vertices form a triangle
    public uint[]? FaceId;    // per triangle: the CAD face it came from (STEP / IGES only)

    public Vector3 Min, Max;
    public string Format = "", Units = "";
    public string FileUnit = "mm";      // what one unit in the file means: mm, cm, m, in or ft. Positions are stored in mm.
    public int UnitSource;              // 0 = stated by the file, 1 = assumed, 2 = chosen by the user
    public bool UnitChoice;             // STL, OBJ and PLY carry no reliable units, so the user may override them
    public bool YUp, FromCache;
    public long FileSize;

    // Filled in by Analysis on a background thread.
    public float[]? EdgeLines;
    public float[]? SmoothNrm;   // crease-aware normals for meshes that arrived without any (STL)
    public double ParseMs;

    // Topology for measuring (also from Analysis). A "feature segment" is a mesh edge on a crease or a CAD face
    // boundary; segments are strung into chains (one chain = one edge the user can click).
    public int[]? Weld;                 // per vertex: the first vertex at the same position
    public int[]? TriAdj;               // per triangle corner: the triangle across that edge, or -1 at a feature edge
    public int[]? SegVerts;             // 2 welded vertex ids per feature segment (same order as EdgeLines)
    public int[]? SegChain;             // per segment: its chain
    public int[]? ChainStart;           // CSR offsets into ChainVerts (length = chains + 1)
    public int[]? ChainVerts;           // ordered vertex ids along each chain
    public bool[]? ChainClosed;
    public int OpenEdges;
    public int[]? TriRegion;            // lazily assigned by Inspect: which face region a triangle belongs to
    public readonly List<Entity> Regions = new();
    public double Area, Volume;
    public bool Closed, Analyzed;

    public int VertexCount => Pos.Length / 3;
    public int TriCount => Idx != null ? Idx.Length / 3 : Pos.Length / 9;

    public int Corner(int tri, int k) => Idx != null ? (int)Idx[tri * 3 + k] : tri * 3 + k;
    public System.Numerics.Vector3 P(int v) => new(Pos[v * 3], Pos[v * 3 + 1], Pos[v * 3 + 2]);

    public void ComputeBounds()
    {
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        float[] p = Pos;
        for (int i = 0; i + 2 < p.Length; i += 3)
        {
            var v = new Vector3(p[i], p[i + 1], p[i + 2]);
            if (float.IsNaN(v.X + v.Y + v.Z) || float.IsInfinity(v.X + v.Y + v.Z)) { p[i] = p[i + 1] = p[i + 2] = 0; continue; }
            min = Vector3.Min(min, v); max = Vector3.Max(max, v);
        }
        if (min.X > max.X) { min = Vector3.Zero; max = Vector3.Zero; }
        Min = min; Max = max;
    }
}

/// <summary>Growable buffers without List&lt;T&gt; overhead.</summary>
internal struct Buf<T> where T : unmanaged
{
    public T[] A; public int N;
    public Buf(int cap) { A = new T[Math.Max(cap, 16)]; N = 0; }
    public void Add(T v)
    {
        if (N == A.Length) Array.Resize(ref A, A.Length * 2);
        A[N++] = v;
    }
    public T[] ToArray() { if (N != A.Length) Array.Resize(ref A, N); return A; }

    /// <summary>Makes sure there is room for <paramref name="extra"/> more items.</summary>
    public void Reserve(int extra)
    {
        long need = (long)N + extra;
        if (need > Array.MaxLength) throw new OutOfMemoryException();
        if (need > A.Length) Array.Resize(ref A, (int)Math.Min(Array.MaxLength, Math.Max(need, (long)A.Length * 2)));
    }
}

/// <summary>
/// Puts the parts of a multi-part file (3MF, PLY, glTF) together into one mesh. Each part arrives as its own
/// vertices and triangle indices with the transform that places it; only the vertices its triangles use are kept.
/// </summary>
internal sealed class MeshBuilder
{
    const int MaxTris = 50_000_000;

    Buf<float> _pos = new(1024), _nrm = new(16);
    Buf<uint> _col = new(16), _idx = new(1024);
    bool _noNormals, _colors;

    public int TriCount => _idx.N / 3;

    /// <param name="nrm">Per-vertex normals, or null. One part without them and the whole mesh is shaded like an STL.</param>
    /// <param name="col">Per-vertex colours, or null to give the whole part <paramref name="flat"/> (0 = no colour).</param>
    public void Add(float[] pos, int vcount, float[]? nrm, uint[]? col, uint flat, uint[] idx, int icount, Matrix4x4 m)
    {
        icount -= icount % 3;
        if ((long)TriCount + icount / 3 > MaxTris) throw new InvalidDataException("This model has too many triangles to show.");
        bool move = !m.IsIdentity, flip = move && m.GetDeterminant() < 0;
        Matrix4x4 nm = default;
        if (nrm == null || (move && !(Matrix4x4.Invert(m, out nm)))) _noNormals = true;
        nm = Matrix4x4.Transpose(nm);
        bool normals = !_noNormals, colored = col != null || flat != 0;
        if (colored && !_colors)
        {
            _colors = true;                                  // the parts before this one had none
            _col.Reserve(_pos.N / 3); _col.N = _pos.N / 3;
        }

        int[] map = new int[vcount];
        Array.Fill(map, -1);
        int used = Math.Min(vcount, icount);
        _pos.Reserve(used * 3); _idx.Reserve(icount);
        if (normals) _nrm.Reserve(used * 3);
        if (_colors) _col.Reserve(used);

        for (int k = 0; k + 2 < icount; k += 3)
        {
            uint a = idx[k], b = idx[k + 1], c = idx[k + 2];
            if (a >= (uint)vcount || b >= (uint)vcount || c >= (uint)vcount) continue;      // a triangle that points nowhere
            if (flip) (b, c) = (c, b);                                                      // a mirrored part: keep the outside out
            _idx.Add((uint)Vertex((int)a)); _idx.Add((uint)Vertex((int)b)); _idx.Add((uint)Vertex((int)c));
        }

        int Vertex(int i)
        {
            int j = map[i];
            if (j >= 0) return j;
            j = map[i] = _pos.N / 3;
            var p = new Vector3(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]);
            if (move) p = Vector3.Transform(p, m);
            _pos.Add(p.X); _pos.Add(p.Y); _pos.Add(p.Z);
            if (normals)
            {
                var n = new Vector3(nrm![i * 3], nrm[i * 3 + 1], nrm[i * 3 + 2]);
                if (move) n = Vector3.TransformNormal(n, nm);
                float len = n.Length();
                if (len > 0) n /= len;
                _nrm.Add(n.X); _nrm.Add(n.Y); _nrm.Add(n.Z);
            }
            if (_colors) _col.Add(col != null ? col[i] : flat);
            return j;
        }
    }

    /// <summary>The finished mesh, with every position multiplied by <paramref name="scale"/> (file units to millimeters).</summary>
    public Mesh Finish(float scale)
    {
        float[] p = _pos.A; uint[] ix = _idx.A;
        int n = _idx.N;
        if (scale != 1f) for (int i = 0; i < _pos.N; i++) p[i] *= scale;

        bool normals = !_noNormals && _nrm.N == _pos.N && n > 0;
        for (int i = 0; normals && i < _nrm.N; i += 3)
        {
            float l = _nrm.A[i] * _nrm.A[i] + _nrm.A[i + 1] * _nrm.A[i + 1] + _nrm.A[i + 2] * _nrm.A[i + 2];
            if (!(l > 0.25f)) normals = false;               // blank or broken normals: better none than a black part
        }
        if (normals)
            return new Mesh { Pos = _pos.ToArray(), Nrm = _nrm.ToArray(), Col = _colors ? _col.ToArray() : null, Idx = _idx.ToArray() };

        // No normals to trust: three vertices of its own for every triangle, exactly like an STL.
        float[] pos = new float[(long)n * 3];
        uint[]? col = _colors ? new uint[n] : null;
        for (int k = 0; k < n; k++)
        {
            int a = (int)ix[k] * 3;
            pos[k * 3] = p[a]; pos[k * 3 + 1] = p[a + 1]; pos[k * 3 + 2] = p[a + 2];
            if (col != null) col[k] = _col.A[ix[k]];
        }
        return new Mesh { Pos = pos, Col = col };
    }
}
