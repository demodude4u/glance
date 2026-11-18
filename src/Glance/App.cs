using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using static Glance.Win32;

namespace Glance;

internal static unsafe partial class App
{
    public static long T0;

    static nint _hwnd, _hdc;
    static int _w = 1, _h = 1;
    static float _scale = 1;
    static bool _dirty = true, _animating, _glReady, _quit;
    static long _lastTick;
    static readonly Renderer R = new();
    static readonly Ui U = new();
    static Theme _theme = Theme.Make(true);
    static readonly ConcurrentQueue<Action> _posted = new();

    // Model
    static Mesh? _mesh;
    static string? _path;
    static int _loadGen;
    static bool _loading;
    static string _loadName = "", _loadStage = "";
    static long _loadStart;
    static string? _error;
    static double _loadMs;
    static Vector3 _wMin, _wMax, _center;
    static float _radius = 1;

    // Orientation of the model in the Z-up world (a pure rotation, row-vector convention like System.Numerics).
    // Identity for Z-up files, YUp for OBJ and glTF, or whatever "Lay flat" produced.
    static readonly Matrix4x4 YUp = new(1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1);
    static Matrix4x4 _orient = Matrix4x4.Identity, _orientNow = Matrix4x4.Identity;
    static Quaternion _orientFrom = Quaternion.Identity;
    static float _orientT = 1;
    static bool _yUp => _orient == YUp;
    static bool _customOrient => _orient != YUp && _orient != Matrix4x4.Identity;
    static Matrix4x4 _vp;

    // Camera
    struct Cam { public Vector3 Target; public float Yaw, Pitch, Dist; }
    static Cam _cur = new() { Dist = 3, Yaw = IsoYaw, Pitch = IsoPitch }, _goal = new() { Dist = 3, Yaw = IsoYaw, Pitch = IsoPitch };
    const float Fov = 0.61f, IsoYaw = -0.96f, IsoPitch = 0.49f;
    static float _velYaw, _velPitch;
    static bool _spin;

    // View state
    static bool _edges = true, _grid = true, _wire, _ortho, _help, _info, _fileColors = true, _smooth = true;

    // Input
    static int _drag, _lastX, _lastY;
    static long _lastMove;

    // Toast
    static string _toast = "";
    static long _toastAt;
    static float _toastFor;

    // Test hooks
    static string? _snapshot;
    static string _press = "";
    static int _settle;
    static double _tWindow, _tGl, _tFirst, _tCtx, _tShaders, _tCreated, _tShown, _tFonts, _tWarm;
    public static double PreMain;
    static string _marks = "";
    static void Mark(string n) => _marks += $" {n}={Seconds(T0) * 1000:0}";

    // The OpenGL driver takes longer to load than everything else put together, so it starts on its own
    // thread the moment the process does, while the main thread builds the window and the fonts.
    static nint _warmRc;
    static int _warmFormat;
    static readonly ManualResetEventSlim _warmDone = new();
    static int _pendingShot = -1;
    static bool _stampLoad;

    static double Seconds(long from) => (Stopwatch.GetTimestamp() - from) / (double)Stopwatch.Frequency;
    static void Invalidate() => _dirty = true;

    /// <summary>A longer-lived toast for news that did not come from something the person just did.</summary>
    public static void Notify(string text) => Toast(text, 6f);

    public static void Post(Action a)
    {
        _posted.Enqueue(a);
        if (_hwnd != 0) PostMessageW(_hwnd, WM_APP + 1, 0, 0);
    }

    static void Drain() { while (_posted.TryDequeue(out var a)) a(); }

    // ───────────────────────── Startup ─────────────────────────

    public static int Run(string? file, string? snapshot, string press)
    {
        _snapshot = snapshot; _press = press;
        Settings.Load();
        _edges = Settings.Edges; _grid = Settings.Grid; _ortho = Settings.Ortho;
        _theme = Theme.Make(Settings.Theme == "auto" ? Settings.SystemIsDark() : Settings.Theme == "dark");

        new Thread(WarmGL) { IsBackground = true, Name = "gl" }.Start();
        if (file != null) StartLoad(Path.GetFullPath(file), T0);   // parsing overlaps window + GL start-up

        CreateWindow();
        _tWindow = Seconds(T0) * 1000;
        U.BakeFonts(_scale);
        _tFonts = Seconds(T0) * 1000;
        InitGL();
        _tGl = Seconds(T0) * 1000;
        _glReady = true;
        Drain();
        Mark("drained");

        // Shown only now: showing a window while the graphics driver is still loading on the other thread
        // stalls both for a few hundred milliseconds. This way the window arrives with the model already in it.
        ShowWindow(_hwnd, _snapshot != null ? SW_SHOWNOACTIVATE : Settings.Maximized ? SW_SHOWMAXIMIZED : SW_SHOW);
        _tShown = Seconds(T0) * 1000;

        MSG msg;
        _lastTick = Stopwatch.GetTimestamp();
        while (!_quit)
        {
            if (!(_dirty || _animating))
            {
                if (GetMessageW(&msg, 0, 0, 0) <= 0) break;
                TranslateMessage(&msg); DispatchMessageW(&msg);
                _lastTick = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 60;
            }
            while (PeekMessageW(&msg, 0, 0, 0, PM_REMOVE) != 0)
            {
                if (msg.message == WM_QUIT) { _quit = true; break; }
                TranslateMessage(&msg); DispatchMessageW(&msg);
            }
            if (!_quit && (_dirty || _animating)) Frame();
        }
        Settings.Edges = _edges; Settings.Grid = _grid; Settings.Ortho = _ortho;
        if (_snapshot == null) { Settings.Save(); Updater.ApplyOnExit(); }
        return Environment.ExitCode;
    }

    static uint ColorRef(Vector3 c) =>
        (uint)(c.X * 255 + .5f) | (uint)(c.Y * 255 + .5f) << 8 | (uint)(c.Z * 255 + .5f) << 16;

    static void CreateWindow()
    {
        nint inst = GetModuleHandleW(null);
        fixed (char* cls = "GlanceWindow")
        fixed (char* title = "Glance " + AppVersion.Number)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW), style = CS_OWNDC | CS_DBLCLKS,
                lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nuint, nint, nint>)&WndProc,
                hInstance = inst, hCursor = LoadCursorW(0, 32512),
                hbrBackground = CreateSolidBrush(ColorRef(_theme.BgEdge)),
                lpszClassName = cls, hIcon = LoadImageW(inst, 32512, 1, 0, 0, 0x8040),
            };
            RegisterClassExW(&wc);
            _hwnd = CreateWindowExW(0, cls, title, WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
                unchecked((int)0x80000000), 0, 800, 600, 0, 0, inst, 0);
        }
        _scale = GetDpiForWindow(_hwnd) / 96f;
        _tCreated = Seconds(T0) * 1000;

        POINT cursor; GetCursorPos(&cursor);
        var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        GetMonitorInfoW(MonitorFromPoint(cursor, 2), &mi);
        int ww = mi.rcWork.Right - mi.rcWork.Left, wh = mi.rcWork.Bottom - mi.rcWork.Top;
        int w = (int)((Settings.Width > 300 ? Settings.Width : 1120) * _scale), h = (int)((Settings.Height > 200 ? Settings.Height : 760) * _scale);
        w = Math.Min(w, (int)(ww * 0.94f)); h = Math.Min(h, (int)(wh * 0.94f));
        SetWindowPos(_hwnd, 0, mi.rcWork.Left + (ww - w) / 2, mi.rcWork.Top + (wh - h) / 2, w, h, 0x0014);
        _scale = GetDpiForWindow(_hwnd) / 96f;

        Mark("pos");
        ApplyChrome();
        Mark("dwm");
        DragAcceptFiles(_hwnd, 1);
        Mark("drag");
        UpdateTitle();
        Mark("title");
        RECT rc; GetClientRect(_hwnd, &rc);
        _w = Math.Max(1, rc.Right); _h = Math.Max(1, rc.Bottom);
    }


    /// <summary>Title bar colours follow the theme so the window reads as one surface.</summary>
    static void ApplyChrome()
    {
        int dark = _theme.Dark ? 1 : 0;
        DwmSetWindowAttribute(_hwnd, 20, &dark, 4);
        uint caption = ColorRef(_theme.BgEdge);
        DwmSetWindowAttribute(_hwnd, 35, &caption, 4);
        uint text = ColorRef(new Vector3(_theme.Text.X, _theme.Text.Y, _theme.Text.Z));
        DwmSetWindowAttribute(_hwnd, 36, &text, 4);
    }

    static void FillPixelFormat(byte* pfd)
    {
        new Span<byte>(pfd, 40).Clear();
        pfd[0] = 40; pfd[2] = 1; *(uint*)(pfd + 4) = 0x25; pfd[9] = 32;      // draw to window | OpenGL | double buffer, RGBA 32
    }

    [UnmanagedCallersOnly]
    static nint WarmProc(nint hwnd, uint msg, nuint wp, nint lp) => DefWindowProcW(hwnd, msg, wp, lp);

    static void WarmGL()
    {
        try
        {
            nint inst = GetModuleHandleW(null);
            fixed (char* cls = "GlanceWarm")
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW), style = CS_OWNDC, hInstance = inst, lpszClassName = cls,
                    lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nuint, nint, nint>)&WarmProc,
                };
                RegisterClassExW(&wc);
                nint w = CreateWindowExW(0, cls, null, 0x80000000, 0, 0, 8, 8, 0, 0, inst, 0);
                nint dc = GetDC(w);
                byte* pfd = stackalloc byte[40];
                FillPixelFormat(pfd);
                int fmt = ChoosePixelFormat(dc, pfd);
                if (fmt != 0 && SetPixelFormat(dc, fmt, pfd) != 0)
                {
                    nint rc = wglCreateContext(dc);
                    if (rc != 0) { _warmRc = rc; _warmFormat = fmt; }
                }
                DestroyWindow(w);
            }
        }
        catch { }
        _tWarm = Seconds(T0) * 1000;
        _warmDone.Set();
    }

    static void InitGL()
    {
        _hdc = GetDC(_hwnd);
        byte* pfd = stackalloc byte[40];
        FillPixelFormat(pfd);
        _warmDone.Wait();
        bool ok = false;
        if (_warmRc != 0 && SetPixelFormat(_hdc, _warmFormat, pfd) != 0) ok = wglMakeCurrent(_hdc, _warmRc) != 0;
        if (!ok)
        {
            // Fallback: the conventional single-threaded path.
            int fmt = ChoosePixelFormat(_hdc, pfd);
            if (fmt != 0) SetPixelFormat(_hdc, fmt, pfd);
            nint rc = wglCreateContext(_hdc);
            if (rc == 0 || wglMakeCurrent(_hdc, rc) == 0) throw new InvalidOperationException("Couldn't start OpenGL on this display.");
        }
        GL.Load();
        _tCtx = Seconds(T0) * 1000;
        if (GL.SwapInterval != null) GL.SwapInterval(0);      // don't make the first frame wait for a vertical blank
        R.Init();
        U.Init();
        _tShaders = Seconds(T0) * 1000;
        U.UploadFonts();
    }

    static void UpdateTitle()
    {
        string name = _loading ? _loadName : _path != null ? Path.GetFileName(_path) : "";
        string t = (name.Length > 0 ? name + " — Glance " : "Glance ") + AppVersion.Number;
        fixed (char* p = t) SetWindowTextW(_hwnd, p);
    }

    // ───────────────────────── Loading ─────────────────────────

    static void StartLoad(string path, long startedAt = 0)
    {
        BeforeOpen(path);                   // AppCompare.cs: a compare in progress ends; is this open one to remember?
        int gen = ++_loadGen;
        _loading = true; _loadName = Path.GetFileName(path); _loadStage = "";
        _loadStart = startedAt != 0 ? startedAt : Stopwatch.GetTimestamp();
        _error = null; _path = path;
        if (_hwnd != 0) UpdateTitle();
        Invalidate();
        new Thread(() =>
        {
            try
            {
                if (!File.Exists(path)) throw new FileNotFoundException("That file isn't there any more.");
                Mesh m = Loaders.Load(path,
                    s => Post(() => { if (gen == _loadGen) { _loadStage = s; Invalidate(); } }),
                    () => gen != _loadGen);
                Post(() => OnLoaded(gen, m, null));
            }
            catch (OperationCanceledException) { }
            catch (OutOfMemoryException) { Post(() => OnLoaded(gen, null, "This model is too large to fit in memory.")); }
            catch (Exception ex) { Post(() => OnLoaded(gen, null, ex.Message)); }
        }) { IsBackground = true, Name = "load" }.Start();
    }

    static void OnLoaded(int gen, Mesh? m, string? error)
    {
        if (gen != _loadGen) return;
        _loading = false;
        if (m == null)
        {
            _mesh = null; R.Clear(); _error = error ?? "Something went wrong."; _info = false;
            UpdateTitle(); Invalidate();
            return;
        }
        _mesh = m; _error = null;
        R.Upload(m);
        _orient = _orientNow = m.YUp ? YUp : Matrix4x4.Identity; _orientT = 1; _layFlat = false;
        _fileColors = true; _spin = false; _velYaw = _velPitch = 0;
        ClearMeasure(); _section = 0; _sectionPos = 0.5f; _sectionFlip = false;
        UpdateBounds();
        _goal = FitCam(IsoYaw, IsoPitch);
        _cur = _goal;
        if (_snapshot == null) { _cur.Dist *= 1.22f; _cur.Yaw -= 0.42f; _cur.Pitch -= 0.10f; }   // a short settle-in
        _loadMs = Seconds(_loadStart) * 1000;
        _stampLoad = true;
        UpdateTitle(); Invalidate();
        AfterOpen();                        // AppCompare.cs: recent files, and a compare to set up again after a reload

        string opened = _path!;
        new Thread(() =>
        {
            Analysis.Run(m);
            Post(() =>
            {
                if (_mesh != m) return;
                if (m.SmoothNrm != null && _smooth) R.UploadNormals(m.SmoothNrm);
                R.UploadEdges(m.EdgeLines); Invalidate();
            });
            if (_snapshot == null && Environment.GetEnvironmentVariable("GLANCE_NO_LOOKAHEAD") == null)
                Loaders.WarmFolder(opened, () => _mesh != m || _quit);
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "analysis" }.Start();

        if (m.UnitChoice && m.UnitSource == 1 && m.FileUnit != "mm" && _unitNote && _snapshot == null)
            Toast($"This file doesn't state its units: reading it as {Loaders.UnitLabel(m.FileUnit)}  ·  Fix units to change", 5f);
        else if (m.UnitDoubt && _unitNote && _snapshot == null)
            Toast("This looks too large: reading it as meters  ·  Fix units to change", 5f);
        _unitNote = true;

        if (!Settings.SeenHint && _snapshot == null)
        {
            Settings.SeenHint = true;
            Toast("Drag to orbit  ·  Right-drag to pan  ·  Scroll to zoom  ·  F1 for more", 6f);
        }
    }

    static void UpdateBounds()
    {
        if (_mesh == null) return;
        Vector3 a = _mesh.Min, b = _mesh.Max;
        if (_yUp) { _wMin = new Vector3(a.X, -b.Z, a.Y); _wMax = new Vector3(b.X, -a.Z, b.Y); }
        else if (!_customOrient) { _wMin = a; _wMax = b; }
        else
        {
            // Exact extents of the turned model, so the ground touches the face it was laid on.
            var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
            float[] p = _mesh.Pos; Matrix4x4 o = _orient;
            for (int i = 0; i + 2 < p.Length; i += 3)
            {
                var v = Vector3.Transform(new Vector3(p[i], p[i + 1], p[i + 2]), o);
                lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v);
            }
            _wMin = lo; _wMax = hi;
        }
        _center = (_wMin + _wMax) * 0.5f;
        _radius = MathF.Max((_wMax - _wMin).Length() * 0.5f, 1e-6f);
        CompareBounds();                    // AppCompare.cs: while comparing, the bounds of both models together
    }

    static void StepFile(int dir)
    {
        if (_path == null) return;
        try
        {
            string folder = Path.GetDirectoryName(_path) ?? ".";
            var files = new List<string>();
            foreach (string f in Directory.EnumerateFiles(folder)) if (Loaders.Supported(f)) files.Add(f);
            files.Sort((x, y) => { fixed (char* px = Path.GetFileName(x)) fixed (char* py = Path.GetFileName(y)) return StrCmpLogicalW(px, py); });
            if (files.Count == 0) return;
            int i = files.FindIndex(f => string.Equals(f, _path, StringComparison.OrdinalIgnoreCase));
            if (files.Count == 1 && i == 0) { Toast("This is the only model in the folder"); return; }
            int next = ((i < 0 ? 0 : i + dir) % files.Count + files.Count) % files.Count;
            _browse = true;                 // stepping through a folder is not kept as a recent file
            StartLoad(files[next]);
            Toast($"{next + 1} of {files.Count}", 1.4f);
        }
        catch { }
    }

    static void OpenDialog()
    {
        char* buf = stackalloc char[2048]; buf[0] = '\0';
        fixed (char* filter = "3D models\0*.stl;*.obj;*.3mf;*.ply;*.glb;*.gltf;*.step;*.stp;*.iges;*.igs;*.brep\0All files\0*.*\0\0")
        {
            var ofn = new OPENFILENAMEW
            {
                lStructSize = (uint)sizeof(OPENFILENAMEW), hwndOwner = _hwnd, lpstrFilter = filter,
                lpstrFile = buf, nMaxFile = 2048, Flags = 0x00080000 | 0x1000 | 0x800 | 0x8,
            };
            if (GetOpenFileNameW(&ofn) != 0) StartLoad(new string(buf));
        }
        _drag = 0; Invalidate();
    }

    // ───────────────────────── Camera ─────────────────────────

    static void Basis(in Cam c, out Vector3 dir, out Vector3 right, out Vector3 up)
    {
        float cp = MathF.Cos(c.Pitch), sp = MathF.Sin(c.Pitch), cy = MathF.Cos(c.Yaw), sy = MathF.Sin(c.Yaw);
        dir = new Vector3(cp * cy, cp * sy, sp);
        right = new Vector3(-sy, cy, 0);
        up = Vector3.Cross(dir, right);
    }

    static Cam FitCam(float yaw, float pitch)
    {
        // Tightest distance at which all eight corners of the bounding box stay inside the view.
        var c = new Cam { Target = _center, Yaw = yaw, Pitch = pitch, Dist = 1 };
        Basis(c, out var dir, out var right, out var up);
        float aspect = _w / (float)_h, t = MathF.Tan(Fov / 2), dist = 0;
        Vector3 h = (_wMax - _wMin) * 0.5f;
        for (int i = 0; i < 8; i++)
        {
            var rel = new Vector3((i & 1) == 0 ? -h.X : h.X, (i & 2) == 0 ? -h.Y : h.Y, (i & 4) == 0 ? -h.Z : h.Z);
            float z = Vector3.Dot(rel, dir);
            dist = MathF.Max(dist, z + MathF.Abs(Vector3.Dot(rel, right)) / (t * aspect));
            dist = MathF.Max(dist, z + MathF.Abs(Vector3.Dot(rel, up)) / t);
        }
        c.Dist = MathF.Max(dist * 1.22f, _radius * 0.01f);
        return c;
    }

    static void SetView(float yaw, float pitch)
    {
        if (_mesh == null) return;
        _spin = false; _velYaw = _velPitch = 0;
        Cam g = FitCam(yaw, pitch);
        g.Yaw = yaw + MathF.Tau * MathF.Round((_cur.Yaw - yaw) / MathF.Tau);
        _goal = g; Invalidate();
    }

    static void Fit()
    {
        if (_mesh == null) return;
        Cam g = FitCam(_goal.Yaw, _goal.Pitch);
        _goal = g; Invalidate();
    }

    static void Zoom(float notches, int mx, int my)
    {
        if (_mesh == null) return;
        Basis(_goal, out _, out var r, out var u);
        float t = MathF.Tan(Fov / 2), aspect = _w / (float)_h;
        float nx = 2f * mx / _w - 1, ny = 1 - 2f * my / _h;
        Vector3 p = _goal.Target + r * (nx * t * aspect * _goal.Dist) + u * (ny * t * _goal.Dist);
        float nd = Math.Clamp(_goal.Dist * MathF.Pow(0.86f, notches), _radius * 0.004f, _radius * 40);
        float f = nd / _goal.Dist;
        _goal.Dist = nd; _goal.Target = p + (_goal.Target - p) * f;
        Invalidate();
    }

    static void Matrices(out Matrix4x4 model, out Matrix4x4 view, out Matrix4x4 proj, out Vector3 eye)
    {
        Basis(_cur, out var d, out var r, out var u);
        eye = _cur.Target + d * (_ortho ? _cur.Dist + _radius * 4 : _cur.Dist);
        view = new Matrix4x4(
            r.X, u.X, d.X, 0,
            r.Y, u.Y, d.Y, 0,
            r.Z, u.Z, d.Z, 0,
            -Vector3.Dot(r, eye), -Vector3.Dot(u, eye), -Vector3.Dot(d, eye), 1);
        float dc = (eye - _center).Length(), far = dc + _radius * 6.5f;
        float aspect = _w / (float)_h, t = MathF.Tan(Fov / 2) * _shotCrop;      // a picture shows only its framed part of the view
        if (_ortho)
        {
            float near = dc - _radius * 6.5f, hh = _cur.Dist * t, hw = hh * aspect;
            proj = new Matrix4x4(1 / hw, 0, 0, 0, 0, 1 / hh, 0, 0, 0, 0, -2 / (far - near), 0, 0, 0, -(far + near) / (far - near), 1);
        }
        else
        {
            float near = MathF.Max((dc - _radius * 1.05f) * 0.8f, _radius * 0.004f), f = 1 / t;
            proj = new Matrix4x4(f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, (far + near) / (near - far), -1, 0, 0, 2 * far * near / (near - far), 0);
        }
        model = _orientNow;
    }

    /// <summary>The view ray under a pixel, in model space (the file's own axes).</summary>
    static void Ray(float mx, float my, out Vector3 o, out Vector3 dir)
    {
        Basis(_cur, out var d, out var r, out var u);
        float t = MathF.Tan(Fov / 2), aspect = _w / (float)_h;
        float nx = 2f * mx / _w - 1, ny = 1 - 2f * my / _h;
        if (_ortho)
        {
            o = _cur.Target + d * (_cur.Dist + _radius * 4) + r * (nx * t * aspect * _cur.Dist) + u * (ny * t * _cur.Dist);
            dir = -d;
        }
        else { o = _cur.Target + d * _cur.Dist; dir = Vector3.Normalize(-d + r * (nx * t * aspect) + u * (ny * t)); }
        Matrix4x4 inv = Matrix4x4.Transpose(_orient);        // a rotation's inverse is its transpose
        o = Vector3.Transform(o, inv); dir = Vector3.TransformNormal(dir, inv);
    }

    static Vector3 ToWorld(Vector3 p) => Vector3.Transform(p, _orient);

    /// <summary>Turns the model to a new orientation with a short animation and re-frames it.</summary>
    static void SetOrientation(Matrix4x4 target)
    {
        _orientFrom = Quaternion.CreateFromRotationMatrix(_orientNow);
        _orient = target; _orientT = _snapshot != null ? 1 : 0;
        if (_orientT >= 1) _orientNow = target;
        UpdateBounds();
        _goal = FitCam(_goal.Yaw, _goal.Pitch);
        _hoverDirty = _measure;
        Invalidate();
    }

    /// <summary>"Lay flat": turn the model so the face under the cursor rests on the ground, as a slicer does.</summary>
    static void LayFlatAt(int mx, int my)
    {
        if (_mesh == null) return;
        Ray(mx, my, out Vector3 o, out Vector3 d);
        if (!Analysis.Raycast(_mesh, o, d, ClipPlane(), out _, out int tri, out _)) { Toast("Click a face on the model"); return; }
        Vector3 p0 = _mesh.P(_mesh.Corner(tri, 0)), p1 = _mesh.P(_mesh.Corner(tri, 1)), p2 = _mesh.P(_mesh.Corner(tri, 2));
        Vector3 nm = Vector3.Cross(p1 - p0, p2 - p0);
        if (_hover is { Kind: Kind.Plane } face && face.Tris != null && Vector3.Dot(face.Axis, nm) > 0) nm = face.Axis;   // the whole flat face, not one facet
        if (nm.LengthSquared() <= 0) return;
        if (Vector3.Dot(nm, d) > 0) nm = -nm;               // the side you can see is the outside
        Vector3 n = Vector3.Normalize(Vector3.TransformNormal(Vector3.Normalize(nm), _orient));
        Vector3 down = -Vector3.UnitZ;
        float c = Math.Clamp(Vector3.Dot(n, down), -1, 1);
        Matrix4x4 q = c > 0.999999f ? Matrix4x4.Identity
            : c < -0.999999f ? Matrix4x4.CreateFromAxisAngle(Vector3.UnitX, MathF.PI)
            : Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(n, down)), MathF.Acos(c));
        _layFlat = false; _hover = null; _spin = false;
        SetOrientation(_orient * q);
        Toast("Laid flat  ·  Z puts it back", 2.4f);
    }

    static void FocusAt(int mx, int my)
    {
        if (_mesh == null) return;
        Ray(mx, my, out Vector3 o, out Vector3 dir);
        if (Analysis.Raycast(_mesh, o, dir, ClipPlane(), out Vector3 hit, out _, out float dist))
        {
            _goal.Target = ToWorld(hit);
            _goal.Dist = MathF.Min(_goal.Dist, MathF.Max(dist, _radius * 0.05f));
        }
        else _goal = FitCam(_goal.Yaw, _goal.Pitch);
        _spin = false; Invalidate();
    }

    // ───────────────────────── Frame ─────────────────────────

    static void Frame()
    {
        long now = Stopwatch.GetTimestamp();
        float dt = Math.Clamp((float)((now - _lastTick) / (double)Stopwatch.Frequency), 0.0005f, 0.05f);
        _lastTick = now;
        _dirty = false;
        bool moving = Update(dt);
        if (_hoverDirty) UpdateHover();
        Render(hud: true);
        if (_tFirst == 0) { GL.glFinish(); Mark("rendered"); }
        SwapBuffers(_hdc);
        if (_stampLoad) { _stampLoad = false; _loadMs = Seconds(_loadStart) * 1000; _dirty = true; }   // time to the picture, not just to the parse
        _animating = moving;
        if (_tFirst == 0)
        {
            _tFirst = Seconds(T0) * 1000;
            if (_snapshot == null) Updater.Start();        // only after the first picture is on screen
            if (GL.SwapInterval != null) GL.SwapInterval(1);
        }
        if (_pendingShot >= 0) TestShot();

        if (_snapshot != null) SnapshotStep();
    }

    static bool Update(float dt)
    {
        bool moving = false;
        if (_orientT < 1)
        {
            _orientT = MathF.Min(1, _orientT + dt / 0.30f);
            float e = 1 - MathF.Pow(1 - _orientT, 3);
            _orientNow = _orientT >= 1 ? _orient
                : Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(_orientFrom, Quaternion.CreateFromRotationMatrix(_orient), e));
            moving = true;
        }
        if (_drag == 0)
        {
            if (_spin && _mesh != null) { float a = dt * 0.5f; _cur.Yaw += a; _goal.Yaw += a; moving = true; }
            if (MathF.Abs(_velYaw) + MathF.Abs(_velPitch) > 0.03f)
            {
                _cur.Yaw += _velYaw * dt; _goal.Yaw += _velYaw * dt;
                float p = Math.Clamp(_goal.Pitch + _velPitch * dt, -MathF.PI / 2, MathF.PI / 2);
                _cur.Pitch += p - _goal.Pitch; _goal.Pitch = p;
                float k = MathF.Exp(-dt * 5.5f); _velYaw *= k; _velPitch *= k;
                moving = true;
            }
            else _velYaw = _velPitch = 0;
        }

        float s = 1 - MathF.Exp(-dt * 14f);
        float err = MathF.Abs(_goal.Yaw - _cur.Yaw) + MathF.Abs(_goal.Pitch - _cur.Pitch)
            + MathF.Abs(_goal.Dist - _cur.Dist) / _goal.Dist + (_goal.Target - _cur.Target).Length() / _goal.Dist;
        if (err < 3e-4f) _cur = _goal;
        else
        {
            _cur.Yaw += (_goal.Yaw - _cur.Yaw) * s; _cur.Pitch += (_goal.Pitch - _cur.Pitch) * s;
            _cur.Dist += (_goal.Dist - _cur.Dist) * s; _cur.Target += (_goal.Target - _cur.Target) * s;
            moving = true;
        }

        if (_loading) moving = true;
        if (UpdateTick(dt)) moving = true;
        if (_toast.Length > 0) { if (Seconds(_toastAt) > _toastFor) _toast = ""; else moving = true; }
        if (_frameFlash != 0) { if (Seconds(_frameFlash) > FrameFlashSeconds) _frameFlash = 0; else moving = true; }
        return moving || _snapshot != null;
    }

    static int MaterialIndex => Settings.Material >= 0 && Settings.Material < Theme.Materials.Length
        ? Settings.Material : _theme.Dark ? 0 : 1;

    /// <param name="toWindow">False when the frame is a picture being rendered for copying or saving.</param>
    static void Render(bool hud, bool toWindow = true)
    {
        if (_w <= 0 || _h <= 0) return;
        if (RenderOverlay(hud, toWindow)) return;       // comparing, both models drawn together (AppCompare.cs)
        bool studio = _style >= 4 && _mesh != null && R.HasMesh;
        // Each mode's own options (see AppOptions.cs). Only the mode that is on is asked, so the others are never set up.
        int light = 0, surface = 1, backdrop = 0;
        bool ground = true;
        if (studio && _style == 4) { light = StudioLook.Light.Value; backdrop = StudioLook.Backdrop.Value; ground = StudioLook.Shadow.Value == 1; }
        else if (studio) { surface = MetalLook.Surface.Value; backdrop = MetalLook.Backdrop.Value; ground = MetalLook.Shadow.Value == 1; }
        bool drawing = _style == 1 && _mesh != null && R.HasMesh;      // Lines: a drawing on its own paper
        float floorZ = _wMin.Z - _radius * 0.002f, floorExtent = _radius * 2.4f;
        if (studio)
        {
            // Two extra looks at the part before the real frame: its depth (for soft shadows in corners)
            // and its outline from above (for the shadow on the ground).
            Matrices(out var sm, out var sv, out var sp, out var se);
            Matrix4x4 svp = sv * sp;
            Vector4? sclip = ClipPlane();
            R.Prepass(sm * svp, sclip, _w, _h);
            if (ground)
            {
                var above = new Matrix4x4(1 / floorExtent, 0, 0, 0, 0, 1 / floorExtent, 0, 0, 0, 0, 0, 0, -_center.X / floorExtent, -_center.Y / floorExtent, 0, 1);
                R.ShadowMap(sm * above, sclip);
            }
            R.SetStudio(sp, _ortho, _radius * 0.20f, _theme.Dark, light, surface);
            R.Occlusion(sm * sv, sm * svp, sclip, _w, _h);
        }
        R.Begin(_w, _h, _shotAlpha);
        if (_shotAlpha) { }
        else if (studio)        // a plain studio backdrop instead of the working background
        {
            var (center, rim) = BackdropColors(backdrop);
            R.Background(center, rim, _w / (float)_h);
        }
        else if (drawing)
        {
            var (center, rim) = LinesLook.Sheet;
            R.Background(center, rim, _w / (float)_h);
        }
        else R.Background(_theme.BgCenter, _theme.BgEdge, _w / (float)_h);
        if (_mesh != null && R.HasMesh)
        {
            Matrices(out var model, out var view, out var proj, out var eye);
            Matrix4x4 mv = model * view, vp = view * proj;
            Vector3 mat = Theme.Materials[MaterialIndex].Color;
            bool useCol = _fileColors && R.HasColors;
            if (_style == 4) StudioLook.Apply(ref mat, ref useCol);
            else if (_style == 5) { mat = MetalLook.Color; useCol = false; }
            float lum = useCol ? 0.6f : mat.X * 0.3f + mat.Y * 0.59f + mat.Z * 0.11f;
            Vector4 edge = lum > 0.32f ? new Vector4(0.05f, 0.06f, 0.08f, 0.62f) : new Vector4(1, 1, 1, 0.42f);
            Matrix4x4 mvp = mv * proj;
            _mvp = mvp;
            Vector4? clip = ClipPlane();
            float lineWidth = (_scale >= 1.75f ? 1.6f : 1.1f) * _shotK, hidden = 0;
            Vector4 mode = default;     // what the mesh shader needs for the style: X-ray's glass, the overhang limit
            bool edges = _edges, wire = _wire, noFill = false;
            if (_style == 1)            // Lines: paper and ink, whatever the material
            {
                mat = LinesLook.Fill; useCol = false; edge = LinesLook.Ink;
                if (LinesLook.Hidden.Value == 1) hidden = 0.26f;
                if (LinesLook.Weight.Value == 1) lineWidth *= 2;
            }
            else if (_style == 2) { edge.W *= 0.55f; mode = new Vector4(XrayLook.Glass, 0, 0); }
            else if (_style == 3) mode.X = MathF.Sin(OverhangLook.Degrees * MathF.PI / 180);
            else if (FeatureWire)       // Wire, feature edges only: nothing filled in, every real edge showing
            {
                noFill = true; wire = false; edges = true;
                edge = _theme.Dark ? new Vector4(1, 1, 1, 0.80f) : new Vector4(0.05f, 0.06f, 0.08f, 0.85f);
            }
            Vector3 upView = Vector3.TransformNormal(Vector3.UnitZ, view);
            R.Model(mv, mvp, mat, useCol, _ortho, _theme.Dark ? 0.10f : 0.05f, edges, wire, edge, lineWidth,
                clip, _theme.Dark ? new Vector3(0.36f, 0.42f, 0.50f) : new Vector3(0.62f, 0.68f, 0.76f),
                _style, new Vector4(upView, _wMin.Z - eye.Z), _radius * 0.004f, mode, hidden, noFill);
            _vp = vp;
            if (hud && (_measure || _layFlat))
            {
                Vector3 ac = new(_theme.Accent.X, _theme.Accent.Y, _theme.Accent.Z);
                if (_hover?.Tris != null && !IsSelected(_hover)) R.Highlight(mvp, _hover.Tris, new Vector4(ac, 0.22f), clip);
                if (_selA?.Tris != null) R.Highlight(mvp, _selA.Tris, new Vector4(ac, 0.50f), clip);
                if (_selB?.Tris != null) R.Highlight(mvp, _selB.Tris, new Vector4(ac, 0.50f), clip);
            }
            if (studio)
            {
                if (ground && eye.Z > floorZ) R.Floor(vp, new Vector3(_center.X, _center.Y, floorZ), floorExtent, backdrop == 2 ? 0.60f : 0.58f);
            }
            else if (_grid)
            {
                float gz = _wMin.Z - _radius * 0.002f;
                bool below = eye.Z < gz;
                Theme on = drawing ? LinesLook.Ground : _theme;          // a drawing's grid has to read on its paper
                Vector4 gc = on.Grid; if (below) gc.W *= 0.5f;
                R.Grid(vp, new Vector3(_center.X, _center.Y, gz), _radius, GridStep(), gc, below ? 0 : on.Shadow);
            }
        }
        if (!toWindow) return;
        R.Resolve(_w, _h);
        if (hud) DrawHud();
    }

    static float GridStep() => MathF.Pow(10, MathF.Floor(MathF.Log10(MathF.Max(_radius * 0.45f, 1e-9f))));

    static void Toast(string text, float seconds = 1.6f)
    {
        _toast = text; _toastAt = Stopwatch.GetTimestamp(); _toastFor = seconds; Invalidate();
    }

    // ───────────────────────── Clipboard / snapshot ─────────────────────────

    static byte[] ReadPixels()
    {
        byte[] px = new byte[_w * _h * 4];
        GL.glReadBuffer(GL.BACK);
        fixed (byte* p = px) GL.glReadPixels(0, 0, _w, _h, GL.BGRA, GL.UNSIGNED_BYTE, p);
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        return px;
    }

    /// <summary>Test hook: once the model is loaded, analysed and still, save the frame as a BMP and exit.</summary>
    static void SnapshotStep()
    {
        bool ready = _error != null || (_mesh != null && _mesh.Analyzed && !_loading) || (_path == null && !_loading);
        if (!ready) { _settle = 0; if (Seconds(T0) > 180) { Environment.ExitCode = 4; _quit = true; } return; }
        _settle++;
        if (_settle == 2 && _press.Length > 0) { foreach (char c in _press) OnKey(char.ToUpperInvariant(c), false); _cur = _goal; _spin = false; }
        if (_settle < 5) return;
        _cur = _goal;
        Render(hud: true);
        byte[] px = ReadPixels();
        WriteBmp(_snapshot!, px);
        string report = _error != null ? "error: " + _error
            : _mesh == null ? "empty"
            : $"ok tris={_mesh.TriCount} verts={_mesh.VertexCount} loadMs={_loadMs:0.0} edges={(_mesh.EdgeLines?.Length ?? 0) / 6} closed={_mesh.Closed} fileUnit={_mesh.FileUnit} unitSource={_mesh.UnitSource} format={_mesh.Format} dims={_mesh.Max.X - _mesh.Min.X:0.000},{_mesh.Max.Y - _mesh.Min.Y:0.000},{_mesh.Max.Z - _mesh.Min.Z:0.000} yUp={_mesh.YUp} colors={_mesh.Col != null} dimX={_mesh.Max.X - _mesh.Min.X:0.000} area={_mesh.Area:0.###} volume={_mesh.Volume:0.###} cache={_mesh.FromCache} size={_w}x{_h} scale={_scale} preMain={PreMain:0} created={_tCreated:0} shown={_tShown:0} fonts={_tFonts:0} warm={_tWarm:0} ctx={_tCtx:0} shaders={_tShaders:0} gl={_tGl:0} firstFrame={_tFirst:0} parse={_mesh.ParseMs:0}{_marks} renderer={System.Runtime.InteropServices.Marshal.PtrToStringAnsi(GL.glGetString(0x1F01))} smooth={_mesh.Nrm != null}";
        try { File.WriteAllText(_snapshot + ".txt", report); } catch { }
        Environment.ExitCode = _error != null ? 3 : 0;
        _quit = true;
    }

    static void WriteBmp(string path, byte[] px)
    {
        using var f = File.Create(path);
        Span<byte> hdr = stackalloc byte[54]; hdr.Clear();
        hdr[0] = (byte)'B'; hdr[1] = (byte)'M';
        BitConverter.TryWriteBytes(hdr[2..], 54 + px.Length); BitConverter.TryWriteBytes(hdr[10..], 54);
        BitConverter.TryWriteBytes(hdr[14..], 40); BitConverter.TryWriteBytes(hdr[18..], _w); BitConverter.TryWriteBytes(hdr[22..], _h);
        hdr[26] = 1; hdr[28] = 32;
        f.Write(hdr); f.Write(px);
    }

    /// <summary>
    /// Test hook, active only when GLANCE_TEST_DIR is set: WM_APP+2 asks for a picture of the window and a line
    /// of state once the view has come to rest, so an automated run can drive real input and check the result.
    /// </summary>
    static void TestShot()
    {
        string? dir = Environment.GetEnvironmentVariable("GLANCE_TEST_DIR");
        if (dir == null) { _pendingShot = -1; return; }
        bool camStill = _orientT >= 1 && _cur.Yaw == _goal.Yaw && _cur.Pitch == _goal.Pitch && _cur.Dist == _goal.Dist && _cur.Target == _goal.Target;
        bool settled = !_loading && (_spin || (camStill && MathF.Abs(_velYaw) + MathF.Abs(_velPitch) == 0)) && (_mesh == null || _mesh.Analyzed) && _posted.IsEmpty;
        if (!settled || !ExtrasSettled) { Invalidate(); return; }
        int n = _pendingShot; _pendingShot = -1;
        Render(hud: true);
        byte[] px = ReadPixels();
        WriteBmp(Path.Combine(dir, $"shot{n}.bmp"), px);
        Mesh? m = _mesh;
        File.WriteAllText(Path.Combine(dir, $"shot{n}.txt"), string.Join('\n',
            TestState(),                    // compare and recent files (AppCompare.cs)
            $"file={(_path != null ? Path.GetFileName(_path) : "")}", $"error={_error}", $"tris={m?.TriCount ?? 0}",
            $"yaw={_cur.Yaw:0.0000}", $"pitch={_cur.Pitch:0.0000}", $"dist={_cur.Dist:0.0000}",
            $"target={_cur.Target.X:0.000},{_cur.Target.Y:0.000},{_cur.Target.Z:0.000}", $"radius={_radius:0.000}",
            $"edges={_edges}", $"edgeLines={(m?.EdgeLines?.Length ?? 0) / 6}", $"wire={_wire}", $"shading={ShadingName.Replace(' ', '_')}", $"grid={_grid}", $"ortho={_ortho}",
            $"dark={_theme.Dark}", $"material={Theme.Materials[MaterialIndex].Name}", $"fileColors={_fileColors && R.HasColors}",
            $"smooth={_smooth && m?.SmoothNrm != null}", $"yUp={_yUp}", $"layFlat={_layFlat}", $"custom={_customOrient}", $"worldDims={_wMax.X - _wMin.X:0.000},{_wMax.Y - _wMin.Y:0.000},{_wMax.Z - _wMin.Z:0.000}", $"help={_help}", $"info={_info}", $"spin={_spin}",
            $"toast={_toast}", $"update={Updater.State}", $"updateFill={_updFill:0.00}", $"updateAt={(_updA > 0 ? $"{_updRect.X + _updRect.W / 2:0},{_updRect.Y + _updRect.H / 2:0}" : "")}", $"picture={_picture}", $"picButtons={string.Join(";", _picButtons.Select(b => $"{b.Action}:{b.X + b.W / 2:0},{b.Y + b.H / 2:0}"))}", $"pictureSize={PictureSize().W}x{PictureSize().H}", $"pictureChoice={PictureSizes[PictureChoice()].Key}", $"pictureLimited={PictureSize().Limited}", $"pictureFrame={PictureFrame().X:0.0},{PictureFrame().Y:0.0},{PictureFrame().W:0.0},{PictureFrame().H:0.0}", $"pictureCard={_picCard.X:0},{_picCard.Y:0},{_picCard.W:0},{_picCard.H:0}",
            $"units={(Settings.Units == 1 ? "in" : Settings.Units == 2 ? "frac" : "mm")}", $"sizeText={(m != null ? Len3(ShownSize().X, ShownSize().Y, ShownSize().Z) : "")}", $"resultText={(_result != null ? (_result.IsAngle ? Deg(_result.Value) : Len(_result.Value)) : "")}",
            $"options={string.Join(";", (ModeOptions() ?? []).Select(o => o.Label + ":" + o.Name))}", $"optionRects={string.Join(";", _buttons.Where(b => b.Action >= 100).Select(b => $"{b.X:0},{b.Y:0},{b.W:0},{b.H:0}"))}", $"optionLines={_optLines}", $"toolbarLines={_toolbarLines}", $"buttonRects={string.Join(";", _buttons.Select(b => $"{b.Action}:{b.X:0},{b.Y:0},{b.W:0},{b.H:0}"))}", $"solidPills={_solidPills}",
            $"sliderBox={_sliderBox.X:0},{_sliderBox.Y:0},{_sliderBox.W:0},{_sliderBox.H:0}", $"toastY={_toastY:0}", $"toolbarRight={_toolbarRight:0}", $"fixUnits={_fixUnits}", $"unitRows={string.Join(";", _unitRows.Select(r => $"{r.X + r.W / 2:0},{r.Y + r.H / 2:0}"))}", $"fileUnit={m?.FileUnit}", $"format={m?.Format}", $"unitSource={m?.UnitSource}", $"unitDoubt={m?.UnitDoubt}", $"dims={(m != null ? $"{m.Max.X - m.Min.X:0.000},{m.Max.Y - m.Min.Y:0.000},{m.Max.Z - m.Min.Z:0.000}" : "")}", $"measure={_measure}", $"hover={_hover?.Kind}", $"selA={_selA?.Kind}", $"selB={_selB?.Kind}",
            $"result={(_result != null ? _result.Title + "|" + _result.Value.ToString("0.0000") : _selA != null ? Single(_selA) : "")}",
            $"section={_section}", $"sectionPos={_sectionPos:0.000}", $"inch={_inch}", $"box={_box}", $"watertight={m?.Closed}",
            $"ppu={_h / (2 * _cur.Dist * MathF.Tan(Fov / 2)):0.00000}", $"regions={m?.Regions.Count ?? 0}", $"chains={(m?.ChainClosed?.Length ?? 0)}", $"loadMs={_loadMs:0.0}", $"cache={m?.FromCache}", $"size={_w}x{_h}", $"scale={_scale}", $"fonts={(U.Body.Name + "/" + U.Medium.Name + "/" + U.Title.Name).Replace(' ', '_')}", $"fontsLoaded={BundledFonts.Loaded}", $"version={AppVersion.Full.Replace(' ', '_')}", $"buttons={string.Join(";", _buttons.Select(b => $"{b.Action}:{b.X + b.W / 2:0},{b.Y + b.H / 2:0}"))}"));
    }
}
