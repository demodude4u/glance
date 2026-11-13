using System.Runtime.InteropServices;

namespace Glance;

/// <summary>
/// The brand typeface travels inside the program, so every PC shows the same Glance whether or not Roboto is
/// installed. The fonts are registered for this process only, straight from memory: nothing is installed into
/// Windows and no file is written. Built from tools/make_fonts.py (family renamed to "Glance Roboto").
/// </summary>
internal static unsafe class BundledFonts
{
    [DllImport("gdi32.dll")] static extern nint AddFontMemResourceEx(void* data, uint size, nint reserved, uint* count);

    public const string Regular = "Glance Roboto", Medium = "Glance Roboto Medium", Light = "Glance Roboto Light";
    public static int Loaded;

    public static void Load()
    {
        var asm = typeof(BundledFonts).Assembly;
        foreach (string name in (string[])["GlanceRoboto-Regular.ttf", "GlanceRoboto-Medium.ttf", "GlanceRoboto-Light.ttf"])
        {
            try
            {
                using var s = asm.GetManifestResourceStream(name);
                if (s == null) continue;
                byte[] data = new byte[s.Length];
                s.ReadExactly(data);
                uint count = 0;
                fixed (byte* p = data)
                    if (AddFontMemResourceEx(p, (uint)data.Length, 0, &count) != 0) Loaded += (int)count;
            }
            catch { }       // the callers fall back to the Windows UI face
        }
    }
}
