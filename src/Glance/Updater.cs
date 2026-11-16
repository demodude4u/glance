using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace Glance;

/// <summary>
/// Keeps an installed Glance up to date from its GitHub releases, and says what it is doing:
///   - as soon as the first picture is on screen, a background thread reads a two-line version.txt
///     (version, SHA-256 of the installer) from the latest release (Stage.Checking);
///   - if that version is newer, it downloads the installer next to Glance's settings and checks the
///     SHA-256 (Stage.Downloading, with Progress);
///   - then the update is ready (Stage.Ready): the Update button installs it and reopens Glance, and
///     otherwise it goes in silently when the last Glance window is closed.
/// Nothing here runs before the first picture is on screen. "autoUpdate=0" in settings.ini turns it off.
/// </summary>
internal static unsafe class Updater
{
    [DllImport("urlmon.dll")] static extern int URLDownloadToFileW(nint caller, char* url, char* file, uint reserved, nint callback);
    [DllImport("wininet.dll")] static extern int DeleteUrlCacheEntryW(char* url);

    public const string Repository = "demodude4u/glance";
    const string Latest = "https://github.com/" + Repository + "/releases/latest/download/";
    const string Installer = "Glance.Setup.exe";

    public enum Stage { None, Checking, Downloading, Ready }

    static volatile int _stage;
    static volatile float _progress = -1;
    static long _since;
    static string _ready = "";
    static bool _now;                   // the Update button was pressed: reopen Glance once the installer is done
    static string? _reopen;

    public static Stage State => (Stage)_stage;
    /// <summary>0 to 1 while downloading, or below zero while the size is not known yet.</summary>
    public static float Progress => _progress;
    /// <summary>When the current stage began (a Stopwatch timestamp), for the busy animation.</summary>
    public static long Since => Volatile.Read(ref _since);
    /// <summary>The version that is waiting to be installed, once State is Ready.</summary>
    public static string ReadyVersion => _ready;

    // Test hooks: another place to look for releases, and (with GLANCE_TEST_DIR) a stage to show without any network.
    static readonly string Source = Environment.GetEnvironmentVariable("GLANCE_UPDATE_URL") is { Length: > 0 } u ? u : Latest;
    static readonly string? Fake = Environment.GetEnvironmentVariable("GLANCE_TEST_DIR") != null ? Environment.GetEnvironmentVariable("GLANCE_UPDATE_FAKE") : null;

    static string Dir => Path.Combine(Loaders.DataDir, "update");
    static string SetupFile => Path.Combine(Dir, Installer);
    static string ReadyFile => Path.Combine(Dir, "ready.txt");          // the version SetupFile holds, once verified
    static string CheckedFile => Path.Combine(Dir, "checked.txt");      // when we last looked

    static void Set(Stage s)
    {
        Volatile.Write(ref _since, Stopwatch.GetTimestamp());
        _stage = (int)s;
        App.Post(App.Refresh);
    }

    /// <summary>Called once the first picture is on screen, every time Glance opens.</summary>
    public static void Start()
    {
        if (Fake != null)
        {
            string[] f = Fake.Split(':');
            if (f[0] == "downloading") _progress = f.Length > 1 ? float.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture) : -1;
            if (f[0] == "ready") _ready = f.Length > 1 ? f[1] : "9.9.9";
            Set(f[0] == "checking" ? Stage.Checking : f[0] == "downloading" ? Stage.Downloading : f[0] == "ready" ? Stage.Ready : Stage.None);
            return;
        }
        if (!Settings.AutoUpdate || !Installed()) return;
        Set(Stage.Checking);
        new Thread(() =>
        {
            try { Welcome(); Check(); }
            catch { }       // no network, GitHub down, disk full: try again next time
            try { if (State != Stage.Ready && Waiting() is string v) { _ready = v; Set(Stage.Ready); } } catch { }   // one from an earlier run
            if (State != Stage.Ready) Set(Stage.None);
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "update" }.Start();
    }

    /// <summary>Only a copy put there by the installer updates itself; a build run from a folder is left alone.</summary>
    static bool Installed()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Glance");
        string? where = k?.GetValue("InstallLocation") as string;
        string here = Path.GetDirectoryName(Environment.ProcessPath!)!;
        return where != null && string.Equals(where.TrimEnd('\\'), here.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    static bool Newer(string candidate) =>
        Version.TryParse(candidate, out var c) && Version.TryParse(AppVersion.Number, out var mine) && c > mine;

    /// <summary>The newer version already downloaded and verified, if there is one.</summary>
    static string? Waiting()
    {
        if (!File.Exists(ReadyFile) || !File.Exists(SetupFile)) return null;
        string v = File.ReadAllText(ReadyFile).Trim();
        return Newer(v) ? v : null;
    }

    /// <summary>First start after an update: say so once, and clear the downloaded installer away.</summary>
    static void Welcome()
    {
        if (!File.Exists(ReadyFile)) return;
        string ready = File.ReadAllText(ReadyFile).Trim();
        if (Newer(ready)) return;                                       // still waiting to be installed
        try { File.Delete(ReadyFile); File.Delete(SetupFile); } catch { }
        if (ready == AppVersion.Number) App.Post(() => App.Notify($"Glance was updated to version {ready}"));
    }

    // ───────────────────────── Download, with progress ─────────────────────────
    // URLDownloadToFile reports how far it has got through an IBindStatusCallback. Native AOT has no COM
    // marshalling, so the object is laid out by hand: a pointer to a table of eleven functions.

    const int S_OK = 0, E_NOTIMPL = unchecked((int)0x80004001), E_NOINTERFACE = unchecked((int)0x80004002);
    static readonly Guid IUnknown = new("00000000-0000-0000-C000-000000000046"), IBindStatusCallback = new("79eac9c1-baf9-11ce-8c82-00aa004ba90b");
    static nint _callback;

    static nint Callback()
    {
        if (_callback != 0) return _callback;
        void** v = (void**)NativeMemory.Alloc(11, (nuint)sizeof(void*));
        v[0] = (delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterface;
        v[1] = (delegate* unmanaged<nint, uint>)&AddRef;
        v[2] = (delegate* unmanaged<nint, uint>)&AddRef;                                    // Release: never freed
        v[3] = (delegate* unmanaged<nint, uint, nint, int>)&OnStartBinding;
        v[4] = (delegate* unmanaged<nint, nint, int>)&GetPriority;
        v[5] = (delegate* unmanaged<nint, uint, int>)&OnLowResource;
        v[6] = (delegate* unmanaged<nint, uint, uint, uint, nint, int>)&OnProgress;
        v[7] = (delegate* unmanaged<nint, int, nint, int>)&OnStopBinding;
        v[8] = (delegate* unmanaged<nint, nint, nint, int>)&GetBindInfo;
        v[9] = (delegate* unmanaged<nint, uint, uint, nint, nint, int>)&OnDataAvailable;
        v[10] = (delegate* unmanaged<nint, nint, nint, int>)&OnObjectAvailable;
        void*** obj = (void***)NativeMemory.Alloc(1, (nuint)sizeof(void*));
        *obj = v;
        return _callback = (nint)obj;
    }

    [UnmanagedCallersOnly] static int QueryInterface(nint self, Guid* iid, nint* obj)
    {
        if (*iid == IUnknown || *iid == IBindStatusCallback) { *obj = self; return S_OK; }
        *obj = 0; return E_NOINTERFACE;
    }
    [UnmanagedCallersOnly] static uint AddRef(nint self) => 1;
    [UnmanagedCallersOnly] static int OnStartBinding(nint self, uint reserved, nint binding) => S_OK;
    [UnmanagedCallersOnly] static int GetPriority(nint self, nint priority) => E_NOTIMPL;
    [UnmanagedCallersOnly] static int OnLowResource(nint self, uint reserved) => S_OK;
    [UnmanagedCallersOnly] static int OnProgress(nint self, uint done, uint total, uint status, nint text)
    {
        if (total > 0 && State == Stage.Downloading) _progress = Math.Clamp(done / (float)total, 0, 1);
        return S_OK;
    }
    [UnmanagedCallersOnly] static int OnStopBinding(nint self, int result, nint error) => S_OK;
    [UnmanagedCallersOnly] static int GetBindInfo(nint self, nint flags, nint info) => E_NOTIMPL;
    [UnmanagedCallersOnly] static int OnDataAvailable(nint self, uint flags, uint size, nint format, nint medium) => S_OK;
    [UnmanagedCallersOnly] static int OnObjectAvailable(nint self, nint iid, nint obj) => S_OK;

    static bool Download(string name, string to, bool progress = false)
    {
        string url = Source + name;
        fixed (char* pu = url) fixed (char* pf = to)
        {
            DeleteUrlCacheEntryW(pu);                                   // never a stale copy from the system cache
            return URLDownloadToFileW(0, pu, pf, 0, progress ? Callback() : 0) == 0 && File.Exists(to);
        }
    }

    static string Sha256(string file)
    {
        using var f = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(f));
    }

    static void Check()
    {
        Directory.CreateDirectory(Dir);
        string info = Path.Combine(Dir, "version.txt");
        try { File.Delete(info); } catch { }
        if (!Download("version.txt", info)) return;
        File.WriteAllText(CheckedFile, DateTime.UtcNow.ToString("o"));
        string[] lines = File.ReadAllLines(info);
        if (lines.Length < 2) return;
        string version = lines[0].Trim(), hash = lines[1].Trim();
        if (!Newer(version) || hash.Length != 64) return;

        if (File.Exists(ReadyFile) && File.ReadAllText(ReadyFile).Trim() == version && File.Exists(SetupFile)
            && Sha256(SetupFile).Equals(hash, StringComparison.OrdinalIgnoreCase))                // already waiting
        {
            _ready = version; Set(Stage.Ready);
            return;
        }

        _progress = -1;
        Set(Stage.Downloading);
        try { File.Delete(ReadyFile); } catch { }
        string part = SetupFile + ".part";
        try { File.Delete(part); } catch { }
        if (!Download(Installer, part, progress: true)) return;
        if (!Sha256(part).Equals(hash, StringComparison.OrdinalIgnoreCase)) { File.Delete(part); return; }   // damaged or not what was published
        File.Move(part, SetupFile, true);
        File.WriteAllText(ReadyFile, version);
        _progress = 1;
        _ready = version; Set(Stage.Ready);
    }

    /// <summary>One line per event in update\update.log, so a failed update can be explained afterwards.</summary>
    static void Log(string what)
    {
        try { Directory.CreateDirectory(Dir); File.AppendAllText(Path.Combine(Dir, "update.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  Glance {AppVersion.Number}: {what}\r\n"); } catch { }
    }

    /// <summary>Another Glance from this install is open, so its files cannot be replaced yet.</summary>
    static bool OthersOpen()
    {
        string here = Path.GetDirectoryName(Environment.ProcessPath!)!;
        foreach (var p in Process.GetProcessesByName("Glance"))
            try
            {
                if (p.Id != Environment.ProcessId && !p.HasExited && p.MainModule!.FileName.StartsWith(here + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            finally { p.Dispose(); }
        return false;
    }

    /// <summary>
    /// The Update button: true if Glance should now close. The installer then replaces it and opens it again
    /// on the same file. False (with a note on screen) if another Glance window is in the way.
    /// </summary>
    public static bool InstallNow(string? file)
    {
        if (State != Stage.Ready) return false;
        if (OthersOpen()) { App.Notify("Close the other Glance windows, then update"); return false; }
        _now = true; _reopen = file;
        return true;
    }

    /// <summary>Called as Glance closes: hand over to the downloaded installer, which waits for us to be gone.</summary>
    public static void ApplyOnExit()
    {
        try
        {
            if (!Settings.AutoUpdate || Waiting() is not string version || !Installed()) return;
            var start = new ProcessStartInfo(SetupFile) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--update");
            start.ArgumentList.Add("--wait-pid");
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            if (_now)
            {
                start.ArgumentList.Add("--reopen");
                if (_reopen != null) start.ArgumentList.Add(_reopen);
            }
            Log((_now ? "Update pressed" : "closing") + "; handing over to the installer for " + version);
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) { Log("could not start the installer: " + ex.Message); }
    }
}
