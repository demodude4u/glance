using System.Globalization;
using System.Numerics;
using Microsoft.Win32;

namespace Glance;

internal static class Settings
{
    public static int Width, Height;
    public static bool Maximized, Edges = true, Grid = true, Ortho, SeenHint, Inch;
    public static string Theme = "auto";     // auto | dark | light
    public static int Material = -1;         // -1 = pick for the theme

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
                    case "inch": Inch = v == "1"; break;
                    case "theme": if (v is "auto" or "dark" or "light") Theme = v; break;
                    case "material": int.TryParse(v, NumberStyles.Integer, null, out Material); break;
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
            System.IO.File.WriteAllLines(File,
            [
                $"width={Width}", $"height={Height}", $"maximized={(Maximized ? 1 : 0)}",
                $"edges={(Edges ? 1 : 0)}", $"grid={(Grid ? 1 : 0)}", $"ortho={(Ortho ? 1 : 0)}",
                $"seenHint={(SeenHint ? 1 : 0)}", $"inch={(Inch ? 1 : 0)}", $"theme={Theme}", $"material={Material}",
            ]);
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
