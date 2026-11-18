using System.Runtime.InteropServices;

namespace Glance;

/// <summary>
/// What the viewer and the thumbnail helper (src\Thumb) have in common beyond the readers themselves.
///
/// 1. A file's bytes can be handed to the readers by whoever already holds them. Explorer gives a thumbnail
///    handler a model as a stream of bytes, never as a file, so the helper reads from memory.
///
/// 2. A converted STEP or IGES file has a second name in the cache, made from what is in the file instead of where
///    the file is. The thumbnail helper is never told where a file is, so the path-based name the viewer uses is out of
///    its reach; the content name lets either side use what the other has already converted. Browsing a folder in
///    Explorer therefore makes its STEP files open at once in Glance, and the other way round.
/// </summary>
internal static unsafe partial class Loaders
{
    static string? _givenPath;
    static byte[]? _given;

    /// <summary>From now on, reading <paramref name="path"/> yields these bytes without touching the disk.</summary>
    public static void Give(string path, byte[] bytes) { _givenPath = path; _given = bytes; }

    static byte[] ReadFile(string path) => _given != null && path == _givenPath ? _given : File.ReadAllBytes(path);

    /// <summary>The thumbnail helper leaves larger STEP and IGES files alone, so nothing bigger is ever shared.</summary>
    public const long SharedLimit = 40L << 20;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int CreateHardLinkW(string name, string existing, nint security);

    static ulong ContentHash(ReadOnlySpan<byte> d)
    {
        ulong h = 14695981039346656037 ^ (ulong)d.Length;
        int whole = d.Length & ~7;
        foreach (ulong w in MemoryMarshal.Cast<byte, ulong>(d[..whole])) { h = (h ^ w) * 0x9E3779B97F4A7C15; h ^= h >> 29; }
        foreach (byte b in d[whole..]) h = (h ^ b) * 1099511628211;
        return h ^ (h >> 32);
    }

    /// <summary>Where the converted form of a STEP or IGES file with exactly these bytes is kept.</summary>
    static string SharedPath(ReadOnlySpan<byte> d) =>
        Path.Combine(CacheDir, $"c{ContentHash(d):x16}-{d.Length:x}-{TessellationVersion}.gmesh");

    /// <summary>One more name for the same file; a copy where the disk cannot do that.</summary>
    static void Link(string existing, string name)
    {
        if (CreateHardLinkW(name, existing, 0) == 0) File.Copy(existing, name, true);
    }

    /// <summary>
    /// The viewer, about to convert a file that is not in the cache under its path: if a file with the same bytes
    /// has been converted already, give that result this file's cache name and report true.
    /// </summary>
    static bool AdoptShared(string path, string cache)
    {
        try
        {
            if (new FileInfo(path).Length > SharedLimit) return false;
            string shared = SharedPath(File.ReadAllBytes(path));
            if (!File.Exists(shared)) return false;
            Link(shared, cache);
            return File.Exists(cache);
        }
        catch { return false; }
    }

    /// <summary>The viewer, having converted a file: make the result findable by content as well.</summary>
    static void PublishShared(string path, string cache)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > SharedLimit) return;
            string shared = SharedPath(File.ReadAllBytes(path));
            if (!File.Exists(shared) && File.Exists(cache)) Link(cache, shared);
        }
        catch { }
    }
}
