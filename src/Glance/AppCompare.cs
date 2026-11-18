using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using static Glance.Win32;

namespace Glance;

/// <summary>
/// Comparing two revisions of a part. A second file is brought in beside the model that is open (D, the Compare
/// button, or a file dropped with Ctrl held). Space then flips between the two with nothing else moving: the camera,
/// the shading and the section cut stay put, so the eye sees what changed. D draws both together instead, coloured
/// where only one of them has material. Esc puts the first model back on its own.
///
/// The model on screen is always <c>_mesh</c>, so measuring, sectioning and every read-out work on it as usual; the
/// other one waits in <c>_cmpMesh</c> and in the renderer's spare buffers, and a flip swaps the two. While comparing,
/// the bounds everything is framed by (grid, ground, section range, near and far planes) are those of both models
/// together, which is what keeps the view from shifting on a flip.
/// </summary>
internal static unsafe partial class App
{
    static bool _cmp;                       // two models are held
    static bool _cmpSecondShown;            // the one on screen is the second (the file that was brought in)
    static bool _cmpOverlay;                // both are drawn together
    static Mesh? _cmpMesh;                  // the one that is not on screen
    static string? _cmpPath;
    static double _cmpLoadMs;
    static int _cmpGen;
    static bool _cmpLoading;
    static string _cmpLoadName = "", _cmpLoadStage = "";
    static long _cmpLoadStart;
    static bool _cmpAligned = true;         // the two overlap enough for an overlay to mean something
    static float _cmpApart, _cmpDiag = 1;   // distance between their centres; size of the two together
    static string? _cmpUnitNote;
    static Vector3 _cmpOwnMin, _cmpOwnMax, _cmpOtherMin, _cmpOtherMax;      // each one's own box as it stands
    static (string Other, bool SecondShown)? _cmpResume;                    // a reload of the model on screen: compare again afterwards
    static (float X, float Y, float W, float H) _cmpCard;
    static bool _cmpCompact;
    static Cam _cmpCamBefore, _cmpCamFit;   // the view before the pair was framed, and the framing it was given
    static bool _cmpReframed;

    const int ActCompare = 40, ActFirst = 41, ActSecond = 42, ActOverlay = 43, ActClose = 44, ActCard = 59;

    [DllImport("user32")] static extern short GetAsyncKeyState(int vk);

    static bool Comparing => _cmp || _cmpLoading;
    static bool Overlaying => _cmp && _cmpOverlay && _cmpMesh != null;
    static Mesh? CmpFirst => _cmpSecondShown ? _cmpMesh : _mesh;
    static Mesh? CmpSecond => _cmpSecondShown ? _mesh : _cmpMesh;
    static string CmpName(bool second) => Path.GetFileName((second == _cmpSecondShown ? _path : _cmpPath) ?? "");

    /// <summary>How a file's own axes stand in the Z-up world before anyone turns the model.</summary>
    static Matrix4x4 Natural(Mesh m) => m.YUp ? YUp : Matrix4x4.Identity;

    /// <summary>Takes a point in one model's own axes to the other's, so that both stand the same way up.</summary>
    static Matrix4x4 Between(Mesh from, Mesh to) => Natural(from) * Matrix4x4.Transpose(Natural(to));

    static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>The size of the model on screen as it stands, without the other one.</summary>
    static Vector3 OwnMin => _cmp && _cmpMesh != null ? _cmpOwnMin : _wMin;
    static Vector3 OwnMax => _cmp && _cmpMesh != null ? _cmpOwnMax : _wMax;

    // ───────────────────────── Bringing in the second file ─────────────────────────

    /// <summary>D, and the Compare button: ask for the file to compare with, starting in the open model's folder.</summary>
    static void PickCompare()
    {
        if (_mesh == null || _path == null || _snapshot != null) return;
        char* buf = stackalloc char[2048]; buf[0] = '\0';
        string dir = Path.GetDirectoryName(_path) ?? "";
        fixed (char* filter = "3D models\0*.stl;*.obj;*.3mf;*.ply;*.glb;*.gltf;*.step;*.stp;*.iges;*.igs;*.brep\0All files\0*.*\0\0")
        fixed (char* title = "Compare with") fixed (char* start = dir)
        {
            var ofn = new OPENFILENAMEW
            {
                lStructSize = (uint)sizeof(OPENFILENAMEW), hwndOwner = _hwnd, lpstrFilter = filter, lpstrTitle = title,
                lpstrInitialDir = dir.Length > 0 ? start : null, lpstrFile = buf, nMaxFile = 2048, Flags = 0x00080000 | 0x1000 | 0x800 | 0x8,
            };
            if (GetOpenFileNameW(&ofn) != 0) StartCompare(new string(buf));
        }
        _drag = 0; Invalidate();
    }

    /// <summary>A file dropped on the window with Ctrl held is compared with the open model instead of replacing it.</summary>
    static bool CompareDrop(string file)
    {
        if (_mesh == null || _loading || (GetAsyncKeyState(VK_CONTROL) & 0x8000) == 0) return false;
        StartCompare(file);
        return true;
    }

    /// <param name="resumeSecond">Set after a reload: the model on screen keeps the place it had (first or second).</param>
    static void StartCompare(string path, bool? resumeSecond = null)
    {
        if (_mesh == null || _path == null || _loading) return;
        try { path = Path.GetFullPath(path); } catch { return; }
        if (_cmp)                           // already comparing: the newcomer replaces the second, the first stays
        {
            float cut = SectionAt();
            if (_cmpSecondShown) FlipCore();
            DropOther();
            UpdateBounds(); SectionTo(cut); UpdateTitle(); FrameBack();
        }
        if (SamePath(path, _path)) { Toast("That is the model already open"); return; }
        int gen = ++_cmpGen;
        _cmpLoading = true; _cmpLoadName = Path.GetFileName(path); _cmpLoadStage = ""; _cmpLoadStart = Stopwatch.GetTimestamp();
        _picture = false; _fixUnits = false;
        Invalidate();
        new Thread(() =>
        {
            try
            {
                if (!File.Exists(path)) throw new FileNotFoundException("That file isn't there any more.");
                Mesh m = Loaders.Load(path,
                    s => Post(() => { if (gen == _cmpGen) { _cmpLoadStage = s; Invalidate(); } }),
                    () => gen != _cmpGen);
                Post(() => CompareLoaded(gen, path, m, null, resumeSecond));
            }
            catch (OperationCanceledException) { }
            catch (OutOfMemoryException) { Post(() => CompareLoaded(gen, path, null, "It is too large to fit in memory beside the first.", resumeSecond)); }
            catch (Exception ex) { Post(() => CompareLoaded(gen, path, null, ex.Message, resumeSecond)); }
        }) { IsBackground = true, Name = "compare" }.Start();
    }

    static void CompareLoaded(int gen, string path, Mesh? m, string? error, bool? resumeSecond)
    {
        if (gen != _cmpGen) return;
        _cmpLoading = false;
        if (m == null || _mesh == null)
        {
            Toast($"Couldn't open {Path.GetFileName(path)}: {(error ?? "something went wrong").ReplaceLineEndings(" ")}", 5f);
            return;
        }
        float cut = SectionAt();
        float fitted = FitCam(_goal.Yaw, _goal.Pitch).Dist;      // how far back the first alone needs the camera
        R.SwapSlots(); R.Upload(m); R.SwapSlots();          // into the spare buffers; the model on screen is untouched
        _cmpMesh = m; _cmpPath = path; _cmpLoadMs = Seconds(_cmpLoadStart) * 1000;
        _cmp = true; _cmpOverlay = false; _cmpSecondShown = resumeSecond == true;
        JudgePlacement();
        UpdateBounds();                                      // from here on: both together
        SectionTo(cut);
        if (resumeSecond == null) FlipCore();                // show the newcomer: the first flip
        // The camera stays exactly where it is, unless the pair needs clearly more room than the first did
        // (the second is much larger, or somewhere else): then the view moves back to take in both.
        _cmpReframed = FitCam(_goal.Yaw, _goal.Pitch).Dist > fitted * 1.15f;
        if (_cmpReframed) { _cmpCamBefore = _goal; _goal = _cmpCamFit = FitCam(_goal.Yaw, _goal.Pitch); }
        UpdateTitle();
        if (resumeSecond == null) NoteOpened(path);          // a recent file like any other

        new Thread(() =>
        {
            Analysis.Run(m);
            Post(() => { SyncSlots(); Invalidate(); });
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "analysis" }.Start();

        CompareToast(_cmpAligned ? "Comparing  ·  Space flips between the two  ·  D overlays them"
            : "Comparing  ·  Space flips between the two  ·  they are not in the same place", 4f);
    }

    /// <summary>
    /// Are the two in the same place? Judged from their boxes as they stand: an overlay is offered when the boxes
    /// share at least half of the smaller one. Nothing is moved to make them fit: a part that grew on one side has
    /// a different centre, so lining up centres or corners would invent differences that are not there.
    /// </summary>
    static void JudgePlacement()
    {
        Mesh a = CmpFirst!, b = CmpSecond!;
        WorldBox(a, Natural(a), out Vector3 aMin, out Vector3 aMax);
        WorldBox(b, Natural(b), out Vector3 bMin, out Vector3 bMax);
        float da = (aMax - aMin).Length(), db = (bMax - bMin).Length();
        _cmpDiag = MathF.Max((Vector3.Max(aMax, bMax) - Vector3.Min(aMin, bMin)).Length(), 1e-6f);
        _cmpApart = ((bMin + bMax) * 0.5f - (aMin + aMax) * 0.5f).Length();
        float shared = 1, eps = MathF.Max(da, db) * 1e-4f;
        for (int i = 0; i < 3; i++)
        {
            float lo = MathF.Max(Axis(aMin, i), Axis(bMin, i)), hi = MathF.Min(Axis(aMax, i), Axis(bMax, i));
            float small = MathF.Min(Axis(aMax, i) - Axis(aMin, i), Axis(bMax, i) - Axis(bMin, i));
            if (small <= eps) { if (hi - lo < -eps) shared = 0; }           // flat this way: touching is enough
            else shared *= Math.Clamp((hi - lo) / small, 0, 1);
        }
        _cmpAligned = shared >= 0.5f;

        // One of them a tidy unit factor larger than the other is far more often a units mix-up than a real change.
        _cmpUnitNote = null;
        if (da > 0 && db > 0)
        {
            float ratio = db / da;
            foreach (float k in (ReadOnlySpan<float>)[25.4f, 10f, 1000f])
            {
                if (MathF.Abs(ratio / k - 1) < 0.02f) _cmpUnitNote = $"2 is about {k:0.#} times the size of 1: one of them may be in the wrong units.";
                else if (MathF.Abs(ratio * k - 1) < 0.02f) _cmpUnitNote = $"1 is about {k:0.#} times the size of 2: one of them may be in the wrong units.";
            }
        }
    }

    static float Axis(Vector3 v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;

    /// <summary>A model's box once turned by <paramref name="orient"/>: exact for the two usual ways up, measured otherwise.</summary>
    static void WorldBox(Mesh m, Matrix4x4 orient, out Vector3 lo, out Vector3 hi)
    {
        Vector3 a = m.Min, b = m.Max;
        if (orient.IsIdentity) { lo = a; hi = b; return; }
        if (orient == YUp) { lo = new Vector3(a.X, -b.Z, a.Y); hi = new Vector3(b.X, -a.Z, b.Y); return; }
        lo = new Vector3(float.MaxValue); hi = new Vector3(float.MinValue);
        float[] p = m.Pos;
        for (int i = 0; i + 2 < p.Length; i += 3)
        {
            var v = Vector3.Transform(new Vector3(p[i], p[i + 1], p[i + 2]), orient);
            lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v);
        }
    }

    /// <summary>Called at the end of UpdateBounds: while comparing, everything is framed by both models together.</summary>
    static void CompareBounds()
    {
        if (!_cmp || _cmpMesh == null || _mesh == null) return;
        _cmpOwnMin = _wMin; _cmpOwnMax = _wMax;
        WorldBox(_cmpMesh, Between(_cmpMesh, _mesh) * _orient, out _cmpOtherMin, out _cmpOtherMax);
        _wMin = Vector3.Min(_wMin, _cmpOtherMin); _wMax = Vector3.Max(_wMax, _cmpOtherMax);
        _center = (_wMin + _wMax) * 0.5f;
        _radius = MathF.Max((_wMax - _wMin).Length() * 0.5f, 1e-6f);
    }

    // ───────────────────────── Flipping, overlaying, leaving ─────────────────────────

    /// <summary>The two change places. The camera, the shading and the section cut are not touched.</summary>
    static void FlipCore()
    {
        if (!_cmp || _cmpMesh == null || _mesh == null) return;
        Mesh was = _mesh;
        (_mesh, _cmpMesh) = (_cmpMesh, _mesh);
        (_path, _cmpPath) = (_cmpPath, _path);
        (_loadMs, _cmpLoadMs) = (_cmpLoadMs, _loadMs);
        R.SwapSlots();
        Matrix4x4 turn = Between(_mesh, was);                // keeps a Y-up file and a Z-up file standing the same way
        _orient = turn * _orient; _orientNow = turn * _orientNow;
        _cmpSecondShown = !_cmpSecondShown;
        ClearMeasure(); _hoverDirty = _measure || _layFlat; _fixUnits = false;
        SyncShown();
        UpdateBounds(); UpdateTitle(); Invalidate();
    }

    static void Flip()
    {
        if (!_cmp || _cmpMesh == null) return;
        _cmpOverlay = false;
        FlipCore();
        CompareToast($"{(_cmpSecondShown ? 2 : 1)}  ·  {Path.GetFileName(_path)}", 1.3f);
    }

    static void ShowSide(bool second)
    {
        if (!_cmp || _cmpMesh == null) return;
        bool was = _cmpOverlay;
        _cmpOverlay = false;
        if (second != _cmpSecondShown) Flip();
        else if (was) CompareToast($"{(second ? 2 : 1)}  ·  {Path.GetFileName(_path)}", 1.3f);
    }

    static void ToggleOverlay()
    {
        if (!_cmp || _cmpMesh == null) return;
        if (!_cmpOverlay && !_cmpAligned)
        {
            Toast("Not in the same place, so there is no overlay", 3f);
            return;
        }
        _cmpOverlay = !_cmpOverlay;
        if (_cmpOverlay)
        {
            _measure = false; _layFlat = false; ClearMeasure();
            CompareToast("Overlay  ·  blue is only in 1, amber only in 2, grey is the same in both", 3.5f);
        }
        else CompareToast($"{(_cmpSecondShown ? 2 : 1)}  ·  {Path.GetFileName(_path)}", 1.3f);
    }

    /// <summary>
    /// If the view was moved back to take in a pair that did not fit, leaving puts it where it was. If the person
    /// has moved it since, the model is framed again from where they are looking instead.
    /// </summary>
    static void FrameBack()
    {
        if (!_cmpReframed) return;
        _cmpReframed = false;
        bool untouched = _goal.Yaw == _cmpCamFit.Yaw && _goal.Pitch == _cmpCamFit.Pitch && _goal.Dist == _cmpCamFit.Dist && _goal.Target == _cmpCamFit.Target;
        _goal = untouched ? _cmpCamBefore : FitCam(_goal.Yaw, _goal.Pitch);
    }

    /// <summary>Lets go of the model that is not on screen: its buffers on the graphics card now, its arrays at the next collection.</summary>
    static void DropOther()
    {
        _cmpGen++;                              // also stops a second file that is still being read
        bool held = _cmpMesh != null;
        _cmpLoading = false;
        R.ReleaseSpare();
        _cmpMesh = null; _cmpPath = null; _cmp = false; _cmpOverlay = false; _cmpSecondShown = false;
        _cmpAligned = true; _cmpUnitNote = null;
        if (held) ThreadPool.QueueUserWorkItem(_ => GC.Collect());
    }

    /// <summary>Esc, the close button on the card, or the Compare button: back to the first model on its own.</summary>
    static void EndCompare(bool say = true)
    {
        if (!Comparing) return;
        bool was = _cmp;
        float cut = was ? SectionAt() : float.NaN;
        if (was && _cmpSecondShown) FlipCore();
        DropOther();
        if (was) { UpdateBounds(); SectionTo(cut); UpdateTitle(); FrameBack(); }
        if (say) Toast(was ? $"Compare closed  ·  {Path.GetFileName(_path)}" : "Compare cancelled", 1.8f);
        Invalidate();
    }

    /// <summary>
    /// Called first thing in StartLoad. Opening another model ends the compare. Reloading the one on screen (F5, a new
    /// unit, the file changing on disk) ends it too, but notes the other file so the compare is set up again
    /// once the reload is in.
    /// </summary>
    static void BeforeOpen(string path)
    {
        bool browse = _browse; _browse = false;
        bool reload = _path != null && SamePath(path, _path);
        _noteOpen = !browse && !reload;
        _cmpResume = null;
        if (!Comparing) return;
        if (reload && _cmp && _cmpPath != null) _cmpResume = (_cmpPath, _cmpSecondShown);
        DropOther();
        UpdateBounds();
        _cmpReframed = false;
    }

    /// <summary>Called when a model has just been put on screen by a normal open.</summary>
    static void AfterOpen()
    {
        if (_noteOpen && _path != null) NoteOpened(_path);
        _noteOpen = false;
        if (_cmpResume is not { } r) return;
        _cmpResume = null;
        StartCompare(r.Other, r.SecondShown);
    }

    /// <summary>Whatever the background analysis has produced for the model on screen goes to the graphics card.</summary>
    static void SyncShown()
    {
        Mesh? m = _mesh;
        if (m == null || !m.Analyzed || !R.HasMesh) return;
        if (m.Nrm == null && m.SmoothNrm != null && R.HasNormals != _smooth) R.UploadNormals(_smooth ? m.SmoothNrm : null);
        if (!R.HasEdges && m.EdgeLines != null) R.UploadEdges(m.EdgeLines);
    }

    static void SyncSlots()
    {
        if (!_cmp || _cmpMesh == null) return;
        SyncShown();
        Mesh? shown = _mesh;
        R.SwapSlots(); _mesh = _cmpMesh;
        SyncShown();
        R.SwapSlots(); _mesh = shown;
    }

    // ───────────────────────── The section cut, shared by both ─────────────────────────

    /// <summary>The range the section slider covers along the cut's axis: both models, in the first one's own axes.</summary>
    static void SectionRange(out float lo, out float hi)
    {
        Mesh first = _cmp ? CmpFirst! : _mesh!;
        Vector3 mn = first.Min, mx = first.Max;
        if (_cmp && CmpSecond is Mesh second)
        {
            Matrix4x4 rel = Between(second, first);
            Vector3 p = Vector3.Transform(second.Min, rel), q = Vector3.Transform(second.Max, rel);
            mn = Vector3.Min(mn, Vector3.Min(p, q)); mx = Vector3.Max(mx, Vector3.Max(p, q));
        }
        int a = _section - 1;
        lo = Axis(mn, a); hi = Axis(mx, a);
    }

    /// <summary>The section plane for one of the two, in that model's own axes. It is the same plane in the world for both.</summary>
    static Vector4? CompareClip(Mesh m)
    {
        if (_section == 0) return null;
        SectionRange(out float lo, out float hi);
        float pad = (hi - lo) * 0.002f;
        float p = lo - pad + (hi - lo + 2 * pad) * _sectionPos;
        int a = _section - 1;
        Vector3 n = a == 0 ? Vector3.UnitX : a == 1 ? Vector3.UnitY : Vector3.UnitZ;
        Mesh first = CmpFirst!;
        if (m != first) n = Vector3.TransformNormal(n, Between(first, m));
        return _sectionFlip ? new Vector4(n, -p) : new Vector4(-n, p);
    }

    /// <summary>Where the cut is now, as a coordinate, so it can be kept there when the range it slides over changes.</summary>
    static float SectionAt() => _section != 0 && _mesh != null ? SectionCoordinate() : float.NaN;

    static void SectionTo(float coordinate)
    {
        if (float.IsNaN(coordinate) || _section == 0 || _mesh == null) return;
        SectionRange(out float lo, out float hi);
        float pad = (hi - lo) * 0.002f, span = hi - lo + 2 * pad;
        if (span > 0) _sectionPos = Math.Clamp((coordinate - lo + pad) / span, 0, 1);
    }

    // ───────────────────────── Overlay ─────────────────────────

    /// The overlay is a look of its own, so it has an option row like the shading modes: how close two surfaces
    /// have to be to count as the same. Three steps around a thousandth of the parts' size, in round numbers.
    static class OverlayLook
    {
        public static readonly ModeOption Match = new("Same within", () => Len(OverlayTolerance()),
            () => Settings.SetOption("overlay.match", (Settings.Option("overlay.match", 1) + 1) % 3));
        public static readonly ModeOption[] All = [Match];
    }

    static float OverlayTolerance()
    {
        float v = MathF.Max(_cmpDiag * 0.001f, 1e-9f);
        float e = MathF.Pow(10, MathF.Floor(MathF.Log10(v))), f = v / e;
        float round = (f < 1.5f ? 1 : f < 3.5f ? 2 : f < 7.5f ? 5 : 10) * e;
        return Settings.Option("overlay.match", 1) switch { 0 => round / 5, 2 => round * 5, _ => round };
    }

    /// <summary>First only, second only, and what the two share. The pair is told apart by more than hue alone.</summary>
    static (Vector3 First, Vector3 Second, Vector3 Shared) OverlayColors => _theme.Dark
        ? (new Vector3(0.20f, 0.50f, 1.00f), Theme.Amber, new Vector3(0.78f, 0.79f, 0.81f))
        : (new Vector3(0.05f, 0.34f, 0.80f), new Vector3(1.00f, 0.72f, 0.03f), new Vector3(0.80f, 0.81f, 0.83f));

    /// <summary>
    /// Called first thing in Render. When the overlay is on it draws the whole frame: both models, each coloured
    /// against the other's depth from this viewpoint (see RendererCompare.cs), on the working background and grid.
    /// </summary>
    static bool RenderOverlay(bool hud, bool toWindow)
    {
        if (_cmpOverlay && (_measure || _layFlat)) _cmpOverlay = false;     // those work on one model: it is shown alone
        if (!Overlaying || !R.HasMesh || !R.HasSpare) return false;
        Mesh shown = _mesh!, other = _cmpMesh!;
        Matrices(out var model, out var view, out var proj, out var eye);
        Matrix4x4 otherModel = Between(other, shown) * model, vp = view * proj;
        Vector4? clip = ClipPlane(), otherClip = CompareClip(other);
        R.OverlayDepth(0, model * vp, clip, _w, _h);
        R.OverlayDepth(1, otherModel * vp, otherClip, _w, _h);
        R.Begin(_w, _h, _shotAlpha);
        if (!_shotAlpha) R.Background(_theme.BgCenter, _theme.BgEdge, _w / (float)_h);
        var (c1, c2, shared) = OverlayColors;
        Vector3 cut = _theme.Dark ? new Vector3(0.36f, 0.42f, 0.50f) : new Vector3(0.62f, 0.68f, 0.76f);
        float tol = OverlayTolerance();
        R.OverlayDraw(0, model * view, model * vp, proj, _ortho, clip, tol, _cmpSecondShown ? c2 : c1, shared, cut);
        R.OverlayDraw(1, otherModel * view, otherModel * vp, proj, _ortho, otherClip, tol, _cmpSecondShown ? c1 : c2, shared, cut);
        _mvp = model * vp; _vp = vp;
        if (_grid)
        {
            float gz = _wMin.Z - _radius * 0.002f;
            bool below = eye.Z < gz;
            Vector4 gc = _theme.Grid; if (below) gc.W *= 0.5f;
            R.Grid(vp, new Vector3(_center.X, _center.Y, gz), _radius, GridStep(), gc, below ? 0 : _theme.Shadow);
        }
        if (!toWindow) return true;
        R.Resolve(_w, _h);
        if (hud) DrawHud();
        return true;
    }

    // ───────────────────────── Keys and clicks ─────────────────────────

    /// <summary>Called first thing in OnKey. D starts a compare, then switches the overlay; Space flips while comparing.</summary>
    static bool CompareKey(int vk)
    {
        if (_help || _picture || _fixUnits || _mesh == null) return false;
        if (vk == 'D')
        {
            if (_cmp) ToggleOverlay();
            else if (!_cmpLoading && !_loading) PickCompare();
            return true;
        }
        if (vk == 0x20 && _cmp) { Flip(); return true; }
        return false;
    }

    static void CompareAction(int action)
    {
        switch (action)
        {
            case ActCompare: if (Comparing) EndCompare(); else PickCompare(); break;
            case ActFirst: ShowSide(false); break;
            case ActSecond: ShowSide(true); break;
            case ActOverlay: ToggleOverlay(); break;
            case ActClose: EndCompare(); break;
        }
    }

    /// <summary>The mouse is over something of this file's that lights up under it.</summary>
    static bool HoverExtra(int mx, int my)
    {
        if (_mesh == null) return RecentHover(mx, my);
        return _cmp && mx >= _cmpCard.X - 4 && mx <= _cmpCard.X + _cmpCard.W + 4 && my >= _cmpCard.Y - 4 && my <= _cmpCard.Y + _cmpCard.H + 4;
    }

    /// <summary>
    /// Test hook, active only when GLANCE_TEST_DIR is set (WM_APP+20): what a dropped file does, without a drag.
    /// The file's path is read from hook.txt in that folder. 1 = dropped with Ctrl held, 2 = dropped.
    /// </summary>
    static void TestHook(int what)
    {
        if (Environment.GetEnvironmentVariable("GLANCE_TEST_DIR") is not string dir) return;
        string file;
        try { file = File.ReadAllText(Path.Combine(dir, "hook.txt")).Trim(); } catch { return; }
        if (what == 1) StartCompare(file);
        else if (what == 2) StartLoad(file);
        Invalidate();
    }

    // ───────────────────────── The card ─────────────────────────

    /// <summary>Called from DrawHud while a model is open: this file's part of the HUD.</summary>
    static void HudExtras(float s, float m)
    {
        FlushOpened();
        if (_cmp && _cmpMesh != null)
        {
            if (!_picture) CompareCard(s, m);
            if (!_mesh!.Analyzed || !_cmpMesh.Analyzed) Invalidate();          // the volumes fill in when the analysis lands
        }
        if (_cmpLoading)
        {
            if (Seconds(_cmpLoadStart) > 0.18) CompareLoadingCard(s);
            Invalidate();
        }
    }

    static void CompareLoadingCard(float s)
    {
        float w = 320 * s, h = 92 * s, x = (_w - w) / 2, y = (_h - h) / 2, pad = 18 * s;
        Card(x, y, w, h, s);
        U.Text(U.Medium, Fit(U.Medium, _cmpLoadName, w - pad * 2), x + pad, y + pad - 2 * s, _theme.Text);
        string stage = _cmpLoadStage.Length > 0 ? _cmpLoadStage : "Opening to compare";
        U.Text(U.Body, Fit(U.Body, stage + "…", w - pad * 2), x + pad, y + pad + U.Medium.Height + 2 * s, _theme.Dim);
        float bx = x + pad, bw = w - pad * 2, bh = 4 * s, by = y + h - pad - bh + 2 * s;
        U.Rect(bx, by, bw, bh, bh / 2, _theme.Panel);
        float t = (float)(Seconds(_cmpLoadStart) * 0.85 % 1.0), e = t * t * (3 - 2 * t);
        float seg = bw * 0.32f, sx = bx - seg + (bw + seg) * e;
        float x0 = MathF.Max(sx, bx), x1 = MathF.Min(sx + seg, bx + bw);
        if (x1 - x0 > bh) U.Rect(x0, by, x1 - x0, bh, bh / 2, _theme.Accent);
    }

    /// <summary>One line of the table: a label, the value for each model, and the change from the first to the second.</summary>
    static List<(string Label, string A, string B, string Change, bool Differs)> CompareRows()
    {
        Mesh first = CmpFirst!, second = CmpSecond!;
        Vector3 shown = _cmpOwnMax - _cmpOwnMin, other = _cmpOtherMax - _cmpOtherMin;
        Vector3 d1 = _cmpSecondShown ? other : shown, d2 = _cmpSecondShown ? shown : other;
        bool plain = _orient.IsIdentity && !first.YUp && !second.YUp;       // both stand as their files say, Z up
        string[] names = plain ? ["Width X", "Depth Y", "Height Z"] : ["Width", "Depth", "Height"];
        var rows = new List<(string, string, string, string, bool)>();
        for (int i = 0; i < 3; i++)
        {
            float a = Axis(d1, i), b = Axis(d2, i);
            string sa = Bare(a), sb = Bare(b);
            bool differs = sa != sb;
            rows.Add((names[i], sa, sb, differs ? (b > a ? "+" : "–") + Bare(MathF.Abs(b - a)) : "=", differs));
        }
        string Vol(Mesh m) => !m.Analyzed ? "…" : !m.Closed ? "–" : _inch ? (m.Volume / 16387.064).ToString("#,0.000") : m.Volume.ToString("#,0");
        string va = Vol(first), vb = Vol(second), vc = "–";
        bool vd = false;
        if (first.Analyzed && second.Analyzed && first.Closed && second.Closed && first.Volume > 0)
        {
            double pct = (second.Volume - first.Volume) / first.Volume * 100;
            vd = va != vb;
            vc = !vd ? "=" : Math.Abs(pct) < 0.05 ? (pct > 0 ? "+" : "–") + "<0.1 %" : (pct > 0 ? "+" : "–") + Math.Abs(pct).ToString(Math.Abs(pct) >= 100 ? "#,0" : "0.0") + " %";
        }
        else if (!first.Analyzed || !second.Analyzed) vc = "…";
        rows.Add(("Volume, " + (_inch ? "in³" : "mm³"), va, vb, vc, vd));
        int ta = first.TriCount, tb = second.TriCount;
        rows.Add(("Triangles", ta.ToString("N0"), tb.ToString("N0"), ta == tb ? "=" : (tb > ta ? "+" : "–") + Math.Abs(tb - ta).ToString("N0"), ta != tb));
        return rows;
    }

    static List<string> Wrap(Face f, string text, float width)
    {
        var lines = new List<string>();
        string line = "";
        foreach (string word in text.Split(' '))
        {
            string tried = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && U.Measure(f, tried) > width) { lines.Add(line); line = word; }
            else line = tried;
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }

    static void Dot(float cx, float cy, float r, Vector3 color)
    {
        U.Rect(cx - r, cy - r, r * 2, r * 2, r, new Vector4(color, 1));
        U.Rect(cx - r, cy - r, r * 2, r * 2, r, _theme.PanelBorder, 1);         // keeps a pale dot visible on a pale card
    }

    /// <summary>Where the card goes: top right, under the Picture button and clear of whatever rows reach that far.</summary>
    static (float X, float Y, float W) CardPlace(float s, float m)
    {
        float w = MathF.Min(312 * s, _w - 2 * m), x = MathF.Max(8 * s, _w - m - w), y = m + 32 * s + 10 * s;
        if (_toolbarWrapRight > x - 12 * s) y = MathF.Max(y, _toolbarBottom + 10 * s);
        if (_optRight > x - 12 * s) y = MathF.Max(y, _optBottom + 10 * s);
        if (_sliderBox.W > 0 && _sliderBox.X + _sliderBox.W > x - 12 * s) y = MathF.Max(y, _sliderBox.Y + _sliderBox.H + 10 * s);
        return (x, y, w);
    }

    /// <summary>
    /// A toast about the compare. The card already says which model is on screen, so where the card sits under the
    /// toasts (a narrow window) the toast is left out instead of being laid over it.
    /// </summary>
    static void CompareToast(string text, float seconds)
    {
        float s = _scale, m = 16 * s, half = U.Measure(U.Body, text) / 2 + 16 * s;
        float x = MathF.Max(8 * s, _w - m - MathF.Min(312 * s, _w - 2 * m));
        if (x < _w / 2f + half + 8 * s) { _toast = ""; return; }
        Toast(text, seconds);
    }

    /// <summary>
    /// The compare card, top right: which of the two is on screen (click one to show it, or both for the overlay),
    /// and the numbers that differ. In a window too short for it, or so narrow that it would sit over the part,
    /// it shrinks to a bar with just the three choices.
    /// </summary>
    static void CompareCard(float s, float m)
    {
        var (c1, c2, shared) = OverlayColors;
        var rows = CompareRows();
        var (x, y, w) = CardPlace(s, m);
        float pad = 16 * s, inner = w - pad * 2, rowH = 30 * s, gap = 5 * s, line = 21 * s;
        Vector4 hotFill = new(_theme.Panel.X, _theme.Panel.Y, _theme.Panel.Z, _theme.Panel.W * 2.2f);
        Vector4 onFill = new(_theme.Accent.X, _theme.Accent.Y, _theme.Accent.Z, _theme.Dark ? 0.22f : 0.10f);
        bool Over(float bx, float by, float bw, float bh) => _drag == 0 && _mouseX >= bx && _mouseX <= bx + bw && _mouseY >= by && _mouseY <= by + bh;

        var notes = new List<string>();
        if (!_cmpAligned)
            notes.AddRange(Wrap(U.Body, $"Not in the same place: 2 is centred {Len(_cmpApart)} from 1, so there is no overlay. Export both from the same position to overlay them.", inner));
        if (_cmpUnitNote != null) notes.AddRange(Wrap(U.Body, _cmpUnitNote, inner));
        string[] legend = _cmpOverlay ? ["Surface only in 1", "Surface only in 2", "The same within " + Len(OverlayTolerance())] : [];
        string foot = _cmpOverlay ? "Hidden changes show with a section cut (X)" : "Space flips  ·  D overlays  ·  Esc closes";

        float head = pad + U.Medium.Height + 14 * s, pick = 3 * rowH + 2 * gap;
        float tableH = 12 * s + U.Body.Height + 4 * s + rows.Count * line;
        float notesH = notes.Count > 0 ? 10 * s + notes.Count * (U.Body.Height + 2 * s) : 0;
        float legendH = legend.Length > 0 ? 12 * s + legend.Length * (U.Body.Height + 3 * s) : 0;
        float h = head + pick + tableH + notesH + legendH + 12 * s + U.Body.Height + pad * 0.8f;
        _cmpCompact = y + h > _h - 8 * s || w > _w * 0.6f;

        void Close(float cx, float cy, float cs)
        {
            bool hot = Over(cx, cy, cs, cs);
            if (hot) U.Rect(cx, cy, cs, cs, cs / 2, hotFill);
            float k = 4.2f * s; var cc = new Vector2(cx + cs / 2, cy + cs / 2);
            Vector4 ink = hot ? _theme.Text : _theme.Dim;
            U.Line(cc - new Vector2(k, k), cc + new Vector2(k, k), 0.8f * s, ink);
            U.Line(cc + new Vector2(-k, k), cc + new Vector2(k, -k), 0.8f * s, ink);
            _buttons.Add((cx, cy, cs, cs, ActClose));
        }

        // One of the three choices: the first, the second, or both together.
        void Choice(float bx, float by, float bw, int action, bool on, bool enabled, string label, string tag, Vector3 dotA, Vector3? dotB)
        {
            bool hot = enabled && Over(bx, by, bw, rowH);
            U.Rect(bx, by, bw, rowH, 9 * s, on ? onFill : hot ? hotFill : _theme.Panel);
            U.Rect(bx, by, bw, rowH, 9 * s, on ? _theme.Accent : _theme.PanelBorder, on ? 1.5f * s : 1);
            float dx = bx + 15 * s, dy = by + rowH / 2;
            if (dotB is Vector3 second) { Dot(dx - 2.5f * s, dy, 4.5f * s, dotA); Dot(dx + 2.5f * s, dy, 4.5f * s, second); }
            else Dot(dx, dy, 4.5f * s, dotA);
            Face face = on ? U.Medium : U.Body;
            Vector4 col = enabled ? _theme.Text : Fade(_theme.Dim, 0.7f);
            float tagW = tag.Length > 0 ? U.Measure(U.Body, tag) + 22 * s : 0;
            U.Text(face, Fit(face, label, bw - 30 * s - tagW), bx + 28 * s, by + (rowH - face.Height) / 2, col);
            if (tag.Length > 0) U.Text(U.Body, tag, bx + bw - 12 * s - U.Measure(U.Body, tag), by + (rowH - U.Body.Height) / 2, Fade(_theme.Dim, enabled ? 0.9f : 0.6f));
            _buttons.Add((bx, by, bw, rowH, action));
        }

        if (_cmpCompact)
        {
            // The bar: 1, 2, Both and the close button in one line.
            float bp = 9 * s, cs = 24 * s;
            h = rowH + bp * 2;
            y = MathF.Max(MathF.Min(y, _h - h - 8 * s), m + 32 * s + 10 * s);
            Card(x, y, w, h, s);
            _cmpCard = (x, y, w, h);
            float seg = (w - bp * 2 - cs - 6 * s - gap * 2) / 3, bx = x + bp;
            Choice(bx, y + bp, seg, ActFirst, !_cmpOverlay && !_cmpSecondShown, true, "1", "", c1, null); bx += seg + gap;
            Choice(bx, y + bp, seg, ActSecond, !_cmpOverlay && _cmpSecondShown, true, "2", "", c2, null); bx += seg + gap;
            Choice(bx, y + bp, seg, ActOverlay, _cmpOverlay, _cmpAligned, "Both", "", c1, c2);
            Close(x + w - bp - cs + 2 * s, y + (h - cs) / 2, cs);
            _buttons.Add((x, y, w, h, ActCard));
            return;
        }

        Card(x, y, w, h, s);
        _cmpCard = (x, y, w, h);
        float ty = y + pad;
        U.Text(U.Medium, "Compare", x + pad, ty, _theme.Text);
        U.Rect(x + pad, ty + U.Medium.Height + 4 * s, 22 * s, 2 * s, s, _theme.Accent);
        Close(x + w - pad - 19 * s, ty + (U.Medium.Height - 24 * s) / 2, 24 * s);
        ty = y + head;
        Choice(x + pad, ty, inner, ActFirst, !_cmpOverlay && !_cmpSecondShown, true, CmpName(false), "1", c1, null); ty += rowH + gap;
        Choice(x + pad, ty, inner, ActSecond, !_cmpOverlay && _cmpSecondShown, true, CmpName(true), "2", c2, null); ty += rowH + gap;
        Choice(x + pad, ty, inner, ActOverlay, _cmpOverlay, _cmpAligned, "Both, overlaid", "D", c1, c2); ty += rowH;

        // The numbers: each model's, and the change from the first to the second.
        ty += 12 * s;
        float labelW = 0;
        foreach (var r in rows) labelW = MathF.Max(labelW, U.Measure(U.Body, r.Label));
        labelW = MathF.Min(labelW + 8 * s, inner * 0.36f);
        float colW = (inner - labelW) / 3, right = x + pad + labelW;
        void Cell(Face f, string text, int col, float cy, Vector4 color)
        {
            text = Fit(f, text, colW - 6 * s);
            U.Text(f, text, right + colW * (col + 1) - U.Measure(f, text), cy, color);
        }
        U.Text(U.Body, UnitName, x + pad, ty, Fade(_theme.Dim, 0.8f));
        Cell(U.Body, "1", 0, ty, _theme.Dim); Cell(U.Body, "2", 1, ty, _theme.Dim); Cell(U.Body, "Change", 2, ty, _theme.Dim);
        ty += U.Body.Height + 4 * s;
        foreach (var r in rows)
        {
            float cy = ty + (line - U.Body.Height) / 2;
            U.Text(U.Body, Fit(U.Body, r.Label, labelW), x + pad, cy, _theme.Dim);
            Cell(U.Body, r.A, 0, cy, _theme.Text); Cell(U.Body, r.B, 1, cy, _theme.Text);
            Cell(r.Differs ? U.Medium : U.Body, r.Change, 2, cy, r.Differs ? _theme.Text : Fade(_theme.Dim, 0.8f));
            ty += line;
        }

        if (notes.Count > 0)
        {
            ty += 10 * s;
            foreach (string n in notes) { U.Text(U.Body, n, x + pad, ty, _theme.Text); ty += U.Body.Height + 2 * s; }
        }
        if (legend.Length > 0)
        {
            ty += 12 * s;
            Vector3[] dots = [c1, c2, shared];
            for (int i = 0; i < legend.Length; i++)
            {
                Dot(x + pad + 5 * s, ty + U.Body.Height / 2f, 4.5f * s, dots[i]);
                U.Text(U.Body, Fit(U.Body, legend[i], inner - 18 * s), x + pad + 18 * s, ty, _theme.Text);
                ty += U.Body.Height + 3 * s;
            }
        }
        ty += 12 * s;
        U.Text(U.Body, Fit(U.Body, foot, inner), x + pad, ty, Fade(_theme.Dim, 0.8f));

        _buttons.Add((x, y, w, h, ActCard));        // a click on the card itself does nothing, rather than turning the model
    }

    // ───────────────────────── Test state ─────────────────────────

    static bool ExtrasSettled => !_cmpLoading && (_cmpMesh == null || _cmpMesh.Analyzed) && RecentSettled;

    /// <summary>More lines for the state dump TestShot writes.</summary>
    static string TestState()
    {
        Mesh? first = CmpFirst, second = _cmp ? CmpSecond : null;
        long t0 = Stopwatch.GetTimestamp();
        GL.glFinish(); Render(hud: true); GL.glFinish();
        double renderMs = Seconds(t0) * 1000;
        string rows = _cmp && _cmpMesh != null ? string.Join(";", CompareRows().Select(r => $"{r.Label}|{r.A}|{r.B}|{r.Change}")) : "";
        return string.Join('\n',
            $"cmp={_cmp}", $"cmpShown={(_cmp ? (_cmpSecondShown ? 2 : 1) : 0)}", $"cmpOverlay={Overlaying}", $"cmpLoading={_cmpLoading}",
            $"cmpFirst={(_cmp ? CmpName(false) : "")}", $"cmpSecond={(_cmp ? CmpName(true) : "")}",
            $"cmpAligned={_cmpAligned}", $"cmpApart={_cmpApart:0.000}", $"cmpUnitNote={_cmpUnitNote}", $"cmpRows={rows}",
            $"cmpCard={_cmpCard.X:0},{_cmpCard.Y:0},{_cmpCard.W:0},{_cmpCard.H:0}", $"cmpCompact={_cmpCompact}",
            $"cmpTris={first?.TriCount ?? 0},{second?.TriCount ?? 0}", $"cmpSpare={R.HasSpare}", $"cmpHeld={_cmpMesh != null}",
            $"cmpOwnDims={(OwnMax - OwnMin).X:0.000},{(OwnMax - OwnMin).Y:0.000},{(OwnMax - OwnMin).Z:0.000}",
            $"overlayTol={(_cmp ? OverlayTolerance() : 0):0.#####}", $"renderMs={renderMs:0.0}",
            $"sectionAt={(_section != 0 && _mesh != null ? SectionCoordinate() : 0):0.0000}",
            $"managedMB={GC.GetTotalMemory(false) / 1048576.0:0.0}", $"orientIdentity={_orient.IsIdentity}",
            RecentState());
    }
}
