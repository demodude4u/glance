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
    public bool UnitChoice;             // STL / OBJ carry no reliable units, so the user may override them
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
}
