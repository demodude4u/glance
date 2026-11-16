using System.Globalization;
using System.Numerics;
using Microsoft.Win32;

namespace Glance;

internal static class Settings
{
    public static int Width, Height;
    public static bool Maximized, Edges = true, Grid = true, Ortho, SeenHint;
    public static int Units;                 // lengths are shown in: 0 mm, 1 inches, 2 inches as fractions where they are exact
    public static string Theme = "auto";     // auto | dark | light
    public static int Material = -1;         // -1 = pick for the theme
    public static string PictureSize = "fullhd";   // window | hd | fullhd | qhd | 4k | square (see App.PictureSizes)
    public static bool AutoUpdate = true;    // "autoUpdate=0" stops Glance fetching new versions
    public static bool PictureAlpha;         // saved pictures have a transparent background

    /// <summary>Choices that belong to one shading mode, such as "studio.light=1". Empty until one is made.</summary>
    public static Dictionary<string, int>? Options;
    public static int Option(string key, int fallback) => Options != null && Options.TryGetValue(key, out int v) ? v : fallback;
    public static void SetOption(string key, int value) => (Options ??= new())[key] = value;

    static string File => Path.Combine(Loaders.DataDir, "settings.ini");

    public static void Load()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return;
            foreach (string line in System.IO.File.ReadAllLines(File))
            {
                int eq = line.IndexOf('=');
                if (eq < 1) continue;
                string k = line[..eq].Trim(), v = line[(eq + 1)..].Trim();
                switch (k)
                {
                    case "width": int.TryParse(v, out Width); break;
                    case "height": int.TryParse(v, out Height); break;
                    case "maximized": Maximized = v == "1"; break;
                    case "edges": Edges = v == "1"; break;
                    case "grid": Grid = v == "1"; break;
                    case "ortho": Ortho = v == "1"; break;
                    case "seenHint": SeenHint = v == "1"; break;
                    case "inch": if (v == "1") Units = 1; break;                        // written by versions before 1.5
                    case "units": Units = v == "in" ? 1 : v == "frac" ? 2 : 0; break;
                    case "theme": if (v is "auto" or "dark" or "light") Theme = v; break;
                    // Before 1.5 a picture was 1, 2 or 4 times the window. 2 was the default, so it becomes the new default;
                    // a deliberate 1 or 4 becomes the nearest named size.
                    case "pictureScale": if (v == "1") PictureSize = "window"; else if (v == "4") PictureSize = "4k"; break;
                    case "pictureSize": PictureSize = v; break;
                    case "autoUpdate": AutoUpdate = v != "0"; break;
                    case "pictureAlpha": PictureAlpha = v == "1"; break;
                    case "material": int.TryParse(v, NumberStyles.Integer, null, out Material); break;
                    default: if (k.Contains('.') && int.TryParse(v, NumberStyles.Integer, null, out int o)) SetOption(k, o); break;
                }
            }
        }
        catch { }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Loaders.DataDir);
            var lines = new List<string>
            {
                $"width={Width}", $"height={Height}", $"maximized={(Maximized ? 1 : 0)}",
                $"edges={(Edges ? 1 : 0)}", $"grid={(Grid ? 1 : 0)}", $"ortho={(Ortho ? 1 : 0)}",
                $"seenHint={(SeenHint ? 1 : 0)}", $"units={(Units == 1 ? "in" : Units == 2 ? "frac" : "mm")}", $"theme={Theme}", $"material={Material}",
                $"autoUpdate={(AutoUpdate ? 1 : 0)}", $"pictureSize={PictureSize}", $"pictureAlpha={(PictureAlpha ? 1 : 0)}",
            };
            if (Options != null) foreach (var (k, v) in Options) lines.Add($"{k}={v}");
            System.IO.File.WriteAllLines(File, lines);
        }
        catch { }
    }

    public static bool SystemIsDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int i && i == 0;
        }
        catch { return true; }
    }
}

/// <summary>The palette: Cobalt, Charcoal, Golden Amber, Slate, Ivory.</summary>
internal sealed class Theme
{
    public bool Dark;
    public Vector3 BgCenter, BgEdge;
    public Vector4 Text, Dim, Panel, PanelBorder, Card, Accent, Grid, Scrim;
    public float Shadow;

    public static readonly Vector3 Cobalt = Rgb(0x0047AB), Charcoal = Rgb(0x333333), Amber = Rgb(0xFFC107),
        Slate = Rgb(0x708090), Ivory = Rgb(0xF8F8F8);

    public static readonly (string Name, Vector3 Color)[] Materials =
    [
        ("Ivory", Rgb(0xE9E9E6)), ("Slate", Slate), ("Cobalt", Cobalt), ("Amber", Amber), ("Charcoal", Rgb(0x3D3D3D)),
    ];

    public static Vector3 Rgb(uint c) => new(((c >> 16) & 255) / 255f, ((c >> 8) & 255) / 255f, (c & 255) / 255f);
    public static Vector4 A(Vector3 c, float a) => new(c, a);

    public static Theme Make(bool dark) => dark
        ? new Theme
        {
            Dark = true, BgCenter = Rgb(0x30343B), BgEdge = Rgb(0x17191D),
            Text = A(Ivory, 0.96f), Dim = A(Rgb(0x9FADBB), 0.95f),
            Panel = A(Rgb(0xFFFFFF), 0.07f), PanelBorder = A(Rgb(0xFFFFFF), 0.10f), Card = A(Rgb(0x23262B), 0.97f),
            Accent = A(Amber, 1), Grid = A(Rgb(0xB4C0CC), 0.20f), Scrim = A(Rgb(0x000000), 0.45f), Shadow = 0.34f,
        }
        : new Theme
        {
            Dark = false, BgCenter = Rgb(0xFFFFFF), BgEdge = Rgb(0xE3E6EA),
            Text = A(Charcoal, 1f), Dim = A(Slate, 1f),
            Panel = A(Rgb(0x1B2430), 0.06f), PanelBorder = A(Rgb(0x1B2430), 0.10f), Card = A(Rgb(0xFFFFFF), 0.98f),
            Accent = A(Cobalt, 1), Grid = A(Slate, 0.30f), Scrim = A(Rgb(0x5A6470), 0.35f), Shadow = 0.16f,
        };
}
