using System.Numerics;

namespace Glance;

/// <summary>
/// Options that belong to one shading mode. While that mode is on they show as a second row of smaller buttons
/// under the toolbar; each button steps through its choices and shows the current one, the way the Shading
/// button does. Choices are remembered per mode in settings.ini. Each mode's options live in a class of their
/// own that is not touched until that mode is on, so the modes that are not in use cost nothing.
/// </summary>
internal static unsafe partial class App
{
    sealed class ModeOption
    {
        public readonly string Label;
        readonly string? _key;
        readonly string[]? _values;
        readonly int _fallback;
        readonly Func<int>? _auto;
        readonly Func<string>? _name;
        readonly Action? _next;

        /// <summary>A choice of its own, kept in settings.ini under <paramref name="key"/> ("studio.light").</summary>
        /// <param name="auto">What to use until a choice is made, when that should follow the theme or the file.</param>
        public ModeOption(string key, string label, string[] values, int fallback = 0, Func<int>? auto = null)
        {
            _key = key; Label = label; _values = values; _fallback = fallback; _auto = auto;
        }

        /// <summary>A button for something that already has a key and a setting (Color is C, Edges is E).</summary>
        public ModeOption(string label, Func<string> name, Action next)
        {
            Label = label; _name = name; _next = next;
        }

        public int Value
        {
            get
            {
                if (_key == null) return 0;
                int v = Settings.Option(_key, -1);
                return v >= 0 && v < _values!.Length ? v : _auto != null ? _auto() : _fallback;
            }
        }

        public string Name => _name != null ? _name() : _values![Value];

        public void Next()
        {
            if (_next != null) _next();
            else Settings.SetOption(_key!, (Value + 1) % _values!.Length);
        }
    }

    static readonly string[] OffOn = ["Off", "On"], Backdrops = ["White", "Grey", "Dark"];

    /// Shaded and Smooth: the two things their own keys already do.
    static class SolidLook
    {
        public static readonly ModeOption[] All =
        [
            new("Color", () => ColorName, StepColor),
            new("Edges", () => OffOn[_edges ? 1 : 0], ToggleEdges),
        ];
    }

    /// <summary>E, and the Edges buttons. X-ray remembers its own choice; elsewhere edges are what tell Shaded from Smooth.</summary>
    static void ToggleEdges()
    {
        _edges = !_edges;
        if (_style == 2) Settings.SetOption("xray.edges", _edges ? 1 : 0);
    }

    static string ColorName => _fileColors && R.HasColors ? "Original" : Theme.Materials[MaterialIndex].Name;

    /// <summary>C, and the Color button: the next material, with the file's own colours as one more stop when it has them.</summary>
    static void StepColor()
    {
        if (R.HasColors && _fileColors) { _fileColors = false; Settings.Material = 0; }
        else if (R.HasColors && MaterialIndex == Theme.Materials.Length - 1) _fileColors = true;
        else Settings.Material = (MaterialIndex + 1) % Theme.Materials.Length;
    }

    /// Studio: where the light comes from, what is behind the part, what the part is made of, and its shadow on the ground.
    static class StudioLook
    {
        public static readonly ModeOption Light = new("studio.light", "Light", ["Soft", "Left", "Right", "Top"]);
        public static readonly ModeOption Backdrop = new("studio.backdrop", "Backdrop", Backdrops, auto: () => _theme.Dark ? 2 : 0);
        public static readonly ModeOption Material = new("studio.material", "Material", ["Clay", "White", "Charcoal", "Part color"], auto: () => _fileColors && R.HasColors ? 3 : 0);
        public static readonly ModeOption Shadow = new("studio.shadow", "Shadow", OffOn, 1);
        public static readonly ModeOption[] All = [Light, Backdrop, Material, Shadow];

        /// <summary>Replaces the working colour with the studio material. "Part color" keeps the file's colours, or the colour chosen with C.</summary>
        public static void Apply(ref Vector3 color, ref bool fileColors)
        {
            switch (Material.Value)
            {
                case 0: color = new Vector3(0.87f, 0.86f, 0.84f); fileColors = false; break;
                case 1: color = new Vector3(0.97f, 0.97f, 0.96f); fileColors = false; break;
                case 2: color = new Vector3(0.23f, 0.23f, 0.24f); fileColors = false; break;
            }
        }
    }

    /// Metal: which metal, how finely it is finished, what is behind it, and its shadow on the ground.
    static class MetalLook
    {
        public static readonly ModeOption Finish = new("metal.finish", "Finish", ["Aluminum", "Steel", "Brass", "Copper", "Black"]);
        public static readonly ModeOption Surface = new("metal.surface", "Surface", ["Polished", "Satin", "Matte"], 1);
        public static readonly ModeOption Backdrop = new("metal.backdrop", "Backdrop", Backdrops, auto: () => _theme.Dark ? 2 : 0);
        public static readonly ModeOption Shadow = new("metal.shadow", "Shadow", OffOn, 1);
        public static readonly ModeOption[] All = [Finish, Surface, Backdrop, Shadow];

        /// <summary>How much of each colour the metal reflects when looked at straight on.</summary>
        public static Vector3 Color => Finish.Value switch
        {
            1 => new Vector3(0.56f, 0.58f, 0.61f),
            2 => new Vector3(0.90f, 0.74f, 0.40f),
            3 => new Vector3(0.88f, 0.50f, 0.34f),
            4 => new Vector3(0.11f, 0.11f, 0.12f),
            _ => new Vector3(0.80f, 0.82f, 0.85f),
        };
    }

    /// Lines: a drawing. Whether edges behind the part show faintly, how heavy the ink is, and the paper.
    static class LinesLook
    {
        public static readonly ModeOption Hidden = new("lines.hidden", "Hidden lines", ["Off", "Faint"]);
        public static readonly ModeOption Weight = new("lines.weight", "Weight", ["Fine", "Bold"]);
        public static readonly ModeOption Paper = new("lines.paper", "Paper", ["White", "Blueprint", "Dark"], auto: () => _theme.Dark ? 2 : 0);
        public static readonly ModeOption[] All = [Hidden, Weight, Paper];

        // White and Dark paper are the light and dark themes' own backgrounds, so the paper that suits the theme
        // looks exactly as Lines always has.
        static readonly Theme OnLight = Theme.Make(false), OnDark = Theme.Make(true);

        public static bool DarkPaper => Paper.Value != 0;
        /// <summary>The grid and ground shadow that read on this paper.</summary>
        public static Theme Ground => DarkPaper ? OnDark : OnLight;
        public static (Vector3 Center, Vector3 Edge) Sheet => Paper.Value switch
        {
            1 => (new Vector3(0.10f, 0.29f, 0.56f), new Vector3(0.05f, 0.17f, 0.39f)),
            2 => (OnDark.BgCenter, OnDark.BgEdge),
            _ => (OnLight.BgCenter, OnLight.BgEdge),
        };
        /// <summary>The part's own fill: paper a shade off the sheet, so only the ink reads.</summary>
        public static Vector3 Fill => Paper.Value switch
        {
            1 => new Vector3(0.09f, 0.26f, 0.52f),
            2 => new Vector3(0.16f, 0.17f, 0.19f),
            _ => new Vector3(0.97f, 0.97f, 0.97f),
        };
        public static Vector4 Ink => DarkPaper ? new Vector4(1, 1, 1, Paper.Value == 1 ? 0.88f : 0.80f) : new Vector4(0.05f, 0.06f, 0.08f, 0.90f);
    }

    /// X-ray: how solid the glass is, and whether edges are drawn on it.
    static class XrayLook
    {
        public static readonly ModeOption Opacity = new("xray.opacity", "Opacity", ["Light", "Medium", "Heavy"], 1);
        public static readonly ModeOption[] All = [Opacity, new("Edges", () => OffOn[_edges ? 1 : 0], ToggleEdges)];

        /// <summary>How opaque the glass is face-on, and how much more towards its silhouette.</summary>
        public static Vector2 Glass => Opacity.Value switch
        {
            0 => new Vector2(0.04f, 0.34f),
            2 => new Vector2(0.24f, 0.62f),
            _ => new Vector2(0.10f, 0.50f),
        };
    }

    /// Wire: every triangle on the solid, or only the part's real edges with nothing filled in.
    static class WireLook
    {
        public static readonly ModeOption Lines = new("wire.lines", "Lines", ["All triangles", "Feature edges only"]);
        public static readonly ModeOption[] All = [Lines];

        /// <summary>Said when feature edges are asked for on a model that has none (a smooth, organic shape): it keeps its triangles.</summary>
        public static string? Remark => Lines.Value == 1 && !R.HasEdges ? "no sharp edges on this model, so every triangle is drawn" : null;
        public static string Note => Lines.Value == 0 ? "every triangle" : Remark ?? "the part's edges only, nothing filled in";
    }

    /// Overhangs: how far past vertical a face may lean before it is marked.
    static class OverhangLook
    {
        public static readonly ModeOption Angle = new("overhangs.angle", "Angle", ["30°", "45°", "60°"], 1);
        public static readonly ModeOption[] All = [Angle];
        public static int Degrees => Angle.Value switch { 0 => 30, 2 => 60, _ => 45 };
    }

    /// <summary>The options of the shading mode that is on.</summary>
    static ModeOption[]? ModeOptions() => _mesh == null ? null : Shadings[ShadingIndex()].Name switch
    {
        "Studio" => StudioLook.All,
        "Metal" => MetalLook.All,
        "Lines" => LinesLook.All,
        "X-ray" => XrayLook.All,
        "Wire" => WireLook.All,
        "Overhangs" => OverhangLook.All,
        _ => SolidLook.All,
    };

    /// <summary>Wire with "Feature edges": only the part's real edges are drawn, with nothing filled in.</summary>
    static bool FeatureWire => _wire && _style == 0 && R.HasEdges && WireLook.Lines.Value == 1;

    /// <summary>Centre and edge colours of a Studio or Metal backdrop: 0 white, 1 grey, 2 dark.</summary>
    static (Vector3 Center, Vector3 Edge) BackdropColors(int i) => i switch
    {
        1 => (new Vector3(0.68f, 0.69f, 0.71f), new Vector3(0.43f, 0.44f, 0.47f)),
        2 => (new Vector3(0.17f, 0.18f, 0.20f), new Vector3(0.07f, 0.075f, 0.085f)),
        _ => (Vector3.One, new Vector3(0.87f, 0.88f, 0.90f)),
    };

    static float _optBottom, _optRight;         // bottom and right edges of the option row; the toolbar's bottom and 0 when there is none
    static int _optLines;
    static bool _sceneDark, _solidPills;

    /// <summary>
    /// A mode's backdrop need not match the theme (a dark backdrop in the light theme, say). The buttons and
    /// read-outs that sit straight on the view are see-through, so there they get a solid chip to stay readable.
    /// </summary>
    static void SceneLook()
    {
        int theme = _theme.Dark ? 2 : 0, behind = theme;        // 0 light, 1 mid grey, 2 dark
        if (_mesh != null)
            behind = _style switch
            {
                4 => StudioLook.Backdrop.Value,
                5 => MetalLook.Backdrop.Value,
                1 => LinesLook.DarkPaper ? 2 : 0,
                _ => theme,
            };
        _sceneDark = behind == 2;
        _solidPills = behind != theme;
    }

    /// <summary>The body of a button or read-out that sits straight on the view.</summary>
    static void Pill(float x, float y, float w, float h, bool hot = false)
    {
        if (_solidPills) U.Rect(x, y, w, h, h / 2, _theme.Card);
        U.Rect(x, y, w, h, h / 2, hot ? new Vector4(_theme.Panel.X, _theme.Panel.Y, _theme.Panel.Z, _theme.Panel.W * 2.2f) : _theme.Panel);
        U.Rect(x, y, w, h, h / 2, _theme.PanelBorder, 1);
    }

    static void CycleOption(int i)
    {
        var options = ModeOptions();
        if (options == null || i < 0 || i >= options.Length) return;
        options[i].Next();
        string said = $"{options[i].Label}  ·  {options[i].Name}";
        if (_wire && _style == 0 && WireLook.Remark is string remark) said += "  ·  " + remark;
        Toast(said, said.Length > 40 ? 2.6f : 1.6f);
    }

    /// <summary>The second row: one small button per option, wrapping onto further lines in a narrow window.</summary>
    static void OptionsRow(float s, float m)
    {
        var options = ModeOptions();
        if (options == null) return;
        float h = 26 * s, pad = 11 * s, gap = 6 * s, between = 6 * s, x = m, y = _optBottom + 8 * s, right = _w - m;      // under the toolbar, however many lines it took
        _optLines = 1;
        for (int i = 0; i < options.Length; i++)
        {
            string label = options[i].Label, value = options[i].Name;
            float lw = U.Measure(U.Body, label), w = pad * 2 + lw + between + U.Measure(U.Medium, value);
            if (x > m && x + w > right) { x = m; y += h + gap; _optLines++; }
            bool hot = _drag == 0 && _mouseX >= x && _mouseX <= x + w && _mouseY >= y && _mouseY <= y + h;
            if (hot) _hotButton = 100 + i;
            Pill(x, y, w, h, hot);
            U.Text(U.Body, label, x + pad, y + (h - U.Body.Height) / 2, _theme.Dim);
            U.Text(U.Medium, value, x + pad + lw + between, y + (h - U.Medium.Height) / 2, _theme.Text);
            _buttons.Add((x, y, w, h, 100 + i));
            _optRight = MathF.Max(_optRight, x + w);
            x += w + gap;
        }
        _optBottom = y + h;
    }
}
