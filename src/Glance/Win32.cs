using System.Runtime.InteropServices;

namespace Glance;

internal static unsafe class Win32
{
    public const uint WM_DESTROY = 0x0002, WM_SIZE = 0x0005, WM_PAINT = 0x000F, WM_CLOSE = 0x0010,
        WM_ERASEBKGND = 0x0014, WM_SETTINGCHANGE = 0x001A, WM_SETCURSOR = 0x0020, WM_GETMINMAXINFO = 0x0024,
        WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_MOUSEMOVE = 0x0200,
        WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203,
        WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208,
        WM_MOUSEWHEEL = 0x020A, WM_CAPTURECHANGED = 0x0215, WM_DROPFILES = 0x0233, WM_DPICHANGED = 0x02E0,
        WM_QUIT = 0x0012, WM_APP = 0x8000;

    public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000;
    public const uint CS_OWNDC = 0x0020, CS_DBLCLKS = 0x0008;
    public const int SW_SHOW = 5, SW_SHOWMAXIMIZED = 3, SW_SHOWNOACTIVATE = 4;
    public const uint PM_REMOVE = 1;
    public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11;

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd; public uint message; public nuint wParam; public nint lParam;
        public uint time; public int ptX, ptY; public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int W, H; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OPENFILENAMEW
    {
        public uint lStructSize; public nint hwndOwner, hInstance;
        public char* lpstrFilter, lpstrCustomFilter; public uint nMaxCustFilter, nFilterIndex;
        public char* lpstrFile; public uint nMaxFile;
        public char* lpstrFileTitle; public uint nMaxFileTitle;
        public char* lpstrInitialDir, lpstrTitle; public uint Flags;
        public ushort nFileOffset, nFileExtension; public char* lpstrDefExt;
        public nint lCustData, lpfnHook; public char* lpTemplateName;
        public nint pvReserved; public uint dwReserved, FlagsEx;
    }

    const string U = "user32", G = "gdi32", K = "kernel32";

    [DllImport(K)] public static extern nint GetModuleHandleW(char* name);
    [DllImport(K)] public static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport(K)] public static extern void* GlobalLock(nint h);
    [DllImport(K)] public static extern int GlobalUnlock(nint h);

    [DllImport(U)] public static extern ushort RegisterClassExW(WNDCLASSEXW* wc);
    [DllImport(U)] public static extern nint CreateWindowExW(uint ex, char* cls, char* title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport(U)] public static extern nint DefWindowProcW(nint hwnd, uint msg, nuint wp, nint lp);
    [DllImport(U)] public static extern int ShowWindow(nint hwnd, int cmd);
    [DllImport(U)] public static extern int GetMessageW(MSG* msg, nint hwnd, uint min, uint max);
    [DllImport(U)] public static extern int PeekMessageW(MSG* msg, nint hwnd, uint min, uint max, uint remove);
    [DllImport(U)] public static extern int TranslateMessage(MSG* msg);
    [DllImport(U)] public static extern nint DispatchMessageW(MSG* msg);
    [DllImport(U)] public static extern int PostMessageW(nint hwnd, uint msg, nuint wp, nint lp);
    [DllImport(U)] public static extern void PostQuitMessage(int code);
    [DllImport(U)] public static extern int DestroyWindow(nint hwnd);
    [DllImport(U)] public static extern nint GetDC(nint hwnd);
    [DllImport(U)] public static extern int GetClientRect(nint hwnd, RECT* r);
    [DllImport(U)] public static extern int GetWindowRect(nint hwnd, RECT* r);
    [DllImport(U)] public static extern int SetWindowTextW(nint hwnd, char* text);
    [DllImport(U)] public static extern nint LoadCursorW(nint inst, nint name);
    [DllImport(U)] public static extern nint SetCursor(nint cursor);
    [DllImport(U)] public static extern nint LoadImageW(nint inst, nint name, uint type, int cx, int cy, uint flags);
    [DllImport(U)] public static extern nint SetCapture(nint hwnd);
    [DllImport(U)] public static extern int ReleaseCapture();
    [DllImport(U)] public static extern short GetKeyState(int vk);
    [DllImport(U)] public static extern int GetCursorPos(POINT* p);
    [DllImport(U)] public static extern int ScreenToClient(nint hwnd, POINT* p);
    [DllImport(U)] public static extern nint MonitorFromPoint(POINT p, uint flags);
    [DllImport(U)] public static extern int GetMonitorInfoW(nint mon, MONITORINFO* mi);
    [DllImport(U)] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport(U)] public static extern uint GetDpiForSystem();
    [DllImport(U)] public static extern int SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport(U)] public static extern int ValidateRect(nint hwnd, RECT* r);
    [DllImport(U)] public static extern int IsZoomed(nint hwnd);
    [DllImport(U)] public static extern int IsIconic(nint hwnd);
    [DllImport(U)] public static extern int OpenClipboard(nint hwnd);
    [DllImport(U)] public static extern int EmptyClipboard();
    [DllImport(U)] public static extern nint SetClipboardData(uint fmt, nint h);
    [DllImport(U)] public static extern int CloseClipboard();
    [DllImport(U)] public static extern int MessageBoxW(nint hwnd, char* text, char* caption, uint type);

    [DllImport(G)] public static extern int ChoosePixelFormat(nint hdc, byte* pfd);
    [DllImport(G)] public static extern int SetPixelFormat(nint hdc, int fmt, byte* pfd);
    [DllImport(G)] public static extern int SwapBuffers(nint hdc);
    [DllImport(G)] public static extern nint CreateCompatibleDC(nint hdc);
    [DllImport(G)] public static extern nint CreateDIBSection(nint hdc, BITMAPINFOHEADER* bmi, uint usage, void** bits, nint section, uint offset);
    [DllImport(G)] public static extern nint SelectObject(nint hdc, nint obj);
    [DllImport(G)] public static extern int DeleteObject(nint obj);
    [DllImport(G)] public static extern int DeleteDC(nint hdc);
    [DllImport(G)] public static extern nint CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, char* face);
    [DllImport(G)] public static extern uint SetTextColor(nint hdc, uint color);
    [DllImport(G)] public static extern int SetBkMode(nint hdc, int mode);
    [DllImport(G)] public static extern int TextOutW(nint hdc, int x, int y, char* s, int n);
    [DllImport(G)] public static extern int GetTextExtentPoint32W(nint hdc, char* s, int n, SIZE* size);
    [DllImport(G)] public static extern int GetTextMetricsW(nint hdc, int* tm);
    [DllImport(G)] public static extern int GetTextFaceW(nint hdc, int n, char* name);
    [DllImport(G)] public static extern int GdiFlush();
    [DllImport(G)] public static extern nint CreateSolidBrush(uint color);

    [DllImport("opengl32")] public static extern nint wglCreateContext(nint hdc);
    [DllImport("opengl32")] public static extern int wglMakeCurrent(nint hdc, nint rc);
    [DllImport("opengl32")] public static extern nint wglGetProcAddress(byte* name);

    [DllImport("dwmapi")] public static extern int DwmSetWindowAttribute(nint hwnd, uint attr, void* value, uint size);

    [DllImport("shell32")] public static extern void DragAcceptFiles(nint hwnd, int accept);
    [DllImport("shell32")] public static extern uint DragQueryFileW(nint drop, uint index, char* file, uint cch);
    [DllImport("shell32")] public static extern void DragFinish(nint drop);
    [DllImport("shell32")] public static extern void SHChangeNotify(int evt, uint flags, nint a, nint b);
    [DllImport("shlwapi")] public static extern int StrCmpLogicalW(char* a, char* b);
    [DllImport("comdlg32")] public static extern int GetOpenFileNameW(OPENFILENAMEW* ofn);
    [DllImport("comdlg32")] public static extern int GetSaveFileNameW(OPENFILENAMEW* ofn);

    public static int LoWord(nint v) => (short)(v & 0xFFFF);
    public static int HiWord(nint v) => (short)((v >> 16) & 0xFFFF);
    public static bool KeyDown(int vk) => (GetKeyState(vk) & 0x8000) != 0;
}
