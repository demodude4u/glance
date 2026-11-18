using System.Buffers.Binary;
using System.Buffers.Text;
using System.Numerics;
using System.Text;

namespace Glance;

// PLY: a text header that describes the data, then the data as text or as binary in either byte order.
// Glance reads positions, normals and colours per vertex, and faces as triangles, polygons or triangle strips.
// Nothing here runs until a .ply file is opened.
internal static unsafe partial class Loaders
{
    static Mesh LoadPly(string path)
    {
        try { return ReadPly(path); }
        catch (Exception e) when (e is not (InvalidDataException or OutOfMemoryException or IOException or UnauthorizedAccessException))
        {
            throw new InvalidDataException("This PLY file is damaged and couldn't be read.");
        }
    }

    struct PlyProperty
    {
        public int Type;            // 0 char, 1 uchar, 2 short, 3 ushort, 4 int, 5 uint, 6 float, 7 double
        public int CountType;       // for a list, the type of its length; otherwise -1
        public int Role;            // what Glance does with it, or -1 to step over it
    }

    sealed class PlyElement
    {
        public string Name = "";
        public long Count;
        public readonly List<PlyProperty> Props = new();
    }

    const int PlyIndices = 9;       // roles 0-2 position, 3-5 normal, 6-8 colour

    static int PlyType(string t) => t switch
    {
        "char" or "int8" => 0, "uchar" or "uint8" => 1, "short" or "int16" => 2, "ushort" or "uint16" => 3,
        "int" or "int32" => 4, "uint" or "uint32" => 5, "float" or "float32" => 6, "double" or "float64" => 7,
        _ => throw new InvalidDataException("This PLY file uses a kind of number Glance doesn't know."),
    };

    static int PlySize(int type) => type < 2 ? 1 : type < 4 ? 2 : type < 7 ? 4 : 8;

    static InvalidDataException PlyShort() => new("This PLY file stops before its data is complete.");

    static Mesh ReadPly(string path)
    {
        byte[] d = ReadFile(path);
        int h = d.Length >= 3 && d[0] == 'p' && d[1] == 'l' && d[2] == 'y' ? d.AsSpan().IndexOf("end_header"u8) : -1;
        if (h < 0) throw new InvalidDataException("This doesn't look like a PLY file.");
        int p = h + 10;
        if (p < d.Length && d[p] == '\r') p++;
        if (p < d.Length && d[p] == '\n') p++;

        // The header: the encoding, then each element (vertex, face, …) with its properties in file order.
        int format = -1;            // 0 text, 1 little-endian, 2 big-endian
        var elements = new List<PlyElement>();
        foreach (string line in Encoding.Latin1.GetString(d, 0, h).Split('\n'))
        {
            string[] w = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (w.Length < 2) continue;
            if (w[0] == "format")
                format = w[1] switch { "ascii" => 0, "binary_little_endian" => 1, "binary_big_endian" => 2, _ => -1 };
            else if (w[0] == "element" && w.Length >= 3 && long.TryParse(w[2], out long count) && count >= 0)
                elements.Add(new PlyElement { Name = w[1], Count = count });
            else if (w[0] == "property" && elements.Count > 0 && w.Length >= 3)
            {
                PlyElement e = elements[^1];
                bool vertex = e.Name == "vertex";
                if (w[1] == "list" && w.Length >= 5)
                {
                    bool corners = !vertex && w[4] is "vertex_indices" or "vertex_index";
                    e.Props.Add(new PlyProperty { CountType = PlyType(w[2]), Type = PlyType(w[3]), Role = corners ? PlyIndices : -1 });
                }
                else
                {
                    int role = !vertex ? -1 : w[2] switch
                    {
                        "x" => 0, "y" => 1, "z" => 2, "nx" => 3, "ny" => 4, "nz" => 5,
                        "red" or "r" or "diffuse_red" => 6, "green" or "g" or "diffuse_green" => 7, "blue" or "b" or "diffuse_blue" => 8,
                        _ => -1,
                    };
                    e.Props.Add(new PlyProperty { CountType = -1, Type = PlyType(w[1]), Role = role });
                }
            }
        }
        if (format < 0) throw new InvalidDataException("This doesn't look like a PLY file.");

        float[] pos = []; float[]? nrm = null; uint[]? col = null;
        int vcount = 0;
        var idx = new Buf<uint>(1024);
        int[] corner = new int[16];
        Span<double> val = stackalloc double[9];
        Span<float> scale = stackalloc float[9];

        foreach (PlyElement e in elements)
        {
            PlyProperty[] props = e.Props.ToArray();
            // Refuse a count the file cannot possibly hold before making room for it.
            long least = 0;
            foreach (PlyProperty pr in props) least += format == 0 ? 2 : PlySize(pr.CountType >= 0 ? pr.CountType : pr.Type);
            if (e.Count > d.Length || e.Count * Math.Max(least, 1) > d.Length - p + 1) throw PlyShort();
            int n = (int)e.Count;

            bool vertex = e.Name == "vertex" && vcount == 0, strips = e.Name == "tristrips";
            bool faces = !vertex && (e.Name == "face" || strips);
            if (faces && !Array.Exists(props, pr => pr.Role == PlyIndices))             // a list under another name
                for (int k = 0; k < props.Length; k++)
                    if (props[k].CountType >= 0) { props[k].Role = PlyIndices; break; }

            int roles = 0;
            scale.Fill(1);
            if (vertex)
                foreach (PlyProperty pr in props)
                    if (pr.Role >= 0 && pr.CountType < 0)
                    {
                        roles |= 1 << pr.Role;
                        // Colours as bytes are 0-255; as fractions they are 0-1.
                        if (pr.Role >= 6) scale[pr.Role] = pr.Type >= 6 ? 255f : pr.Type is 2 or 3 ? 255f / 65535f : 1f;
                    }
            if (vertex)
            {
                if ((roles & 7) != 7) throw new InvalidDataException("This PLY file has no x, y, z positions.");
                vcount = n;
                pos = new float[(long)n * 3];
                if ((roles & 0x38) == 0x38) nrm = new float[(long)n * 3];
                if ((roles & 0x1C0) == 0x1C0) col = new uint[n];
            }

            for (int i = 0; i < n; i++)
            {
                foreach (PlyProperty pr in props)
                {
                    if (pr.CountType < 0)
                    {
                        if (pr.Role >= 0 && vertex) val[pr.Role] = PlyNumber(d, ref p, pr.Type, format);
                        else if (format == 0) PlyNumber(d, ref p, pr.Type, 0);
                        else p += PlySize(pr.Type);
                        continue;
                    }
                    double len = PlyNumber(d, ref p, pr.CountType, format);
                    if (!(len >= 0 && len * (format == 0 ? 1 : PlySize(pr.Type)) <= d.Length - p)) throw PlyShort();
                    int m = (int)len;
                    if (pr.Role != PlyIndices || !faces)
                    {
                        if (format == 0) for (int k = 0; k < m; k++) PlyNumber(d, ref p, pr.Type, 0);
                        else p += m * PlySize(pr.Type);
                        continue;
                    }
                    if (m > corner.Length) corner = new int[m];
                    for (int k = 0; k < m; k++)
                    {
                        double v = PlyNumber(d, ref p, pr.Type, format);
                        corner[k] = v >= 0 && v < int.MaxValue ? (int)v : -1;
                    }
                    if (strips) PlyStrip(corner, m, ref idx);
                    else
                        for (int k = 1; k + 1 < m; k++)                                 // a polygon becomes a fan of triangles
                        { idx.Add((uint)corner[0]); idx.Add((uint)corner[k]); idx.Add((uint)corner[k + 1]); }
                }
                if (p > d.Length) throw PlyShort();
                if (!vertex) continue;
                pos[i * 3] = (float)val[0]; pos[i * 3 + 1] = (float)val[1]; pos[i * 3 + 2] = (float)val[2];
                if (nrm != null) { nrm[i * 3] = (float)val[3]; nrm[i * 3 + 1] = (float)val[4]; nrm[i * 3 + 2] = (float)val[5]; }
                if (col != null)
                    col[i] = Pack((float)val[6] * scale[6] / 255f, (float)val[7] * scale[7] / 255f, (float)val[8] * scale[8] / 255f);
            }
        }

        if (vcount > 0 && idx.N == 0)
            throw new InvalidDataException("This PLY file is a point cloud: it has points but no surfaces to show.");
        var b = new MeshBuilder();
        b.Add(pos, vcount, nrm, col, 0, idx.A, idx.N, Matrix4x4.Identity);
        return b.Finish(1f);
    }

    /// <summary>A triangle strip: every vertex after the second makes a triangle with the two before it; -1 starts a new strip.</summary>
    static void PlyStrip(int[] s, int count, ref Buf<uint> idx)
    {
        for (int i = 0, start = 0; i < count; i++)
        {
            if (s[i] < 0) { start = i + 1; continue; }
            int k = i - start;
            if (k < 2) continue;
            int a = s[i - 2], b = s[i - 1], c = s[i];
            if (a == b || b == c || a == c) continue;
            if ((k & 1) != 0) (a, b) = (b, a);                                           // every other triangle runs the other way round
            idx.Add((uint)a); idx.Add((uint)b); idx.Add((uint)c);
        }
    }

    static double PlyNumber(byte[] d, ref int p, int type, int format)
    {
        if (format == 0)
        {
            while (p < d.Length && d[p] <= ' ') p++;
            if (p < d.Length && d[p] == '+') p++;
            if (p >= d.Length || !Utf8Parser.TryParse(d.AsSpan(p, Math.Min(64, d.Length - p)), out double v, out int used) || used == 0) throw PlyShort();
            p += used;
            return v;
        }
        int size = PlySize(type);
        if (p < 0 || p + size > d.Length) throw PlyShort();
        ReadOnlySpan<byte> s = d.AsSpan(p, size);
        p += size;
        bool big = format == 2;
        return type switch
        {
            0 => (sbyte)s[0],
            1 => s[0],
            2 => big ? BinaryPrimitives.ReadInt16BigEndian(s) : BinaryPrimitives.ReadInt16LittleEndian(s),
            3 => big ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s),
            4 => big ? BinaryPrimitives.ReadInt32BigEndian(s) : BinaryPrimitives.ReadInt32LittleEndian(s),
            5 => big ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s),
            6 => big ? BinaryPrimitives.ReadSingleBigEndian(s) : BinaryPrimitives.ReadSingleLittleEndian(s),
            _ => big ? BinaryPrimitives.ReadDoubleBigEndian(s) : BinaryPrimitives.ReadDoubleLittleEndian(s),
        };
    }
}
