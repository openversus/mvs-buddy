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

    /// <summary>
    /// The buddy's own folder in the user's profile: <c>%LOCALAPPDATA%\mvs-buddy</c> on Windows,
    /// <c>$XDG_DATA_HOME/mvs-buddy</c> (by default <c>~/.local/share/mvs-buddy</c>) on Linux. Nothing the game or Ultimate
    /// ASI Loader reads is ever under it, which is why held files go there.
    /// </summary>
    internal string DataDirectory { get; init; } = "";

    /// <summary>Whether MultiVersus is running now.</summary>
    internal Func<bool> IsGameRunning { get; init; } = () => false;

    /// <summary>Where a search of the drives starts.</summary>
    internal Func<IReadOnlyList<string>> SearchRoots { get; init; } = () => [];

    /// <summary>This machine.</summary>
    internal static HostSystem Current()
    {
        bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        string home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        string? xdgData = System.Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return new HostSystem
        {
            IsWindows = windows,
            Home = home,
            DataDirectory = windows
                ? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "mvs-buddy")
                : Path.Combine(string.IsNullOrEmpty(xdgData) ? Path.Combine(home, ".local", "share") : xdgData!, "mvs-buddy"),
            Environment = System.Environment.GetEnvironmentVariable,
            Registry = windows ? ReadRegistry : (_, _) => null,
            SearchRoots = windows ? FixedDrives : LinuxSearchRoots,
            IsGameRunning = windows ? GameRunningOnWindows : GameRunningUnderWine,
        };
    }

    private static bool GameRunningOnWindows()
    {
        var processes = System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Games.GameFiles.ExecutableName));
        foreach (var process in processes)
        {
            process.Dispose();
        }
        return processes.Length > 0;
    }

    // Under Wine or Proton the process name is cut to 15 characters, so the command lines are read instead.
    private static bool GameRunningUnderWine()
    {
        try
        {
            foreach (string process in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(process), out _))
                {
                    continue;
                }
                try
                {
                    if (File.ReadAllText(Path.Combine(process, "cmdline")).IndexOf(Games.GameFiles.ExecutableName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    // A process that ended, or one that is not ours to read.
                }
            }
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            return false;
        }
        return false;
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
