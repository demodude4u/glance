using System.Diagnostics;
using Microsoft.Win32;

namespace Glance;

/// <summary>Per-user registration (HKCU only; no admin rights, fully reversible with --uninstall).</summary>
internal static class Registration
{
    static readonly (string Ext, string Name)[] Types =
    [
        (".stl", "STL Model"), (".obj", "OBJ Model"), (".step", "STEP Model"), (".stp", "STEP Model"),
        (".iges", "IGES Model"), (".igs", "IGES Model"),
    ];

    const string Classes = @"Software\Classes\";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Glance";
    static string ProgId(string ext) => "Glance.Model" + ext;
    static string MenuKey(string ext) => Classes + @"SystemFileAssociations\" + ext + @"\shell\Glance";

    /// <param name="associate">Make Glance the app that opens these types on double-click.</param>
    /// <param name="menu">Add a right-click "Open in Glance" entry.</param>
    /// Glance is always listed under "Open with", whatever the two options say.
    public static void Register(bool associate = true, bool menu = true)
    {
        string exe = Environment.ProcessPath!;
        string command = $"\"{exe}\" \"%1\"";

        foreach (var (ext, name) in Types)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Classes + ProgId(ext)))
            {
                k.SetValue("", name);
                k.SetValue("FriendlyTypeName", name);
                using var icon = k.CreateSubKey("DefaultIcon"); icon.SetValue("", $"\"{exe}\",0");
                using var cmd = k.CreateSubKey(@"shell\open\command"); cmd.SetValue("", command);
            }
            using (var k = Registry.CurrentUser.CreateSubKey(Classes + ext))
            {
                string cur = k.GetValue("") as string ?? "";
                bool ours = cur.StartsWith("Glance.", StringComparison.Ordinal);
                if (associate && (cur.Length == 0 || ours)) k.SetValue("", ProgId(ext));
                else if (!associate && ours) k.DeleteValue("", false);
                using var with = k.CreateSubKey("OpenWithProgids");
                with.SetValue(ProgId(ext), Array.Empty<byte>(), RegistryValueKind.None);
            }
            if (associate) ClearDeadChoice(ext);

            // Right-click > Open in Glance: works whatever the default app for the type is.
            if (menu)
            {
                using var k = Registry.CurrentUser.CreateSubKey(MenuKey(ext));
                k.SetValue("", "Open in Glance");
                k.SetValue("Icon", $"\"{exe}\",0");
                using var cmd = k.CreateSubKey("command"); cmd.SetValue("", command);
            }
            else Registry.CurrentUser.DeleteSubKeyTree(MenuKey(ext), false);
        }

        using (var k = Registry.CurrentUser.CreateSubKey(Classes + @"Applications\Glance.exe"))
        {
            k.SetValue("FriendlyAppName", "Glance");
            using var cmd = k.CreateSubKey(@"shell\open\command"); cmd.SetValue("", command);
            using var st = k.CreateSubKey("SupportedTypes");
            foreach (var (ext, _) in Types) st.SetValue(ext, "");
        }

        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Glance\Capabilities"))
        {
            k.SetValue("ApplicationName", "Glance");
            k.SetValue("ApplicationDescription", "A fast, simple viewer for STL, OBJ and STEP models.");
            using var fa = k.CreateSubKey("FileAssociations");
            foreach (var (ext, _) in Types) fa.SetValue(ext, ProgId(ext));
        }
        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            k.SetValue("Glance", @"Software\Glance\Capabilities");

        // Settings > Apps > Installed apps
        string dir = Path.GetDirectoryName(exe)!;
        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "Glance");
            k.SetValue("DisplayVersion", AppVersion.Number);
            k.SetValue("DisplayIcon", $"\"{exe}\",0");
            k.SetValue("InstallLocation", dir);
            k.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            try
            {
                long bytes = 0;
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) bytes += new FileInfo(f).Length;
                k.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
            }
            catch { }
        }

        Win32.SHChangeNotify(0x08000000, 0, 0, 0);
    }

    /// <summary>Registers again with whatever was chosen at install time (used when Glance updates itself).</summary>
    public static void Reregister()
    {
        bool associate = false, menu = false;
        foreach (var (ext, _) in Types)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Classes + ext))
                if (k?.GetValue("") is string cur && cur.StartsWith("Glance.", StringComparison.Ordinal)) associate = true;
            using (var k = Registry.CurrentUser.OpenSubKey(MenuKey(ext)))
                if (k != null) menu = true;
        }
        Register(associate, menu);
    }

    /// A saved "open with" choice that points at an app which is no longer installed makes Explorer ignore the
    /// registration above. Remove only those dead pointers; a choice for an app that still exists is left alone.
    static void ClearDeadChoice(string ext)
    {
        try
        {
            string exts = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + ext;
            string? progId;
            using (var c = Registry.CurrentUser.OpenSubKey(exts + @"\UserChoice")) progId = c?.GetValue("ProgId") as string;
            if (string.IsNullOrEmpty(progId)) return;
            using var cls = Registry.ClassesRoot.OpenSubKey(progId);
            if (cls == null) Registry.CurrentUser.DeleteSubKeyTree(exts + @"\UserChoice", false);
        }
        catch { }       // Windows may protect the key; then the person picks Glance once in the Open with list
    }

    public static void Unregister()
    {
        foreach (var (ext, _) in Types)
        {
            Registry.CurrentUser.DeleteSubKeyTree(Classes + ProgId(ext), false);
            Registry.CurrentUser.DeleteSubKeyTree(MenuKey(ext), false);
            using var k = Registry.CurrentUser.OpenSubKey(Classes + ext, true);
            if (k == null) continue;
            if (k.GetValue("") is string cur && cur == ProgId(ext)) k.DeleteValue("", false);
            using var with = k.OpenSubKey("OpenWithProgids", true);
            with?.DeleteValue(ProgId(ext), false);
        }
        Registry.CurrentUser.DeleteSubKeyTree(Classes + @"Applications\Glance.exe", false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Glance", false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        using (var k = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true))
            k?.DeleteValue("Glance", false);
        Win32.SHChangeNotify(0x08000000, 0, 0, 0);
    }

    /// Removes everything: registration, Start menu entry, settings and cache, then the program folder itself.
    public static unsafe void Uninstall(bool quiet)
    {
        string exe = Environment.ProcessPath!, dir = Path.GetDirectoryName(exe)!;
        if (!quiet)
            fixed (char* t = "Remove Glance from this PC?") fixed (char* c = "Glance")
                if (Win32.MessageBoxW(0, t, c, 0x24) != 6) return;          // question, Yes/No

        foreach (string name in (string[])["Glance", "glance-step"])
            foreach (var p in Process.GetProcessesByName(name))
                try { if (p.Id != Environment.ProcessId && p.MainModule!.FileName.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) p.Kill(); } catch { }

        // Only ever delete the folder the installer registered, never wherever a stray copy happens to sit.
        string? installed;
        using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey)) installed = k?.GetValue("InstallLocation") as string;
        bool removeFolder = string.Equals(installed?.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                            && Path.GetFileName(dir.TrimEnd('\\')).Equals("Glance", StringComparison.OrdinalIgnoreCase);

        Unregister();
        string lnk = Environment.GetEnvironmentVariable("GLANCE_SETUP_LNK")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Glance.lnk");
        try { File.Delete(lnk); } catch { }
        try { Directory.Delete(Loaders.DataDir, true); } catch { }

        if (!quiet)
            fixed (char* t = "Glance has been removed.") fixed (char* c = "Glance")
                Win32.MessageBoxW(0, t, c, 0x40);

        // A running program cannot delete its own file: a windowless helper does it a moment after we exit.
        if (removeFolder)
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{dir}\"")
            { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
    }
}
