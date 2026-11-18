using System.Numerics;

namespace Glance;

/// <summary>
/// Recent files in the window: the list under "Drop a model to take a look" when Glance is opened without a file,
/// where one click reopens a model. With a model open there is nothing of it on screen. The list is read, and
/// each file checked to still be there, on a background thread after the first picture; a model that has just
/// been opened is written to the list the same way (see Recent.cs).
/// </summary>
internal static partial class App
{
    const int RecentShown = 5;
    static List<string>? _recent;               // null until read
    static bool _recentAsked;
    static bool _browse, _noteOpen;             // the open in hand came from an arrow key; it is one to remember
    static string? _openedPending;
    static int _recentHot = -1;
    static float _recentA;                      // the list fades in once it has been read
    static readonly List<(float X, float Y, float W, float H)> _recentRows = new();

    static bool RecentSettled => (_openedPending == null || _mesh == null) && (!_recentAsked || _recent != null) && _noting == 0
        && (_mesh != null || _error != null || _recent is not { Count: > 0 } || _recentA >= 1);
    static int _noting;

    /// <summary>A model was opened on purpose (not stepped to with the arrow keys, not reloaded): remember it.</summary>
    static void NoteOpened(string path)
    {
        if (_snapshot != null) return;
        _openedPending = path;
        if (_tFirst != 0) FlushOpened();
    }

    /// <summary>Writes the pending entry once the first picture is out, on a thread of its own.</summary>
    static void FlushOpened()
    {
        if (_openedPending is not string path || _tFirst == 0) return;
        _openedPending = null;
        Interlocked.Increment(ref _noting);
        new Thread(() =>
        {
            try { Recent.Opened(path); } catch { }
            Interlocked.Decrement(ref _noting);
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "recent" }.Start();
    }

    /// <summary>Starts reading the list for the empty window. Never before the first picture.</summary>
    static void AskRecent()
    {
        if (_recentAsked || _snapshot != null) return;
        if (_tFirst == 0) { Invalidate(); return; }             // this frame is the first: ask on the next one
        _recentAsked = true;
        new Thread(() =>
        {
            List<string> found;
            try { found = Recent.Existing(RecentShown); } catch { found = new(); }
            Post(() => { _recent = found; Invalidate(); });
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "recent" }.Start();
    }

    /// <summary>How far the empty window's greeting moves up to make room for the list.</summary>
    static float RecentLift(float s) => _recent is { Count: > 0 } r ? (30 * s + RecentCount(s) * 36 * s) / 2 : 0;

    static int RecentCount(float s) => _recent == null ? 0 : Math.Min(_recent.Count, Math.Max(1, (int)((_h - 190 * s) / (36 * s))));

    /// <summary>The list itself, under the greeting: file name, and the folder it is in.</summary>
    static void RecentList(float s, float top)
    {
        AskRecent();
        _recentRows.Clear();
        if (_recent is not { Count: > 0 } list) return;
        if (_recentA < 1) { _recentA = MathF.Min(1, _recentA + 0.12f); Invalidate(); }
        float a = _recentA, w = MathF.Min(460 * s, _w - 48 * s), x = (_w - w) / 2, rowH = 34 * s, y = top;
        U.Text(U.Body, "Recent", x + 12 * s, y, Fade(_theme.Dim, 0.8f * a));
        y += U.Body.Height + 8 * s;
        int count = RecentCount(s);
        _recentHot = -1;
        for (int i = 0; i < count; i++)
        {
            string path = list[i], name = Path.GetFileName(path), folder = Path.GetFileName(Path.GetDirectoryName(path) ?? "") ?? "";
            bool hot = _drag == 0 && _mouseX >= x && _mouseX <= x + w && _mouseY >= y && _mouseY <= y + rowH;
            if (hot) _recentHot = i;
            U.Rect(x, y, w, rowH, 9 * s, Fade(hot ? new Vector4(_theme.Panel.X, _theme.Panel.Y, _theme.Panel.Z, _theme.Panel.W * 2.2f) : _theme.Panel, a));
            U.Rect(x, y, w, rowH, 9 * s, Fade(_theme.PanelBorder, a), 1);
            float pad = 12 * s, room = w - pad * 2;
            string shownFolder = folder.Length > 0 ? Fit(U.Body, folder, room * 0.38f) : "";
            float fw = U.Measure(U.Body, shownFolder);
            U.Text(U.Medium, Fit(U.Medium, name, room - fw - (fw > 0 ? 14 * s : 0)), x + pad, y + (rowH - U.Medium.Height) / 2, Fade(_theme.Text, a));
            if (fw > 0) U.Text(U.Body, shownFolder, x + w - pad - fw, y + (rowH - U.Body.Height) / 2, Fade(_theme.Dim, 0.9f * a));
            _recentRows.Add((x, y, w, rowH));
            y += rowH + 2 * s;
        }
    }

    static int RecentAt(int mx, int my)
    {
        if (_mesh != null || _loading || _error != null || _help) return -1;
        for (int i = 0; i < _recentRows.Count; i++)
        {
            var r = _recentRows[i];
            if (mx >= r.X && mx <= r.X + r.W && my >= r.Y && my <= r.Y + r.H) return i;
        }
        return -1;
    }

    static bool RecentHover(int mx, int my) => RecentAt(mx, my) != _recentHot;

    /// <summary>A click in the empty window: on a row, that model opens.</summary>
    static bool RecentClick(int mx, int my)
    {
        int i = RecentAt(mx, my);
        if (i < 0 || _recent == null || i >= _recent.Count) return false;
        StartLoad(_recent[i]);
        return true;
    }

    static string RecentState()
    {
        bool shown = _mesh == null && !_loading && _error == null;          // the list is only on screen in the empty window
        return string.Join('\n',
            $"recent={(_recent == null ? "" : string.Join(";", _recent.Select(Path.GetFileName)))}", $"recentRead={_recent != null}",
            $"recentRows={(shown ? string.Join(";", _recentRows.Select(r => $"{r.X + r.W / 2:0},{r.Y + r.H / 2:0}")) : "")}",
            $"recentRects={(shown ? string.Join(";", _recentRows.Select(r => $"{r.X:0},{r.Y:0},{r.W:0},{r.H:0}")) : "")}");
    }
}
