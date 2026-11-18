using System.Runtime.InteropServices;
using System.Text;

namespace Glance;

/// <summary>
/// The thumbnail handler Explorer loads: an in-process COM server with one class per file format, each giving out
/// objects that implement IThumbnailProvider and the three ways Windows may hand a file over (IInitializeWithStream,
/// IInitializeWithFile, IInitializeWithItem).
///
/// Where it runs. Windows loads a thumbnail handler into a helper process of its own (dllhost.exe, "COM Surrogate"),
/// not into Explorer, and in that process the only way a file arrives is as a stream of bytes: no name, no folder.
/// (The other two interfaces are there for programs that load the handler directly and do pass a path.)
///
/// What it does. As little as possible. It copies the stream into shared memory, starts glance-thumb.exe on it inside
/// a job (a memory limit, and the job dies with this process), waits no longer than the format's time budget, and
/// turns the pixels that come back into a bitmap. Reading the model happens in that other process, so no file,
/// however large or damaged, can crash, stall or bloat the process Windows lent us. Any failure is answered with an
/// error code, which makes Explorer show the ordinary icon.
///
/// Native AOT has no COM marshalling, so the objects are laid out by hand, as in Updater.cs: a COM object is a
/// pointer to a table of functions. A native AOT library cannot be unloaded, so DllCanUnloadNow always says no;
/// the helper process Windows uses for thumbnails goes away by itself when it has been idle for a while.
/// </summary>
internal static unsafe class ThumbShell
{
    const string K = "kernel32.dll";

    [StructLayout(LayoutKind.Sequential)] struct STARTUPINFOW
    {
        public uint cb; public char* reserved, desktop, title; public uint x, y, xSize, ySize, xChars, yChars, fill, flags;
        public ushort show, reserved2; public byte* reserved3; public nint stdIn, stdOut, stdErr;
    }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public nint process, thread; public uint pid, tid; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER
    {
        public uint size; public int width, height; public ushort planes, bitCount; public uint compression, sizeImage;
        public int xPels, yPels; public uint used, important;
    }

    [DllImport(K)] static extern nint CreateFileMappingW(nint file, nint security, uint protect, uint high, uint low, char* name);
    [DllImport(K)] static extern byte* MapViewOfFile(nint map, uint access, uint high, uint low, nuint bytes);
    [DllImport(K)] static extern int UnmapViewOfFile(void* view);
    [DllImport(K)] static extern int CloseHandle(nint handle);
    [DllImport(K)] static extern int CreateProcessW(char* app, char* commandLine, nint pa, nint ta, int inherit, uint flags, nint env, char* dir, STARTUPINFOW* si, PROCESS_INFORMATION* pi);
    [DllImport(K)] static extern uint WaitForSingleObject(nint handle, uint ms);
    [DllImport(K)] static extern int TerminateProcess(nint process, uint code);
    [DllImport(K)] static extern int GetExitCodeProcess(nint process, uint* code);
    [DllImport(K)] static extern uint ResumeThread(nint thread);
    [DllImport(K)] static extern nint CreateJobObjectW(nint security, char* name);
    [DllImport(K)] static extern int SetInformationJobObject(nint job, int kind, void* info, uint size);
    [DllImport(K)] static extern int AssignProcessToJobObject(nint job, nint process);
    [DllImport(K)] static extern int TerminateJobObject(nint job, uint code);
    [DllImport(K)] static extern nint CreateSemaphoreW(nint security, int initial, int maximum, char* name);
    [DllImport(K)] static extern int ReleaseSemaphore(nint semaphore, int count, int* previous);
    [DllImport(K)] static extern int GetModuleHandleExW(uint flags, void* address, nint* module);
    [DllImport(K)] static extern uint GetModuleFileNameW(nint module, char* file, uint size);
    [DllImport(K)] static extern uint GetEnvironmentVariableW(char* name, char* value, uint size);
    [DllImport(K)] static extern uint GetCurrentProcessId();
    [DllImport(K)] static extern ulong GetTickCount64();
    [DllImport(K)] static extern uint GetFileAttributesW(char* file);
    [DllImport(K)] static extern nint CreateFileW(char* file, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport(K)] static extern int GetFileSizeEx(nint file, long* size);
    [DllImport(K)] static extern int WriteFile(nint file, byte* data, uint count, uint* written, nint overlapped);
    [DllImport(K)] static extern void GetLocalTime(ushort* systemTime);
    [DllImport("gdi32.dll")] static extern nint CreateDIBSection(nint dc, BITMAPINFOHEADER* info, uint usage, void** bits, nint section, uint offset);
    [DllImport("ole32.dll")] static extern void CoTaskMemFree(void* p);

    const int S_OK = 0, S_FALSE = 1;
    const int E_NOINTERFACE = unchecked((int)0x80004002), E_POINTER = unchecked((int)0x80004003), E_FAIL = unchecked((int)0x80004005),
        E_UNEXPECTED = unchecked((int)0x8000FFFF), E_OUTOFMEMORY = unchecked((int)0x8007000E), E_ALREADY = unchecked((int)0x800704DF),
        CLASS_E_NOAGGREGATION = unchecked((int)0x80040110), CLASS_E_CLASSNOTAVAILABLE = unchecked((int)0x80040111);

    static readonly Guid IUnknown = new(0x00000000, 0x0000, 0x0000, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46),
        IClassFactory = new(0x00000001, 0x0000, 0x0000, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46),
        IThumbnailProvider = new(0xE357FCCD, 0xA995, 0x4576, 0xB0, 0x1F, 0x23, 0x46, 0x30, 0x15, 0x4E, 0x96),
        IInitializeWithStream = new(0xB824B49D, 0x22AC, 0x4161, 0xAC, 0x8A, 0x99, 0x16, 0xE8, 0xFA, 0x3F, 0x7F),
        IInitializeWithFile = new(0xB7D14566, 0x0509, 0x4CCE, 0xA7, 0x1F, 0x0A, 0x55, 0x42, 0x33, 0xBD, 0x9B),
        IInitializeWithItem = new(0x7F73BE3F, 0xFB79, 0x493C, 0xA6, 0xC7, 0x7E, 0xE1, 0x4E, 0x24, 0x58, 0x41);

    /// <summary>A class object: its function table, and which format it stands for. There is one per format, for good.</summary>
    [StructLayout(LayoutKind.Sequential)] struct Factory { public void** Table; public int Format; }

    /// <summary>
    /// A handler object. The first four fields are its four interfaces: a caller holding interface number k holds
    /// the address of field k, which is how the functions find their way back to the object.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)] struct Handler
    {
        public void** Thumbnail, WithStream, WithFile, WithItem;
        public int Refs, Format;
        public nint Stream;             // what IInitializeWithStream gave us, until it has been read
        public nint Map;                // the file's bytes in shared memory, once read
        public long Bytes, Serial;
        public char* Path;              // what IInitializeWithFile or IInitializeWithItem gave us
    }

    static readonly object Gate = new();
    static volatile bool _ready;
    static Factory* _factories;
    static void** _thumbnail, _withStream, _withFile, _withItem;
    static string _dir = "";            // where this DLL was when it was loaded, and so where the helper is
    static nint _turns;                 // at most two helpers at a time
    static long _serial;

    // ───────────────────────── The two exports ─────────────────────────

    [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject")]
    static int DllGetClassObject(Guid* clsid, Guid* iid, nint* obj)
    {
        try
        {
            if (obj == null) return E_POINTER;
            *obj = 0;
            if (clsid == null || iid == null) return E_POINTER;
            Setup();
            Guid asked = *clsid;
            int format = ((byte*)&asked)[15];
            ((byte*)&asked)[15] = 0;
            if (asked != new Guid(ThumbTypes.Clsid(0)) || format >= ThumbTypes.Formats.Length) return CLASS_E_CLASSNOTAVAILABLE;
            if (*iid != IUnknown && *iid != IClassFactory) return E_NOINTERFACE;
            *obj = (nint)(_factories + format);
            return S_OK;
        }
        catch { return E_FAIL; }
    }

    [UnmanagedCallersOnly(EntryPoint = "DllCanUnloadNow")]
    static int DllCanUnloadNow() => S_FALSE;

    static void Setup()
    {
        if (_ready) return;
        lock (Gate)
        {
            if (_ready) return;
            void** f = Table(5);
            f[0] = (delegate* unmanaged<nint, Guid*, nint*, int>)&FactoryQuery;
            f[1] = (delegate* unmanaged<nint, uint>)&FactoryRef;
            f[2] = (delegate* unmanaged<nint, uint>)&FactoryRef;
            f[3] = (delegate* unmanaged<nint, nint, Guid*, nint*, int>)&CreateInstance;
            f[4] = (delegate* unmanaged<nint, int, int>)&LockServer;
            int n = ThumbTypes.Formats.Length;
            _factories = (Factory*)NativeMemory.AllocZeroed((nuint)n, (nuint)sizeof(Factory));
            for (int i = 0; i < n; i++) { _factories[i].Table = f; _factories[i].Format = i; }

            _thumbnail = Table(4);
            _thumbnail[0] = (delegate* unmanaged<nint, Guid*, nint*, int>)&Query0;
            _thumbnail[1] = (delegate* unmanaged<nint, uint>)&AddRef0;
            _thumbnail[2] = (delegate* unmanaged<nint, uint>)&Release0;
            _thumbnail[3] = (delegate* unmanaged<nint, uint, nint*, int*, int>)&GetThumbnail;
            _withStream = Table(4);
            _withStream[0] = (delegate* unmanaged<nint, Guid*, nint*, int>)&Query1;
            _withStream[1] = (delegate* unmanaged<nint, uint>)&AddRef1;
            _withStream[2] = (delegate* unmanaged<nint, uint>)&Release1;
            _withStream[3] = (delegate* unmanaged<nint, nint, uint, int>)&InitializeWithStream;
            _withFile = Table(4);
            _withFile[0] = (delegate* unmanaged<nint, Guid*, nint*, int>)&Query2;
            _withFile[1] = (delegate* unmanaged<nint, uint>)&AddRef2;
            _withFile[2] = (delegate* unmanaged<nint, uint>)&Release2;
            _withFile[3] = (delegate* unmanaged<nint, char*, uint, int>)&InitializeWithFile;
            _withItem = Table(4);
            _withItem[0] = (delegate* unmanaged<nint, Guid*, nint*, int>)&Query3;
            _withItem[1] = (delegate* unmanaged<nint, uint>)&AddRef3;
            _withItem[2] = (delegate* unmanaged<nint, uint>)&Release3;
            _withItem[3] = (delegate* unmanaged<nint, nint, uint, int>)&InitializeWithItem;

            nint module;
            char* file = stackalloc char[1024];
            if (GetModuleHandleExW(6, (delegate* unmanaged<int>)&DllCanUnloadNow, &module) != 0)       // from an address inside this DLL, without holding it
            {
                uint len = GetModuleFileNameW(module, file, 1024);
                string full = new(file, 0, (int)len);
                int slash = full.LastIndexOf('\\');
                _dir = slash > 0 ? full[..slash] : "";
            }
            _turns = CreateSemaphoreW(0, 2, 2, null);
            _ready = true;
        }
    }

    static void** Table(int entries) => (void**)NativeMemory.AllocZeroed((nuint)entries, (nuint)sizeof(void*));

    // ───────────────────────── The class objects ─────────────────────────

    [UnmanagedCallersOnly] static int FactoryQuery(nint self, Guid* iid, nint* obj)
    {
        if (obj == null) return E_POINTER;
        if (iid != null && (*iid == IUnknown || *iid == IClassFactory)) { *obj = self; return S_OK; }
        *obj = 0; return E_NOINTERFACE;
    }
    [UnmanagedCallersOnly] static uint FactoryRef(nint self) => 1;          // never freed, so there is nothing to count
    [UnmanagedCallersOnly] static int LockServer(nint self, int locked) => S_OK;

    [UnmanagedCallersOnly] static int CreateInstance(nint self, nint outer, Guid* iid, nint* obj)
    {
        try
        {
            if (obj == null) return E_POINTER;
            *obj = 0;
            if (iid == null) return E_POINTER;
            if (outer != 0) return CLASS_E_NOAGGREGATION;
            var h = (Handler*)NativeMemory.AllocZeroed((nuint)sizeof(Handler));
            h->Thumbnail = _thumbnail; h->WithStream = _withStream; h->WithFile = _withFile; h->WithItem = _withItem;
            h->Refs = 1; h->Format = ((Factory*)self)->Format;
            int hr = Query(h, iid, obj);
            Release(h);                                                     // the caller's reference, if it got one, keeps it alive
            return hr;
        }
        catch (OutOfMemoryException) { return E_OUTOFMEMORY; }
        catch { return E_FAIL; }
    }

    // ───────────────────────── IUnknown, once for each of the four interfaces ─────────────────────────

    static Handler* Of(nint self, int k) => (Handler*)(self - k * sizeof(nint));

    static int Query(Handler* h, Guid* iid, nint* obj)
    {
        if (obj == null) return E_POINTER;
        *obj = 0;
        if (iid == null) return E_POINTER;
        Guid g = *iid;
        int k = g == IUnknown || g == IThumbnailProvider ? 0 : g == IInitializeWithStream ? 1 : g == IInitializeWithFile ? 2 : g == IInitializeWithItem ? 3 : -1;
        if (k < 0) return E_NOINTERFACE;
        Interlocked.Increment(ref h->Refs);
        *obj = (nint)h + k * sizeof(nint);
        return S_OK;
    }

    static uint Release(Handler* h)
    {
        int left = Interlocked.Decrement(ref h->Refs);
        if (left == 0)
        {
            try { LetGo(h); } catch { }
            if (h->Map != 0) CloseHandle(h->Map);
            if (h->Path != null) NativeMemory.Free(h->Path);
            NativeMemory.Free(h);
        }
        return (uint)left;
    }

    /// <summary>Gives the stream back, which is what closes the file.</summary>
    static void LetGo(Handler* h)
    {
        nint s = h->Stream;
        if (s == 0) return;
        h->Stream = 0;
        ((delegate* unmanaged<nint, uint>)(*(void***)s)[2])(s);
    }

    [UnmanagedCallersOnly] static int Query0(nint self, Guid* iid, nint* obj) => Query(Of(self, 0), iid, obj);
    [UnmanagedCallersOnly] static int Query1(nint self, Guid* iid, nint* obj) => Query(Of(self, 1), iid, obj);
    [UnmanagedCallersOnly] static int Query2(nint self, Guid* iid, nint* obj) => Query(Of(self, 2), iid, obj);
    [UnmanagedCallersOnly] static int Query3(nint self, Guid* iid, nint* obj) => Query(Of(self, 3), iid, obj);
    [UnmanagedCallersOnly] static uint AddRef0(nint self) => (uint)Interlocked.Increment(ref Of(self, 0)->Refs);
    [UnmanagedCallersOnly] static uint AddRef1(nint self) => (uint)Interlocked.Increment(ref Of(self, 1)->Refs);
    [UnmanagedCallersOnly] static uint AddRef2(nint self) => (uint)Interlocked.Increment(ref Of(self, 2)->Refs);
    [UnmanagedCallersOnly] static uint AddRef3(nint self) => (uint)Interlocked.Increment(ref Of(self, 3)->Refs);
    [UnmanagedCallersOnly] static uint Release0(nint self) => Release(Of(self, 0));
    [UnmanagedCallersOnly] static uint Release1(nint self) => Release(Of(self, 1));
    [UnmanagedCallersOnly] static uint Release2(nint self) => Release(Of(self, 2));
    [UnmanagedCallersOnly] static uint Release3(nint self) => Release(Of(self, 3));

    // ───────────────────────── Being handed a file ─────────────────────────

    static bool Initialized(Handler* h) => h->Stream != 0 || h->Map != 0 || h->Path != null;

    [UnmanagedCallersOnly] static int InitializeWithStream(nint self, nint stream, uint mode)
    {
        Handler* h = Of(self, 1);
        if (stream == 0) return E_POINTER;
        if (Initialized(h)) return E_ALREADY;
        ((delegate* unmanaged<nint, uint>)(*(void***)stream)[1])(stream);           // AddRef
        h->Stream = stream;
        return S_OK;
    }

    [UnmanagedCallersOnly] static int InitializeWithFile(nint self, char* path, uint mode)
    {
        try
        {
            Handler* h = Of(self, 2);
            if (path == null) return E_POINTER;
            if (Initialized(h)) return E_ALREADY;
            return Keep(h, path);
        }
        catch { return E_FAIL; }
    }

    [UnmanagedCallersOnly] static int InitializeWithItem(nint self, nint item, uint mode)
    {
        try
        {
            Handler* h = Of(self, 3);
            if (item == 0) return E_POINTER;
            if (Initialized(h)) return E_ALREADY;
            char* path = null;
            int hr = ((delegate* unmanaged<nint, uint, char**, int>)(*(void***)item)[5])(item, 0x80058000, &path);     // IShellItem::GetDisplayName, as a file path
            if (hr < 0 || path == null) return hr < 0 ? hr : E_FAIL;
            hr = Keep(h, path);
            CoTaskMemFree(path);
            return hr;
        }
        catch { return E_FAIL; }
    }

    static int Keep(Handler* h, char* path)
    {
        int n = 0;
        while (path[n] != 0) { if (++n > 32000) return E_FAIL; }
        if (n == 0) return E_FAIL;
        char* copy = (char*)NativeMemory.Alloc((nuint)(n + 1), sizeof(char));
        Buffer.MemoryCopy(path, copy, (n + 1) * sizeof(char), (n + 1) * sizeof(char));
        h->Path = copy;
        return S_OK;
    }

    // ───────────────────────── IThumbnailProvider ─────────────────────────

    [UnmanagedCallersOnly] static int GetThumbnail(nint self, uint cx, nint* bitmap, int* alphaType)
    {
        try
        {
            if (bitmap == null || alphaType == null) return E_POINTER;
            *bitmap = 0; *alphaType = 0;
            return Picture(Of(self, 0), cx, bitmap, alphaType);
        }
        catch (OutOfMemoryException) { return E_OUTOFMEMORY; }
        catch { return E_FAIL; }
    }

    static int Picture(Handler* h, uint cx, nint* bitmap, int* alphaType)
    {
        var format = ThumbTypes.Formats[h->Format];
        int px = (int)Math.Clamp(cx, 16u, (uint)ThumbTypes.MaxPixels);
        ulong began = GetTickCount64();
        nint outMap = 0, process = 0, thread = 0, job = 0;
        byte* view = null;
        bool turn = false, inJob = false;
        int code = -1, w = 0, h2 = 0;
        int budget = format.BudgetMs;
        if (Env("GLANCE_TEST_THUMB_MS") is string given && int.TryParse(given, out int ms) && ms > 0) budget = ms;      // test hook
        string note = "";
        try
        {
            string source;
            if (h->Path != null) source = "file:" + new string(h->Path);
            else
            {
                if (h->Map == 0 && !Take(h, format.MaxBytes, out note)) return E_FAIL;
                source = "map:" + MapName(h->Serial, "in") + ":" + h->Bytes;
            }

            long serial = Interlocked.Increment(ref _serial);
            long room = 32 + (long)px * px * 4;
            fixed (char* name = MapName(serial, "out")) outMap = CreateFileMappingW(-1, 0, 4, 0, (uint)room, name);
            if (outMap == 0) { note = "no memory for the picture"; return E_OUTOFMEMORY; }
            view = MapViewOfFile(outMap, 4, 0, 0, (nuint)room);                 // FILE_MAP_READ
            if (view == null) { note = "no memory for the picture"; return E_OUTOFMEMORY; }

            // Explorer may ask for several thumbnails at once; two helpers at a time is as much as a PC should give them.
            if (WaitForSingleObject(_turns, 30000) != 0) { note = "waited too long for a turn"; return E_FAIL; }
            turn = true;

            string helper = _dir + "\\" + ThumbTypes.Helper;
            string command = $"\"{helper}\" {ThumbTypes.Protocol} {format.Format} {px} {budget} \"{source}\" \"map:{MapName(serial, "out")}\"";
            var si = new STARTUPINFOW { cb = (uint)sizeof(STARTUPINFOW), flags = 0x81, show = 0 };      // hidden, and no "busy" mouse pointer
            PROCESS_INFORMATION pi;
            // Suspended until it is in its job; no window; below normal priority, so thumbnails never slow down work.
            const uint flags = 0x00000004 | 0x08000000 | 0x00004000;
            fixed (char* app = helper) fixed (char* dir = _dir) fixed (char* text = command)
            {
                char* line = stackalloc char[command.Length + 1];               // CreateProcessW may write to it
                Buffer.MemoryCopy(text, line, (command.Length + 1) * sizeof(char), (command.Length + 1) * sizeof(char));
                if (CreateProcessW(app, line, 0, 0, 0, flags, 0, dir, &si, &pi) == 0) { note = "the helper could not be started"; return E_FAIL; }
            }
            process = pi.process; thread = pi.thread;

            // The job: a ceiling on memory for the helper and anything it starts, no crash dialogs, and nothing outlives us.
            job = CreateJobObjectW(0, null);
            if (job != 0)
            {
                byte* limits = stackalloc byte[144];                            // JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                new Span<byte>(limits, 144).Clear();
                *(uint*)(limits + 16) = 0x2000 | 0x0400 | 0x0200;               // kill on close, die on unhandled exception, job memory
                *(ulong*)(limits + 120) = (ulong)ThumbTypes.JobMemory;
                inJob = SetInformationJobObject(job, 9, limits, 144) != 0 && AssignProcessToJobObject(job, process) != 0;
            }
            ResumeThread(thread);

            if (WaitForSingleObject(process, (uint)budget + 1500) != 0)
            {
                if (job != 0) TerminateJobObject(job, 3);
                TerminateProcess(process, 3);
                WaitForSingleObject(process, 2000);
                code = 3; note = "stopped: out of time";
                return E_FAIL;
            }
            uint exit;
            GetExitCodeProcess(process, &exit);
            code = (int)exit;
            int* head = (int*)view;
            if (code != 0 || head[0] != 0x31485447) { note = Reason(code); return E_FAIL; }
            w = head[1]; h2 = head[2];
            if (w < 1 || h2 < 1 || w > px || h2 > px) { note = "a picture of the wrong size"; return E_FAIL; }
            note = $"{head[3]} triangles, read {head[4]} ms, edges {head[7]} ms, draw {head[5]} ms{(head[6] != 0 ? ", from the cache" : "")}";

            var info = new BITMAPINFOHEADER { size = 40, width = w, height = -h2, planes = 1, bitCount = 32 };       // top row first
            void* bits;
            nint dib = CreateDIBSection(0, &info, 0, &bits, 0, 0);
            if (dib == 0 || bits == null) { note = "no bitmap"; return E_OUTOFMEMORY; }
            Buffer.MemoryCopy(view + 32, bits, (long)w * h2 * 4, (long)w * h2 * 4);
            *bitmap = dib;
            *alphaType = 2;                                                     // WTSAT_ARGB: the alpha channel is real
            return S_OK;
        }
        finally
        {
            if (turn) ReleaseSemaphore(_turns, 1, null);
            if (thread != 0) CloseHandle(thread);
            if (process != 0) CloseHandle(process);
            if (job != 0) CloseHandle(job);                                     // ends whatever is still running in it
            if (view != null) UnmapViewOfFile(view);
            if (outMap != 0) CloseHandle(outMap);
            try { Log($"{format.Format} {(h->Path != null ? "file" : h->Bytes + " bytes")} {px}px -> {(*bitmap != 0 ? w + "x" + h2 : "no picture")} in {GetTickCount64() - began} ms ({note}{(process != 0 && !inJob ? "; not in a job" : "")})"); } catch { }
        }
    }

    static string Reason(int code) => code switch
    {
        2 => "the file could not be read", 3 => "out of time", 4 => "too large or out of memory", 1 => "the helper did not understand",
        5 => "took too long to convert when it was tried before; opening it in Glance once gives it its picture",
        6 => "could not be read when it was tried before",
        _ => "the helper failed (" + code + ")",
    };

    static string MapName(long serial, string what) => $"Local\\GlanceThumb.{GetCurrentProcessId()}.{serial}.{what}";

    /// <summary>Copies the stream into shared memory and lets the stream (and with it the file) go.</summary>
    static bool Take(Handler* h, long limit, out string note)
    {
        nint s = h->Stream;
        note = "nothing to read";
        if (s == 0) return false;
        void** t = *(void***)s;
        var read = (delegate* unmanaged<nint, void*, uint, uint*, int>)t[3];
        var seek = (delegate* unmanaged<nint, long, uint, ulong*, int>)t[5];
        var stat = (delegate* unmanaged<nint, void*, uint, int>)t[12];
        try
        {
            long size = -1;
            byte* st = stackalloc byte[80];                                     // STATSTG; the size is at offset 16
            new Span<byte>(st, 80).Clear();
            if (stat(s, st, 1) >= 0) size = *(long*)(st + 16);                  // 1: without the name
            else { ulong end; if (seek(s, 0, 2, &end) >= 0) size = (long)end; }
            if (size <= 0) { note = "an empty file, or one of unknown size"; return false; }
            if (size > limit) { note = $"too large ({size} bytes; the limit is {limit})"; return false; }
            seek(s, 0, 0, null);

            long serial = Interlocked.Increment(ref _serial);
            nint map;
            fixed (char* name = MapName(serial, "in")) map = CreateFileMappingW(-1, 0, 4, (uint)(size >> 32), (uint)size, name);
            if (map == 0) { note = "no memory for the file"; return false; }
            byte* view = MapViewOfFile(map, 2, 0, 0, (nuint)size);              // FILE_MAP_WRITE
            if (view == null) { CloseHandle(map); note = "no memory for the file"; return false; }

            long got = 0;
            ulong until = GetTickCount64() + 20000;                             // a file on a slow network share
            while (got < size)
            {
                uint n = 0;
                int hr = read(s, view + got, (uint)Math.Min(size - got, 4 << 20), &n);
                if (hr < 0 || n == 0) break;
                got += n;
                if (GetTickCount64() > until) { got = -1; break; }
            }
            UnmapViewOfFile(view);
            if (got <= 0) { CloseHandle(map); note = got < 0 ? "reading the file took too long" : "the file could not be read"; return false; }
            h->Map = map; h->Bytes = got; h->Serial = serial;
            note = "";
            return true;
        }
        finally { LetGo(h); }
    }

    // ───────────────────────── A log, for finding out why a file has no picture ─────────────────────────
    // Off unless the file %LOCALAPPDATA%\Glance\thumbnails.log exists (create an empty one to switch it on),
    // or GLANCE_THUMB_LOG names a file. One line per thumbnail; it stops growing at 1 MB.

    static string? Env(string name)
    {
        char* value = stackalloc char[1024];
        fixed (char* n = name)
        {
            uint len = GetEnvironmentVariableW(n, value, 1024);
            return len > 0 && len < 1024 ? new string(value, 0, (int)len) : null;
        }
    }

    static void Log(string what)
    {
        string? file = Env("GLANCE_THUMB_LOG");
        if (file == null)
        {
            string? data = Env("GLANCE_DATA_DIR") ?? (Env("LOCALAPPDATA") is string local ? local + "\\Glance" : null);
            if (data == null) return;
            file = data + "\\thumbnails.log";
            fixed (char* f = file) if (GetFileAttributesW(f) == 0xFFFFFFFF) return;
        }
        nint handle;
        fixed (char* f = file) handle = CreateFileW(f, 0x0004, 7, 0, 4, 0x80, 0);       // append; open or create; shared
        if (handle == -1) return;
        long size;
        if (GetFileSizeEx(handle, &size) != 0 && size < 1 << 20)
        {
            ushort* now = stackalloc ushort[8];
            GetLocalTime(now);
            byte[] line = Encoding.UTF8.GetBytes($"{now[0]:0000}-{now[1]:00}-{now[3]:00} {now[4]:00}:{now[5]:00}:{now[6]:00}  {what}\r\n");
            uint written;
            fixed (byte* p = line) WriteFile(handle, p, (uint)line.Length, &written, 0);
        }
        CloseHandle(handle);
    }
}
