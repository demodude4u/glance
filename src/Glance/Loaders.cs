using System.Buffers.Text;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Glance;

internal static unsafe partial class Loaders
{
    public static readonly string[] Extensions = [".stl", ".obj", ".3mf", ".ply", ".glb", ".gltf", ".step", ".stp", ".iges", ".igs", ".brep"];

    public static bool Supported(string path) =>
        Array.IndexOf(Extensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;

    /// <summary>Settings and the STEP cache live here. GLANCE_DATA_DIR redirects it (used by the automated tests).</summary>
    public static string DataDir { get; } = Environment.GetEnvironmentVariable("GLANCE_DATA_DIR") is { Length: > 0 } dir
        ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glance");

    public static Mesh Load(string path, Action<string> stage, Func<bool> cancelled)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Mesh m = ext switch
        {
            ".stl" => LoadStl(path),
            ".obj" => LoadObj(path),
            ".3mf" => Load3mf(path),
            ".ply" => LoadPly(path),
            ".glb" or ".gltf" => LoadGltf(path),
            ".step" or ".stp" => LoadCad(path, "step", stage, cancelled),
            ".iges" or ".igs" => LoadCad(path, "iges", stage, cancelled),
            ".brep" => LoadCad(path, "brep", stage, cancelled),
            _ => throw new InvalidDataException("Glance opens STL, OBJ, 3MF, PLY, glTF, STEP and IGES files."),
        };
        if (m.TriCount == 0) throw new InvalidDataException("There are no surfaces in this file.");
        m.Format = ext switch { ".stp" => "STEP", ".igs" => "IGES", ".gltf" => "glTF", _ => ext[1..].ToUpperInvariant() };
        m.FileSize = new FileInfo(path).Length;
        m.ComputeBounds();
        if (ext is ".stl" or ".obj" or ".ply") ApplyUnits(m, path);
        else if (ext is ".glb" or ".gltf") GltfUnits(m, path);
        m.ParseMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return m;
    }

    // ───────────────────────── Units ─────────────────────────

    public static readonly string[] UnitNames = ["mm", "in", "cm", "m", "ft"];

    // "um" (microns) is a unit a 3MF file may state; it is not one of the choices offered for files without units.
    public static float UnitToMm(string u) => u switch { "in" => 25.4f, "cm" => 10f, "m" => 1000f, "ft" => 304.8f, "um" => 0.001f, _ => 1f };

    public static string UnitLabel(string u) => u switch { "in" => "inches", "cm" => "centimeters", "m" => "meters", "ft" => "feet", "um" => "microns", _ => "millimeters" };

    static string UnitsFile => Path.Combine(DataDir, "units.ini");

    /// <summary>The unit the user chose for this file earlier, if any.</summary>
    static string? UnitOverride(string path)
    {
        try
        {
            if (!File.Exists(UnitsFile)) return null;
            string key = Path.GetFullPath(path).ToLowerInvariant() + "=";
            string? found = null;
            foreach (string line in File.ReadLines(UnitsFile))
                if (line.StartsWith(key, StringComparison.Ordinal)) found = line[key.Length..].Trim();
            return found != null && Array.IndexOf(UnitNames, found) >= 0 ? found : null;
        }
        catch { return null; }
    }

    public static void SetUnitOverride(string path, string unit)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            string key = Path.GetFullPath(path).ToLowerInvariant() + "=";
            var lines = new List<string>();
            if (File.Exists(UnitsFile))
                foreach (string line in File.ReadLines(UnitsFile))
                    if (!line.StartsWith(key, StringComparison.Ordinal)) lines.Add(line);
            lines.Add(key + unit);
            if (lines.Count > 4000) lines.RemoveRange(0, lines.Count - 4000);
            File.WriteAllLines(UnitsFile, lines);
        }
        catch { }
    }

    /// <summary>Some exporters write the unit into the STL header or first line, e.g. "alibre stl binary : Units=Inch".</summary>
    static string? DeclaredUnit(string path)
    {
        try
        {
            byte[] head = new byte[512];
            int n;
            using (var f = File.OpenRead(path)) n = f.Read(head, 0, head.Length);
            string text = Encoding.Latin1.GetString(head, 0, n).ToLowerInvariant();
            int i = text.IndexOf("units", StringComparison.Ordinal);
            if (i < 0) return null;
            i += 5;
            while (i < text.Length && (text[i] == ' ' || text[i] == '=' || text[i] == ':')) i++;
            int e = i;
            while (e < text.Length && char.IsLetter(text[e])) e++;
            return text[i..e] switch
            {
                "mm" or "millimeter" or "millimeters" or "millimetre" or "millimetres" => "mm",
                "in" or "inch" or "inches" => "in",
                "cm" or "centimeter" or "centimeters" => "cm",
                "m" or "meter" or "meters" or "metre" or "metres" => "m",
                "ft" or "foot" or "feet" => "ft",
                _ => null,
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// STL, OBJ and PLY are bare numbers. Take the user's earlier choice, else what the file states, else a guess:
    /// a part whose largest dimension is under 6 is far more likely to be inches than a 6 mm speck.
    /// The mesh is then scaled so that everything downstream works in millimeters.
    /// </summary>
    static void ApplyUnits(Mesh m, string path)
    {
        m.UnitChoice = true;
        Vector3 size = m.Max - m.Min;
        float largest = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        if (UnitOverride(path) is string chosen) { m.FileUnit = chosen; m.UnitSource = 2; }
        else if (DeclaredUnit(path) is string stated) { m.FileUnit = stated; m.UnitSource = 0; }
        else { m.FileUnit = largest > 0 && largest < 6 ? "in" : "mm"; m.UnitSource = 1; }

        float k = UnitToMm(m.FileUnit);
        if (k == 1f) return;
        float[] p = m.Pos;
        for (int i = 0; i < p.Length; i++) p[i] *= k;
        m.Min *= k; m.Max *= k;
    }

    /// <summary>A glTF model larger than this is more likely saved in the wrong unit than really that big.</summary>
    const float GltfDoubtMm = 10_000f;

    /// <summary>
    /// glTF is in meters by its standard, and the reader has already brought the model to millimeters on that basis.
    /// CAD exporters often write millimeters into it unchanged, though, which makes a part a thousand times too large.
    /// So a model over 10 m, or one whose unit the user has already chosen, may have its unit changed like an STL.
    /// </summary>
    static void GltfUnits(Mesh m, string path)
    {
        Vector3 size = m.Max - m.Min;
        string? chosen = UnitOverride(path);
        m.UnitDoubt = chosen == null && MathF.Max(size.X, MathF.Max(size.Y, size.Z)) > GltfDoubtMm;
        m.UnitChoice = chosen != null || m.UnitDoubt;
        if (chosen == null) return;

        m.FileUnit = chosen; m.UnitSource = 2;
        float k = UnitToMm(chosen) / 1000f;
        if (k == 1f) return;
        float[] p = m.Pos;
        for (int i = 0; i < p.Length; i++) p[i] *= k;
        m.Min *= k; m.Max *= k;
    }

    // ───────────────────────── STL ─────────────────────────

    static Mesh LoadStl(string path)
    {
        byte[] d = File.ReadAllBytes(path);
        uint n = d.Length >= 84 ? BitConverter.ToUInt32(d, 80) : 0;
        if (d.Length >= 84 && 84 + (long)n * 50 == d.Length) return BinaryStl(d, (int)n);

        var ascii = AsciiStl(d);
        if (ascii.TriCount > 0) return ascii;

        if (d.Length >= 134) return BinaryStl(d, (int)Math.Min(n, (uint)((d.Length - 84) / 50)));
        throw new InvalidDataException("This doesn't look like an STL file.");
    }

    static Mesh BinaryStl(byte[] d, int n)
    {
        float[] pos = new float[(long)n * 9];
        fixed (byte* src = d)
        fixed (float* dst = pos)
        {
            byte* s = src + 84 + 12;
            float* o = dst;
            for (int i = 0; i < n; i++, s += 50, o += 9)
                Buffer.MemoryCopy(s, o, 36, 36);
        }
        return new Mesh { Pos = pos };
    }

    static Mesh AsciiStl(byte[] d)
    {
        var pos = new Buf<float>(d.Length / 24);
        ReadOnlySpan<byte> key = "vertex"u8;
        int i = 0;
        while (true)
        {
            int k = d.AsSpan(i).IndexOf(key);
            if (k < 0) break;
            i += k + 6;
            for (int c = 0; c < 3; c++)
            {
                while (i < d.Length && (d[i] == ' ' || d[i] == '\t')) i++;
                if (!ParseFloat(d, ref i, out float f)) { f = 0; }
                pos.Add(f);
            }
        }
        pos.N -= pos.N % 9;
        return new Mesh { Pos = pos.ToArray() };
    }

    static bool ParseFloat(byte[] d, ref int i, out float f)
    {
        if (i < d.Length && d[i] == '+') i++;
        if (Utf8Parser.TryParse(d.AsSpan(i, Math.Min(64, d.Length - i)), out f, out int used) && used > 0)
        { i += used; return true; }
        f = 0; return false;
    }

    // ───────────────────────── OBJ ─────────────────────────

    static Mesh LoadObj(string path)
    {
        byte[] d = File.ReadAllBytes(path);
        var v = new Buf<float>(d.Length / 30);
        var vn = new Buf<float>(1024);
        var cp = new Buf<int>(d.Length / 20);    // per corner: position index
        var cn = new Buf<int>(d.Length / 20);    // per corner: normal index or -1
        var cc = new Buf<uint>(16);              // per corner: colour (only filled when materials carry colour)
        Dictionary<string, uint>? mtl = null;
        uint cur = 0; bool anyCol = false, allNormals = true;
        int[] fp = new int[64], fn = new int[64];
        int n = d.Length, i = 0;

        while (i < n)
        {
            int rel = d.AsSpan(i).IndexOfAny((byte)'\n', (byte)'\r');
            int end = rel < 0 ? n : i + rel;
            int s = i; i = end + 1;
            while (s < end && (d[s] == ' ' || d[s] == '\t')) s++;
            if (end - s < 3) continue;
            byte c0 = d[s], c1 = d[s + 1];

            if (c0 == 'v' && (c1 == ' ' || c1 == '\t'))
            {
                int p = s + 1;
                for (int k = 0; k < 3; k++) { Skip(d, ref p, end); ParseFloat(d, ref p, out float f); v.Add(f); }
            }
            else if (c0 == 'v' && c1 == 'n')
            {
                int p = s + 2;
                for (int k = 0; k < 3; k++) { Skip(d, ref p, end); ParseFloat(d, ref p, out float f); vn.Add(f); }
            }
            else if (c0 == 'f' && (c1 == ' ' || c1 == '\t'))
            {
                int p = s + 1, m = 0, vc = v.N / 3, nc = vn.N / 3;
                bool bad = false;
                while (true)
                {
                    Skip(d, ref p, end);
                    if (p >= end) break;
                    if (!ParseInt(d, ref p, end, out int a)) break;
                    int b = int.MinValue;
                    if (p < end && d[p] == '/')
                    {
                        p++;
                        ParseInt(d, ref p, end, out _);              // texture index (unused)
                        if (p < end && d[p] == '/') { p++; if (!ParseInt(d, ref p, end, out b)) b = int.MinValue; }
                    }
                    a = a < 0 ? vc + a : a - 1;
                    if ((uint)a >= (uint)vc) { bad = true; break; }
                    int bn = -1;
                    if (b != int.MinValue) { bn = b < 0 ? nc + b : b - 1; if ((uint)bn >= (uint)nc) bn = -1; }
                    if (m == fp.Length) { Array.Resize(ref fp, m * 2); Array.Resize(ref fn, m * 2); }
                    fp[m] = a; fn[m] = bn; m++;
                }
                if (bad || m < 3) continue;
                for (int k = 1; k + 1 < m; k++)
                {
                    cp.Add(fp[0]); cp.Add(fp[k]); cp.Add(fp[k + 1]);
                    cn.Add(fn[0]); cn.Add(fn[k]); cn.Add(fn[k + 1]);
                    if (fn[0] < 0 || fn[k] < 0 || fn[k + 1] < 0) allNormals = false;
                    if (anyCol) { cc.Add(cur); cc.Add(cur); cc.Add(cur); }
                }
            }
            else if (c0 == 'u' && d.AsSpan(s, end - s).StartsWith("usemtl"u8))
            {
                string name = Encoding.UTF8.GetString(d, s + 6, end - s - 6).Trim();
                cur = mtl != null && mtl.TryGetValue(name, out uint col) ? col : 0;
                if (cur != 0 && !anyCol)
                {
                    anyCol = true;
                    for (int k = 0; k < cp.N; k++) cc.Add(0);
                }
            }
            else if (c0 == 'm' && d.AsSpan(s, end - s).StartsWith("mtllib"u8))
            {
                string name = Encoding.UTF8.GetString(d, s + 6, end - s - 6).Trim();
                try { mtl = ReadMtl(Path.Combine(Path.GetDirectoryName(path) ?? "", name), mtl); } catch { }
            }
        }

        int corners = cp.N;
        float[] pos = new float[(long)corners * 3];
        for (int k = 0; k < corners; k++)
        {
            int a = cp.A[k] * 3;
            pos[k * 3] = v.A[a]; pos[k * 3 + 1] = v.A[a + 1]; pos[k * 3 + 2] = v.A[a + 2];
        }

        float[] nrm = new float[(long)corners * 3];
        if (allNormals && vn.N > 0)
        {
            for (int k = 0; k < corners; k++)
            {
                int a = cn.A[k] * 3;
                nrm[k * 3] = vn.A[a]; nrm[k * 3 + 1] = vn.A[a + 1]; nrm[k * 3 + 2] = vn.A[a + 2];
            }
        }
        else SmoothNormals(pos, cp.A, corners, v.N / 3, nrm);

        return new Mesh { Pos = pos, Nrm = nrm, Col = anyCol ? cc.ToArray() : null, YUp = true };
    }

    /// <summary>Crease-aware normals: each corner averages the neighbouring faces within ~40° of its own.</summary>
    public static void SmoothNormals(float[] pos, int[] cp, int corners, int vcount, float[] nrm)
    {
        int tris = corners / 3;
        var fnA = new Vector3[tris];       // area-weighted
        var fnN = new Vector3[tris];       // unit
        int[] start = new int[vcount + 1];
        for (int t = 0; t < tris; t++)
        {
            int o = t * 9;
            var a = new Vector3(pos[o], pos[o + 1], pos[o + 2]);
            var b = new Vector3(pos[o + 3], pos[o + 4], pos[o + 5]);
            var c = new Vector3(pos[o + 6], pos[o + 7], pos[o + 8]);
            var nn = Vector3.Cross(b - a, c - a);
            fnA[t] = nn;
            float len = nn.Length();
            fnN[t] = len > 0 ? nn / len : Vector3.UnitZ;
            start[cp[t * 3] + 1]++; start[cp[t * 3 + 1] + 1]++; start[cp[t * 3 + 2] + 1]++;
        }
        for (int k = 0; k < vcount; k++) start[k + 1] += start[k];
        int[] fill = new int[vcount];
        int[] adj = new int[corners];
        for (int k = 0; k < corners; k++) { int p = cp[k]; adj[start[p] + fill[p]++] = k / 3; }

        const float crease = 0.766f;
        for (int k = 0; k < corners; k++)
        {
            int t = k / 3, p = cp[k];
            var own = fnN[t];
            var sum = Vector3.Zero;
            int s0 = start[p], s1 = start[p + 1];
            if (s1 - s0 > 64) sum = fnA[t];           // pathological fan: keep it cheap
            else for (int j = s0; j < s1; j++) { int u = adj[j]; if (Vector3.Dot(own, fnN[u]) > crease) sum += fnA[u]; }
            float len = sum.Length();
            var r = len > 0 ? sum / len : own;
            nrm[k * 3] = r.X; nrm[k * 3 + 1] = r.Y; nrm[k * 3 + 2] = r.Z;
        }
    }

    static Dictionary<string, uint> ReadMtl(string file, Dictionary<string, uint>? into)
    {
        into ??= new Dictionary<string, uint>();
        string? name = null;
        foreach (string raw in File.ReadLines(file))
        {
            string line = raw.Trim();
            if (line.StartsWith("newmtl", StringComparison.Ordinal)) name = line[6..].Trim();
            else if (name != null && line.StartsWith("Kd", StringComparison.Ordinal))
            {
                string[] p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 4 && float.TryParse(p[1], out float r) && float.TryParse(p[2], out float g) && float.TryParse(p[3], out float b))
                    into[name] = Pack(r, g, b);
            }
        }
        return into;
    }

    static uint Pack(float r, float g, float b) =>
        (uint)(Math.Clamp(r, 0, 1) * 255 + .5f) | (uint)(Math.Clamp(g, 0, 1) * 255 + .5f) << 8 |
        (uint)(Math.Clamp(b, 0, 1) * 255 + .5f) << 16 | 0xFF000000u;

    static void Skip(byte[] d, ref int p, int end) { while (p < end && (d[p] == ' ' || d[p] == '\t')) p++; }

    static bool ParseInt(byte[] d, ref int p, int end, out int value)
    {
        int s = p; bool neg = false; long acc = 0;
        if (p < end && (d[p] == '-' || d[p] == '+')) { neg = d[p] == '-'; p++; }
        int digits = p;
        while (p < end && (uint)(d[p] - '0') <= 9) { acc = acc * 10 + (d[p] - '0'); p++; }
        if (p == digits) { p = s; value = 0; return false; }
        value = (int)(neg ? -acc : acc);
        return true;
    }

    // ───────────────────────── STEP / IGES (OpenCascade helper + cache) ─────────────────────────

    const string TessellationVersion = "t5";

    static string CacheDir => Path.Combine(DataDir, "cache");

    static string CachePath(string path)
    {
        var fi = new FileInfo(path);
        return Path.Combine(CacheDir, Fnv($"{fi.FullName.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{TessellationVersion}") + ".gmesh");
    }

    static string CadFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".step" or ".stp" => "step", ".iges" or ".igs" => "iges", ".brep" => "brep", _ => "",
    };

    static string Worker => Path.Combine(AppContext.BaseDirectory, "step", "glance-step.exe");

    static Process? _active;             // the conversion the user is waiting for
    static Process? _background;         // a look-ahead conversion of a neighbouring file

    /// <summary>Stops any conversion still running (called when the window closes).</summary>
    public static void KillWorkers()
    {
        try { _active?.Kill(true); } catch { }
        try { _background?.Kill(true); } catch { }
    }

    /// <summary>Runs the OpenCascade reader on one file. Returns null on success, otherwise the reason it failed.</summary>
    static string? Convert(string path, string fmt, string cache, Action<string>? stage, Func<bool> cancelled, bool background)
    {
        string tmp = cache + "." + Environment.ProcessId + (background ? ".bg" : "") + ".tmp";
        var psi = new ProcessStartInfo(Worker)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add(path); psi.ArgumentList.Add(tmp); psi.ArgumentList.Add(fmt);

        using var proc = Process.Start(psi);
        if (proc == null) return "Couldn't start the STEP reader.";
        if (background) { _background = proc; try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { } }
        else _active = proc;
        var err = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) stage?.Invoke(e.Data!); };
        proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data) && !e.Data!.StartsWith("trace ")) lock (err) err.AppendLine(e.Data); };
        proc.BeginOutputReadLine(); proc.BeginErrorReadLine();
        try
        {
            while (!proc.WaitForExit(15))
            {
                if (!cancelled()) continue;
                try { proc.Kill(true); } catch { }
                proc.WaitForExit(2000);
                try { File.Delete(tmp); } catch { }
                throw new OperationCanceledException();
            }
            proc.WaitForExit();
            if (proc.ExitCode != 0 || !File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { }
                string msg; lock (err) msg = err.ToString().Trim();
                return msg.Length > 0 ? msg : "The geometry in this file couldn't be read.";
            }
            File.Move(tmp, cache, true);
            return null;
        }
        finally
        {
            if (background) _background = null; else _active = null;
        }
    }

    static Mesh LoadCad(string path, string fmt, Action<string> stage, Func<bool> cancelled)
    {
        Directory.CreateDirectory(CacheDir);
        string cache = CachePath(path);

        if (File.Exists(cache))
        {
            try
            {
                Mesh hit = ReadGmesh(cache);
                hit.FromCache = true; hit.Units = "mm";
                try { File.SetLastWriteTimeUtc(cache, DateTime.UtcNow); } catch { }
                return hit;
            }
            catch { try { File.Delete(cache); } catch { } }
        }

        if (!File.Exists(Worker))
            throw new InvalidOperationException("STEP support isn't installed next to Glance (step\\glance-step.exe is missing).");

        try { _background?.Kill(true); } catch { }      // the file being waited for gets every core
        string? error = Convert(path, fmt, cache, stage, cancelled, background: false);
        if (error != null) throw new InvalidDataException(error);
        Mesh m = ReadGmesh(cache);
        m.Units = "mm";
        ThreadPool.QueueUserWorkItem(_ => PruneCache(CacheDir));
        return m;
    }

    /// <summary>
    /// Look-ahead: quietly converts the other CAD files in the folder at low priority, smallest first, so that
    /// stepping to them (or double-clicking them later) is instant. Stops as soon as <paramref name="cancelled"/> is true.
    /// </summary>
    public static void WarmFolder(string current, Func<bool> cancelled)
    {
        try
        {
            if (!File.Exists(Worker)) return;
            string? folder = Path.GetDirectoryName(current);
            if (folder == null) return;
            var todo = new List<FileInfo>();
            foreach (string f in Directory.EnumerateFiles(folder))
            {
                if (CadFormat(f).Length == 0 || string.Equals(f, current, StringComparison.OrdinalIgnoreCase)) continue;
                var fi = new FileInfo(f);
                if (fi.Length > 40L << 20 || File.Exists(CachePath(f))) continue;
                todo.Add(fi);
            }
            todo.Sort((a, b) => a.Length.CompareTo(b.Length));
            Directory.CreateDirectory(CacheDir);
            long budget = 120L << 20; int count = 0;
            foreach (var fi in todo)
            {
                if (cancelled() || count++ >= 24 || (budget -= fi.Length) < 0) return;
                if (_active != null) { Thread.Sleep(200); if (_active != null) return; }
                string cache = CachePath(fi.FullName);
                if (!File.Exists(cache)) Convert(fi.FullName, CadFormat(fi.FullName), cache, null, cancelled, background: true);
            }
        }
        catch { }
    }

    static Mesh ReadGmesh(string file)
    {
        byte[] d = File.ReadAllBytes(file);
        if (d.Length < 16 || BitConverter.ToUInt32(d, 0) != 0x314D4C47) throw new InvalidDataException("bad cache");
        int v = BitConverter.ToInt32(d, 4), ix = BitConverter.ToInt32(d, 8);
        uint flags = BitConverter.ToUInt32(d, 12);
        long need = 16 + (long)v * 12 + ((flags & 1) != 0 ? (long)v * 12 : 0) + ((flags & 2) != 0 ? (long)v * 4 : 0) + (long)ix * 4
            + ((flags & 4) != 0 ? (long)(ix / 3) * 4 : 0);
        if (v < 0 || ix < 0 || need != d.Length) throw new InvalidDataException("bad cache");
        var m = new Mesh();
        int o = 16;
        m.Pos = MemoryMarshal.Cast<byte, float>(d.AsSpan(o, v * 12)).ToArray(); o += v * 12;
        if ((flags & 1) != 0) { m.Nrm = MemoryMarshal.Cast<byte, float>(d.AsSpan(o, v * 12)).ToArray(); o += v * 12; }
        if ((flags & 2) != 0) { m.Col = MemoryMarshal.Cast<byte, uint>(d.AsSpan(o, v * 4)).ToArray(); o += v * 4; }
        m.Idx = MemoryMarshal.Cast<byte, uint>(d.AsSpan(o, ix * 4)).ToArray(); o += ix * 4;
        if ((flags & 4) != 0) m.FaceId = MemoryMarshal.Cast<byte, uint>(d.AsSpan(o, ix / 3 * 4)).ToArray();
        foreach (uint i in m.Idx) if (i >= (uint)v) throw new InvalidDataException("bad cache");
        return m;
    }

    static void PruneCache(string dir)
    {
        try
        {
            const long limit = 1L << 30;
            foreach (var stale in new DirectoryInfo(dir).GetFiles("*.tmp"))
                if (stale.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1)) stale.Delete();
            var files = new DirectoryInfo(dir).GetFiles("*.gmesh");
            Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            long total = 0;
            foreach (var f in files) { total += f.Length; if (total > limit) f.Delete(); }
        }
        catch { }
    }

    static string Fnv(string s)
    {
        ulong h = 14695981039346656037;
        foreach (char c in s) { h ^= c; h *= 1099511628211; }
        return h.ToString("x16");
    }
}
