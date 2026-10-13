using System.Runtime.InteropServices;

namespace MvsBuddy;

/// <summary>What the buddy reads from the machine besides files, so tests can stand in for another one.</summary>
internal sealed class HostSystem
{
    /// <summary>Whether this is Windows (registry, drive letters) rather than Linux (Proton prefixes).</summary>
    internal bool IsWindows { get; init; }

    /// <summary>The user's home folder.</summary>
    internal string Home { get; init; } = "";

    /// <summary>An environment variable, or null.</summary>
    internal Func<string, string?> Environment { get; init; } = _ => null;

    /// <summary>A registry string by full key name (with its hive) and value name, or null. Windows only.</summary>
    internal Func<string, string, string?> Registry { get; init; } = (_, _) => null;

    /// <summary>Where a search of the drives starts.</summary>
    internal Func<IReadOnlyList<string>> SearchRoots { get; init; } = () => [];

    /// <summary>This machine.</summary>
    internal static HostSystem Current()
    {
        bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        return new HostSystem
        {
            IsWindows = windows,
            Home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            Environment = System.Environment.GetEnvironmentVariable,
            Registry = windows ? ReadRegistry : (_, _) => null,
            SearchRoots = windows ? FixedDrives : LinuxSearchRoots,
        };
    }

    private static string? ReadRegistry(string key, string value)
    {
        try
        {
            return Microsoft.Win32.Registry.GetValue(key, value, null) as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException || e is IOException || e is UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> FixedDrives() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToList();

    private static IReadOnlyList<string> LinuxSearchRoots()
    {
        string home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        // Removable media (a Deck's SD card is under /run/media) and other mounted drives.
        return new[] { home, "/run/media", "/media", "/mnt" }.Where(Directory.Exists).ToList();
    }
}
