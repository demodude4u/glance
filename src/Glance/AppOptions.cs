using System.Numerics;

namespace Glance;

/// <summary>
/// Options that belong to one shading mode. While that mode is on they show as a second row of smaller buttons
/// under the toolbar; each button steps through its choices and shows the current one, the way the Shading
/// button does. Choices are remembered per mode in settings.ini. Nothing here is touched until a mode that has
/// options is chosen, so it costs nothing at start-up.
/// </summary>
internal static unsafe partial class App
{
    sealed class ModeOption(string key, string label, string[] values, int fallback = 0, Func<int>? auto = null)
    {
        public readonly string Key = key, Label = label;
        public readonly string[] Values = values;

        /// <summary>The current choice. Until one is made, an option may follow the theme or the file instead of a fixed default.</summary>
        public int Value
        {
            get
            {
                int v = Settings.Option(Key, -1);
                return v >= 0 && v < Values.Length ? v : auto != null ? auto() : fallback;
            }
        }

        public string Name => Values[Value];
        public void Next() => Settings.SetOption(Key, (Value + 1) % Values.Length);
    }

    /// Studio: where the light comes from, what is behind the part, what the part is made of, and its shadow on the ground.
    static class StudioLook
    {
        public static readonly ModeOption Light = new("studio.light", "Light", ["Soft", "Left", "Right", "Top"]);
        public static readonly ModeOption Backdrop = new("studio.backdrop", "Backdrop", ["White", "Grey", "Dark", "Plain"], auto: () => _theme.Dark ? 2 : 0);
        public static readonly ModeOption Material = new("studio.material", "Material", ["Clay", "White", "Charcoal", "Part color"], auto: () => _fileColors && R.HasColors ? 3 : 0);
        public static readonly ModeOption Shadow = new("studio.shadow", "Shadow", ["Off", "On"], 1);
        public static readonly ModeOption[] All = [Light, Backdrop, Material, Shadow];

        /// <summary>Centre and edge colours of the backdrop. Plain is one flat white, easy to cut out or drop into a document.</summary>
        public static (Vector3 Center, Vector3 Edge) BackdropColors(int i) => i switch
        {
            1 => (new Vector3(0.68f, 0.69f, 0.71f), new Vector3(0.43f, 0.44f, 0.47f)),
            2 => (new Vector3(0.17f, 0.18f, 0.20f), new Vector3(0.07f, 0.075f, 0.085f)),
            3 => (Vector3.One, Vector3.One),
            _ => (Vector3.One, new Vector3(0.87f, 0.88f, 0.90f)),
        };

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

    /// <summary>The options of the shading mode that is on, or null when it has none.</summary>
    static ModeOption[]? ModeOptions() => _mesh == null ? null : Shadings[ShadingIndex()].Name switch
    {
        "Studio" => StudioLook.All,
        _ => null,
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
        _sceneDark = _theme.Dark; _solidPills = false;
        if (_mesh == null || _style != 4) return;
        int backdrop = StudioLook.Backdrop.Value;
        _sceneDark = backdrop == 2;
        _solidPills = _theme.Dark ? backdrop != 2 : backdrop is 1 or 2;
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
        Toast($"{options[i].Label}  ·  {options[i].Name}");
    }

    /// <summary>The second row: one small button per option, wrapping onto further lines in a narrow window.</summary>
    static void OptionsRow(float s, float m)
    {
        var options = ModeOptions();
        if (options == null) return;
        float h = 26 * s, pad = 11 * s, gap = 6 * s, between = 6 * s, x = m, y = m + 32 * s + 8 * s, right = _w - m;
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
