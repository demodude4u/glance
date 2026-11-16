using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using static Glance.Win32;

namespace Glance;

internal static unsafe partial class App
{
    [UnmanagedCallersOnly]
    static nint WndProc(nint hwnd, uint msg, nuint wp, nint lp)
    {
        try { if (Handle(hwnd, msg, wp, lp, out nint result)) return result; }
        catch (Exception ex) { Program.Crash(ex); }
        return DefWindowProcW(hwnd, msg, wp, lp);
    }

    static bool Handle(nint hwnd, uint msg, nuint wp, nint lp, out nint result)
    {
        result = 0;
        int mx = LoWord(lp), my = HiWord(lp);
        switch (msg)
        {
            case WM_SIZE:
                _w = Math.Max(1, (int)(lp & 0xFFFF)); _h = Math.Max(1, (int)((lp >> 16) & 0xFFFF));
                if (_glReady && wp != 1) Frame();
                return true;

            case WM_PAINT:
                if (!_glReady) return false;
                ValidateRect(hwnd, null);
                Frame();
                return true;

            case WM_ERASEBKGND:
                if (!_glReady) return false;
                result = 1; return true;

            case WM_GETMINMAXINFO:
                var mm = (MINMAXINFO*)lp;
                mm->ptMinTrackSize = new POINT { X = (int)(420 * _scale), Y = (int)(320 * _scale) };
                return true;

            case WM_DPICHANGED:
                _scale = (wp & 0xFFFF) / 96f;
                var r = (RECT*)lp;
                SetWindowPos(hwnd, 0, r->Left, r->Top, r->Right - r->Left, r->Bottom - r->Top, 0x0014);
                if (_glReady) { U.BakeFonts(_scale); U.UploadFonts(); Invalidate(); }
                return true;

            case WM_SETTINGCHANGE:
                if (Settings.Theme == "auto" && _glReady)
                {
                    bool dark = Settings.SystemIsDark();
                    if (dark != _theme.Dark) { _theme = Theme.Make(dark); ApplyChrome(); Invalidate(); }
                }
                return false;

            case WM_APP + 1:
                if (_glReady) Drain();
                return true;

            case WM_APP + 3:
                if (Environment.GetEnvironmentVariable("GLANCE_TEST_DIR") != null) CycleFileUnit();      // test hook: step to the next unit
                return true;

            case WM_APP + 5:        // test hook: save a picture without the dialog. wp bit 0 = transparent, wp >> 1 = size factor (0 = as set)
                if (Environment.GetEnvironmentVariable("GLANCE_TEST_DIR") is string shots && _mesh != null)
                {
                    if (wp != 0) { Settings.PictureAlpha = (wp & 1) != 0; if ((int)(wp >> 1) is 1 or 2 or 4) Settings.PictureScale = (int)(wp >> 1); }
                    SavePicture(Path.Combine(shots, "export.png"), wait: true);
                    Invalidate();
                }
                return true;

            case WM_APP + 4:
                if (Environment.GetEnvironmentVariable("GLANCE_TEST_DIR") != null) { ToggleFixUnits(); Invalidate(); }   // test hook for Shift+U
                return true;

            case WM_APP + 2:
                _pendingShot = (int)wp; Invalidate();
                return true;

            case WM_LBUTTONDOWN:
                if (_help) { _help = false; Invalidate(); return true; }
                if (HudClick(mx, my)) return true;
                BeginDrag(KeyDown(VK_SHIFT) ? 2 : 1, mx, my);
                return true;
            case WM_RBUTTONDOWN:
            case WM_MBUTTONDOWN:
                BeginDrag(2, mx, my);
                return true;
            case WM_LBUTTONUP:
            case WM_RBUTTONUP:
            case WM_MBUTTONUP:
                if (_drag != 0)
                {
                    if (_drag != 1 || Seconds(_lastMove) > 0.07) _velYaw = _velPitch = 0;
                    bool click = _drag == 1 && !_moved && msg == WM_LBUTTONUP;
                    _drag = 0; ReleaseCapture(); Invalidate();
                    if (click && _layFlat) LayFlatAt(mx, my);
                    else if (click && _measure) ClickMeasure(mx, my);
                }
                return true;
            case WM_CAPTURECHANGED:
                _drag = 0;
                return true;

            case WM_MOUSEMOVE:
                _mouseX = mx; _mouseY = my;
                if (OverUpdate(mx, my) != _updHot) { _updHot = !_updHot; Invalidate(); }
                if (_drag == 3) { DragSlider(mx); return true; }
                if (_drag != 0 && _mesh != null) Drag(mx, my);
                else if (_mesh != null && (_measure || _layFlat || _fixUnits || _picture || my < 64 * _scale || _hotButton >= 0)) { _hoverDirty = _measure || _layFlat; Invalidate(); }
                return true;

            case WM_LBUTTONDBLCLK:
                if (HudClick(mx, my)) return true;
                if (_measure || _layFlat) BeginDrag(1, mx, my);   // while picking, a fast second click is just a click
                else FocusAt(mx, my);
                return true;

            case WM_MOUSEWHEEL:
                var pt = new POINT { X = mx, Y = my };
                ScreenToClient(hwnd, &pt);
                float notches = (short)((wp >> 16) & 0xFFFF) / 120f;
                if (_section != 0 && ((wp & 0x8) != 0 || KeyDown(VK_CONTROL)))
                {
                    _sectionPos = Math.Clamp(_sectionPos + notches * (KeyDown(VK_SHIFT) ? 0.002f : 0.02f), 0, 1);
                    _hoverDirty = _measure; Invalidate();
                }
                else Zoom(notches, pt.X, pt.Y);
                return true;

            case WM_KEYDOWN:
                OnKey((int)wp, KeyDown(VK_CONTROL));
                return true;

            case WM_DROPFILES:
                char* buf = stackalloc char[2048];
                if (DragQueryFileW((nint)wp, 0, buf, 2048) > 0) StartLoad(new string(buf));
                DragFinish((nint)wp);
                return true;

            case WM_CLOSE:
                if (IsIconic(hwnd) == 0)
                {
                    Settings.Maximized = IsZoomed(hwnd) != 0;
                    if (!Settings.Maximized)
                    {
                        RECT wr; GetWindowRect(hwnd, &wr);
                        Settings.Width = (int)((wr.Right - wr.Left) / _scale); Settings.Height = (int)((wr.Bottom - wr.Top) / _scale);
                    }
                }
                _loadGen++;             // cancels any STEP conversion still running
                Loaders.KillWorkers();
                DestroyWindow(hwnd);
                return true;

            case WM_DESTROY:
                PostQuitMessage(0);
                return true;
        }
        return false;
    }

    static void BeginDrag(int mode, int mx, int my)
    {
        _drag = mode; _lastX = mx; _lastY = my; _lastMove = Stopwatch.GetTimestamp();
        _downX = mx; _downY = my; _moved = false;
        _velYaw = _velPitch = 0; _spin = false;
        _goal.Yaw = _cur.Yaw; _goal.Pitch = _cur.Pitch;
        SetCapture(_hwnd);
    }

    static void Drag(int mx, int my)
    {
        if (!_moved)
        {
            // A press that barely moves is a click, not the start of an orbit.
            if (Math.Abs(mx - _downX) + Math.Abs(my - _downY) <= 4 * _scale) return;
            _moved = true; _lastX = mx; _lastY = my; _lastMove = Stopwatch.GetTimestamp();
            return;
        }
        int dx = mx - _lastX, dy = my - _lastY;
        if (dx == 0 && dy == 0) return;
        _lastX = mx; _lastY = my;
        long now = Stopwatch.GetTimestamp();
        float dt = MathF.Max((float)((now - _lastMove) / (double)Stopwatch.Frequency), 0.004f);
        _lastMove = now;

        if (_drag == 1)
        {
            float k = 0.0078f / MathF.Sqrt(_scale);
            float dyaw = -dx * k;
            float pitch = Math.Clamp(_goal.Pitch + dy * k, -MathF.PI / 2, MathF.PI / 2);
            float dpitch = pitch - _goal.Pitch;
            _goal.Yaw += dyaw; _cur.Yaw += dyaw; _goal.Pitch = pitch; _cur.Pitch += dpitch;
            _velYaw = _velYaw * 0.5f + dyaw / dt * 0.5f; _velPitch = _velPitch * 0.5f + dpitch / dt * 0.5f;
        }
        else
        {
            Basis(_cur, out _, out var r, out var u);
            float perPx = 2 * _cur.Dist * MathF.Tan(Fov / 2) / _h;
            var d = (-dx * r + dy * u) * perPx;
            _goal.Target += d; _cur.Target += d;
        }
        Invalidate();
    }

    const float Front = -MathF.PI / 2;

    static void OnKey(int vk, bool ctrl)
    {
        if (vk >= 0x60 && vk <= 0x69) vk = 0x30 + (vk - 0x60);      // numpad digits
        switch (vk)
        {
            case 0x1B:
                if (_help) _help = false;
                else if (_picture) _picture = false;
                else if (_fixUnits) _fixUnits = false;
                else if (_layFlat) { _layFlat = false; _hover = null; }
                else if (_measure && (_selA != null || _selB != null)) ClearMeasure();
                else if (_measure) { _measure = false; ClearMeasure(); }
                else if (_section != 0) _section = 0;
                else PostMessageW(_hwnd, WM_CLOSE, 0, 0);
                break;
            case 'M': _layFlat = false; ToggleMeasure(); break;
            case 'L': ToggleLayFlat(); break;
            case 'X':
                if (_mesh == null) break;
                if (KeyDown(VK_SHIFT) && _section != 0) { _sectionFlip = !_sectionFlip; Toast("Section flipped"); }
                else CycleSection();
                break;
            case 'B': _box = !_box; Toast(_box ? "Bounding box on" : "Bounding box off"); break;
            case 'U':
                if (KeyDown(VK_SHIFT)) { ToggleFixUnits(); break; }
                _inch = !_inch; Settings.Inch = _inch; Toast(_inch ? "Showing inches" : "Showing millimeters");
                break;
            case 0x70 or 'H' or 0xBF or '?': _help = !_help; break;
            case 'F' or 0x24: Fit(); break;
            case '0' or 0x20: SetView(IsoYaw, IsoPitch); break;
            case '1': SetView(Front, 0); Toast("Front"); break;
            case '2': SetView(Front + MathF.PI, 0); Toast("Back"); break;
            case '3': SetView(MathF.PI, 0); Toast("Left"); break;
            case '4': SetView(0, 0); Toast("Right"); break;
            case '5': SetView(Front, MathF.PI / 2); Toast("Top"); break;
            case '6': SetView(Front, -MathF.PI / 2); Toast("Bottom"); break;
            case 'P': ToggleOrtho(); break;
            case 'V': if (_mesh != null) CycleShading(KeyDown(VK_SHIFT)); break;
            case 'E': _edges = !_edges; Toast(_edges ? "Edges on" : "Edges off"); break;
            case 'W': _wire = !_wire; Toast(_wire ? "Wireframe on" : "Wireframe off"); break;
            case 'G': _grid = !_grid; Toast(_grid ? "Grid on" : "Grid off"); break;
            case 'R': _spin = !_spin; _velYaw = _velPitch = 0; Toast(_spin ? "Turntable" : "Turntable off"); break;
            case 'I' or 0x09: _info = !_info; break;
            case 'S':
                if (ctrl) { _picture = false; SavePicture(); break; }
                if (_mesh == null) break;
                if (_mesh.SmoothNrm == null) { Toast(_mesh.Nrm != null ? "This model has its own shading" : "Smooth shading isn't ready yet"); break; }
                _smooth = !_smooth;
                R.UploadNormals(_smooth ? _mesh.SmoothNrm : null);
                Toast(_smooth ? "Smooth shading" : "Flat shading");
                break;
            case 'O': OpenDialog(); break;
            case 0x74: if (_path != null) StartLoad(_path); break;                 // F5
            case 0x25 or 0x21: StepFile(-1); break;
            case 0x27 or 0x22: StepFile(+1); break;
            case 'Z':
                if (_mesh == null) break;
                if (_customOrient) { SetOrientation(_mesh.YUp ? YUp : Matrix4x4.Identity); Toast("Orientation put back"); }
                else { SetOrientation(_yUp ? Matrix4x4.Identity : YUp); Toast(_yUp ? "Y is up" : "Z is up"); }
                break;
            case 'T':
                _theme = Theme.Make(!_theme.Dark);
                Settings.Theme = _theme.Dark ? "dark" : "light";
                ApplyChrome();
                break;
            case 'C':
                if (ctrl) { CopyImage(); break; }
                if (_mesh == null) break;
                if (R.HasColors && _fileColors) { _fileColors = false; Settings.Material = 0; }
                else if (R.HasColors && MaterialIndex == Theme.Materials.Length - 1) { _fileColors = true; Toast("Original colors"); break; }
                else Settings.Material = (MaterialIndex + 1) % Theme.Materials.Length;
                Toast(Theme.Materials[MaterialIndex].Name);
                break;
            default: return;
        }
        Invalidate();
    }
}
