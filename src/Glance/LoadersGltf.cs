using System.Buffers.Binary;
using System.Buffers.Text;
using System.Numerics;
using System.Text;

namespace Glance;

// glTF 2.0, as .gltf (JSON beside its .bin files, or with the data inside it) or .glb (both in one file).
// Glance reads the scene's nodes with their transforms and the triangles of every mesh, with normals, the
// material's base colour and vertex colours. Textures, skins and animation are left out; Draco and meshopt
// compression are refused with a plain message. Nothing here runs until a .glb or .gltf file is opened.
internal static unsafe partial class Loaders
{
    static Mesh LoadGltf(string path)
    {
        try { return ReadGltf(path); }
        catch (Exception e) when (e is not (InvalidDataException or OutOfMemoryException or IOException or UnauthorizedAccessException))
        {
            throw new InvalidDataException("This glTF file is damaged and couldn't be read.");
        }
    }

    static InvalidDataException GltfBad() => new("This glTF file is damaged and couldn't be read.");

    sealed class Gltf
    {
        public required Json Root;
        public required string Folder;
        public ArraySegment<byte> Bin;                  // the binary chunk of a .glb
        public ArraySegment<byte>[]? Buffers;
        public readonly MeshBuilder Mesh = new();

        public ArraySegment<byte> Buffer(int i)
        {
            Json? list = Root["buffers"];
            Buffers ??= new ArraySegment<byte>[list?.Count ?? 0];
            if ((uint)i >= (uint)Buffers.Length) throw GltfBad();
            if (Buffers[i].Array != null) return Buffers[i];
            string? uri = list![i]?["uri"]?.Str;
            byte[] data;
            if (uri == null)
            {
                if (i != 0 || Bin.Array == null) throw new InvalidDataException("This glTF file is missing its mesh data.");
                return Buffers[i] = Bin;
            }
            if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = uri.IndexOf(',');
                if (comma < 0 || !uri.AsSpan(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) throw GltfBad();
                data = new byte[(uri.Length - comma) / 4 * 3 + 3];
                if (!System.Convert.TryFromBase64Chars(uri.AsSpan(comma + 1), data, out int n)) throw GltfBad();
                return Buffers[i] = new ArraySegment<byte>(data, 0, n);
            }
            // A file beside this one. Anything with a drive, a server or a web address in it is not fetched.
            string name = Unescape(uri);
            if (name.Contains(':') || name.StartsWith('/') || name.StartsWith('\\'))
                throw new InvalidDataException("This glTF file keeps its mesh data somewhere Glance doesn't fetch from.");
            string file = Path.Combine(Folder, name);
            if (!File.Exists(file)) throw new InvalidDataException($"{Path.GetFileName(name)} is missing: it belongs beside this file.");
            data = File.ReadAllBytes(file);
            return Buffers[i] = new ArraySegment<byte>(data);
        }

        static string Unescape(string s)
        {
            if (s.IndexOf('%') < 0) return s;
            byte[] o = new byte[Encoding.UTF8.GetByteCount(s)];
            int n = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '%' && i + 2 < s.Length && char.IsAsciiHexDigit(s[i + 1]) && char.IsAsciiHexDigit(s[i + 2]))
                { o[n++] = (byte)(Hex(s[i + 1]) * 16 + Hex(s[i + 2])); i += 2; }
                else if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length) { n += Encoding.UTF8.GetBytes(s, i, 2, o, n); i++; }
                else n += Encoding.UTF8.GetBytes(s, i, 1, o, n);
            }
            return Encoding.UTF8.GetString(o, 0, n);

            static int Hex(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;
        }

        /// <summary>An accessor's numbers as floats: count × comps of them, normalised integers scaled to 0-1 or -1-1.</summary>
        public float[] Floats(int accessor, out int comps, out int count)
        {
            Json a = Root["accessors"]?[accessor] ?? throw GltfBad();
            comps = a["type"]?.Str switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, _ => throw GltfBad() };
            count = a.Int("count", 0);
            int type = a.Int("componentType", 0);
            int size = type switch { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4, _ => throw GltfBad() };
            if (count < 0) throw GltfBad();
            if (count == 0) return [];
            if (a["bufferView"] == null)
            {
                if ((long)count * comps > 64 << 20) throw GltfBad();
                return new float[count * comps];                        // by the rules, an accessor without data is all zeros
            }
            Json view = Root["bufferViews"]?[a.Int("bufferView", -1)] ?? throw GltfBad();
            ArraySegment<byte> buf = Buffer(view.Int("buffer", -1));
            long at = (long)view.Int("byteOffset", 0) + a.Int("byteOffset", 0);
            int stride = view.Int("byteStride", 0);
            if (stride <= 0) stride = size * comps;
            if (at < 0 || (count > 0 && at + (long)stride * (count - 1) + size * comps > buf.Count)) throw GltfBad();

            float[] o = new float[(long)count * comps];
            ReadOnlySpan<byte> s = buf.AsSpan();
            bool norm = a["normalized"] is { True: true };
            int p = (int)at;
            if (type == 5126 && BitConverter.IsLittleEndian && stride == comps * 4)
                s.Slice(p, o.Length * 4).CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(o.AsSpan()));
            else
                for (int i = 0, k = 0; i < count; i++, p += stride)
                    for (int c = 0, q = p; c < comps; c++, q += size)
                        o[k++] = type switch
                        {
                            5126 => BinaryPrimitives.ReadSingleLittleEndian(s[q..]),
                            5120 => norm ? MathF.Max((sbyte)s[q] / 127f, -1f) : (sbyte)s[q],
                            5121 => norm ? s[q] / 255f : s[q],
                            5122 => norm ? MathF.Max(BinaryPrimitives.ReadInt16LittleEndian(s[q..]) / 32767f, -1f) : BinaryPrimitives.ReadInt16LittleEndian(s[q..]),
                            5123 => norm ? BinaryPrimitives.ReadUInt16LittleEndian(s[q..]) / 65535f : BinaryPrimitives.ReadUInt16LittleEndian(s[q..]),
                            _ => BinaryPrimitives.ReadUInt32LittleEndian(s[q..]),
                        };
            return o;
        }

        public uint[] Indices(int accessor)
        {
            Json a = Root["accessors"]?[accessor] ?? throw GltfBad();
            int count = a.Int("count", 0), type = a.Int("componentType", 0);
            int size = type switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => throw GltfBad() };
            Json view = Root["bufferViews"]?[a.Int("bufferView", -1)] ?? throw GltfBad();
            ArraySegment<byte> buf = Buffer(view.Int("buffer", -1));
            long at = (long)view.Int("byteOffset", 0) + a.Int("byteOffset", 0);
            if (count < 0 || at < 0 || at + (long)count * size > buf.Count) throw GltfBad();
            uint[] o = new uint[count];
            ReadOnlySpan<byte> s = buf.AsSpan((int)at, count * size);
            for (int i = 0; i < count; i++)
                o[i] = size == 1 ? s[i] : size == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(s[(i * 2)..]) : BinaryPrimitives.ReadUInt32LittleEndian(s[(i * 4)..]);
            return o;
        }
    }

    static Mesh ReadGltf(string path)
    {
        byte[] d = ReadFile(path);
        int start = 0, end = d.Length;
        ArraySegment<byte> bin = default;
        if (d.Length >= 12 && BitConverter.ToUInt32(d, 0) == 0x46546C67)                  // "glTF": the binary container
        {
            if (BitConverter.ToUInt32(d, 4) != 2) throw new InvalidDataException("This is a glTF 1.0 file. Glance reads glTF 2.0.");
            end = 0;
            for (long p = 12; p + 8 <= d.Length;)
            {
                long len = BitConverter.ToUInt32(d, (int)p);
                uint kind = BitConverter.ToUInt32(d, (int)p + 4);
                if (p + 8 + len > d.Length) throw GltfBad();
                if (kind == 0x4E4F534A && end == 0) { start = (int)p + 8; end = start + (int)len; }
                else if (kind == 0x004E4942 && bin.Array == null) bin = new ArraySegment<byte>(d, (int)p + 8, (int)len);
                p += 8 + len;
            }
            if (end == 0) throw GltfBad();
        }

        Json root = Json.Parse(d, start, end);
        if (root.Obj == null) throw new InvalidDataException("This doesn't look like a glTF file.");
        if (root["asset"]?["version"]?.Str is string version && !version.StartsWith('2'))
            throw new InvalidDataException("This is a glTF " + version + " file. Glance reads glTF 2.0.");
        for (int i = 0; i < (root["extensionsRequired"]?.Count ?? 0); i++)
            switch (root["extensionsRequired"]![i]?.Str)
            {
                case "KHR_draco_mesh_compression": throw GltfPacked("Draco");
                case "EXT_meshopt_compression" or "KHR_meshopt_compression": throw GltfPacked("meshopt");
            }

        var g = new Gltf { Root = root, Folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "", Bin = bin };
        Json? nodes = root["nodes"], meshes = root["meshes"];
        int nodeCount = nodes?.Count ?? 0;

        // Where to start: the chosen scene's nodes, or failing that every node that is nobody's child.
        var todo = new Stack<(int Node, Matrix4x4 Parent)>();
        Json? roots = (root["scenes"]?[root.Int("scene", 0)] ?? root["scenes"]?[0])?["nodes"];
        if (roots?.Arr != null)
            for (int i = roots.Count - 1; i >= 0; i--) todo.Push((roots.Arr[i].AsInt(-1), Matrix4x4.Identity));
        else
        {
            bool[] child = new bool[nodeCount];
            for (int i = 0; i < nodeCount; i++)
                for (int k = 0; k < (nodes![i]?["children"]?.Count ?? 0); k++)
                    if ((uint)nodes[i]!["children"]![k]!.AsInt(-1) < (uint)nodeCount) child[nodes[i]!["children"]![k]!.AsInt(-1)] = true;
            for (int i = nodeCount - 1; i >= 0; i--) if (!child[i]) todo.Push((i, Matrix4x4.Identity));
        }

        bool[] seen = new bool[nodeCount];                  // a node belongs to one parent; this also stops loops
        while (todo.Count > 0)
        {
            var (index, parent) = todo.Pop();
            if ((uint)index >= (uint)nodeCount || seen[index] || nodes![index] is not Json node) continue;
            seen[index] = true;
            Matrix4x4 world = GltfLocal(node) * parent;
            if (node["mesh"] != null) GltfMesh(g, meshes?[node.Int("mesh", -1)], world);
            for (int k = (node["children"]?.Count ?? 0) - 1; k >= 0; k--) todo.Push((node["children"]![k]!.AsInt(-1), world));
        }
        if (nodeCount == 0)                                   // meshes with no scene around them
            for (int i = 0; i < (meshes?.Count ?? 0); i++) GltfMesh(g, meshes![i], Matrix4x4.Identity);

        // glTF is in meters with Y up; Glance works in millimeters.
        Mesh m = g.Mesh.Finish(1000f);
        m.FileUnit = "m"; m.YUp = true;
        return m;
    }

    static InvalidDataException GltfPacked(string how) =>
        new($"This file is packed with {how} compression, which Glance doesn't read. Export it without compression.");

    /// <summary>A node's own transform: a matrix, or translation, rotation and scale.</summary>
    static Matrix4x4 GltfLocal(Json node)
    {
        Span<float> v = stackalloc float[16];
        if (node["matrix"] is { Count: 16 } mx)
        {
            // glTF lists a matrix column by column for column vectors; that is row by row for the row vectors used here.
            for (int i = 0; i < 16; i++) v[i] = (float)mx[i]!.Num;
            return new Matrix4x4(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
        }
        Matrix4x4 m = Matrix4x4.Identity;
        if (node["scale"] is { Count: 3 } s) m = Matrix4x4.CreateScale((float)s[0]!.Num, (float)s[1]!.Num, (float)s[2]!.Num);
        if (node["rotation"] is { Count: 4 } r)
        {
            var q = new Quaternion((float)r[0]!.Num, (float)r[1]!.Num, (float)r[2]!.Num, (float)r[3]!.Num);
            if (q.LengthSquared() > 0) m *= Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q));
        }
        if (node["translation"] is { Count: 3 } t) m *= Matrix4x4.CreateTranslation((float)t[0]!.Num, (float)t[1]!.Num, (float)t[2]!.Num);
        return m;
    }

    static void GltfMesh(Gltf g, Json? mesh, Matrix4x4 world)
    {
        Json? prims = mesh?["primitives"];
        for (int i = 0; i < (prims?.Count ?? 0); i++)
        {
            if (prims![i] is not Json prim) continue;
            int mode = prim.Int("mode", 4);
            if (mode is not (4 or 5 or 6)) continue;                                    // points and lines: nothing to show
            int at = prim["attributes"]?.Int("POSITION", -1) ?? -1;
            if (at < 0) continue;
            if (prim["extensions"]?["KHR_draco_mesh_compression"] != null && g.Root["accessors"]?[at]?["bufferView"] == null)
                throw GltfPacked("Draco");

            float[] pos = g.Floats(at, out int comps, out int count);
            if (comps != 3) throw GltfBad();
            float[]? nrm = null;
            if (prim["attributes"]!["NORMAL"] != null)
            {
                nrm = g.Floats(prim["attributes"]!.Int("NORMAL", -1), out int nc, out int nn);
                if (nc != 3 || nn != count) nrm = null;
            }

            // Colour: the material's base colour, times the vertex colours when there are any. Both are linear light.
            Vector3 tint = GltfColor(g.Root["materials"]?[prim.Int("material", -1)], out bool colored);
            uint[]? col = null;
            if (prim["attributes"]!["COLOR_0"] != null)
            {
                float[] c = g.Floats(prim["attributes"]!.Int("COLOR_0", -1), out int cc, out int cn);
                if (cc >= 3 && cn == count)
                {
                    col = new uint[count];
                    for (int k = 0; k < count; k++)
                        col[k] = Pack(Srgb(c[k * cc] * tint.X), Srgb(c[k * cc + 1] * tint.Y), Srgb(c[k * cc + 2] * tint.Z));
                }
            }
            uint flat = colored ? Pack(Srgb(tint.X), Srgb(tint.Y), Srgb(tint.Z)) : 0;

            uint[] idx;
            if (prim["indices"] != null) idx = g.Indices(prim.Int("indices", -1));
            else { idx = new uint[count]; for (int k = 0; k < count; k++) idx[k] = (uint)k; }
            if (mode != 4)
            {
                // Strips and fans, written out as separate triangles.
                int tris = Math.Max(idx.Length - 2, 0);
                uint[] list = new uint[tris * 3];
                for (int k = 0; k < tris; k++)
                {
                    bool odd = mode == 5 && (k & 1) != 0;
                    list[k * 3] = mode == 6 ? idx[0] : idx[odd ? k + 1 : k];
                    list[k * 3 + 1] = mode == 6 ? idx[k + 1] : idx[odd ? k : k + 1];
                    list[k * 3 + 2] = idx[k + 2];
                }
                idx = list;
            }
            g.Mesh.Add(pos, count, nrm, col, flat, idx, idx.Length, world);
        }
    }

    /// <summary>A material's base colour in linear light. No colour when there is no material, or its colour is all in a texture.</summary>
    static Vector3 GltfColor(Json? material, out bool colored)
    {
        colored = false;
        if (material == null) return Vector3.One;
        Json? pbr = material["pbrMetallicRoughness"], factor = pbr?["baseColorFactor"];
        bool textured = pbr?["baseColorTexture"] != null;
        if (pbr == null && material["extensions"]?["KHR_materials_pbrSpecularGlossiness"] is Json old)       // the older way to say it
        { factor = old["diffuseFactor"]; textured = old["diffuseTexture"] != null; }
        Vector3 c = factor is { Count: >= 3 } ? new Vector3((float)factor[0]!.Num, (float)factor[1]!.Num, (float)factor[2]!.Num) : Vector3.One;
        colored = !(textured && c.X > 0.999f && c.Y > 0.999f && c.Z > 0.999f);
        return c;
    }

    static float Srgb(float linear) =>
        linear <= 0.0031308f ? MathF.Max(linear, 0) * 12.92f : 1.055f * MathF.Pow(MathF.Min(linear, 1), 1 / 2.4f) - 0.055f;

    // ───────────────────────── JSON, just enough ─────────────────────────

    /// <summary>A parsed JSON value: a number, a string, a list or an object. Missing things read as null.</summary>
    sealed class Json
    {
        public double Num = double.NaN;
        public string? Str;
        public List<Json>? Arr;
        public Dictionary<string, Json>? Obj;
        public bool True;

        public Json? this[string key] => Obj != null && Obj.TryGetValue(key, out Json? v) ? v : null;
        public Json? this[int i] => Arr != null && (uint)i < (uint)Arr.Count ? Arr[i] : null;
        public int Count => Arr?.Count ?? 0;

        public int AsInt(int fallback) => Num >= 0 && Num <= int.MaxValue ? (int)Num : fallback;
        public int Int(string key, int fallback) => this[key] is Json v ? v.AsInt(fallback) : fallback;

        static InvalidDataException Bad() => new("This doesn't look like a glTF file.");

        public static Json Parse(byte[] d, int start, int end)
        {
            int p = start;
            if (end - p >= 3 && d[p] == 0xEF && d[p + 1] == 0xBB && d[p + 2] == 0xBF) p += 3;       // a byte-order mark
            return Value(d, ref p, end, 0);
        }

        static void Space(byte[] d, ref int p, int end) { while (p < end && (d[p] == ' ' || d[p] == '\n' || d[p] == '\r' || d[p] == '\t')) p++; }

        static Json Value(byte[] d, ref int p, int end, int depth)
        {
            if (depth > 100) throw Bad();
            Space(d, ref p, end);
            if (p >= end) throw Bad();
            switch (d[p])
            {
                case (byte)'{':
                {
                    var o = new Json { Obj = new Dictionary<string, Json>() };
                    p++;
                    Space(d, ref p, end);
                    if (p < end && d[p] == '}') { p++; return o; }
                    while (true)
                    {
                        Space(d, ref p, end);
                        if (p >= end || d[p] != '"') throw Bad();
                        string key = String(d, ref p, end);
                        Space(d, ref p, end);
                        if (p >= end || d[p] != ':') throw Bad();
                        p++;
                        o.Obj[key] = Value(d, ref p, end, depth + 1);
                        Space(d, ref p, end);
                        if (p < end && d[p] == ',') { p++; continue; }
                        if (p < end && d[p] == '}') { p++; return o; }
                        throw Bad();
                    }
                }
                case (byte)'[':
                {
                    var a = new Json { Arr = new List<Json>() };
                    p++;
                    Space(d, ref p, end);
                    if (p < end && d[p] == ']') { p++; return a; }
                    while (true)
                    {
                        a.Arr.Add(Value(d, ref p, end, depth + 1));
                        Space(d, ref p, end);
                        if (p < end && d[p] == ',') { p++; continue; }
                        if (p < end && d[p] == ']') { p++; return a; }
                        throw Bad();
                    }
                }
                case (byte)'"': return new Json { Str = String(d, ref p, end) };
                case (byte)'t': p += 4; return new Json { True = true };
                case (byte)'f': p += 5; return new Json();
                case (byte)'n': p += 4; return new Json();
                default:
                    if (!Utf8Parser.TryParse(d.AsSpan(p, Math.Min(64, end - p)), out double v, out int used) || used == 0) throw Bad();
                    p += used;
                    return new Json { Num = v };
            }
        }

        static string String(byte[] d, ref int p, int end)
        {
            int s = ++p;
            while (p < end && d[p] != '"' && d[p] != '\\') p++;
            if (p >= end) throw Bad();
            if (d[p] == '"') return Encoding.UTF8.GetString(d, s, p++ - s);

            var sb = new StringBuilder(Encoding.UTF8.GetString(d, s, p - s));                      // the rare string with escapes in it
            while (true)
            {
                if (p >= end) throw Bad();
                byte c = d[p++];
                if (c == '"') return sb.ToString();
                if (c != '\\')
                {
                    int run = p - 1;
                    while (p < end && d[p] != '"' && d[p] != '\\') p++;
                    sb.Append(Encoding.UTF8.GetString(d, run, p - run));
                    continue;
                }
                if (p >= end) throw Bad();
                c = d[p++];
                if (c == 'u')
                {
                    if (p + 4 > end || !Utf8Parser.TryParse(d.AsSpan(p, 4), out ushort code, out int used, 'X') || used != 4) throw Bad();
                    sb.Append((char)code);
                    p += 4;
                }
                else sb.Append(c switch { (byte)'n' => '\n', (byte)'t' => '\t', (byte)'r' => '\r', (byte)'b' => '\b', (byte)'f' => '\f', _ => (char)c });
            }
        }
    }
}
