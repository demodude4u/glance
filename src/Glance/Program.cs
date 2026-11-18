using System.Diagnostics;

namespace Glance;

internal static unsafe class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        App.T0 = Stopwatch.GetTimestamp();
        try
        {
            string? file = null, snapshot = null; string press = "";
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--register":
                        if (args.Contains("--keep")) Registration.Reregister();          // an update: leave the person's choices as they are
                        else Registration.Register(!args.Contains("--no-associate"), !args.Contains("--no-menu"), !args.Contains("--no-thumbnails"));
                        return 0;
                    case "--unregister": Registration.Unregister(); return 0;
                    case "--uninstall": Registration.Uninstall(args.Contains("--quiet")); return 0;
                    case "--snapshot" when i + 1 < args.Length: snapshot = args[++i]; break;   // test hook
                    case "--press" when i + 1 < args.Length: press = args[++i]; break;         // test hook
                    default: file ??= args[i]; break;
                }
            }
            if (snapshot != null)
                try { App.PreMain = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds - Stopwatch.GetElapsedTime(App.T0).TotalMilliseconds; } catch { }
            return App.Run(file, snapshot, press);
        }
        catch (Exception ex) { Crash(ex); return 1; }
    }

    public static void Crash(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Loaders.DataDir);
            File.WriteAllText(Path.Combine(Loaders.DataDir, "error.log"), DateTime.Now + "\r\n" + ex);
        }
        catch { }
        if (Environment.GetEnvironmentVariable("GLANCE_QUIET") == null)
            fixed (char* t = ex.Message) fixed (char* c = "Glance")
                Win32.MessageBoxW(0, t, c, 0x10);
        Environment.Exit(1);
    }
}
