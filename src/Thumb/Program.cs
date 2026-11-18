using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Glance;

/// <summary>
/// glance-thumb.exe: reads one model, draws one picture of it, and exits. The Explorer thumbnail handler
/// (src\ThumbShell) starts one of these for every thumbnail and waits for it, inside a job that limits its memory
/// and ends it when its time is up. Whatever a damaged or enormous file does, it does here: this process may fail,
/// run out of memory or be stopped, and all Explorer notices is that there is no picture for that file.
///
///   glance-thumb.exe 1 FORMAT PIXELS MILLISECONDS SOURCE map:NAME
///
/// SOURCE is "map:NAME:BYTES" (the file's bytes in shared memory: all Explorer hands over) or "file:PATH". The
/// picture goes into the shared memory called NAME: eight 32-bit numbers (see Write) and then the pixels.
///
/// Exit codes: 0 a picture was written, 1 not asked properly, 2 the file cannot be read, 3 out of time,
/// 4 out of memory or too large, 5 and 6 a STEP or IGES file given up on before (too slow, unreadable).
///
/// GLANCE_TEST_THUMB (hang, crash or memory) makes the helper misbehave on purpose, so that the tests can watch the
/// handler cope with a helper that never answers, dies, or eats memory (tools\hidden_thumbs.py).
/// </summary>
internal static unsafe class ThumbProgram
{
    const string K = "kernel32.dll";
    [DllImport(K)] static extern uint SetErrorMode(uint mode);
    [DllImport(K, CharSet = CharSet.Unicode)] static extern nint OpenFileMappingW(uint access, int inherit, string name);
    [DllImport(K)] static extern byte* MapViewOfFile(nint map, uint access, uint high, uint low, nuint bytes);
    [DllImport(K)] static extern int UnmapViewOfFile(void* view);
    [DllImport(K)] static extern int CloseHandle(nint handle);

    public const uint Magic = 0x31485447;        // "GTH1"

    static int Main(string[] args)
    {
        SetErrorMode(0x8003);                    // never a system error box, whatever happens in here
        try { return Run(args); }
        catch (OutOfMemoryException) { return 4; }
        catch (TimeoutException) { return 3; }
        catch (GivenUpException e) { return e.Slow ? 5 : 6; }
        catch { return 2; }
    }

    static int Run(string[] a)
    {
        if (a.Length < 6 || a[0] != ThumbTypes.Protocol || !a[5].StartsWith("map:", StringComparison.Ordinal)) return 1;
        string format = a[1];
        if (!int.TryParse(a[2], out int px) || !int.TryParse(a[3], out int ms)) return 1;
        px = Math.Clamp(px, 16, ThumbTypes.MaxPixels);
        ms = Math.Clamp(ms, 20, 120_000);
        long limit = 0;
        foreach (var f in ThumbTypes.Formats) if (f.Format == format) limit = f.MaxBytes;
        if (limit == 0) return 1;

        switch (Environment.GetEnvironmentVariable("GLANCE_TEST_THUMB"))
        {
            case "hang": Thread.Sleep(Timeout.Infinite); break;
            case "crash": *(int*)0x7FFF_0000_1000 = 1; break;                // not a null pointer: nothing catches this
            case "memory":                                                    // 2.5 GB, well past what the job allows
                var hoard = new List<byte[]>();
                for (int i = 0; i < 40; i++) { var block = new byte[64 << 20]; Array.Fill(block, (byte)1); hoard.Add(block); }
                return 9;
        }

        // Whatever the readers are doing when the time is up, the process ends. (The handler would end it a moment later anyway.)
        long start = Stopwatch.GetTimestamp();
        new Thread(() => { Thread.Sleep(ms); Loaders.OutOfTime(); Environment.Exit(3); }) { IsBackground = true, Name = "time" }.Start();
        bool Late() => Stopwatch.GetElapsedTime(start).TotalMilliseconds > ms - 800;

        byte[] data; string path; bool real;
        if (a[4].StartsWith("map:", StringComparison.Ordinal))
        {
            int colon = a[4].LastIndexOf(':');
            if (colon <= 4 || !long.TryParse(a[4][(colon + 1)..], out long bytes) || bytes <= 0) return 1;
            if (bytes > limit) return 4;
            nint map = OpenFileMappingW(0x0004, 0, a[4][4..colon]);         // FILE_MAP_READ
            if (map == 0) return 1;
            byte* view = MapViewOfFile(map, 0x0004, 0, 0, (nuint)bytes);
            if (view == null) { CloseHandle(map); return 1; }
            data = new byte[bytes];
            new ReadOnlySpan<byte>(view, (int)bytes).CopyTo(data);
            UnmapViewOfFile(view); CloseHandle(map);
            // Nothing lies beside a model that arrived as bytes: its name is in a folder that does not exist.
            path = Path.Combine(Path.GetTempPath(), "glance-thumb-no-folder", "model." + format);
            real = false;
        }
        else if (a[4].StartsWith("file:", StringComparison.Ordinal))
        {
            path = Path.GetFullPath(a[4][5..]);
            // Shared in every way: reading a file for its picture must never stand in the way of whoever is using it.
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            if (f.Length <= 0) return 2;
            if (f.Length > limit) return 4;
            data = new byte[f.Length];
            f.ReadExactly(data);
            real = true;
        }
        else return 1;

        Mesh m = Loaders.ForThumbnail(path, format, data, real, Late, out bool cached);
        double loadMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        // Edge lines and smooth shading, as in the viewer. A very dense mesh goes without: its triangles are smaller
        // than a pixel here, so there is nothing for either to add, and the analysis would take most of the time.
        if (m.TriCount <= 300_000) Analysis.Run(m);
        double readyMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        byte[] image = Raster.Draw(m, px, out int w, out int h);
        double totalMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        return Write(a[5][4..], px, image, w, h, m.TriCount, (int)loadMs, (int)(readyMs - loadMs), (int)(totalMs - readyMs), cached) ? 0 : 1;
    }

    static bool Write(string name, int px, byte[] image, int w, int h, int tris, int loadMs, int edgesMs, int drawMs, bool cached)
    {
        nint map = OpenFileMappingW(0x0002, 0, name);                       // FILE_MAP_WRITE
        if (map == 0) return false;
        long room = 32 + (long)px * px * 4;                                 // what the handler made room for
        byte* view = MapViewOfFile(map, 0x0002, 0, 0, (nuint)room);
        if (view == null) { CloseHandle(map); return false; }
        bool ok = 32 + (long)image.Length <= room;
        if (ok)
        {
            image.CopyTo(new Span<byte>(view + 32, image.Length));
            int* head = (int*)view;
            head[1] = w; head[2] = h; head[3] = tris; head[4] = loadMs; head[5] = drawMs; head[6] = cached ? 1 : 0; head[7] = edgesMs;
            head[0] = unchecked((int)Magic);                                // last: the picture is whole
        }
        UnmapViewOfFile(view); CloseHandle(map);
        return ok;
    }
}
