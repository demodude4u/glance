namespace Glance;

/// <summary>
/// What the three parts of Explorer thumbnails agree on: the handler Explorer loads (src\ThumbShell, GlanceThumbs.dll),
/// the helper that draws the picture (src\Thumb, glance-thumb.exe) and the registration (src\Glance\RegistrationThumbs.cs).
/// </summary>
internal static class ThumbTypes
{
    public const string Dll = "GlanceThumbs.dll", Helper = "glance-thumb.exe", HelperProcess = "glance-thumb";

    /// <summary>The handler and the helper talk in this version of their command line; a mismatch means "no picture".</summary>
    public const string Protocol = "1";

    /// <summary>
    /// One COM class per format. Explorer hands a thumbnail handler the bytes of a file but not its name, so the
    /// class that was asked for is how the handler knows which reader to use.
    /// </summary>
    public static readonly (string Format, string[] Extensions, long MaxBytes, int BudgetMs)[] Formats =
    [
        ("stl", [".stl"], 192L << 20, 8000),
        ("obj", [".obj"], 192L << 20, 8000),
        ("3mf", [".3mf"], 64L << 20, 8000),
        ("ply", [".ply"], 192L << 20, 8000),
        ("glb", [".glb"], 192L << 20, 8000),
        ("gltf", [".gltf"], 192L << 20, 8000),
        ("step", [".step", ".stp"], 40L << 20, 15000),      // the same limit as the viewer's look-ahead conversion
        ("iges", [".iges", ".igs"], 40L << 20, 15000),
    ];

    /// <summary>The class id for format number <paramref name="i"/>: the ids differ in their last byte only.</summary>
    public static string Clsid(int i) => "{4614499B-2F56-4003-83C3-D6115D33A5" + i.ToString("X2") + "}";

    /// <summary>The whole job (the helper and a STEP conversion it may start) may not use more memory than this.</summary>
    public const long JobMemory = 1536L << 20;

    /// <summary>The largest picture drawn, in pixels along its longer side. Explorer scales a smaller one down itself.</summary>
    public const int MaxPixels = 1024;
}
