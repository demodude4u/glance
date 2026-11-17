using System.Numerics;
using static Glance.Win32;

namespace Glance;

/// <summary>Inspection tools: click-to-measure, section cut, bounding-box dimensions, units, and the toolbar.</summary>
internal static unsafe partial class App
{
    static bool _measure, _box, _layFlat;
    static bool _inch => Settings.Units != 0;
    static bool _frac => Settings.Units == 2;          // inches, written as fractions where the length is one
    static Entity? _hover, _selA, _selB;
    static Measurement? _result;
    static bool _hoverDirty, _moved;
    static int _mouseX, _mouseY, _downX, _downY;
    static int _section;                    // 0 off, 1 X, 2 Y, 3 Z (the file's own axes)
    static float _sectionPos = 0.5f;
    static bool _sectionFlip;
    static Matrix4x4 _mvp;
    static float _pillRight;
    static int _hotButton = -1;
    static readonly List<(float X, float Y, float W, float H, int Action)> _buttons = new();
    static (float X, float Y, float W, float H) _slider, _sliderBox;       // the slider's track, and the whole pill it sits in

    // ───────────────────────── Units ─────────────────────────

    static string UnitName => _inch ? "in" : "mm";
    static string UnitButton => _frac ? "in 1/64" : UnitName;

    // Fraction mode only ever shows a fraction for a length that really is one: within half a thousandth of an
    // inch of a 64th. Anything else stays decimal, so 1 3/8" on screen means the part is 1 3/8".
    const double FractionTolerance = 0.0005;

    /// <summary>Inches as a reduced fraction ("1 3/8", "5/16", "2"), or null when the length is not on a 64th.</summary>
    static string? Fraction(double inches)
    {
        double n = Math.Round(inches * 64);
        if (!(Math.Abs(inches - n / 64) <= FractionTolerance) || Math.Abs(n) > 1e12) return null;
        long k = (long)Math.Abs(n), whole = k / 64, num = k % 64, den = 64;
        while (num != 0 && (num & 1) == 0) { num >>= 1; den >>= 1; }
        string s = num == 0 ? whole.ToString("#,0") : whole == 0 ? $"{num}/{den}" : $"{whole:#,0} {num}/{den}";
        return n < 0 ? "-" + s : s;
    }

    static string Bare(double mm) => !_inch ? mm.ToString("#,0.00#")
        : _frac && Fraction(mm / 25.4) is string f ? f : (mm / 25.4).ToString("#,0.000#");
    static string Len(double mm) => _frac ? Bare(mm) + "\"" : Bare(mm) + " " + UnitName;
    /// <summary>Three lengths side by side: one unit at the end, or an inch mark on each when fractions are on.</summary>
    static string Len3(double x, double y, double z, string gap = " × ") =>
        _frac ? Len(x) + gap + Len(y) + gap + Len(z) : Bare(x) + gap + Bare(y) + gap + Bare(z) + " " + UnitName;

    static void CycleUnits()
    {
        Settings.Units = (Settings.Units + 1) % 3;
        Toast(Settings.Units switch
        {
            1 => "Showing inches",
            2 => "Showing inches  ·  as fractions (to 1/64) where the size is exactly one",
            _ => "Showing millimeters",
        }, Settings.Units == 2 ? 2.6f : 1.6f);
    }
    static string AreaText(double mm2) => _inch ? (mm2 / 645.16).ToString("#,0.000") + " in²" : mm2.ToString("#,0.0#") + " mm²";
    static string VolumeText(double mm3) => _inch ? (mm3 / 16387.064).ToString("#,0.000") + " in³" : mm3.ToString("#,0") + " mm³";
    static string Deg(double a) => a.ToString("0.0#") + "°";

    // ───────────────────────── State ─────────────────────────

    static void ClearMeasure() { _hover = _selA = _selB = null; _result = null; }

    static bool IsSelected(Entity e) => (_selA != null && _selA.SameAs(e)) || (_selB != null && _selB.SameAs(e));

    static void ToggleMeasure()
    {
        if (_mesh == null) return;
        _measure = !_measure;
        ClearMeasure();
        if (_measure && !_mesh.Analyzed) Toast("Measuring will be ready in a moment");
        else if (_measure && _mesh.TriAdj == null) Toast("This model is too large to measure");
        _hoverDirty = _measure;
    }

    static void ToggleLayFlat()
    {
        if (_mesh == null) return;
        _layFlat = !_layFlat;
        if (_layFlat) { _measure = false; ClearMeasure(); }
        _hover = null; _hoverDirty = _layFlat;
    }

    /// <summary>Orthographic (parallel) view: no perspective, so equal sizes look equal. Remembered between runs.</summary>
    static void ToggleOrtho()
    {
        _ortho = !_ortho;
        Toast(_ortho ? "Orthographic  ·  no perspective, true proportions" : "Perspective");
        _hoverDirty = _measure || _layFlat;
    }

    // ───────────────────────── Shading ─────────────────────────

    static int _style;      // how faces are filled: 0 shaded, 1 lines (flat paper), 2 x-ray, 3 overhangs, 4 studio clay, 5 metal
    static float _toolbarRight, _toolbarWrapRight, _toolbarBottom;      // right edge of the toolbar's first line and of the lines under it; its bottom
    static int _toolbarLines = 1;

    /// The looks the Shading button steps through. E and W still switch edges and wireframe on their own.
    static readonly (string Name, string Note, int Style, bool Edges, bool Wire)[] Shadings =
    [
        ("Shaded", "solid with edges", 0, true, false),
        ("Smooth", "solid, no edges", 0, false, false),
        ("Studio", "matte clay with soft shadows, for pictures", 4, false, false),
        ("Metal", "machined metal in a photo studio", 5, false, false),
        ("Lines", "edges only, like a drawing", 1, true, false),
        ("X-ray", "see through to inner features", 2, true, false),
        ("Wire", "every triangle", 0, true, true),
        ("Overhangs", "faces leaning past 45° as the part stands", 3, true, false),
    ];

    static int ShadingIndex()
    {
        for (int i = 0; i < Shadings.Length; i++)
            if (Shadings[i].Style == _style && Shadings[i].Edges == _edges && Shadings[i].Wire == _wire) return i;
        for (int i = 0; i < Shadings.Length; i++)
            if (Shadings[i].Style == _style && Shadings[i].Wire == _wire) return i;
        return 0;
    }

    static string ShadingName => Shadings[ShadingIndex()].Name;

    static void CycleShading(bool back = false)
    {
        var next = Shadings[(ShadingIndex() + (back ? Shadings.Length - 1 : 1)) % Shadings.Length];
        _style = next.Style; _edges = next.Edges; _wire = next.Wire;
        if (_style == 2) _edges = Settings.Option("xray.edges", 1) == 1;       // X-ray remembers whether its edges were on
        string note = _style == 3 ? $"faces leaning past {OverhangLook.Degrees}° as the part stands" : _wire ? WireLook.Note : next.Note;
        Toast($"{next.Name}  ·  {note}");
    }

    static void CycleSection()
    {
        _section = (_section + 1) % 4;
        _sectionPos = 0.5f; _sectionFlip = false;
        Toast(_section == 0 ? "Section off" : $"Section along {"XYZ"[_section - 1]}  ·  Ctrl+scroll or drag the slider");
        _hoverDirty = _measure;
    }

    /// <summary>STL, OBJ and PLY only: read the file's numbers as the next unit (mm, in, cm, m, ft) and remember it for this file.</summary>
    static void CycleFileUnit()
    {
        if (_mesh == null || _path == null) return;
        if (!_mesh.UnitChoice) { Toast($"{_mesh.Format} files state their own units"); return; }
        int i = Array.IndexOf(Loaders.UnitNames, _mesh.FileUnit);
        string next = Loaders.UnitNames[(i + 1) % Loaders.UnitNames.Length];
        Loaders.SetUnitOverride(_path, next);
        _unitNote = false;
        StartLoad(_path);
        Toast($"Reading this file as {Loaders.UnitLabel(next)}", 2.4f);
    }

    static bool _unitNote = true;

    // "Fix units": a chooser that shows how big the part would be in each unit.
    static bool _fixUnits;
    static int _hotUnit = -1;
    static readonly List<(float X, float Y, float W, float H)> _unitRows = new();

    static void ToggleFixUnits()
    {
        if (_mesh == null || _path == null) return;
        if (!_mesh.UnitChoice) { Toast($"{_mesh.Format} files state their own units"); return; }
        _fixUnits = !_fixUnits;
        if (_fixUnits) { _measure = false; _layFlat = false; ClearMeasure(); }
    }

    static void SetFileUnit(string unit)
    {
        _fixUnits = false;
        if (_mesh == null || _path == null) return;
        if (unit == _mesh.FileUnit && _mesh.UnitSource == 2) return;
        Loaders.SetUnitOverride(_path, unit);
        _unitNote = false;
        StartLoad(_path);
        Toast($"Reading this file as {Loaders.UnitLabel(unit)}", 2.4f);
    }

    static string Proper(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    static void FixUnitsCard(float s)
    {
        Mesh mesh = _mesh!;
        Vector3 raw = (mesh.Max - mesh.Min) / Loaders.UnitToMm(mesh.FileUnit);      // the numbers as written in the file
        string Num(float v) => v.ToString(MathF.Abs(v) >= 1000 ? "#,0" : MathF.Abs(v) >= 10 ? "0.##" : "0.###");
        string title = "What units is this file in?";
        string line1 = $"{mesh.Format} files only store numbers. This one measures {Num(raw.X)} × {Num(raw.Y)} × {Num(raw.Z)}.";
        string line2 = mesh.UnitSource switch
        {
            0 => $"The file says {Loaders.UnitLabel(mesh.FileUnit)}. Pick the size that looks right.",
            2 => $"You chose {Loaders.UnitLabel(mesh.FileUnit)}. Pick the size that looks right.",
            _ => $"Glance guessed {Loaders.UnitLabel(mesh.FileUnit)}. Pick the size that looks right.",
        };
        string foot = "Remembered for this file  ·  Esc to cancel";

        string[] units = Loaders.UnitNames;
        var sizes = new string[units.Length];
        float nameW = 0, sizeW = 0;
        for (int i = 0; i < units.Length; i++)
        {
            Vector3 d = raw * Loaders.UnitToMm(units[i]);
            sizes[i] = Len3(d.X, d.Y, d.Z);
            nameW = MathF.Max(nameW, U.Measure(U.Medium, Proper(Loaders.UnitLabel(units[i]))));
            sizeW = MathF.Max(sizeW, U.Measure(U.Body, sizes[i]));
        }
        float pad = 22 * s, rowH = 40 * s, rowGap = 6 * s, inPad = 14 * s;
        float w = MathF.Max(MathF.Max(U.Measure(U.Body, line1), U.Measure(U.Body, line2)), nameW + sizeW + inPad * 2 + 36 * s) + pad * 2;
        w = MathF.Min(w, _w - 24 * s);
        float h = pad + U.Medium.Height + 8 * s + U.Body.Height * 2 + 4 * s + 16 * s + units.Length * (rowH + rowGap) + 8 * s + U.Body.Height + pad * 0.8f;
        float x = (_w - w) / 2, y = MathF.Max(12 * s, (_h - h) / 2);

        U.Rect(0, 0, _w, _h, 0, _theme.Scrim);
        Card(x, y, w, h, s);
        float ty = y + pad;
        U.Text(U.Medium, title, x + pad, ty, _theme.Text); ty += U.Medium.Height + 8 * s;
        U.Text(U.Body, Fit(U.Body, line1, w - pad * 2), x + pad, ty, _theme.Dim); ty += U.Body.Height + 4 * s;
        U.Text(U.Body, Fit(U.Body, line2, w - pad * 2), x + pad, ty, _theme.Dim); ty += U.Body.Height + 16 * s;

        _unitRows.Clear(); _hotUnit = -1;
        for (int i = 0; i < units.Length; i++)
        {
            float rx = x + pad, rw = w - pad * 2;
            bool current = units[i] == mesh.FileUnit;
            bool hot = _mouseX >= rx && _mouseX <= rx + rw && _mouseY >= ty && _mouseY <= ty + rowH;
            if (hot) _hotUnit = i;
            if (current) U.Rect(rx, ty, rw, rowH, 9 * s, new Vector4(_theme.Accent.X, _theme.Accent.Y, _theme.Accent.Z, _theme.Dark ? 0.22f : 0.10f));
            else U.Rect(rx, ty, rw, rowH, 9 * s, hot ? new Vector4(_theme.Panel.X, _theme.Panel.Y, _theme.Panel.Z, _theme.Panel.W * 2.2f) : _theme.Panel);
            U.Rect(rx, ty, rw, rowH, 9 * s, current ? _theme.Accent : _theme.PanelBorder, current ? 1.5f * s : 1);
            U.Text(U.Medium, Proper(Loaders.UnitLabel(units[i])), rx + inPad, ty + (rowH - U.Medium.Height) / 2, _theme.Text);
            string size = Fit(U.Body, sizes[i], rw - nameW - inPad * 2 - 16 * s);
            U.Text(U.Body, size, rx + rw - inPad - U.Measure(U.Body, size), ty + (rowH - U.Body.Height) / 2, current ? _theme.Text : _theme.Dim);
            _unitRows.Add((rx, ty, rw, rowH));
            ty += rowH + rowGap;
        }
        ty += 8 * s;
        U.Text(U.Body, foot, x + pad, ty, Fade(_theme.Dim, 0.8f));
    }

    /// <summary>The section plane in model space: points with xyz·p + w ≥ 0 are kept.</summary>
    static Vector4? ClipPlane()
    {
        if (_section == 0 || _mesh == null) return null;
        int a = _section - 1;
        float lo = a == 0 ? _mesh.Min.X : a == 1 ? _mesh.Min.Y : _mesh.Min.Z, hi = a == 0 ? _mesh.Max.X : a == 1 ? _mesh.Max.Y : _mesh.Max.Z;
        float pad = (hi - lo) * 0.002f;
        float p = lo - pad + (hi - lo + 2 * pad) * _sectionPos;
        Vector3 n = a == 0 ? Vector3.UnitX : a == 1 ? Vector3.UnitY : Vector3.UnitZ;
        return _sectionFlip ? new Vector4(n, -p) : new Vector4(-n, p);
    }

    static float SectionCoordinate()
    {
        Vector4 c = ClipPlane() ?? default;
        return _sectionFlip ? -c.W : c.W;
    }

    static Entity? PickEntity(int mx, int my)
    {
        if (_mesh == null || _mesh.TriAdj == null) return null;
        Matrices(out var model, out var view, out var proj, out _);
        _mvp = model * view * proj;
        Ray(mx, my, out Vector3 o, out Vector3 d);
        return Inspect.PickAt(_mesh, o, d, _mvp, mx, my, _w, _h, 9 * _scale, ClipPlane());
    }

    static void UpdateHover()
    {
        _hoverDirty = false;
        if (!(_measure || _layFlat) || _drag != 0) return;
        _hover = PickEntity(_mouseX, _mouseY);
    }

    static void ClickMeasure(int mx, int my)
    {
        Entity? e = PickEntity(mx, my);
        _hover = e;
        if (e == null) { _selA = _selB = null; _result = null; return; }
        if (_selA == null || _selB != null) { _selA = e; _selB = null; _result = null; }
        else if (_selA.SameAs(e)) { _selA = null; }
        else { _selB = e; _result = Inspect.Between(_selA, _selB); }
        Invalidate();
    }

    /// <summary>The headline value of one feature on its own.</summary>
    static string Single(Entity e) => e.Kind switch
    {
        Kind.Point => _frac ? $"X {Len(e.Pick.X)}   Y {Len(e.Pick.Y)}   Z {Len(e.Pick.Z)}"
            : $"X {Bare(e.Pick.X)}   Y {Bare(e.Pick.Y)}   Z {Bare(e.Pick.Z)} {UnitName}",
        Kind.Line or Kind.Curve => Len(e.Length),
        Kind.Circle => "Ø " + Len(e.Radius * 2),
        Kind.Cylinder => e.Full ? "Ø " + Len(e.Radius * 2) : "R " + Len(e.Radius),
        _ => AreaText(e.Area),
    };

    static string SingleNote(Entity e) => e.Kind switch
    {
        Kind.Line => "Length",
        Kind.Curve => "Length along the edge",
        Kind.Circle => e.Full ? $"Diameter  ·  radius {Len(e.Radius)}" : $"Diameter  ·  radius {Len(e.Radius)}  ·  arc length {Len(e.Length)}",
        Kind.Cylinder => e.Full ? $"Diameter  ·  radius {Len(e.Radius)}" : $"Radius  ·  diameter {Len(e.Radius * 2)}",
        Kind.Point => "Position",
        _ => "Area",
    };

    // ───────────────────────── Mouse on the HUD ─────────────────────────

    static bool HudClick(int mx, int my)
    {
        if (UpdateClick(mx, my)) return true;
        if (_mesh == null) return false;
        if (_picture) return PictureClick(mx, my);
        if (_fixUnits)                      // the chooser is modal: a row picks, anywhere else closes
        {
            for (int i = 0; i < _unitRows.Count; i++)
            {
                var r = _unitRows[i];
                if (mx >= r.X && mx <= r.X + r.W && my >= r.Y && my <= r.Y + r.H) { SetFileUnit(Loaders.UnitNames[i]); Invalidate(); return true; }
            }
            _fixUnits = false; Invalidate();
            return true;
        }
        foreach (var b in _buttons)
        {
            if (mx < b.X || mx > b.X + b.W || my < b.Y || my > b.Y + b.H) continue;
            switch (b.Action)
            {
                case 0: _layFlat = false; ToggleMeasure(); break;
                case 1: CycleSection(); break;
                case 2: _box = !_box; break;
                case 3: CycleUnits(); break;
                case 4: _sectionFlip = !_sectionFlip; _hoverDirty = _measure; break;
                case 5: ToggleFixUnits(); break;
                case 6: ToggleLayFlat(); break;
                case 7: ToggleOrtho(); break;
                case 8: CycleShading(); break;
                case 9: TogglePicture(); break;
                case >= 100: CycleOption(b.Action - 100); break;
            }
            Invalidate();
            return true;
        }
        if (_section != 0 && _slider.W > 0 && mx >= _slider.X - 10 * _scale && mx <= _slider.X + _slider.W + 10 * _scale
            && my >= _slider.Y - 8 * _scale && my <= _slider.Y + _slider.H + 8 * _scale)
        {
            _drag = 3; SetCapture(_hwnd);
            DragSlider(mx);
            return true;
        }
        return false;
    }

    static void DragSlider(int mx)
    {
        if (_slider.W <= 0) return;
        _sectionPos = Math.Clamp((mx - _slider.X) / _slider.W, 0, 1);
        _hoverDirty = _measure;
        Invalidate();
    }

    // ───────────────────────── Drawing ─────────────────────────

    static bool OnScreen(Vector3 modelPoint, out Vector2 px) => Inspect.Project(modelPoint, _mvp, _w, _h, out px);

    static Vector4 AccentInk => _theme.Dark ? new Vector4(0.13f, 0.13f, 0.13f, 1) : new Vector4(1, 1, 1, 1);

    static void Toolbar(float s, float m)
    {
        string[] labels = ["Measure", _section == 0 ? "Section" : "Section " + "XYZ"[_section - 1], "Box", "Lay flat", "Ortho", ShadingName, UnitButton];
        bool[] on = [_measure, _section != 0, _box, _layFlat, _ortho, false, false];
        int[] actions = [0, 1, 2, 6, 7, 8, 3];
        float x = m, y = m, h = 32 * s, pad = 14 * s, gap = 6 * s;
        // In a narrow window the buttons wrap onto more lines. The first line stops short of the Picture button
        // (top right); the lines under it can use the whole width.
        float limit = _w - m - (U.Measure(U.Medium, "Picture") + pad * 2) - 12 * s;
        _toolbarLines = 1; _toolbarRight = 0; _toolbarWrapRight = 0;
        void Wrap(float w, float lead = 0)
        {
            if (x > m && x + lead + w > limit) { x = m; y += h + gap; limit = _w - m; _toolbarLines++; }
            else if (x > m) x += lead;
        }
        void Placed(float w)
        {
            if (_toolbarLines == 1) _toolbarRight = x + w; else _toolbarWrapRight = MathF.Max(_toolbarWrapRight, x + w);
        }
        _hotButton = -1;
        for (int i = 0; i < labels.Length; i++)
        {
            float w = U.Measure(U.Medium, labels[i]) + pad * 2;
            Wrap(w);
            bool hot = _drag == 0 && _mouseX >= x && _mouseX <= x + w && _mouseY >= y && _mouseY <= y + h;
            if (hot) _hotButton = i;
            if (on[i]) U.Rect(x, y, w, h, h / 2, _theme.Accent);
            else Pill(x, y, w, h, hot);
            U.Text(U.Medium, labels[i], x + pad, y + (h - U.Medium.Height) / 2, on[i] ? AccentInk : _theme.Text);
            _buttons.Add((x, y, w, h, actions[i]));
            Placed(w);
            x += w + gap;
        }

        // Files without trustworthy units get one more button: what the file's numbers are being read as.
        if (_mesh!.UnitChoice)
        {
            bool guess = _mesh.UnitSource == 1 && _mesh.FileUnit != "mm";      // only flag a guess that changed the size
            string label = "Fix units";
            float w = U.Measure(U.Medium, label) + pad * 2;
            Wrap(w, gap * 2);                                                    // set a little apart from the rest
            bool hot = _drag == 0 && _mouseX >= x && _mouseX <= x + w && _mouseY >= y && _mouseY <= y + h;
            if (hot) _hotButton = 5;
            Pill(x, y, w, h, hot);
            if (guess) U.Rect(x, y, w, h, h / 2, _theme.Accent, 1.5f * s);
            U.Text(U.Medium, label, x + pad, y + (h - U.Medium.Height) / 2, _theme.Text);
            _buttons.Add((x, y, w, h, 5));
            Placed(w);
        }
        _toolbarBottom = y + h;
        _optBottom = _toolbarBottom;
    }

    static void SectionSlider(float s, float m)
    {
        string label = $"{"XYZ"[_section - 1]}  {Len(SectionCoordinate())}";
        float track = MathF.Min(260 * s, _w * 0.3f), pad = 16 * s, h = 34 * s, flipW = U.Measure(U.Medium, "Flip") + 22 * s;
        float lw = U.Measure(U.Medium, "X  " + Len(-8888.888)) + 6 * s;
        if (_frac) lw = MathF.Max(lw, U.Measure(U.Medium, "X  -888 63/64\"") + 6 * s);
        float w = pad + lw + track + pad + flipW + 6 * s;
        float x = (_w - w) / 2, y = m;
        // Beside the toolbar when there is room for it there; otherwise under the toolbar and the mode's options.
        bool beside = _toolbarLines == 1 && x >= _toolbarRight + 12 * s && (_picButton.W <= 0 || x + w <= _picButton.X - 12 * s);
        if (!beside) y = _optBottom + 8 * s;
        _sliderBox = (x, y, w, h);
        U.Rect(x, y + 2 * s, w, h, h / 2, new Vector4(0, 0, 0, _theme.Dark ? 0.22f : 0.08f));
        U.Rect(x, y, w, h, h / 2, _theme.Card);
        U.Rect(x, y, w, h, h / 2, _theme.PanelBorder, 1);
        U.Text(U.Medium, label, x + pad, y + (h - U.Medium.Height) / 2, _theme.Text);
        float tx = x + pad + lw, ty = y + h / 2 - 2 * s;
        U.Rect(tx, ty, track, 4 * s, 2 * s, new Vector4(_theme.Dim.X, _theme.Dim.Y, _theme.Dim.Z, 0.35f));
        U.Rect(tx, ty, track * _sectionPos, 4 * s, 2 * s, _theme.Accent);
        float kx = tx + track * _sectionPos;
        U.Rect(kx - 8 * s, y + h / 2 - 8 * s, 16 * s, 16 * s, 8 * s, _theme.Accent);
        U.Rect(kx - 3 * s, y + h / 2 - 3 * s, 6 * s, 6 * s, 3 * s, AccentInk);
        _slider = (tx, ty - 6 * s, track, 16 * s);

        float fx = tx + track + pad, fh = 24 * s, fy = y + (h - fh) / 2;
        U.Rect(fx, fy, flipW, fh, fh / 2, _sectionFlip ? _theme.Accent : _theme.Panel);
        U.Text(U.Medium, "Flip", fx + 11 * s, fy + (fh - U.Medium.Height) / 2, _sectionFlip ? AccentInk : _theme.Text);
        _buttons.Add((fx, fy, flipW, fh, 4));
    }

    static void DrawEntity(Entity e, Vector4 color, float s, float width)
    {
        if (e.Path != null)
        {
            int step = Math.Max(1, e.Path.Length / 3000);
            Vector2 prev = default; bool have = false;
            for (int i = 0; i < e.Path.Length; i += step)
            {
                if (!OnScreen(e.Path[i], out Vector2 p)) { have = false; continue; }
                if (have) U.Line(prev, p, width, color);
                prev = p; have = true;
            }
        }
        if (e.Kind == Kind.Point && OnScreen(e.Pick, out Vector2 pt))
        {
            U.Rect(pt.X - 6 * s, pt.Y - 6 * s, 12 * s, 12 * s, 6 * s, color);
            U.Rect(pt.X - 2.5f * s, pt.Y - 2.5f * s, 5 * s, 5 * s, 2.5f * s, AccentInk);
        }
        if (e.Kind is Kind.Circle or Kind.Cylinder && OnScreen(e.Center, out Vector2 c))
        {
            U.Line(c - new Vector2(6 * s, 0), c + new Vector2(6 * s, 0), 0.9f * s, color);
            U.Line(c - new Vector2(0, 6 * s), c + new Vector2(0, 6 * s), 0.9f * s, color);
        }
    }

    static void Tag(Vector2 at, string text, float s)
    {
        float w = U.Measure(U.Medium, text) + 20 * s, h = 26 * s;
        float x = Math.Clamp(at.X - w / 2, 4, MathF.Max(4, _w - w - 4)), y = Math.Clamp(at.Y - h / 2, 4, MathF.Max(4, _h - h - 4));
        U.Rect(x, y + 1.5f * s, w, h, h / 2, new Vector4(0, 0, 0, 0.25f));
        U.Rect(x, y, w, h, h / 2, _theme.Accent);
        U.Text(U.Medium, text, x + 10 * s, y + (h - U.Medium.Height) / 2, AccentInk);
    }

    static void MeasureOverlay(float s)
    {
        Vector4 accent = _theme.Accent;
        Vector4 soft = new(accent.X, accent.Y, accent.Z, 0.75f);
        if (_hover != null && !IsSelected(_hover) && _drag == 0) DrawEntity(_hover, soft, s, 1.3f * s);
        if (_selA != null) DrawEntity(_selA, accent, s, 2.0f * s);
        if (_selB != null) DrawEntity(_selB, accent, s, 2.0f * s);

        if (_result != null && OnScreen(_result.P1, out Vector2 a) && OnScreen(_result.P2, out Vector2 b))
        {
            if (_result.HasSpan)
            {
                U.Line(a, b, 1.2f * s, accent);
                foreach (Vector2 p in (ReadOnlySpan<Vector2>)[a, b])
                    U.Rect(p.X - 4 * s, p.Y - 4 * s, 8 * s, 8 * s, 4 * s, accent);
            }
            Tag((a + b) / 2, _result.IsAngle ? Deg(_result.Value) : Len(_result.Value), s);
        }
        else if (_result == null && _selA != null && _selA.Kind != Kind.Point)
        {
            Vector3 at = _selA.Kind is Kind.Circle or Kind.Cylinder ? _selA.Center : _selA.Pick;
            if (OnScreen(at, out Vector2 p)) Tag(p - new Vector2(0, 24 * s), Single(_selA), s);
        }
    }

    static void MeasureCard(float s, float m)
    {
        string title, main, note;
        if (_result != null)
        {
            title = $"{_selA!.Name}  →  {_selB!.Name}";
            main = _result.IsAngle ? Deg(_result.Value) : Len(_result.Value);
            var parts = new List<string> { _result.Title };
            foreach (var (label, value, angle) in _result.Extra) parts.Add($"{label} {(angle ? Deg(value) : Bare(value))}");
            note = string.Join("   ·   ", parts);
        }
        else if ((_selA ?? _hover) is Entity e)
        {
            title = _selA != null ? e.Name + "  ·  click a second feature to measure between them" : e.Name;
            main = Single(e); note = SingleNote(e);
        }
        else
        {
            title = "Measure";
            main = _mesh!.TriAdj == null ? (_mesh.Analyzed ? "Not available for this model" : "Getting ready…") : "Click a face, an edge or a corner";
            note = "Holes and round edges give a diameter  ·  two features give the distance or angle between them";
        }

        Face big = _result != null || (_selA ?? _hover) != null ? U.Title : U.Medium;
        float pad = 20 * s;
        float w = MathF.Max(MathF.Max(U.Measure(U.Body, title), U.Measure(big, main)), U.Measure(U.Body, note)) + pad * 2;
        w = MathF.Min(w, _w - 2 * m);
        float h = pad * 1.5f + U.Body.Height * 2 + big.Height + 8 * s;
        float x = (_w - w) / 2, y = _h - m - h;
        if (x < _pillRight + 12 * s || x + w > _updLeft - 12 * s) y -= 44 * s;         // keep clear of the size read-out and the update indicator
        Card(x, y, w, h, s);
        float ty = y + pad * 0.75f;
        U.TextCentered(U.Body, Fit(U.Body, title, w - pad), x + w / 2, ty, _theme.Dim); ty += U.Body.Height + 3 * s;
        U.TextCentered(big, Fit(big, main, w - pad), x + w / 2, ty, _theme.Text); ty += big.Height + 3 * s;
        U.TextCentered(U.Body, Fit(U.Body, note, w - pad), x + w / 2, ty, _theme.Dim);
    }

    /// <summary>Size as it stands on the ground: the file's own axes, or the turned model after "Lay flat".</summary>
    static Vector3 ShownSize() => _customOrient ? _wMax - _wMin : _mesh!.Max - _mesh.Min;

    static void BoxDims(float s)
    {
        // Laid flat: the box around the model as it now stands (width, depth, height).
        // Otherwise: the box in the file's own axes.
        bool custom = _customOrient;
        Vector3 lo = custom ? _wMin : _mesh!.Min, hi = custom ? _wMax : _mesh!.Max;
        Matrix4x4 toClip = custom ? _vp : _mvp;
        Vector3 C(int i) => new((i & 1) == 0 ? lo.X : hi.X, (i & 2) == 0 ? lo.Y : hi.Y, (i & 4) == 0 ? lo.Z : hi.Z);
        Vector4 line = new(_theme.Dim.X, _theme.Dim.Y, _theme.Dim.Z, 0.55f);
        Span<Vector2> best = stackalloc Vector2[3];
        Span<float> score = [float.MinValue, float.MinValue, float.MinValue];
        Span<bool> have = [false, false, false];
        for (int axis = 0; axis < 3; axis++)
        {
            int bit = 1 << axis;
            for (int i = 0; i < 8; i++)
            {
                if ((i & bit) != 0) continue;
                if (!Inspect.Project(C(i), toClip, _w, _h, out Vector2 a) || !Inspect.Project(C(i | bit), toClip, _w, _h, out Vector2 b)) continue;
                U.Line(a, b, 0.7f * s, line);
                Vector2 mid = (a + b) / 2;
                float sc = mid.Y - MathF.Abs(a.Y - b.Y) * 2 - mid.X * 0.001f;      // prefer the lowest, flattest edge for each label
                if (axis == 2 && !custom && !_yUp || custom && axis == 2) sc = -mid.X;
                if (sc > score[axis]) { score[axis] = sc; best[axis] = mid; have[axis] = true; }
            }
        }
        Vector3 d = hi - lo;
        string names = custom ? "WDH" : "XYZ";
        for (int axis = 0; axis < 3; axis++)
            if (have[axis]) Tag(best[axis], $"{names[axis]}  {Len(axis == 0 ? d.X : axis == 1 ? d.Y : d.Z)}", s);
    }

    static void LayFlatCard(float s, float m)
    {
        string main = "Click the face that should sit on the ground", note = "Esc to cancel  ·  Z puts the model back afterwards";
        float pad = 20 * s, w = MathF.Min(MathF.Max(U.Measure(U.Medium, main), U.Measure(U.Body, note)) + pad * 2, _w - 2 * m);
        float h = pad * 1.5f + U.Medium.Height + U.Body.Height + 6 * s, x = (_w - w) / 2, y = _h - m - h;
        if (x < _pillRight + 12 * s || x + w > _updLeft - 12 * s) y -= 44 * s;
        Card(x, y, w, h, s);
        U.TextCentered(U.Medium, Fit(U.Medium, main, w - pad), x + w / 2, y + pad * 0.75f, _theme.Text);
        U.TextCentered(U.Body, Fit(U.Body, note, w - pad), x + w / 2, y + pad * 0.75f + U.Medium.Height + 4 * s, _theme.Dim);
    }
}
