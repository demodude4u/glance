using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace GlanceSetup;

/// <summary>
/// One small window: two ticked boxes and an Install button. Unpacks Glance into the current user's
/// Programs folder, asks Glance to register itself with the chosen options, and adds a Start menu entry.
/// No administrator rights, nothing outside the user's own profile and registry.
/// </summary>
internal static unsafe class Setup
{
    const string U = "user32.dll", G = "gdi32.dll", K = "kernel32.dll", O = "ole32.dll";

    [StructLayout(LayoutKind.Sequential)] struct WNDCLASSEXW
    {
        public uint cbSize, style; public delegate* unmanaged<nint, uint, nuint, nint, nint> lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground; public char* lpszMenuName, lpszClassName; public nint hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int l, t, r, b; }
    [StructLayout(LayoutKind.Sequential)] struct PAINTSTRUCT { public nint hdc; public int erase; public RECT rc; public int restore, incUpdate; public fixed byte reserved[32]; }
    [StructLayout(LayoutKind.Sequential)] struct DRAWITEMSTRUCT { public uint CtlType, CtlID, itemID, itemAction, itemState; public nint hwndItem, hDC; public RECT rcItem; public nuint itemData; }

    [DllImport(K)] static extern nint GetModuleHandleW(char* name);
    [DllImport(U)] static extern ushort RegisterClassExW(WNDCLASSEXW* wc);
    [DllImport(U)] static extern nint CreateWindowExW(uint ex, char* cls, char* title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport(U)] static extern nint DefWindowProcW(nint h, uint m, nuint w, nint l);
    [DllImport(U)] static extern int GetMessageW(MSG* m, nint h, uint min, uint max);
    [DllImport(U)] static extern int TranslateMessage(MSG* m);
    [DllImport(U)] static extern nint DispatchMessageW(MSG* m);
    [DllImport(U)] static extern int IsDialogMessageW(nint h, MSG* m);
    [DllImport(U)] static extern void PostQuitMessage(int code);
    [DllImport(U)] static extern int PostMessageW(nint h, uint m, nuint w, nint l);
    [DllImport(U)] static extern nint SendMessageW(nint h, uint m, nuint w, nint l);
    [DllImport(U)] static extern int ShowWindow(nint h, int cmd);
    [DllImport(U)] static extern int DestroyWindow(nint h);
    [DllImport(U)] static extern int EnableWindow(nint h, int on);
    [DllImport(U)] static extern nint SetFocus(nint h);
    [DllImport(U)] static extern int SetWindowTextW(nint h, char* text);
    [DllImport(U)] static extern int GetWindowTextW(nint h, char* text, int max);
    [DllImport(U)] static extern int InvalidateRect(nint h, RECT* r, int erase);
    [DllImport(U)] static extern nint BeginPaint(nint h, PAINTSTRUCT* ps);
    [DllImport(U)] static extern int EndPaint(nint h, PAINTSTRUCT* ps);
    [DllImport(U)] static extern int GetClientRect(nint h, RECT* r);
    [DllImport(U)] static extern int AdjustWindowRectExForDpi(RECT* r, uint style, int menu, uint ex, uint dpi);
    [DllImport(U)] static extern uint GetDpiForSystem();
    [DllImport(U)] static extern int GetSystemMetrics(int i);
    [DllImport(U)] static extern int FillRect(nint dc, RECT* r, nint brush);
    [DllImport(U)] static extern int DrawTextW(nint dc, char* s, int n, RECT* r, uint fmt);
    [DllImport(U)] static extern nint LoadImageW(nint inst, char* name, uint type, int cx, int cy, uint flags);
    [DllImport(U)] static extern int DrawIconEx(nint dc, int x, int y, nint icon, int cx, int cy, uint step, nint brush, uint flags);
    [DllImport(U)] static extern nint LoadCursorW(nint inst, nint name);
    [DllImport(U)] static extern int MessageBoxW(nint h, char* text, char* caption, uint type);
    [DllImport(G)] static extern nint CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, char* face);
    [DllImport(G)] static extern nint CreateSolidBrush(uint color);
    [DllImport(G)] static extern nint CreatePen(int style, int width, uint color);
    [DllImport(G)] static extern nint SelectObject(nint dc, nint obj);
    [DllImport(G)] static extern int DeleteObject(nint obj);
    [DllImport(G)] static extern uint SetTextColor(nint dc, uint color);
    [DllImport(G)] static extern uint SetBkColor(nint dc, uint color);
    [DllImport(G)] static extern int SetBkMode(nint dc, int mode);
    [DllImport(G)] static extern int RoundRect(nint dc, int l, int t, int r, int b, int w, int h);
    [DllImport(G)] static extern int GetTextFaceW(nint dc, int n, char* face);
    [DllImport(U)] static extern nint GetDC(nint h);
    [DllImport(U)] static extern int ReleaseDC(nint h, nint dc);
    [DllImport(O)] static extern int CoInitializeEx(nint r, uint flags);
    [DllImport(O)] static extern int CoCreateInstance(Guid* clsid, nint outer, uint ctx, Guid* iid, nint* obj);

    // Brand palette (COLORREF is 0x00BBGGRR)
    const uint Ivory = 0xF8F8F8, Charcoal = 0x333333, Slate = 0x908070, Cobalt = 0xAB4700, CobaltDown = 0x8A3900, Amber = 0x07C1FF, White = 0xFFFFFF, Mist = 0xD8D2CC;

    const int IdAssociate = 101, IdMenu = 102, IdGo = 103;
    const uint WM_DONE = 0x8000 + 1;

    static nint _wnd, _associate, _menu, _go, _bg, _icon;
    static nint _fTitle, _fText, _fSmall, _fButton;
    static float _s = 1;
    static int _state;                 // 0 ready, 1 working, 2 installed, 3 failed
    static string _error = "";

    static string Target => Environment.GetEnvironmentVariable("GLANCE_SETUP_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Glance");
    static string Shortcut => Environment.GetEnvironmentVariable("GLANCE_SETUP_LNK")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Glance.lnk");

    static int D(float v) => (int)MathF.Round(v * _s);

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--update")) return Update(args);
            if (args.Contains("--quiet"))        // unattended: no window, both options unless switched off
            {
                Install(!args.Contains("--no-associate"), !args.Contains("--no-menu"));
                return 0;
            }
            return Window();
        }
        catch (Exception ex)
        {
            if (args.Contains("--update")) return 1;                 // a failed self-update stays silent; Glance tries again later
            if (args.Contains("--quiet")) { Console.Error.WriteLine(ex.Message); return 1; }
            fixed (char* t = "Glance could not be installed.\n\n" + ex.Message) fixed (char* c = "Glance Setup") MessageBoxW(0, t, c, 0x10);
            return 1;
        }
    }

    static nint Font(string face, float pt, int weight)
    {
        fixed (char* f = face) return CreateFontW(-D(pt * 96f / 72f), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, f);
    }

    /// The brand face is built in; if it could not be loaded, fall back to the Windows UI face.
    static bool HasFont(string face)
    {
        nint f = Font(face, 10, 400), dc = GetDC(0), old = SelectObject(dc, f);
        char* got = stackalloc char[64];
        GetTextFaceW(dc, 64, got);
        SelectObject(dc, old); ReleaseDC(0, dc); DeleteObject(f);
        return new string(got).Equals(face, StringComparison.OrdinalIgnoreCase);
    }

    static int Window()
    {
        nint inst = GetModuleHandleW(null);
        _s = GetDpiForSystem() / 96f;
        _bg = CreateSolidBrush(Ivory);
        Glance.BundledFonts.Load();
        bool regular = HasFont(Glance.BundledFonts.Regular), medium = HasFont(Glance.BundledFonts.Medium);
        _fTitle = Font(HasFont(Glance.BundledFonts.Light) ? Glance.BundledFonts.Light : "Segoe UI Light", 24, 300);
        _fText = Font(regular ? Glance.BundledFonts.Regular : "Segoe UI", 10.5f, 400);
        _fSmall = Font(regular ? Glance.BundledFonts.Regular : "Segoe UI", 9, 400);
        _fButton = Font(medium ? Glance.BundledFonts.Medium : "Segoe UI Semibold", 10.5f, medium ? 500 : 600);
        if (Environment.GetEnvironmentVariable("GLANCE_SETUP_REPORT") is string report)        // test hook: which faces are in use
            File.WriteAllText(report, $"version={Glance.AppVersion.Number} loaded={Glance.BundledFonts.Loaded} regular={regular} medium={medium} light={HasFont(Glance.BundledFonts.Light)}");
        _icon = LoadImageW(inst, (char*)32512, 1, D(44), D(44), 0);

        const uint style = 0x00C80000 | 0x00020000;        // caption + system menu + minimise box, fixed size
        fixed (char* cls = "GlanceSetup") fixed (char* title = "Glance " + Glance.AppVersion.Number + " Setup") fixed (char* button = "BUTTON")
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &WndProc, hInstance = inst, lpszClassName = cls,
                hCursor = LoadCursorW(0, 32512), hbrBackground = _bg,
                hIcon = LoadImageW(inst, (char*)32512, 1, GetSystemMetrics(11), GetSystemMetrics(12), 0),
                hIconSm = LoadImageW(inst, (char*)32512, 1, GetSystemMetrics(49), GetSystemMetrics(50), 0),
            };
            RegisterClassExW(&wc);

            var r = new RECT { r = D(500), b = D(352) };
            AdjustWindowRectExForDpi(&r, style, 0, 0, GetDpiForSystem());
            int w = r.r - r.l, h = r.b - r.t;
            _wnd = CreateWindowExW(0, cls, title, style, (GetSystemMetrics(0) - w) / 2, (GetSystemMetrics(1) - h) / 2, w, h, 0, 0, inst, 0);

            const uint check = 0x50010003;                 // child, visible, tab stop, auto checkbox
            fixed (char* t = "Open model files with Glance")
                _associate = CreateWindowExW(0, button, t, check | 0x00020000, D(32), D(112), D(436), D(24), _wnd, IdAssociate, inst, 0);
            fixed (char* t = "Add “Open in Glance” to the right-click menu")
                _menu = CreateWindowExW(0, button, t, check, D(32), D(176), D(436), D(24), _wnd, IdMenu, inst, 0);
            fixed (char* t = "Install")
                _go = CreateWindowExW(0, button, t, 0x5001000B, D(336), D(288), D(132), D(36), _wnd, IdGo, inst, 0);   // owner drawn
        }
        foreach (nint c in (nint[])[_associate, _menu])
        {
            SendMessageW(c, 0x0030, (nuint)_fText, 1);      // WM_SETFONT
            SendMessageW(c, 0x00F1, 1, 0);                  // BM_SETCHECK: ticked by default
        }
        SetFocus(_go);
        ShowWindow(_wnd, 5);

        MSG m;
        while (GetMessageW(&m, 0, 0, 0) > 0)
        {
            if (IsDialogMessageW(_wnd, &m) != 0) continue;
            TranslateMessage(&m); DispatchMessageW(&m);
        }
        return _state == 3 ? 1 : 0;
    }

    [UnmanagedCallersOnly]
    static nint WndProc(nint h, uint msg, nuint wp, nint lp)
    {
        try
        {
            switch (msg)
            {
                case 0x000F: Paint(h); return 0;                                    // WM_PAINT
                case 0x0138:                                                        // WM_CTLCOLORSTATIC (tick boxes)
                    SetBkColor((nint)wp, Ivory); SetTextColor((nint)wp, Charcoal); return _bg;
                case 0x002B: DrawButton((DRAWITEMSTRUCT*)lp); return 1;             // WM_DRAWITEM
                case 0x0111:                                                        // WM_COMMAND
                    int id = (int)(wp & 0xFFFF);
                    if (id == IdGo || id == 1) Go();                                // click or Enter
                    else if (id == 2 && _state != 1) DestroyWindow(h);              // Esc
                    return 0;
                case WM_DONE: Done(); return 0;
                case 0x0010: if (_state != 1) DestroyWindow(h); return 0;           // WM_CLOSE: not half way through
                case 0x0002: PostQuitMessage(0); return 0;                          // WM_DESTROY
            }
        }
        catch { }
        return DefWindowProcW(h, msg, wp, lp);
    }

    static void Label(nint dc, string s, nint font, uint color, int x, int y, int w, int h)
    {
        SelectObject(dc, font); SetTextColor(dc, color);
        var r = new RECT { l = D(x), t = D(y), r = D(x + w), b = D(y + h) };
        fixed (char* p = s) DrawTextW(dc, p, s.Length, &r, 0x0810);                 // word break, no prefix
    }

    static void Paint(nint h)
    {
        PAINTSTRUCT ps; nint dc = BeginPaint(h, &ps);
        RECT rc; GetClientRect(h, &rc); FillRect(dc, &rc, _bg);
        SetBkMode(dc, 1);
        if (_icon != 0) DrawIconEx(dc, D(32), D(28), _icon, D(44), D(44), 0, 0, 3);
        Label(dc, "Glance", _fTitle, Charcoal, 90, 20, 380, 44);
        Label(dc, "A fast, simple viewer for STL, OBJ and STEP models", _fSmall, Slate, 92, 60, 380, 20);

        Label(dc, "Double-click an STL, OBJ, STEP or IGES file and it opens in Glance.", _fSmall, Slate, 51, 138, 420, 20);
        Label(dc, "Works whichever app is set to open the file.", _fSmall, Slate, 51, 202, 420, 20);

        (string text, uint color) = _state switch
        {
            1 => ("Installing…", Slate),
            2 => ("Glance is installed. Double-click a model to open it.", Charcoal),
            3 => ("Glance could not be installed. " + _error, Charcoal),
            _ => ("Installs for you only, no administrator rights needed. Keeps itself up to date from GitHub.", Slate),
        };
        if (_state == 2 || _state == 3)
        {
            nint dot = CreateSolidBrush(_state == 2 ? Cobalt : Amber);
            var d = new RECT { l = D(32), t = D(249), r = D(35), b = D(249 + (_state == 3 ? 34 : 17)) };
            FillRect(dc, &d, dot); DeleteObject(dot);
        }
        Label(dc, text, _fSmall, color, _state >= 2 ? 44 : 32, 248, 280, 36);
        EndPaint(h, &ps);
    }

    static void DrawButton(DRAWITEMSTRUCT* d)
    {
        bool down = (d->itemState & 1) != 0, off = (d->itemState & 4) != 0, focus = (d->itemState & 0x10) != 0;
        FillRect(d->hDC, &d->rcItem, _bg);
        nint brush = CreateSolidBrush(off ? Mist : down ? CobaltDown : Cobalt), pen = CreatePen(0, 1, off ? Mist : down ? CobaltDown : Cobalt);
        nint ob = SelectObject(d->hDC, brush), op = SelectObject(d->hDC, pen);
        RoundRect(d->hDC, d->rcItem.l, d->rcItem.t, d->rcItem.r, d->rcItem.b, D(8), D(8));
        if (focus && !off)                                                           // keyboard focus ring, inside the fill
        {
            nint ring = CreatePen(0, 1, White), hollow = SelectObject(d->hDC, GetStockObject(5));
            SelectObject(d->hDC, ring);
            RoundRect(d->hDC, d->rcItem.l + D(3), d->rcItem.t + D(3), d->rcItem.r - D(3), d->rcItem.b - D(3), D(5), D(5));
            SelectObject(d->hDC, hollow); SelectObject(d->hDC, pen); DeleteObject(ring);
        }
        SelectObject(d->hDC, ob); SelectObject(d->hDC, op); DeleteObject(brush); DeleteObject(pen);

        char* text = stackalloc char[64]; int n = GetWindowTextW(d->hwndItem, text, 64);
        SetBkMode(d->hDC, 1); SetTextColor(d->hDC, off ? Slate : White); SelectObject(d->hDC, _fButton);
        var r = d->rcItem; DrawTextW(d->hDC, text, n, &r, 0x25);                     // centred, single line
    }
    [DllImport(G)] static extern nint GetStockObject(int i);

    static void SetText(nint h, string s) { fixed (char* p = s) SetWindowTextW(h, p); InvalidateRect(h, null, 1); }

    static void Go()
    {
        if (_state >= 2) { DestroyWindow(_wnd); return; }
        if (_state == 1) return;
        bool associate = SendMessageW(_associate, 0x00F0, 0, 0) == 1, menu = SendMessageW(_menu, 0x00F0, 0, 0) == 1;   // BM_GETCHECK
        _state = 1;
        EnableWindow(_associate, 0); EnableWindow(_menu, 0); EnableWindow(_go, 0);
        SetText(_go, "Installing…"); InvalidateRect(_wnd, null, 1);
        new Thread(() =>
        {
            try { Install(associate, menu); }
            catch (Exception ex) { _error = ex.Message; }
            PostMessageW(_wnd, WM_DONE, 0, 0);
        }) { IsBackground = true }.Start();
    }

    static void Done()
    {
        _state = _error.Length == 0 ? 2 : 3;
        SetText(_go, _state == 2 ? "Done" : "Close");
        EnableWindow(_go, 1); SetFocus(_go);
        InvalidateRect(_wnd, null, 1);
    }

    // ------------------------------------------------------------------ the actual work

    static bool Running(string dir, string name)
    {
        foreach (var p in Process.GetProcessesByName(name))
            try { if (!p.HasExited && p.MainModule!.FileName.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)) return true; } catch { }
        return false;       // a process that has exited can linger in the list while something still holds its handle
    }

    /// <summary>
    /// Glance started us as it closed, to replace itself with this newer version. Nobody is watching, so: no window,
    /// never close a Glance that is still open (try again next time instead), and keep the choices made at install.
    /// </summary>
    static int Update(string[] args)
    {
        try { return Replace(args); }
        finally { Reopen(args); }
    }

    /// <summary>
    /// "--reopen [file]": the person pressed Update in a Glance window, which then closed. Whatever happened to the
    /// update, give them their window back, on the file they were looking at.
    /// </summary>
    static void Reopen(string[] args)
    {
        try
        {
            int at = Array.IndexOf(args, "--reopen");
            string exe = Path.Combine(Target, "Glance.exe");
            if (at < 0 || !File.Exists(exe)) return;
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            if (at + 1 < args.Length && !args[at + 1].StartsWith("--") && File.Exists(args[at + 1])) start.ArgumentList.Add(args[at + 1]);
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) { Log("could not reopen Glance: " + ex.Message); }
    }

    static int Replace(string[] args)
    {
        int at = Array.IndexOf(args, "--wait-pid");
        if (at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out int pid))
            try { using var parent = Process.GetProcessById(pid); parent.WaitForExit(15000); } catch { }      // already gone
        for (int i = 0; i < 20 && Running(Target, "glance-step"); i++) Thread.Sleep(250);
        if (Running(Target, "Glance") || Running(Target, "glance-step"))
        {
            string who = "";
            foreach (string name in (string[])["Glance", "glance-step"])
                foreach (var p in Process.GetProcessesByName(name))
                    try { who += $" {name}:{p.Id}:{(p.HasExited ? "exited" : "alive")}:{p.MainModule!.FileName}"; } catch (Exception ex) { who += $" {name}:{p.Id}:?{ex.GetType().Name}"; }
            Log("Glance is still open; will try again next time (waited for " + string.Join(" ", args) + "; saw" + who + ")");
            return 2;
        }
        try { Install(true, true, keep: true); }
        catch (Exception ex) { Log("update failed: " + ex.Message); throw; }
        Log("updated to " + Glance.AppVersion.Number);
        return 0;
    }

    /// <summary>One line per attempt, next to the downloaded installer (Glance's update folder).</summary>
    static void Log(string what)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "update.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  Setup {Glance.AppVersion.Number}: {what}\r\n"); } catch { }
    }

    static void Install(bool associate, bool menu, bool keep = false)
    {
        string dir = Target;

        // Glance may be open, or still reading ahead in the background; it has to let go of its files.
        if (!keep)
        foreach (string name in (string[])["Glance", "glance-step"])
            foreach (var p in Process.GetProcessesByName(name))
                try { if (p.MainModule!.FileName.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(3000); } } catch { }

        for (int attempt = 0; Directory.Exists(dir); attempt++)
        {
            try { Directory.Delete(dir, true); }
            catch when (attempt < 10) { Thread.Sleep(200); }
        }
        Directory.CreateDirectory(dir);
        using (var zip = typeof(Setup).Assembly.GetManifestResourceStream("payload.zip") ?? throw new InvalidOperationException("This setup file is incomplete."))
            ZipFile.ExtractToDirectory(zip, dir, true);

        string exe = Path.Combine(dir, "Glance.exe");
        var reg = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        reg.ArgumentList.Add("--register");
        if (keep) reg.ArgumentList.Add("--keep");
        else
        {
            if (!associate) reg.ArgumentList.Add("--no-associate");
            if (!menu) reg.ArgumentList.Add("--no-menu");
        }
        using (var p = Process.Start(reg)!)
        {
            if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } throw new InvalidOperationException("Registering the file types did not finish."); }
            if (p.ExitCode != 0) throw new InvalidOperationException("Registering the file types failed.");
        }

        try { Link(Shortcut, exe, "A fast, simple viewer for STL, OBJ and STEP models"); } catch { }    // a missing Start menu entry is not fatal
    }

    /// Writes a .lnk through IShellLinkW / IPersistFile (raw COM: no runtime marshalling under native AOT).
    static void Link(string lnk, string target, string description)
    {
        CoInitializeEx(0, 2);
        Guid clsid = new("00021401-0000-0000-C000-000000000046"), iid = new("000214F9-0000-0000-C000-000000000046"), ipf = new("0000010b-0000-0000-C000-000000000046");
        nint link;
        if (CoCreateInstance(&clsid, 0, 1, &iid, &link) != 0) return;
        void** v = *(void***)link;
        fixed (char* p = target) ((delegate* unmanaged[Stdcall]<nint, char*, int>)v[20])(link, p);                      // SetPath
        fixed (char* p = description) ((delegate* unmanaged[Stdcall]<nint, char*, int>)v[7])(link, p);                  // SetDescription
        fixed (char* p = Path.GetDirectoryName(target)) ((delegate* unmanaged[Stdcall]<nint, char*, int>)v[9])(link, p); // SetWorkingDirectory
        nint file;
        if (((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)v[0])(link, &ipf, &file) == 0)
        {
            void** f = *(void***)file;
            Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
            fixed (char* p = lnk) ((delegate* unmanaged[Stdcall]<nint, char*, int, int>)f[6])(file, p, 1);              // Save
            ((delegate* unmanaged[Stdcall]<nint, uint>)f[2])(file);
        }
        ((delegate* unmanaged[Stdcall]<nint, uint>)v[2])(link);
    }
}
