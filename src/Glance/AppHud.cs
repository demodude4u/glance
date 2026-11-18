using System.Numerics;

namespace Glance;

internal static partial class App
{
    static string Num(float v)
    {
        float a = MathF.Abs(v);
        return a >= 1000 ? v.ToString("N0") : a >= 100 ? v.ToString("0.0") : a >= 10 ? v.ToString("0.00") : v.ToString("0.###");
    }

    static string Big(double v) => v >= 1e9 ? v.ToString("0.###e0") : v >= 100 ? v.ToString("N0") : v.ToString("0.##");

    static string Time(double ms) => ms < 1000 ? $"{Math.Max(1, Math.Round(ms)):0} ms" : $"{ms / 1000:0.0} s";

    static string Bytes(long b) => b >= 1 << 20 ? $"{b / 1048576.0:0.0} MB" : b >= 1024 ? $"{b / 1024.0:0} KB" : $"{b} bytes";

    static string Fit(Face f, string s, float max)
    {
        if (U.Measure(f, s) <= max) return s;
        while (s.Length > 1 && U.Measure(f, s + "…") > max) s = s[..^1];
        return s + "…";
    }

    static Vector4 Fade(Vector4 c, float a) => new(c.X, c.Y, c.Z, c.W * a);

    static void DrawHud()
    {
        float s = _scale, m = 16 * s;
        U.Begin(_w, _h);

        _buttons.Clear(); _slider = default; _sliderBox = default;
        _optBottom = m + 32 * s; _optRight = 0; _optLines = 0;
        SceneLook();
        UpdateLayout(s, m);
        if (_mesh != null)
        {
            if (FrameShowing) PictureFrameOverlay(s);
            if (_box) BoxDims(s);
            if (_measure) MeasureOverlay(s);
            StatusPill(s, m);
            Gizmo(s, m);
            Toolbar(s, m);
            PictureButton(s, m, _toolbarRight);
            OptionsRow(s, m);
            if (_section != 0) SectionSlider(s, m);
            if (_layFlat) { MeasureOverlay(s); LayFlatCard(s, m); }
            if (_measure) MeasureCard(s, m);
            HudExtras(s, m);                    // the compare card (AppCompare.cs)
            if (_info) InfoCard(s, m);
            if (_fixUnits) FixUnitsCard(s);
            if (_weightPick) WeightCard(s);
            if (_picture) PictureCard(s, m);
        }
        else if (_error != null) ErrorState(s);
        else if (!_loading) EmptyState(s);

        UpdateIndicator(s);
        if (_loading && Seconds(_loadStart) > 0.18) LoadingCard(s);
        if (_toast.Length > 0) ToastPill(s, m);
        if (_help) HelpCard(s);
        U.Flush();
    }

    static void StatusPill(float s, float m)
    {
        Mesh mesh = _mesh!;
        Vector3 d = ShownSize();
        var parts = new List<(string Text, Vector4 Color)>
        {
            (Len3(d.X, d.Y, d.Z), _theme.Text),
            ($"{mesh.TriCount:N0} triangles", _theme.Dim),
            (Time(_loadMs), _theme.Dim),
        };
        float pad = 14 * s, gap = 22 * s, h = 32 * s, avail = MathF.Min(_w - m - 96 * s, _updLeft - 12 * s) - m;
        float width;
        while (true)
        {
            width = pad * 2 + gap * (parts.Count - 1);
            foreach (var p in parts) width += U.Measure(U.Body, p.Text);
            if (width <= avail || parts.Count == 1) break;
            parts.RemoveAt(parts.Count - 1);
        }
        float x = m, y = _h - m - h;
        _pillRight = x + width;
        Pill(x, y, width, h, _pillHot);              // a click copies the size
        float tx = x + pad, ty = y + (h - U.Body.Height) / 2;
        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                U.Rect(tx + gap / 2 - 1.5f * s, y + h / 2 - 1.5f * s, 3 * s, 3 * s, 2 * s, Fade(_theme.Dim, 0.6f));
                tx += gap;
            }
            tx += U.Text(i == 0 ? U.Medium : U.Body, parts[i].Text, tx, ty, parts[i].Color);
        }
    }

    static void Gizmo(float s, float m)
    {
        Basis(_cur, out var dir, out var right, out var up);
        var c = new Vector2(_w - m - 34 * s, _h - m - 34 * s);
        float len = 25 * s;
        Vector3[] axes =
        [
            Vector3.TransformNormal(Vector3.UnitX, _orientNow), Vector3.TransformNormal(Vector3.UnitY, _orientNow),
            Vector3.TransformNormal(Vector3.UnitZ, _orientNow),
        ];
        Vector4[] colors = [new(0.90f, 0.33f, 0.31f, 1), new(0.36f, 0.72f, 0.36f, 1), new(0.27f, 0.52f, 0.95f, 1)];
        string[] names = ["X", "Y", "Z"];
        Span<int> order = [0, 1, 2];
        Span<float> depth = [Vector3.Dot(axes[0], dir), Vector3.Dot(axes[1], dir), Vector3.Dot(axes[2], dir)];
        for (int i = 0; i < 2; i++) for (int j = 0; j < 2 - i; j++)
            if (depth[order[j]] > depth[order[j + 1]]) (order[j], order[j + 1]) = (order[j + 1], order[j]);

        U.Rect(c.X - 3 * s, c.Y - 3 * s, 6 * s, 6 * s, 3 * s, Fade(_theme.Dim, 0.7f));
        foreach (int i in order)
        {
            var p = new Vector2(Vector3.Dot(axes[i], right), -Vector3.Dot(axes[i], up));
            float fade = 0.55f + 0.45f * Math.Clamp(depth[i] * 0.5f + 0.5f, 0, 1);
            Vector4 col = Fade(colors[i], fade);
            Vector2 tip = c + p * len;
            U.Line(c, tip, 1.1f * s, col);
            if (p.Length() > 0.25f)
            {
                Vector2 lp = c + p * (len + 9 * s);
                U.Text(U.Medium, names[i], lp.X - U.Measure(U.Medium, names[i]) / 2, lp.Y - U.Medium.Height / 2f, col);
            }
            else U.Rect(tip.X - 3 * s, tip.Y - 3 * s, 6 * s, 6 * s, 3 * s, col);
        }
    }

    static void Card(float x, float y, float w, float h, float s)
    {
        U.Rect(x - 1, y + 2 * s, w + 2, h + 2 * s, 12 * s, new Vector4(0, 0, 0, _theme.Dark ? 0.28f : 0.10f));
        U.Rect(x, y, w, h, 11 * s, _theme.Card);
        U.Rect(x, y, w, h, 11 * s, _theme.PanelBorder, 1);
    }

    static void EmptyState(float s)
    {
        float cx = _w / 2f, cy = _h / 2f - 34 * s - RecentLift(s);
        U.Rect(cx - 14 * s, cy - 18 * s, 28 * s, 3 * s, 1.5f * s, _theme.Accent);
        U.TextCentered(U.Title, "Drop a model to take a look", cx, cy, _theme.Text);
        U.TextCentered(U.Body, "STL  ·  OBJ  ·  3MF  ·  PLY  ·  glTF  ·  STEP  ·  IGES", cx, cy + U.Title.Height + 10 * s, _theme.Dim);
        U.TextCentered(U.Body, "or press O to browse", cx, cy + U.Title.Height + U.Body.Height + 16 * s, Fade(_theme.Dim, 0.75f));
        RecentList(s, cy + U.Title.Height + U.Body.Height * 2 + 46 * s);      // recently opened models (AppRecent.cs)
    }

    static void ErrorState(float s)
    {
        float cx = _w / 2f, cy = _h / 2f - 30 * s, max = _w - 80 * s;
        U.Rect(cx - 14 * s, cy - 18 * s, 28 * s, 3 * s, 1.5f * s, _theme.Accent);
        U.TextCentered(U.Medium, Fit(U.Medium, "Couldn't open " + Path.GetFileName(_path ?? "this file"), max), cx, cy, _theme.Text);
        string msg = _error!.ReplaceLineEndings(" ");
        U.TextCentered(U.Body, Fit(U.Body, msg, max), cx, cy + U.Medium.Height + 8 * s, _theme.Dim);
        U.TextCentered(U.Body, "Drop another file, or press O to browse", cx, cy + U.Medium.Height * 2 + 22 * s, Fade(_theme.Dim, 0.75f));
    }

    static void LoadingCard(float s)
    {
        float w = 320 * s, h = 92 * s, x = (_w - w) / 2, y = (_h - h) / 2, pad = 18 * s;
        Card(x, y, w, h, s);
        U.Text(U.Medium, Fit(U.Medium, _loadName, w - pad * 2), x + pad, y + pad - 2 * s, _theme.Text);
        string stage = _loadStage.Length > 0 ? _loadStage : "Opening";
        U.Text(U.Body, Fit(U.Body, stage + "…", w - pad * 2), x + pad, y + pad + U.Medium.Height + 2 * s, _theme.Dim);
        float bx = x + pad, bw = w - pad * 2, bh = 4 * s, by = y + h - pad - bh + 2 * s;
        U.Rect(bx, by, bw, bh, bh / 2, _theme.Panel);
        float t = (float)(Seconds(_loadStart) * 0.85 % 1.0);
        float e = t * t * (3 - 2 * t);
        float seg = bw * 0.32f, sx = bx - seg + (bw + seg) * e;
        float x0 = MathF.Max(sx, bx), x1 = MathF.Min(sx + seg, bx + bw);
        if (x1 - x0 > bh) U.Rect(x0, by, x1 - x0, bh, bh / 2, _theme.Accent);
    }

    static float _toastY;

    static void ToastPill(float s, float m)
    {
        double age = Seconds(_toastAt);
        float a = (float)Math.Clamp(Math.Min(age / 0.12, (_toastFor - age) / 0.30), 0, 1);
        string text = Fit(U.Body, _toast, _w - 2 * m - 32 * s);                        // never wider than the window
        float pad = 16 * s, h = 34 * s, w = U.Measure(U.Body, text) + pad * 2;
        // Below whatever is at the top: the toolbar, the mode's options and the section slider.
        float top = _mesh == null ? m : MathF.Max(_optBottom, _sliderBox.W > 0 ? _sliderBox.Y + _sliderBox.H : 0) + 14 * s;
        float x = (_w - w) / 2, y = top - (1 - a) * 6 * s;
        _toastY = y;
        U.Rect(x, y + 2 * s, w, h, h / 2, new Vector4(0, 0, 0, 0.18f * a));
        U.Rect(x, y, w, h, h / 2, Fade(_theme.Card, a));
        U.Rect(x, y, w, h, h / 2, Fade(_theme.PanelBorder, a), 1);
        U.Text(U.Body, text, x + pad, y + (h - U.Body.Height) / 2, Fade(_theme.Text, a));
    }

    static readonly (string Key, string What)[] HelpLeft =
    [
        ("Drag", "Orbit"), ("Right-drag", "Pan"), ("Scroll", "Zoom to the cursor"), ("Double-click", "Focus on a point"),
        ("F", "Fit to window"), ("1 – 6", "Front, back, left, right, top, bottom"), ("0", "Isometric view"),
        ("P", "Perspective / orthographic"), ("R", "Turntable"),
        ("M", "Measure: click faces, edges, corners"), ("L", "Lay flat: click the face to rest on"), ("X", "Section cut (Ctrl+scroll to move)"),
        ("D", "Compare with another file"), ("Space", "Comparing: flip between the two"),
    ];
    static readonly (string Key, string What)[] HelpRight =
    [
        ("V", "Shading, with options under the toolbar"), ("E", "Edges"), ("W", "Wireframe"), ("S", "Smooth / flat shading"), ("G", "Grid"), ("C", "Color"), ("T", "Light / dark"),
        ("B", "Bounding box"), ("U", "Show mm / inch / inch fractions"), ("Shift U", "STL / OBJ / PLY: file's own units"), ("Z", "Flip the up axis / undo lay flat"), ("I", "Details: weight, holes, stock size"), ("← →", "Previous / next file"), ("O", "Open a file"),
        ("Ctrl C", "Copy a picture"), ("Ctrl Shift C", "Copy the size or measurement"), ("Ctrl S", "Save a picture (PNG, Full HD, 4K…)"), ("Esc", "Close"),
    ];

    static void HelpCard(float s)
    {
        U.Rect(-2, -2, _w + 4, _h + 4, 0, _theme.Scrim);
        float row = 30 * s, pad = 26 * s, keyCol = 104 * s, colW = 350 * s;
        int rows = Math.Max(HelpLeft.Length, HelpRight.Length);
        float w = MathF.Min(pad * 2 + colW * 2, _w - 24 * s), h = pad * 2 + U.Title.Height + 14 * s + rows * row;
        bool single = w < pad * 2 + colW * 2 - 1;
        float x = (_w - w) / 2, y = MathF.Max((_h - h) / 2, 8 * s);
        Card(x, y, w, h, s);
        U.Text(U.Title, "Glance", x + pad, y + pad - 6 * s, _theme.Text);
        string version = "Version " + AppVersion.Full;
        U.Text(U.Body, version, x + w - pad - U.Measure(U.Body, version), y + pad - 6 * s + U.Title.Height - U.Body.Height - 5 * s, Fade(_theme.Dim, 0.85f));
        U.Rect(x + pad, y + pad + U.Title.Height - 2 * s, 26 * s, 3 * s, 1.5f * s, _theme.Accent);
        float top = y + pad + U.Title.Height + 14 * s;
        Column(HelpLeft, x + pad, top);
        if (!single) Column(HelpRight, x + pad + colW, top);

        void Column((string Key, string What)[] items, float cx, float cy)
        {
            foreach (var (key, what) in items)
            {
                float kw = U.Measure(U.Medium, key) + 16 * s, kh = 22 * s;
                float kx = cx + keyCol - 14 * s - kw;
                U.Rect(kx, cy + (row - kh) / 2, kw, kh, 6 * s, _theme.Panel);
                U.Rect(kx, cy + (row - kh) / 2, kw, kh, 6 * s, _theme.PanelBorder, 1);
                U.Text(U.Medium, key, kx + 8 * s, cy + (row - U.Medium.Height) / 2, _theme.Text);
                U.Text(U.Body, what, cx + keyCol, cy + (row - U.Body.Height) / 2, _theme.Dim);
                cy += row;
            }
        }
    }
}
