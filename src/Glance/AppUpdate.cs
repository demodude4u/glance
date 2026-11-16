using System.Diagnostics;
using System.Numerics;
using static Glance.Win32;

namespace Glance;

/// <summary>
/// The update indicator, bottom right: a slim bar that slides while Glance looks for a newer version, fills as
/// one is downloaded, and turns into an Update button when it is ready. It fades away when there is nothing new.
/// </summary>
internal static partial class App
{
    static (float X, float Y, float W, float H) _updRect;      // where the indicator is this frame
    static float _updLeft;                                      // its left edge, or the window's width when it is not shown
    static float _updA, _updReady, _updFill;                    // presence, bar-to-button, and shown progress, each eased 0..1
    static long _updAppeared;
    static bool _updHot;

    const string UpdateLabel = "Update";

    /// <summary>Called from another thread's Post when the updater moves to a new stage.</summary>
    public static void Refresh() => Invalidate();

    static bool UpdateWanted()
    {
        var st = Updater.State;
        if (st == Updater.Stage.Checking) return Seconds(Updater.Since) < 12;        // offline: stop waving after a while
        return st != Updater.Stage.None;
    }

    /// <summary>Advances the indicator's animations; true while it needs another frame.</summary>
    static bool UpdateTick(float dt)
    {
        var st = Updater.State;
        bool want = UpdateWanted();
        if (want && _updA == 0) _updAppeared = Stopwatch.GetTimestamp();
        bool hold = _updA > 0 && Seconds(_updAppeared) < 0.9;                        // never just a flicker
        bool a = Ease(ref _updA, want || hold ? 1 : 0, dt, 9);
        bool r = Ease(ref _updReady, st == Updater.Stage.Ready ? 1 : 0, dt, 10);
        float p = st == Updater.Stage.Ready ? 1 : st == Updater.Stage.Downloading ? MathF.Max(Updater.Progress, 0) : 0;
        if (_updFill > p) _updFill = p;                                              // a new download starts from empty
        bool f = Ease(ref _updFill, p, dt, 8);
        bool busy = want && st != Updater.Stage.Ready;                               // the bar itself is moving
        return a || r || f || busy || hold;

        static bool Ease(ref float v, float to, float dt, float rate)
        {
            if (v == to) return false;
            v += (to - v) * (1 - MathF.Exp(-dt * rate));
            if (MathF.Abs(to - v) < 0.004f) v = to;
            return true;
        }
    }

    /// <summary>Decides where the indicator goes, before the read-outs that have to keep clear of it are drawn.</summary>
    static void UpdateLayout(float s, float m)
    {
        _updRect = default; _updLeft = _w;
        if (_updA <= 0) return;
        float h = 32 * s, w = U.Measure(U.Medium, UpdateLabel) + 28 * s;
        float right = _w - m - (_mesh != null ? 86 * s : 0);                         // left of the axis gizmo
        _updRect = (right - w, _h - m - h, w, h);
        _updLeft = right - w;
    }

    static void UpdateIndicator(float s)
    {
        if (_updA <= 0) return;
        var (x, y, w, h) = _updRect;
        var st = Updater.State;
        float bar = _updA * (1 - _updReady);
        if (bar > 0.01f)
        {
            float bh = 4 * s, by = y + (h - bh) / 2;
            U.Rect(x, by, w, bh, bh / 2, Fade(_theme.Panel, bar * 1.6f));
            if (st == Updater.Stage.Downloading && Updater.Progress >= 0 || st == Updater.Stage.Ready)
                U.Rect(x, by, MathF.Max(bh, w * _updFill), bh, bh / 2, Fade(_theme.Accent, bar));
            else if (st != Updater.Stage.None)
            {
                float t = (float)(Seconds(Updater.Since) * 0.85 % 1.0), e = t * t * (3 - 2 * t);
                float seg = w * 0.36f, sx = x - seg + (w + seg) * e;
                float x0 = MathF.Max(sx, x), x1 = MathF.Min(sx + seg, x + w);
                if (x1 - x0 > bh) U.Rect(x0, by, x1 - x0, bh, bh / 2, Fade(_theme.Accent, bar));
            }
        }
        if (_updReady > 0.01f)
        {
            float a = _updA * _updReady, rise = (1 - _updReady) * 5 * s;
            bool hot = _updHot && _drag == 0;
            U.Rect(x, y + rise + 2 * s, w, h, h / 2, new Vector4(0, 0, 0, (_theme.Dark ? 0.28f : 0.14f) * a));
            U.Rect(x, y + rise, w, h, h / 2, Fade(_theme.Accent, hot ? a * 0.86f : a));
            U.TextCentered(U.Medium, UpdateLabel, x + w / 2, y + rise + (h - U.Medium.Height) / 2, Fade(AccentInk, a));
        }
    }

    static bool OverUpdate(int mx, int my) =>
        Updater.State == Updater.Stage.Ready && _updA > 0 && !_help
        && mx >= _updRect.X && mx <= _updRect.X + _updRect.W && my >= _updRect.Y && my <= _updRect.Y + _updRect.H;

    /// <summary>Update pressed: close, let the installer replace Glance, and come back on the same file.</summary>
    static bool UpdateClick(int mx, int my)
    {
        if (!OverUpdate(mx, my)) return false;
        if (Updater.InstallNow(_path)) PostMessageW(_hwnd, WM_CLOSE, 0, 0);
        return true;
    }
}
