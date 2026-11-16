using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace Glance;

/// <summary>
/// Keeps an installed Glance up to date from its GitHub releases, without getting in anyone's way:
///   - a few seconds after start, at most once a day, a background thread reads a two-line version.txt
///     (version, SHA-256 of the installer) from the latest release;
///   - if that version is newer, it downloads the installer next to Glance's settings and checks the SHA-256;
///   - when the last Glance window is closed, the installer runs silently and keeps the person's choices.
/// Nothing here runs before the first picture is on screen. "autoUpdate=0" in settings.ini turns it off.
/// </summary>
internal static unsafe class Updater
{
    [DllImport("urlmon.dll")] static extern int URLDownloadToFileW(nint caller, char* url, char* file, uint reserved, nint callback);
    [DllImport("wininet.dll")] static extern int DeleteUrlCacheEntryW(char* url);

    public const string Repository = "demodude4u/glance";
    const string Latest = "https://github.com/" + Repository + "/releases/latest/download/";
    const string Installer = "Glance.Setup.exe";

    // Test hooks: another place to look for releases, and "check now" instead of waiting and rate-limiting.
    static readonly string Source = Environment.GetEnvironmentVariable("GLANCE_UPDATE_URL") is { Length: > 0 } u ? u : Latest;
    static readonly bool Now = Environment.GetEnvironmentVariable("GLANCE_UPDATE_NOW") != null;

    static string Dir => Path.Combine(Loaders.DataDir, "update");
    static string SetupFile => Path.Combine(Dir, Installer);
    static string ReadyFile => Path.Combine(Dir, "ready.txt");          // the version SetupFile holds, once verified
    static string CheckedFile => Path.Combine(Dir, "checked.txt");      // when we last looked

    public static void Start()
    {
        if (!Settings.AutoUpdate) return;
        new Thread(() =>
        {
            try
            {
                if (!Now) Thread.Sleep(4000);
                if (!Installed()) return;
                Welcome();
                if (!Now && File.Exists(CheckedFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(CheckedFile) < TimeSpan.FromHours(20)) return;
                Check();
            }
            catch { }       // no network, GitHub down, disk full: try again another day
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

    /// <summary>First start after an update: say so once, and clear the downloaded installer away.</summary>
    static void Welcome()
    {
        if (!File.Exists(ReadyFile)) return;
        string ready = File.ReadAllText(ReadyFile).Trim();
        if (Newer(ready)) return;                                       // still waiting to be installed
        try { File.Delete(ReadyFile); File.Delete(SetupFile); } catch { }
        if (ready == AppVersion.Number) App.Post(() => App.Notify($"Glance was updated to version {ready}"));
    }

    static bool Download(string name, string to)
    {
        string url = Source + name;
        fixed (char* pu = url) fixed (char* pf = to)
        {
            DeleteUrlCacheEntryW(pu);                                   // never a stale copy from the system cache
            return URLDownloadToFileW(0, pu, pf, 0, 0) == 0 && File.Exists(to);
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
            && Sha256(SetupFile).Equals(hash, StringComparison.OrdinalIgnoreCase)) return;       // already waiting

        try { File.Delete(ReadyFile); } catch { }
        string part = SetupFile + ".part";
        try { File.Delete(part); } catch { }
        if (!Download(Installer, part)) return;
        if (!Sha256(part).Equals(hash, StringComparison.OrdinalIgnoreCase)) { File.Delete(part); return; }   // damaged or not what was published
        File.Move(part, SetupFile, true);
        File.WriteAllText(ReadyFile, version);
    }

    /// <summary>One line per event in update\update.log, so a failed update can be explained afterwards.</summary>
    static void Log(string what)
    {
        try { Directory.CreateDirectory(Dir); File.AppendAllText(Path.Combine(Dir, "update.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  Glance {AppVersion.Number}: {what}\r\n"); } catch { }
    }

    /// <summary>Called as Glance closes: hand over to the downloaded installer, which waits for us to be gone.</summary>
    public static void ApplyOnExit()
    {
        try
        {
            if (!Settings.AutoUpdate || !File.Exists(ReadyFile) || !File.Exists(SetupFile)) return;
            if (!Newer(File.ReadAllText(ReadyFile).Trim()) || !Installed()) return;
            var start = new ProcessStartInfo(SetupFile) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--update");
            start.ArgumentList.Add("--wait-pid");
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            Log("closing; handing over to the installer for " + File.ReadAllText(ReadyFile).Trim());
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) { Log("could not start the installer: " + ex.Message); }
    }
}
