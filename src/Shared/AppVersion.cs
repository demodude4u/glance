using System.Reflection;

namespace Glance;

/// <summary>The version from src/Directory.Build.props, plus the git commit the build was made from.</summary>
internal static class AppVersion
{
    /// <summary>For example "1.1.0".</summary>
    public static readonly string Number;
    /// <summary>Short commit id, or "" when built outside the repository.</summary>
    public static readonly string Commit;
    /// <summary>For example "1.1.0 (8ff894b)".</summary>
    public static string Full => Commit.Length > 0 ? $"{Number} ({Commit})" : Number;

    static AppVersion()
    {
        string v = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        int plus = v.IndexOf('+');
        Number = plus < 0 ? v : v[..plus];
        Commit = plus < 0 ? "" : v[(plus + 1)..Math.Min(v.Length, plus + 8)];
    }
}
