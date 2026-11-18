using System.Runtime.InteropServices;

namespace Glance;

/// <summary>
/// The models opened lately: a short list in the per-user data folder (recent.txt, newest first, one path a line),
/// and the same news passed to Windows so the taskbar's Jump List for Glance shows them under Recent.
/// Everything here does disk or shell work and is only ever called from a background thread.
/// </summary>
internal static unsafe class Recent
{
    const int Keep = 12;
    static readonly object Gate = new();
    static string ListFile => Path.Combine(Loaders.DataDir, "recent.txt");

    /// <summary>
    /// When the data folder has been redirected (the automated tests) Windows is told nothing, so that test files
    /// never turn up in anyone's real Jump List. GLANCE_TEST_RECENTDOCS=1 makes the call all the same, for the one
    /// check that it can be made at all.
    /// </summary>
    static bool TellWindows => string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GLANCE_DATA_DIR"))
        || Environment.GetEnvironmentVariable("GLANCE_TEST_RECENTDOCS") == "1";

    [DllImport("shell32")] static extern void SHAddToRecentDocs(uint flags, char* path);
    const uint SHARD_PATHW = 3;

    /// <summary>Every path on the list, newest first, whether or not the file is still there.</summary>
    static List<string> ReadAll()
    {
        var list = new List<string>();
        try
        {
            if (File.Exists(ListFile))
                foreach (string line in File.ReadAllLines(ListFile))
                    if (line.Trim() is { Length: > 0 } p && !list.Exists(q => string.Equals(q, p, StringComparison.OrdinalIgnoreCase))) list.Add(p);
        }
        catch { }
        return list;
    }

    /// <summary>The models on the list that can still be opened, newest first. A file on a drive that is away is left out, not forgotten.</summary>
    public static List<string> Existing(int max)
    {
        List<string> all;
        lock (Gate) all = ReadAll();
        var found = new List<string>();
        foreach (string p in all)
        {
            if (found.Count >= max) break;
            try { if (Loaders.Supported(p) && File.Exists(p)) found.Add(p); } catch { }
        }
        return found;
    }

    /// <summary>A model has been opened: it moves to the top of the list, and Windows hears of it for the Jump List.</summary>
    public static void Opened(string path)
    {
        try { path = Path.GetFullPath(path); } catch { return; }
        lock (Gate)
        {
            try
            {
                var list = ReadAll();
                list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                list.Insert(0, path);
                if (list.Count > Keep) list.RemoveRange(Keep, list.Count - Keep);
                Directory.CreateDirectory(Loaders.DataDir);
                string tmp = ListFile + "." + Environment.ProcessId + ".tmp";
                File.WriteAllLines(tmp, list);
                File.Move(tmp, ListFile, true);                 // another Glance window may be writing too: all or nothing
            }
            catch { }
        }
        // Glance sets no application ID of its own, so Windows files this under the ID it works out for Glance.exe,
        // the same one its taskbar button and Start menu entry have. The call has to come from Glance itself for that.
        if (TellWindows)
            try { fixed (char* p = path) SHAddToRecentDocs(SHARD_PATHW, p); } catch { }
    }
}
