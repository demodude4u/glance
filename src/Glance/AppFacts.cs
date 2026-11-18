using System.Globalization;
using System.Numerics;
using static Glance.Win32;

namespace Glance;

/// <summary>
/// The Details card and what hangs off it: stock size, weight from a material, the hole list, and copying any of
/// the numbers as text. Nothing here runs until the card is opened or something is clicked.
/// </summary>
internal static unsafe partial class App
{
    // ───────────────────────── Facts worked out in the background ─────────────────────────

    // Kept with the mesh they describe and let go with it. While comparing there are two meshes, flipped between
    // at a keypress, and each keeps its own.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Mesh, PartFacts> _factsOf = new();
    static Mesh? _factsBusy;

    /// <summary>The facts for this mesh, or null while they are being worked out (the card is drawn again when they arrive).</summary>
    static PartFacts? FactsFor(Mesh m)
    {
        if (_factsOf.TryGetValue(m, out PartFacts? known)) return known;
        if (!m.Analyzed || _factsBusy != null) return null;
        _factsBusy = m;
        new Thread(() =>
        {
            PartFacts f = Facts.Compute(m);
            Post(() =>
            {
                _factsBusy = null;
                _factsOf.AddOrUpdate(m, f);
                Invalidate();
            });
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "facts" }.Start();
        return null;
    }

    static void DropFacts() => _hotFact = -1;

    /// <summary>True while the open card is still waiting for its numbers (the tests wait for them).</summary>
    static bool FactsWaiting => _info && _mesh != null && !_factsOf.TryGetValue(_mesh, out _);

    // ───────────────────────── Weight ─────────────────────────

    static readonly (string Key, string Name, double Density)[] WeightMaterials =
    [
        ("aluminum", "Aluminum", 2.70), ("steel", "Steel", 7.85), ("stainless", "Stainless steel", 8.00),
        ("brass", "Brass", 8.50), ("copper", "Copper", 8.96), ("titanium", "Titanium", 4.43),
        ("abs", "ABS", 1.04), ("pla", "PLA", 1.24), ("petg", "PETG", 1.27),
        ("nylon", "Nylon", 1.14), ("acetal", "Acetal", 1.41), ("polycarbonate", "Polycarbonate", 1.20),
    ];

    /// <summary>The chosen material's density in g/cm³, or 0 when none is chosen.</summary>
    static double Density
    {
        get
        {
            if (Settings.WeightMaterial == "custom") return Settings.Density > 0 ? Settings.Density : 0;
            foreach (var mat in WeightMaterials) if (mat.Key == Settings.WeightMaterial) return mat.Density;
            return 0;
        }
    }

    static string WeightMaterialName
    {
        get
        {
            if (Settings.WeightMaterial == "custom") return "Custom";
            foreach (var mat in WeightMaterials) if (mat.Key == Settings.WeightMaterial) return mat.Name;
            return "";
        }
    }

    static string DensityText(double gcm3) => _inch
        ? (gcm3 * 0.0361273).ToString("0.000#") + " lb/in³"
        : gcm3.ToString("0.00#") + " g/cm³";

    static double Grams(double mm3, double density) => mm3 / 1000.0 * density;

    static string WeightText(double grams)
    {
        if (_inch)
        {
            double lb = grams / 453.59237;
            return lb < 0.1 ? (lb * 16).ToString("0.000") + " oz" : lb < 1000 ? lb.ToString("#,0.000") + " lb" : lb.ToString("#,0") + " lb";
        }
        return grams < 1 ? grams.ToString("0.000") + " g" : grams < 100 ? grams.ToString("0.00") + " g" : grams < 1000 ? grams.ToString("0.0") + " g"
            : grams < 1e6 ? (grams / 1000).ToString("#,0.000") + " kg" : (grams / 1000).ToString("#,0") + " kg";
    }

    // ───────────────────────── Copying numbers ─────────────────────────

    static string _copied = "";

    /// <summary>Puts text on the clipboard and says so. Under test the clipboard is left alone: it belongs to the person at the PC.</summary>
    static void CopyText(string text, string? said = null)
    {
        _copied = text;
        if (Environment.GetEnvironmentVariable("GLANCE_TEST_DIR") == null)
        {
            nint mem = GlobalAlloc(0x0002, (nuint)((text.Length + 1) * 2));
            if (mem == 0) return;
            char* dst = (char*)GlobalLock(mem);
            text.AsSpan().CopyTo(new Span<char>(dst, text.Length));
            dst[text.Length] = '\0';
            GlobalUnlock(mem);
            if (OpenClipboard(_hwnd) == 0) return;
            EmptyClipboard(); SetClipboardData(13, mem); CloseClipboard();      // 13 = Unicode text
        }
        Toast("Copied  ·  " + (said ?? text), 1.8f);
    }

    static string SizeText() { Vector3 d = ShownSize(); return Len3(d.X, d.Y, d.Z); }

    /// <summary>
    /// The number on the measurement card, or null when nothing has been clicked yet. Worked out from what is
    /// selected now, not from what was last drawn, so a click that follows straight after another is not fooled.
    /// A feature that is only pointed at does not count: a click there is a click on the model.
    /// </summary>
    static string? MeasureText() =>
        _result != null ? (_result.IsAngle ? Deg(_result.Value) : Len(_result.Value)) : _selA != null ? Single(_selA) : null;

    /// <summary>Ctrl+Shift+C: the measurement on screen, or failing that the part's size.</summary>
    static void CopyNumbers()
    {
        if (_mesh == null) return;
        if (_measure && MeasureText() is string measured) CopyText(measured);
        else CopyText(SizeText());
    }

    // ───────────────────────── The Details card ─────────────────────────

    const int RowPlain = 0, RowMaterial = 1, RowStock = 2, RowHoles = 3, RowNote = 5;

    struct FactRow
    {
        public string Label, Value, Copy;
        public int Kind, Index;
        public bool Section;            // a thin rule above this row
    }

    static readonly List<FactRow> _factRows = new();
    static readonly List<(float X, float Y, float W, float H)> _factRects = new();      // one per row; the title is the last
    static (float X, float Y, float W, float H) _factCard;
    static int _hotFact = -1;           // the row under the cursor, or _factRows.Count for the title
    static bool _pillHot;
    static (float X, float Y, float W, float H) _measureRect;

    static string HoleText(HoleGroup g) => $"{g.Holes.Count} ×  Ø {Len(g.Diameter)}";

    static void BuildFactRows(Mesh mesh, PartFacts? facts)
    {
        _factRows.Clear();
        Vector3 d = ShownSize();
        bool turned = _customOrient, yUp = !turned && _yUp;
        void Add(string label, string value, int kind = RowPlain, int index = 0, string? copy = null, bool section = false) =>
            _factRows.Add(new FactRow { Label = label, Value = value, Copy = copy ?? value, Kind = kind, Index = index, Section = section });

        Add("Format", mesh.Format);
        Add("File size", Bytes(mesh.FileSize));
        Add("File units", Loaders.UnitLabel(mesh.FileUnit) + (!mesh.UnitChoice || mesh.UnitSource == 0 ? "" : mesh.UnitSource == 1 ? "  ·  assumed" : "  ·  your choice"));
        Add("Triangles", mesh.TriCount.ToString("N0"));
        // A Y-up file (OBJ, glTF) stands on its Y: that is its height, and Z runs front to back.
        Add(turned ? "Width" : "Width  X", Len(d.X));
        Add(turned ? "Depth" : yUp ? "Height  Y" : "Depth  Y", Len(d.Y));
        Add(turned ? "Height" : yUp ? "Depth  Z" : "Height  Z", Len(d.Z));
        Add("Stock size", facts == null ? "…" : Len3(facts.Stock.X, facts.Stock.Y, facts.Stock.Z), RowStock);
        Add("Surface area", mesh.Analyzed ? AreaText(mesh.Area) : "…");
        Add("Volume", !mesh.Analyzed ? "…" : mesh.Closed ? VolumeText(mesh.Volume) : "–");
        double density = Density;
        Add("Material", density > 0 ? $"{WeightMaterialName}  ·  {DensityText(density)}" : "Choose…", RowMaterial);
        if (density > 0) Add("Weight", !mesh.Analyzed ? "…" : mesh.Closed ? WeightText(Grams(mesh.Volume, density)) : "–");
        Add("Watertight", !mesh.Analyzed ? "…" : mesh.Closed ? "Yes" : mesh.OpenEdges > 0 ? $"No  ·  {mesh.OpenEdges:N0} open edges" : "No");
        Add("Opened in", Time(_loadMs) + (mesh.FromCache ? "  ·  cached" : ""));

        string all = "";
        if (facts != null)
            foreach (HoleGroup g in facts.Holes)
                all += (all.Length > 0 ? "\n" : "") + HoleText(g) + (double.IsNaN(g.Depth) ? "" : $", {Len(g.Depth)} deep");
        Add("Holes", facts == null ? "…" : !facts.HolesKnown ? "–" : facts.HoleCount == 0 ? "None" : facts.HoleCount.ToString("N0"),
            copy: all.Length > 0 ? all : null, section: true);
        if (facts != null)
            for (int i = 0; i < facts.Holes.Count; i++)
            {
                HoleGroup g = facts.Holes[i];
                Add(HoleText(g), double.IsNaN(g.Depth) ? "" : $"{Len(g.Depth)} deep", RowHoles, i,
                    HoleText(g) + (double.IsNaN(g.Depth) ? "" : $", {Len(g.Depth)} deep"));
            }
    }

    /// <summary>Everything on the card as text, one "label, tab, value" line each: pastes into an email or a spreadsheet.</summary>
    static string AllFactsText()
    {
        var sb = new System.Text.StringBuilder(Path.GetFileName(_path ?? ""));
        foreach (FactRow r in _factRows)
        {
            if (r.Kind == RowNote || (r.Kind == RowMaterial && Density <= 0)) continue;
            sb.Append('\n').Append(r.Label);
            if (r.Value.Length > 0) sb.Append('\t').Append(r.Value);
        }
        return sb.ToString();
    }

    static string _factsAll = "";       // the whole card as text, taken before a long hole list is cut to fit

    static void InfoCard(float s, float m)
    {
        Mesh mesh = _mesh!;
        PartFacts? facts = FactsFor(mesh);
        BuildFactRows(mesh, facts);
        _factsAll = AllFactsText();

        float pad = 16 * s, row = 23 * s, w = MathF.Min(304 * s, _w - 2 * m), head = 30 * s, rule = 9 * s;
        float x = m, y = _optBottom + 14 * s;                                           // under the toolbar and the mode's options
        if (_sliderBox.W > 0 && _sliderBox.X < x + w + 12 * s) y = MathF.Max(y, _sliderBox.Y + _sliderBox.H + 10 * s);

        // The card stops above the size read-out. If the hole list does not fit, its tail becomes one line saying so.
        float room = _h - m - 32 * s - 10 * s - y - pad * 2 - head - row;               // the last row is the hint
        int holeRows = 0, firstHole = -1;
        for (int i = 0; i < _factRows.Count; i++) if (_factRows[i].Kind == RowHoles) { holeRows++; if (firstHole < 0) firstHole = i; }
        float fixedH = (_factRows.Count - holeRows) * row + rule;
        int show = Math.Clamp((int)((room - fixedH) / row), 0, Math.Min(holeRows, 10));
        if (show < holeRows)
        {
            show = Math.Max(0, show - 1);
            int more = holeRows - show;
            _factRows.RemoveRange(firstHole + show, more);
            _factRows.Add(new FactRow { Label = more == 1 ? "and 1 more size" : $"and {more} more sizes", Value = "", Copy = "", Kind = RowNote });
        }

        float h = pad * 2 + head + _factRows.Count * row + rule + row - 4 * s;
        _factCard = (x, y, w, h);
        HoverMarks(s, facts);
        Card(x, y, w, h, s);

        int titleRow = _factRows.Count;
        bool live = FactsLive;
        if (live && _hotFact == titleRow) U.Rect(x + 6 * s, y + 8 * s, w - 12 * s, head - 2 * s, 6 * s, _theme.Panel);
        U.Text(U.Medium, Fit(U.Medium, Path.GetFileName(_path ?? ""), w - pad * 2), x + pad, y + pad, _theme.Text);
        U.Rect(x + pad, y + pad + head - 8 * s, 22 * s, 2 * s, s, _theme.Accent);

        _factRects.Clear();
        float ty = y + pad + head;
        for (int i = 0; i < _factRows.Count; i++)
        {
            FactRow r = _factRows[i];
            if (r.Section)
            {
                U.Rect(x + pad, ty + 2 * s, w - pad * 2, 1, 0, _theme.PanelBorder);
                ty += rule;
            }
            _factRects.Add((x + 6 * s, ty - 4 * s, w - 12 * s, row));
            bool hot = live && _hotFact == i && r.Kind != RowNote && (r.Copy.Length > 0 || r.Kind == RowMaterial);
            if (hot) U.Rect(x + 6 * s, ty - 4 * s, w - 12 * s, row, 6 * s, _theme.Panel);
            bool hole = r.Kind == RowHoles;
            U.Text(U.Body, r.Label, x + pad + (hole ? 10 * s : 0), ty, hole ? _theme.Text : _theme.Dim);
            float lw = U.Measure(U.Body, r.Label) + (hole ? 10 * s : 0);
            string v = Fit(U.Body, r.Value, w - pad * 2 - lw - 10 * s);
            Vector4 vc = hole ? _theme.Dim : r.Kind == RowMaterial && Density <= 0 ? _theme.Accent : _theme.Text;
            U.Text(U.Body, v, x + w - pad - U.Measure(U.Body, v), ty, vc);
            ty += row;
        }
        _factRects.Add((x + 6 * s, y + 8 * s, w - 12 * s, head - 2 * s));               // the title, last
        U.Text(U.Body, "Click a value to copy it", x + pad, ty + 2 * s, Fade(_theme.Dim, 0.7f));
    }

    /// <summary>The card answers the mouse only when nothing modal is in front of it.</summary>
    static bool FactsLive => _info && _mesh != null && _drag == 0 && !_picture && !_fixUnits && !_weightPick && !_help;

    static bool In((float X, float Y, float W, float H) r, int mx, int my) => r.W > 0 && mx >= r.X && mx <= r.X + r.W && my >= r.Y && my <= r.Y + r.H;

    static (float X, float Y, float W, float H) PillRect => (16 * _scale, _h - 16 * _scale - 32 * _scale, _pillRight - 16 * _scale, 32 * _scale);

    /// <summary>Mouse moved: true when what is under it changed, so the window is drawn again.</summary>
    static bool FactsHover(int mx, int my)
    {
        if (_weightPick) return true;
        int hot = -1;
        if (FactsLive && In(_factCard, mx, my))
            for (int i = 0; i < _factRects.Count; i++) if (In(_factRects[i], mx, my)) { hot = i; break; }
        bool pill = _mesh != null && _drag == 0 && !_picture && !_fixUnits && !_help && In(PillRect, mx, my);
        if (hot == _hotFact && pill == _pillHot) return false;
        _hotFact = hot; _pillHot = pill;
        return true;
    }

    /// <summary>A click on the card, the size read-out or the measurement: copies what was clicked.</summary>
    static bool FactsClick(int mx, int my)
    {
        if (_mesh == null) return false;
        if (_info && In(_factCard, mx, my))
        {
            for (int i = 0; i < _factRects.Count; i++)
            {
                if (!In(_factRects[i], mx, my)) continue;
                if (i == _factRows.Count) { CopyText(_factsAll, "all the details"); break; }
                FactRow r = _factRows[i];
                if (r.Kind == RowMaterial) OpenWeightPick();
                else if (r.Kind != RowNote && r.Copy.Length > 0 && r.Copy != "…" && r.Copy != "–")
                    CopyText(r.Copy, r.Copy.Contains('\n') ? "the hole list" : null);
                break;
            }
            Invalidate();
            return true;                    // the card is not something to orbit the model through
        }
        if (In(PillRect, mx, my)) { CopyText(SizeText()); return true; }
        if (_measure && In(_measureRect, mx, my) && MeasureText() is string measured) { CopyText(measured); return true; }
        return false;
    }

    /// <summary>What the row under the cursor is about, shown on the model: the holes of that size, or the stock box.</summary>
    static void HoverMarks(float s, PartFacts? facts)
    {
        if (facts == null || !FactsLive || _hotFact < 0 || _hotFact >= _factRows.Count) return;
        FactRow r = _factRows[_hotFact];
        Vector4 accent = _theme.Accent;
        if (r.Kind == RowStock && facts.StockBox.Length == 8)
        {
            for (int axis = 0; axis < 3; axis++)
                for (int i = 0; i < 8; i++)
                {
                    if ((i & (1 << axis)) != 0) continue;
                    if (OnScreen(facts.StockBox[i], out Vector2 a) && OnScreen(facts.StockBox[i | (1 << axis)], out Vector2 b)) U.Line(a, b, 1.0f * s, accent);
                }
        }
        else if (r.Kind == RowHoles && r.Index < facts.Holes.Count)
        {
            int drawn = 0;
            foreach (var hole in facts.Holes[r.Index].Holes)
            {
                if (++drawn > 80) break;
                Vector3 u = Vector3.Normalize(Vector3.Cross(hole.Axis, MathF.Abs(hole.Axis.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY));
                Vector3 v = Vector3.Cross(hole.Axis, u);
                foreach (float end in (ReadOnlySpan<float>)[-0.5f, 0.5f])
                {
                    Vector3 c = hole.Center + hole.Axis * (hole.Depth * end);
                    Vector2 prev = default; bool have = false;
                    for (int k = 0; k <= 40; k++)
                    {
                        float a = k * MathF.Tau / 40;
                        if (!OnScreen(c + (u * MathF.Cos(a) + v * MathF.Sin(a)) * hole.Radius, out Vector2 p)) { have = false; continue; }
                        if (have) U.Line(prev, p, 1.6f * s, accent);
                        prev = p; have = true;
                    }
                }
                if (OnScreen(hole.Center, out Vector2 mid)) U.Rect(mid.X - 3 * s, mid.Y - 3 * s, 6 * s, 6 * s, 3 * s, accent);
            }
        }
    }

    // ───────────────────────── Choosing the material ─────────────────────────

    static bool _weightPick;
    static string _densityText = "";
    static readonly List<(float X, float Y, float W, float H, string Key)> _weightRows = new();

    static void OpenWeightPick()
    {
        _weightPick = true;
        _densityText = Settings.Density > 0 ? Settings.Density.ToString("0.###", CultureInfo.InvariantCulture) : "";
    }

    static bool TypedDensity(out double d) =>
        double.TryParse(_densityText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out d) && d > 0 && d < 100;

    static void SetWeightMaterial(string key)
    {
        if (key == "custom")
        {
            if (!TypedDensity(out double d)) { Toast("Type a density first, for example 2.7"); return; }
            Settings.Density = d;
        }
        Settings.WeightMaterial = key;
        _weightPick = false;
        if (_mesh is { Analyzed: true, Closed: true } mesh && Density > 0)
            Toast($"{WeightMaterialName}  ·  {WeightText(Grams(mesh.Volume, Density))}", 2.2f);
        else if (key == "") Toast("No material");
    }

    /// <summary>Keys while the chooser is open: digits and a point type a density, Enter uses it.</summary>
    static bool WeightKey(int vk)
    {
        if (vk >= '0' && vk <= '9') { if (_densityText.Length < 7) _densityText += (char)vk; return true; }
        if (vk is 0xBE or 0x6E or 0xBC) { if (!_densityText.Contains('.') && _densityText.Length < 6) _densityText += _densityText.Length == 0 ? "0." : "."; return true; }
        if (vk == 0x08) { if (_densityText.Length > 0) _densityText = _densityText[..^1]; return true; }
        if (vk == 0x0D) { SetWeightMaterial("custom"); return true; }
        return false;
    }

    static bool WeightClick(int mx, int my)
    {
        foreach (var r in _weightRows)
            if (mx >= r.X && mx <= r.X + r.W && my >= r.Y && my <= r.Y + r.H) { SetWeightMaterial(r.Key); Invalidate(); return true; }
        _weightPick = false; Invalidate();
        return true;
    }

    static void WeightCard(float s)
    {
        Mesh mesh = _mesh!;
        bool known = mesh.Analyzed && mesh.Closed;
        string title = "What is it made of?";
        string line = !mesh.Analyzed ? "Working out the volume…"
            : known ? $"The part's volume is {VolumeText(mesh.Volume)}. Pick a material to see what it weighs."
            : "This model isn't watertight, so its volume and weight can't be worked out.";
        string foot = "Type a density for a material that isn't listed  ·  Esc to cancel";
        string Weigh(double density) => known ? WeightText(Grams(mesh.Volume, density)) : "–";

        float pad = 22 * s, rowH = 34 * s, rowGap = 6 * s, colGap = 10 * s, inPad = 12 * s;
        int half = (WeightMaterials.Length + 1) / 2;
        float w = MathF.Min(MathF.Max(560 * s, U.Measure(U.Body, line) + pad * 2), _w - 24 * s);
        float cw = (w - pad * 2 - colGap) / 2;
        float h = pad + U.Medium.Height + 8 * s + U.Body.Height + 16 * s + (half + 1) * (rowH + rowGap) + 8 * s + U.Body.Height + pad * 0.8f;
        float x = (_w - w) / 2, y = MathF.Max(12 * s, (_h - h) / 2);

        U.Rect(0, 0, _w, _h, 0, _theme.Scrim);
        Card(x, y, w, h, s);
        float ty = y + pad;
        U.Text(U.Medium, title, x + pad, ty, _theme.Text); ty += U.Medium.Height + 8 * s;
        U.Text(U.Body, Fit(U.Body, line, w - pad * 2), x + pad, ty, _theme.Dim); ty += U.Body.Height + 16 * s;

        _weightRows.Clear();
        void Cell(float cx, float cy, float cwid, string key, string name, string note, string value, bool current)
        {
            bool hot = _mouseX >= cx && _mouseX <= cx + cwid && _mouseY >= cy && _mouseY <= cy + rowH;
            if (current) U.Rect(cx, cy, cwid, rowH, 9 * s, new Vector4(_theme.Accent.X, _theme.Accent.Y, _theme.Accent.Z, _theme.Dark ? 0.22f : 0.10f));
            else U.Rect(cx, cy, cwid, rowH, 9 * s, hot ? new Vector4(_theme.Panel.X, _theme.Panel.Y, _theme.Panel.Z, _theme.Panel.W * 2.2f) : _theme.Panel);
            U.Rect(cx, cy, cwid, rowH, 9 * s, current ? _theme.Accent : _theme.PanelBorder, current ? 1.5f * s : 1);
            float vw = U.Measure(U.Body, value);
            float tx = cx + inPad;
            tx += U.Text(U.Medium, Fit(U.Medium, name, cwid - inPad * 2 - vw - 8 * s), tx, cy + (rowH - U.Medium.Height) / 2, _theme.Text);
            if (note.Length > 0 && tx + 8 * s + U.Measure(U.Body, note) < cx + cwid - inPad - vw - 8 * s)
                U.Text(U.Body, note, tx + 8 * s, cy + (rowH - U.Body.Height) / 2, Fade(_theme.Dim, 0.85f));
            U.Text(U.Body, value, cx + cwid - inPad - vw, cy + (rowH - U.Body.Height) / 2, current ? _theme.Text : _theme.Dim);
            _weightRows.Add((cx, cy, cwid, rowH, key));
        }

        for (int i = 0; i < WeightMaterials.Length; i++)
        {
            var mat = WeightMaterials[i];
            float cx = x + pad + (i < half ? 0 : cw + colGap), cy = ty + (i < half ? i : i - half) * (rowH + rowGap);
            Cell(cx, cy, cw, mat.Key, mat.Name, mat.Density.ToString("0.00", CultureInfo.InvariantCulture), Weigh(mat.Density), Settings.WeightMaterial == mat.Key);
        }
        ty += half * (rowH + rowGap);

        // Custom density on the left, "no material" on the right.
        bool custom = Settings.WeightMaterial == "custom";
        bool typed = TypedDensity(out double td);
        Cell(x + pad, ty, cw, "custom", "Custom", "", typed ? Weigh(td) : "", custom);
        float fx = x + pad + inPad + U.Measure(U.Medium, "Custom") + 12 * s, fw = 74 * s, fh = 22 * s, fy = ty + (rowH - fh) / 2;
        U.Rect(fx, fy, fw, fh, 5 * s, _theme.Card);
        U.Rect(fx, fy, fw, fh, 5 * s, _theme.PanelBorder, 1);
        float tw = U.Text(U.Body, _densityText, fx + 7 * s, fy + (fh - U.Body.Height) / 2, _theme.Text);
        U.Rect(fx + 8 * s + tw, fy + 4 * s, MathF.Max(1, s), fh - 8 * s, 0, _theme.Accent);                                 // the caret
        U.Text(U.Body, "g/cm³", fx + fw + 7 * s, ty + (rowH - U.Body.Height) / 2, Fade(_theme.Dim, 0.85f));
        Cell(x + pad + cw + colGap, ty, cw, "", "No material", "", "", Settings.WeightMaterial == "");
        ty += rowH + rowGap + 8 * s;
        U.Text(U.Body, Fit(U.Body, foot, w - pad * 2), x + pad, ty, Fade(_theme.Dim, 0.8f));
    }

    // ───────────────────────── For the tests ─────────────────────────

    static IEnumerable<string> FactsState()
    {
        Mesh? m = _mesh;
        PartFacts? f = m != null && _factsOf.TryGetValue(m, out PartFacts? known) ? known : null;
        string inv(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);
        yield return $"copied={_copied.Replace('\n', '|').Replace('\t', '~')}";
        yield return $"reloads={_reloads}";
        yield return $"stock={(f != null ? $"{inv(f.Stock.X)},{inv(f.Stock.Y)},{inv(f.Stock.Z)}" : "")}";
        yield return $"stockTurned={f?.StockTurned}";
        yield return $"holeCount={(f != null ? f.HoleCount : -1)}";
        yield return $"holes={(f != null ? string.Join(";", f.Holes.Select(g => $"{g.Holes.Count}x{inv(g.Diameter)}:{(double.IsNaN(g.Depth) ? "" : inv(g.Depth))}")) : "")}";
        yield return $"volume={(m != null ? inv(m.Volume) : "")}";
        yield return $"weightMaterial={Settings.WeightMaterial}";
        yield return $"weightG={(m is { Analyzed: true, Closed: true } && Density > 0 ? inv(Grams(m.Volume, Density)) : "")}";
        yield return $"weightPick={_weightPick}";
        yield return $"densityText={_densityText}";
        yield return $"weightRows={string.Join(";", _weightRows.Select(r => $"{(r.Key.Length > 0 ? r.Key : "none")}:{r.X + r.W - 20 * _scale:0},{r.Y + r.H / 2:0}"))}";
        yield return $"infoCard={_factCard.X:0},{_factCard.Y:0},{_factCard.W:0},{_factCard.H:0}";
        yield return $"factRows={string.Join(";", _factRows.Take(_factRects.Count).Select((r, i) => $"{r.Label.Replace(' ', '_')}:{_factRects[i].X + _factRects[i].W / 2:0},{_factRects[i].Y + _factRects[i].H / 2:0}"))}";
        yield return $"factValues={string.Join(";", _factRows.Select(r => $"{r.Label.Replace(' ', '_')}:{r.Value}"))}";
        yield return $"factTitle={(_factRects.Count > _factRows.Count ? $"{_factRects[^1].X + _factRects[^1].W / 2:0},{_factRects[^1].Y + _factRects[^1].H / 2:0}" : "")}";
        yield return $"hotFact={_hotFact}";
        yield return $"pill={PillRect.X + PillRect.W / 2:0},{PillRect.Y + PillRect.H / 2:0}";
        yield return $"measureCard={_measureRect.X + _measureRect.W / 2:0},{_measureRect.Y + _measureRect.H / 2:0}";
    }
}
