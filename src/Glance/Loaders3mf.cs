using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace Glance;

// 3MF: a zip package whose model part is XML. Glance reads the mesh objects, places them through components and
// the build's items, and takes the unit the model states. Materials, textures, slices and other extensions are
// stepped over. Nothing here runs until a .3mf file is opened.
internal static unsafe partial class Loaders
{
    static Mesh Load3mf(string path)
    {
        try { return Read3mf(path); }
        catch (Exception e) when (e is not (InvalidDataException or OutOfMemoryException or IOException or UnauthorizedAccessException))
        {
            throw new InvalidDataException("This 3MF file is damaged and couldn't be read.");
        }
    }

    sealed class Object3mf
    {
        public Buf<float> V = new(64);
        public Buf<uint> T = new(64);
        public List<(string Model, int Id, Matrix4x4 M)>? Parts;        // components: other objects placed inside this one
    }

    sealed class Model3mf
    {
        public readonly Dictionary<int, Object3mf> Objects = new();
        public readonly List<(string Model, int Id, Matrix4x4 M)> Items = new();      // the build: what is actually made
        public string Unit = "mm";
    }

    sealed class Package3mf
    {
        public required Zip Zip;
        public readonly Dictionary<string, Model3mf> Models = new(StringComparer.OrdinalIgnoreCase);
        public readonly MeshBuilder Mesh = new();
        public int Budget = 2_000_000;       // placements: stops components that refer to each other for ever

        public Model3mf Model(string name)
        {
            if (Models.TryGetValue(name, out Model3mf? m)) return m;
            var (d, start, end) = Zip.Read(name);
            m = d == null ? new Model3mf() : ParseModel3mf(d, start, end, name);
            Models[name] = m;
            return m;
        }

        public void Place(string model, int id, Matrix4x4 m, int depth)
        {
            if (depth > 32 || --Budget < 0 || !Model(model).Objects.TryGetValue(id, out Object3mf? o)) return;
            if (o.T.N > 0) Mesh.Add(o.V.A, o.V.N / 3, null, null, 0, o.T.A, o.T.N, m);
            if (o.Parts != null)
                foreach (var (pm, pid, pt) in o.Parts) Place(pm, pid, pt * m, depth + 1);
        }
    }

    static Mesh Read3mf(string path)
    {
        var zip = new Zip(File.ReadAllBytes(path));
        var pack = new Package3mf { Zip = zip };

        // The package says where its model is; nearly always 3D/3dmodel.model.
        string? root = null;
        var (rels, rs, re) = zip.Read("_rels/.rels");
        if (rels != null)
        {
            var x = new XmlTags { D = rels, I = rs, End = re };
            while (root == null && x.Next())
                if (!x.Closing && x.Value("Type"u8) is string type && type.EndsWith("/3dmodel", StringComparison.OrdinalIgnoreCase))
                    root = x.Value("Target"u8)?.TrimStart('/');
        }
        if (root == null || !zip.Has(root)) root = zip.Has("3D/3dmodel.model") ? "3D/3dmodel.model" : zip.FirstEnding(".model");
        if (root == null) throw new InvalidDataException("There is no 3D model in this 3MF file.");

        Model3mf main = pack.Model(root);
        foreach (var (model, id, m) in main.Items) pack.Place(model, id, m, 0);
        if (pack.Mesh.TriCount == 0)                                    // no build to go by: show every object as it is
            foreach (int id in main.Objects.Keys) pack.Place(root, id, Matrix4x4.Identity, 0);

        Mesh mesh = pack.Mesh.Finish(UnitToMm(main.Unit));
        mesh.FileUnit = main.Unit;
        return mesh;
    }

    static Model3mf ParseModel3mf(byte[] d, int start, int end, string self)
    {
        var model = new Model3mf();
        var x = new XmlTags { D = d, I = start, End = end };
        int prefix = -1, prefixAt = 0;          // the namespace prefix the core elements carry (usually none)
        Object3mf? obj = null; bool mesh = false;

        while (x.Next())
        {
            ReadOnlySpan<byte> name = d.AsSpan(x.NameS, x.NameE - x.NameS);
            if (prefix < 0)
            {
                int colon = name.IndexOf((byte)':');
                if (x.Closing || !name[(colon + 1)..].SequenceEqual("model"u8)) throw new InvalidDataException("This doesn't look like a 3MF file.");
                prefix = colon + 1; prefixAt = x.NameS;
                model.Unit = x.Value("unit"u8) switch
                {
                    "micron" => "um", "centimeter" => "cm", "inch" => "in", "foot" => "ft", "meter" => "m", _ => "mm",
                };
                continue;
            }
            // Elements from extensions (slices, beam lattices, materials) carry another prefix: not ours.
            if (prefix > 0 ? name.Length <= prefix || !name[..prefix].SequenceEqual(d.AsSpan(prefixAt, prefix)) : name.IndexOf((byte)':') >= 0) continue;
            name = name[prefix..];

            if (x.Closing)
            {
                if (name.SequenceEqual("mesh"u8)) mesh = false;
                else if (name.SequenceEqual("object"u8)) { obj = null; mesh = false; }
                continue;
            }

            if (mesh)
            {
                if (name.SequenceEqual("vertex"u8))
                {
                    float vx = 0, vy = 0, vz = 0;
                    int p = x.AttrS;
                    while (x.Attr(ref p, out int ns, out int ne, out int vs, out _))
                    {
                        if (ne - ns != 1) continue;
                        byte c = d[ns];
                        if (c == 'x') vx = Number3mf(d, vs); else if (c == 'y') vy = Number3mf(d, vs); else if (c == 'z') vz = Number3mf(d, vs);
                    }
                    obj!.V.Add(vx); obj.V.Add(vy); obj.V.Add(vz);
                }
                else if (name.SequenceEqual("triangle"u8))
                {
                    uint a = uint.MaxValue, b = uint.MaxValue, c = uint.MaxValue;
                    int p = x.AttrS;
                    while (x.Attr(ref p, out int ns, out int ne, out int vs, out int ve))
                    {
                        if (ne - ns != 2 || d[ns] != 'v') continue;
                        Skip(d, ref vs, ve);
                        uint v = ParseInt(d, ref vs, ve, out int n) && n >= 0 ? (uint)n : uint.MaxValue;
                        byte k = d[ns + 1];
                        if (k == '1') a = v; else if (k == '2') b = v; else if (k == '3') c = v;
                    }
                    obj!.T.Add(a); obj.T.Add(b); obj.T.Add(c);
                }
                continue;
            }

            if (name.SequenceEqual("object"u8))
            {
                obj = null;
                if (x.Value("id"u8) is string id && int.TryParse(id, out int n))
                {
                    var o = new Object3mf();
                    model.Objects[n] = o;
                    if (!x.Empty) obj = o;
                }
            }
            else if (name.SequenceEqual("mesh"u8)) mesh = obj != null && !x.Empty;
            else if (name.SequenceEqual("component"u8) || name.SequenceEqual("item"u8))
            {
                bool item = name[0] == 'i';
                if (!item && obj == null) continue;
                if (x.Value("objectid"u8) is not string id || !int.TryParse(id, out int n)) continue;
                // The production extension keeps objects in other model parts of the package: path="/3D/Objects/a.model".
                string part = x.Value("path"u8, anyPrefix: true)?.TrimStart('/') ?? self;
                Matrix4x4 m = x.Find("transform"u8, false, out int ts, out int te) ? Transform3mf(d, ts, te) : Matrix4x4.Identity;
                if (item) model.Items.Add((part, n, m));
                else (obj!.Parts ??= new()).Add((part, n, m));
            }
        }
        if (prefix < 0) throw new InvalidDataException("This doesn't look like a 3MF file.");
        return model;
    }

    static float Number3mf(byte[] d, int i)
    {
        while (i < d.Length && d[i] <= ' ') i++;
        ParseFloat(d, ref i, out float f);
        return f;
    }

    /// <summary>"m00 m01 m02 m10 m11 m12 m20 m21 m22 m30 m31 m32": a 4 × 3 matrix for row vectors, as System.Numerics uses.</summary>
    static Matrix4x4 Transform3mf(byte[] d, int s, int e)
    {
        Span<float> v = stackalloc float[12];
        for (int n = 0; n < 12; n++)
        {
            while (s < e && d[s] <= ' ') s++;
            if (s >= e || !ParseFloat(d, ref s, out v[n])) return Matrix4x4.Identity;
        }
        return new Matrix4x4(v[0], v[1], v[2], 0, v[3], v[4], v[5], 0, v[6], v[7], v[8], 0, v[9], v[10], v[11], 1);
    }

    // ───────────────────────── XML, just enough ─────────────────────────

    /// <summary>Steps through the tags of an XML document. Text, comments and processing instructions are passed over.</summary>
    struct XmlTags
    {
        public byte[] D; public int I, End;
        public int NameS, NameE, AttrS, AttrE;
        public bool Closing, Empty;

        public bool Next()
        {
            byte[] d = D;
            while (true)
            {
                int k = I < End ? d.AsSpan(I, End - I).IndexOf((byte)'<') : -1;
                if (k < 0 || I + k + 1 >= End) return false;
                int p = I + k + 1;
                byte c = d[p];
                if (c == '?') { I = After(p, "?>"u8); continue; }
                if (c == '!')
                {
                    ReadOnlySpan<byte> rest = d.AsSpan(p, End - p);
                    I = rest.StartsWith("!--"u8) ? After(p, "-->"u8) : rest.StartsWith("![CDATA["u8) ? After(p, "]]>"u8) : After(p, ">"u8);
                    continue;
                }
                Closing = c == '/';
                if (Closing) p++;
                NameS = p;
                while (p < End && d[p] != '>' && d[p] != '/' && d[p] > ' ') p++;
                NameE = p; AttrS = p;
                byte quote = 0;                                 // a '>' inside a quoted value does not end the tag
                for (; p < End; p++)
                {
                    byte b = d[p];
                    if (quote != 0) { if (b == quote) quote = 0; }
                    else if (b == '"' || b == '\'') quote = b;
                    else if (b == '>') break;
                }
                if (p >= End) return false;
                Empty = p > AttrS && d[p - 1] == '/';
                AttrE = Empty ? p - 1 : p;
                I = p + 1;
                return true;
            }
        }

        int After(int from, ReadOnlySpan<byte> mark)
        {
            int k = D.AsSpan(from, End - from).IndexOf(mark);
            return k < 0 ? End : from + k + mark.Length;
        }

        /// <summary>The next attribute of the current tag, as ranges in <see cref="D"/>. Start with p = AttrS.</summary>
        public readonly bool Attr(ref int p, out int ns, out int ne, out int vs, out int ve)
        {
            byte[] d = D; int end = AttrE;
            while (p < end && d[p] <= ' ') p++;
            ns = p;
            while (p < end && d[p] != '=' && d[p] > ' ') p++;
            ne = p;
            while (p < end && d[p] <= ' ') p++;
            vs = ve = p;
            if (p >= end || d[p] != '=' || ne == ns) return false;
            p++;
            while (p < end && d[p] <= ' ') p++;
            if (p >= end || (d[p] != '"' && d[p] != '\'')) return false;
            byte quote = d[p++];
            vs = p;
            while (p < end && d[p] != quote) p++;
            ve = p;
            if (p < end) p++;
            return true;
        }

        public readonly bool Find(ReadOnlySpan<byte> name, bool anyPrefix, out int vs, out int ve)
        {
            int p = AttrS;
            while (Attr(ref p, out int ns, out int ne, out vs, out ve))
            {
                ReadOnlySpan<byte> n = D.AsSpan(ns, ne - ns);
                if (anyPrefix) n = n[(n.LastIndexOf((byte)':') + 1)..];
                if (n.SequenceEqual(name)) return true;
            }
            return false;
        }

        public readonly string? Value(ReadOnlySpan<byte> name, bool anyPrefix = false) =>
            Find(name, anyPrefix, out int vs, out int ve) ? Encoding.UTF8.GetString(D, vs, ve - vs).Trim() : null;
    }

    // ───────────────────────── Zip, just enough ─────────────────────────

    /// <summary>The files inside a zip, found through its central directory. Stored and deflated entries, zip64 sizes.</summary>
    sealed class Zip
    {
        readonly byte[] _d;
        readonly Dictionary<string, (long Offset, long Packed, long Size, int Method, int Flags)> _files = new(StringComparer.OrdinalIgnoreCase);

        static InvalidDataException Bad() => new("This doesn't look like a 3MF file.");

        uint U16(long p) => (uint)(_d[p] | _d[p + 1] << 8);
        uint U32(long p) => BitConverter.ToUInt32(_d, (int)p);
        long U64(long p) => (long)BitConverter.ToUInt64(_d, (int)p);

        public Zip(byte[] d)
        {
            _d = d;
            int e = d.Length - 22, stop = Math.Max(0, d.Length - 22 - 65535);
            while (e >= stop && U32(e) != 0x06054B50) e--;                  // "end of central directory", behind an optional comment
            if (e < stop) throw Bad();
            long count = U16(e + 10), at = U32(e + 16);
            if ((count == 0xFFFF || at == 0xFFFFFFFF) && e >= 20 && U32(e - 20) == 0x07064B50)      // zip64
            {
                long z = U64(e - 20 + 8);
                if (z < 0 || z + 56 > d.Length || U32(z) != 0x06064B50) throw Bad();
                count = U64(z + 32); at = U64(z + 48);
            }
            for (long i = 0, p = at; i < count; i++)
            {
                if (p < 0 || p + 46 > d.Length || U32(p) != 0x02014B50) break;
                int flags = (int)U16(p + 8), method = (int)U16(p + 10);
                long packed = U32(p + 20), size = U32(p + 24), offset = U32(p + 42);
                int nameLen = (int)U16(p + 28), extraLen = (int)U16(p + 30), commentLen = (int)U16(p + 32);
                if (p + 46 + nameLen + extraLen + commentLen > d.Length) break;
                string name = Encoding.UTF8.GetString(d, (int)p + 46, nameLen);
                for (long x = p + 46 + nameLen, xe = x + extraLen; x + 4 <= xe; x += 4 + U16(x + 2))
                {
                    if (U16(x) != 1) continue;                               // the zip64 field holds whichever values did not fit
                    long q = x + 4, qe = Math.Min(xe, q + U16(x + 2));
                    if (size == 0xFFFFFFFF && q + 8 <= qe) { size = U64(q); q += 8; }
                    if (packed == 0xFFFFFFFF && q + 8 <= qe) { packed = U64(q); q += 8; }
                    if (offset == 0xFFFFFFFF && q + 8 <= qe) offset = U64(q);
                    break;
                }
                _files[name.Replace('\\', '/').TrimStart('/')] = (offset, packed, size, method, flags);
                p += 46 + nameLen + extraLen + commentLen;
            }
        }

        public bool Has(string name) => _files.ContainsKey(name);

        public string? FirstEnding(string suffix)
        {
            foreach (string name in _files.Keys) if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return name;
            return null;
        }

        /// <summary>One file's bytes as a range of an array, or a null array when the package has no such file.</summary>
        public (byte[]? D, int Start, int End) Read(string name)
        {
            if (!_files.TryGetValue(name, out var f)) return (null, 0, 0);
            if ((f.Flags & 1) != 0) throw new InvalidDataException("This 3MF file is password-protected.");
            long h = f.Offset;
            if (h < 0 || h + 30 > _d.Length || U32(h) != 0x04034B50) throw Bad();
            long data = h + 30 + U16(h + 26) + U16(h + 28);
            if (f.Packed < 0 || data + f.Packed > _d.Length) throw Bad();
            if (f.Method == 0) return (_d, (int)data, (int)(data + f.Packed));
            if (f.Method != 8) throw new InvalidDataException("This 3MF file is packed in a way Glance doesn't read.");
            // Deflate cannot shrink anything by more than about a thousand times: a size beyond that is not honest.
            if (f.Size < 0 || f.Size > f.Packed * 1100 + 4096) throw Bad();
            if (f.Size > Array.MaxLength) throw new OutOfMemoryException();
            byte[] o = new byte[f.Size];
            int n = 0;
            try
            {
                using var z = new DeflateStream(new MemoryStream(_d, (int)data, (int)f.Packed, false), CompressionMode.Decompress);
                while (n < o.Length)
                {
                    int got = z.Read(o, n, o.Length - n);
                    if (got <= 0) break;
                    n += got;
                }
            }
            catch (InvalidDataException) { throw new InvalidDataException("This 3MF file is damaged and couldn't be read."); }
            return (o, 0, n);
        }
    }
}
