namespace Glance;

/// <summary>
/// Reload when the file changes on disk: export again from CAD and the open window shows the new part, with the
/// view where it was. The watcher is one quiet thread that starts after the first picture and looks at the file's
/// time and size twice a second; nothing is read until they change.
/// </summary>
internal static unsafe partial class App
{
    /// <summary>The file as it was just before it was last read.</summary>
    sealed class Watched { public string Path = ""; public long Stamp, Length; }

    static volatile Watched? _watch;
    static Thread? _watcher;
    static int _reloads;                // times the open model was replaced because its file changed
    static int _keepGen = -1;           // the load that is a reload of the open file: it keeps the view

    static bool FileState(string path, out long stamp, out long length)
    {
        stamp = 0; length = 0;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return false;
            stamp = fi.LastWriteTimeUtc.Ticks; length = fi.Length;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Called on the loading thread, before the file is read, so a change made while it loads is not missed.</summary>
    static Watched Baseline(string path)
    {
        var w = new Watched { Path = path };
        FileState(path, out w.Stamp, out w.Length);
        return w;
    }

    /// <summary>Called once, after the first picture is on screen.</summary>
    static void StartWatcher()
    {
        if (_watcher != null || !Settings.AutoReload) return;
        _watcher = new Thread(WatchLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "watch" };
        _watcher.Start();
    }

    static void WatchLoop()
    {
        Watched? handled = null;
        while (!_quit)
        {
            Thread.Sleep(500);
            Watched? w = _watch;
            // While comparing, a flip puts the other file on screen: the one on screen is the one that is watched.
            string? shown = _path;
            if (shown != null && !_loading && w != null && w.Path != shown) { _watch = Baseline(shown); continue; }
            if (w == null || ReferenceEquals(w, handled)) continue;
            if (!FileState(w.Path, out long stamp, out long length)) continue;      // gone for the moment: it may be being replaced
            if (stamp == w.Stamp && length == w.Length) continue;

            // Changed. Wait for whoever is writing to finish: the same time and size twice running, and the file opens.
            Thread.Sleep(250);
            if (!FileState(w.Path, out long stamp2, out long length2) || stamp2 != stamp || length2 != length || length == 0) continue;
            try { using var probe = new FileStream(w.Path, FileMode.Open, FileAccess.Read, FileShare.Read); }
            catch { continue; }

            handled = w;
            Post(() => { if (ReferenceEquals(_watch, w) && _path == w.Path && !_quit) Reload(); });
        }
    }

    /// <summary>Reads the open file again and keeps the view, the section cut and the shading as they are.</summary>
    static void Reload()
    {
        if (_path == null) return;
        StartLoad(_path, keepView: _mesh != null);
    }
}
