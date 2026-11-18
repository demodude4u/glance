using Microsoft.Win32;

namespace Glance;

/// <summary>
/// Explorer thumbnails, registered for the current user only (HKCU; see Registration.cs for the rest).
///
/// A thumbnail handler is found through the file type: the key ShellEx\{E357FCCD-...} names the COM class to use.
/// It is written in three places per extension, because which one Windows consults depends on which app is the
/// default for the type: Glance's own file type (used when Glance is the default), the extension itself, and
/// SystemFileAssociations (used whatever the default app is, unless that app brings a handler of its own).
/// The class points at GlanceThumbs.dll in the program folder. Nothing asks for it to run inside Explorer:
/// Windows keeps it in its separate thumbnail process.
/// </summary>
internal static class ThumbnailRegistration
{
    const string Classes = @"Software\Classes\";
    const string Handler = @"\ShellEx\{E357FCCD-A995-4576-B01F-234630154E96}";
    const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved";
    const string Glance = @"Software\Glance";
    const string Replaced = Glance + @"\ThumbnailsReplaced";       // handlers of other apps that ours took the place of

    static string ProgId(string ext) => "Glance.Model" + ext;
    static string[] Places(string ext) =>
        [Classes + ProgId(ext) + Handler, Classes + ext + Handler, Classes + @"SystemFileAssociations\" + ext + Handler];

    static bool Ours(string? clsid) => clsid != null && clsid.StartsWith(ThumbTypes.Clsid(0)[..^3], StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What was chosen when Glance was installed. A Glance from before thumbnails existed never asked, and counts
    /// as yes: an update brings the feature the way a new install would.
    /// </summary>
    public static bool Chosen
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(Glance);
            return k?.GetValue("Thumbnails") is not int v || v != 0;
        }
    }

    public static void Set(bool on, string dir)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(Glance)) k.SetValue("Thumbnails", on ? 1 : 0, RegistryValueKind.DWord);
        ThumbFiles.Sweep(dir);
        string dll = Path.Combine(dir, ThumbTypes.Dll);
        if (!on || !File.Exists(dll) || !File.Exists(Path.Combine(dir, ThumbTypes.Helper))) { Remove(); return; }

        for (int i = 0; i < ThumbTypes.Formats.Length; i++)
        {
            string clsid = ThumbTypes.Clsid(i);
            using (var k = Registry.CurrentUser.CreateSubKey(Classes + @"CLSID\" + clsid))
            {
                k.SetValue("", "Glance thumbnail (" + ThumbTypes.Formats[i].Format.ToUpperInvariant() + ")");
                using var server = k.CreateSubKey("InprocServer32");
                server.SetValue("", dll);
                server.SetValue("ThreadingModel", "Apartment");
            }
            using (var k = Registry.CurrentUser.CreateSubKey(Approved)) k.SetValue(clsid, "Glance thumbnails");

            foreach (string ext in ThumbTypes.Formats[i].Extensions)
            {
                foreach (string place in Places(ext))
                {
                    using var k = Registry.CurrentUser.CreateSubKey(place);
                    if (k.GetValue("") is string other && other.Length > 0 && !Ours(other))
                        using (var kept = Registry.CurrentUser.CreateSubKey(Replaced)) kept.SetValue(place, other);    // put back by Remove
                    k.SetValue("", clsid);
                }
                // The picture as it is: no frame or shadow around it, no app icon in its corner.
                using var type = Registry.CurrentUser.CreateSubKey(Classes + ProgId(ext));
                type.SetValue("Treatment", 0, RegistryValueKind.DWord);
                type.SetValue("TypeOverlay", "");
            }
        }
    }

    /// <summary>Takes the handler out again, and puts back any other app's handler that it replaced.</summary>
    public static void Remove()
    {
        using (var kept = Registry.CurrentUser.OpenSubKey(Replaced))
            for (int i = 0; i < ThumbTypes.Formats.Length; i++)
                foreach (string ext in ThumbTypes.Formats[i].Extensions)
                {
                    foreach (string place in Places(ext))
                    {
                        string? now;
                        using (var k = Registry.CurrentUser.OpenSubKey(place)) now = k?.GetValue("") as string;
                        if (!Ours(now)) continue;                                   // not there, or someone else's by now
                        if (kept?.GetValue(place) is string other)
                            using (var k = Registry.CurrentUser.CreateSubKey(place)) k.SetValue("", other);
                        else
                        {
                            Registry.CurrentUser.DeleteSubKeyTree(place, false);
                            string shellEx = place[..place.LastIndexOf('\\')];
                            try { Registry.CurrentUser.DeleteSubKey(shellEx, false); } catch { }       // only if nothing else is in it
                        }
                    }
                    using var type = Registry.CurrentUser.OpenSubKey(Classes + ProgId(ext), true);
                    type?.DeleteValue("Treatment", false);
                    type?.DeleteValue("TypeOverlay", false);
                }

        for (int i = 0; i < ThumbTypes.Formats.Length; i++)
        {
            Registry.CurrentUser.DeleteSubKeyTree(Classes + @"CLSID\" + ThumbTypes.Clsid(i), false);
            using var k = Registry.CurrentUser.OpenSubKey(Approved, true);
            k?.DeleteValue(ThumbTypes.Clsid(i), false);
        }
        Registry.CurrentUser.DeleteSubKeyTree(Replaced, false);
    }
}
