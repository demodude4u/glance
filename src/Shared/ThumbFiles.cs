using System.Diagnostics;

namespace Glance;

/// <summary>
/// Replacing or removing the thumbnail files while Windows may be using them. Explorer loads the handler DLL into a
/// short-lived helper process of its own ("COM Surrogate", dllhost.exe), which can still be running when Glance is
/// updated or removed. A DLL that is loaded cannot be deleted or overwritten, but it can be renamed, so it is moved
/// out of the way and deleted the next time round.
/// </summary>
internal static class ThumbFiles
{
    const string Old = "glance-thumbs-old-";

    /// <summary>Makes sure nothing in <paramref name="dir"/> belonging to thumbnails is still held. Returns a file left behind, if any.</summary>
    public static string? Free(string dir)
    {
        Sweep(dir);
        // The helper first: once it is gone, a handler that is still loaded can no longer start one.
        string helper = Path.Combine(dir, ThumbTypes.Helper);
        for (int attempt = 0; attempt < 15 && File.Exists(helper); attempt++)
        {
            foreach (var p in Process.GetProcessesByName(ThumbTypes.HelperProcess))
                try { if (p.MainModule!.FileName.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)) { p.Kill(true); p.WaitForExit(3000); } }
                catch { }
                finally { p.Dispose(); }
            try { File.Delete(helper); } catch { Thread.Sleep(200); }
        }

        string dll = Path.Combine(dir, ThumbTypes.Dll);
        if (!File.Exists(dll)) return null;
        try { File.Delete(dll); return null; } catch { }

        string name = Old + Guid.NewGuid().ToString("N") + ".dll";
        foreach (string to in (string[])[Path.Combine(Path.GetTempPath(), name), Path.Combine(Path.GetDirectoryName(dir.TrimEnd('\\')) ?? dir, name)])
            try { File.Move(dll, to); return to; } catch { }

        // Renaming failed too (another disk, a policy): stop the thumbnail processes that hold the file. Explorer starts new ones as needed.
        foreach (var p in Process.GetProcessesByName("dllhost"))
            try
            {
                foreach (ProcessModule m in p.Modules)
                    if (string.Equals(m.FileName, dll, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(3000); break; }
            }
            catch { }
            finally { p.Dispose(); }
        try { File.Delete(dll); } catch { }
        return null;
    }

    /// <summary>Deletes handler files that were moved out of the way earlier and are no longer in use.</summary>
    public static void Sweep(string dir)
    {
        foreach (string where in (string[])[Path.GetTempPath(), Path.GetDirectoryName(dir.TrimEnd('\\')) ?? dir])
            try
            {
                foreach (string f in Directory.EnumerateFiles(where, Old + "*.dll"))
                    try { File.Delete(f); } catch { }
            }
            catch { }
    }
}
