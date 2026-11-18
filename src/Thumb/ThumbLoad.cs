namespace Glance;

/// <summary>What the viewer's mesh type refers to but a thumbnail never needs (the measuring tools, src\Glance\Inspect.cs).</summary>
internal sealed class Entity { }

/// <summary>This very file was tried before and given up on: it was too slow to convert, or could not be read.</summary>
internal sealed class GivenUpException(bool slow) : Exception { public bool Slow = slow; }

/// <summary>
/// Reading a model for a thumbnail. The readers are the viewer's own; what differs is what surrounds them: no
/// units (a picture has no scale), no settings, and for STEP and IGES a conversion that gives up when its time is
/// over and remembers that it did.
/// </summary>
internal static unsafe partial class Loaders
{
    /// <param name="path">Where the file is, or a name in a folder that does not exist when only its bytes are known.</param>
    /// <param name="real">The path is the file's real one, so what lies beside it (an .mtl, a .bin) can be read.</param>
    /// <param name="late">True once the time for this thumbnail is nearly over.</param>
    public static Mesh ForThumbnail(string path, string format, byte[] data, bool real, Func<bool> late, out bool cached)
    {
        cached = false;
        Give(path, data);
        Mesh m = format switch
        {
            "stl" => LoadStl(path),
            "obj" => LoadObj(path),
            "3mf" => Load3mf(path),
            "ply" => LoadPly(path),
            "glb" or "gltf" => LoadGltf(path),
            "step" or "iges" => Cad(path, format, data, real, late, out cached),
            _ => throw new InvalidDataException(),
        };
        if (m.TriCount == 0) throw new InvalidDataException();
        m.ComputeBounds();
        return m;
    }

    // What a conversion leaves on disk while it runs: the copy of the file's bytes that the OpenCASCADE reader is
    // given, and the reader's unfinished output.
    static string? _scratch, _partial;
    static string? _skip;               // where to note that the conversion now running was given up

    /// <summary>Deletes both. Also called when the time is up, with the reader only just stopped and perhaps still holding them.</summary>
    public static void DropScratch()
    {
        foreach (string? file in (string?[])[Interlocked.Exchange(ref _scratch, null), Interlocked.Exchange(ref _partial, null)])
            for (int attempt = 0; file != null && attempt < 20; attempt++)
                try { File.Delete(file); break; } catch { Thread.Sleep(25); }
    }

    /// <summary>The time for this thumbnail is over and the process is about to end: stop a conversion, note it, tidy up.</summary>
    public static void OutOfTime()
    {
        KillWorkers();
        if (Interlocked.Exchange(ref _skip, null) is string note) try { File.WriteAllBytes(note, []); } catch { }       // empty: too slow
        DropScratch();
    }

    /// <summary>
    /// STEP and IGES go through the OpenCASCADE reader, which can take seconds. In order: the viewer's cache (when the
    /// path is known), the cache by content, a note that this very file was already too slow or unreadable, and
    /// only then a conversion, which is stopped when <paramref name="late"/> says so.
    /// </summary>
    static Mesh Cad(string path, string format, byte[] data, bool real, Func<bool> late, out bool cached)
    {
        cached = true;
        Directory.CreateDirectory(CacheDir);
        string shared = SharedPath(data);
        foreach (string hit in real ? (string[])[CachePath(path), shared] : [shared])
        {
            if (!File.Exists(hit)) continue;
            try
            {
                Mesh m = ReadGmesh(hit);
                try { File.SetLastWriteTimeUtc(hit, DateTime.UtcNow); } catch { }       // keeps it from being pruned
                return m;
            }
            catch { try { File.Delete(hit); } catch { } }
        }
        cached = false;

        string skip = shared[..^".gmesh".Length] + ".skip";
        if (File.Exists(skip)) throw new GivenUpException(new FileInfo(skip).Length == 0);
        if (!File.Exists(Worker)) throw new InvalidOperationException();

        string source = path;
        if (!real)
        {
            source = _scratch = shared + "." + Environment.ProcessId + ".src.tmp";      // *.tmp: swept by PruneCache if we are killed
            File.WriteAllBytes(source, data);
        }
        try
        {
            string? error;
            _partial = shared + "." + Environment.ProcessId + ".bg.tmp";                // the name Convert writes to
            _skip = skip;
            try { error = Convert(source, format, shared, null, late, background: true); }
            catch (OperationCanceledException) { error = "slow"; }
            if (error != null)
            {
                // The same bytes would be just as slow or as broken next time, so do not try again. If the viewer
                // converts the file later (it waits as long as it takes), its result is found before this note.
                try { File.WriteAllBytes(skip, error == "slow" ? [] : [(byte)'x']); } catch { }       // empty: too slow; otherwise unreadable
                _skip = null;
                if (error == "slow") throw new TimeoutException();
                throw new InvalidDataException(error);
            }
            _partial = null;                                                            // it became the cache file
            _skip = null;
        }
        finally { DropScratch(); }
        if (real) try { Link(shared, CachePath(path)); } catch { }
        Mesh mesh = ReadGmesh(shared);
        PruneCache(CacheDir);
        try
        {
            foreach (var old in new DirectoryInfo(CacheDir).GetFiles("*.skip"))
                if (old.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-90)) old.Delete();
        }
        catch { }
        return mesh;
    }
}
