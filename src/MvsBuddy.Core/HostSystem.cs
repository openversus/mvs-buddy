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
    /// ASI Loader reads is ever under it, which is why the states and the pak sets are kept there.
    /// </summary>
    internal string DataDirectory { get; init; } = "";

    /// <summary>Whether MultiVersus is running now.</summary>
    internal Func<bool> IsGameRunning { get; init; } = () => false;

    /// <summary>Whether Wine runs in this prefix (its wineserver), which may write the prefix's registry files over when it ends.</summary>
    internal Func<string, bool> IsWineRunningIn { get; init; } = _ => false;

    /// <summary>Whether Steam is running, which writes its launch options file over when it exits.</summary>
    internal Func<bool> IsSteamRunning { get; init; } = () => false;

    /// <summary>
    /// Whether this account is refused changes in an existing folder: adding files and folders to it and taking them out.
    /// True only when the system answers "access denied"; any other answer (the folder busy, gone, on an odd file system)
    /// is not a refusal, so the change is tried and put back if it fails.
    /// </summary>
    internal Func<string, bool> IsChangeDenied { get; init; } = _ => false;

    /// <summary>The SID of the account the buddy runs as (Windows), or null.</summary>
    internal Func<string?> UserSid { get; init; } = () => null;

    /// <summary>
    /// Runs a program as administrator, Windows asking the person first, and waits for it to end: its exit code, or null
    /// when the person said no.
    /// </summary>
    internal Func<string, string, CancellationToken, Task<int?>> RunElevated { get; init; } = (_, _, _) => Task.FromResult<int?>(null);

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
            IsWineRunningIn = windows ? _ => false : WineRunningIn,
            IsSteamRunning = windows ? SteamRunningOnWindows : SteamRunning,
            IsChangeDenied = windows ? ChangeDeniedOnWindows : ChangeDeniedOnUnix,
            UserSid = windows ? WindowsUserSid : () => null,
            RunElevated = windows ? RunElevatedOnWindows : (_, _, _) => Task.FromResult<int?>(null),
        };
    }

    /// <summary>
    /// Whether this account is refused changes in <paramref name="folder"/>, or, when it does not exist yet, in the nearest
    /// folder above it that does, where it would be made.
    /// </summary>
    internal bool ChangeDeniedIn(string folder)
    {
        for (var at = new DirectoryInfo(folder); at != null; at = at.Parent)
        {
            if (at.Exists)
            {
                return IsChangeDenied(at.FullName);
            }
        }
        return false;
    }

    // Opens the folder for adding files and folders and for deleting, the rights a move into or out of it needs, and closes
    // it again. Windows checks the rights on the open itself, as UAC has left them to this process, and nothing changes.
    private static bool ChangeDeniedOnWindows(string folder)
    {
        const uint AddFile = 0x2, AddSubdirectory = 0x4, Delete = 0x10000, ShareAll = 0x7, OpenExisting = 3, BackupSemantics = 0x02000000;
        const int AccessDenied = 5;
        var handle = CreateFile(folder, AddFile | AddSubdirectory | Delete, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle == new IntPtr(-1))
        {
            return Marshal.GetLastWin32Error() == AccessDenied;
        }
        CloseHandle(handle);
        return false;
    }

    /// <summary>access(2) for writing and entering: refused when the modes or ACLs say no, the file system is read-only, or
    /// the folder is immutable.</summary>
    internal static bool ChangeDeniedOnUnix(string folder)
    {
        const int WriteOk = 2, ExecuteOk = 1, NotPermitted = 1, AccessDenied = 13, ReadOnlyFileSystem = 30;
        if (Access(folder, WriteOk | ExecuteOk) == 0)
        {
            return false;
        }
        int errno = Marshal.GetLastWin32Error();
        return errno == AccessDenied || errno == ReadOnlyFileSystem || errno == NotPermitted;
    }

    private static string? WindowsUserSid()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return identity.User?.Value;
    }

    private static async Task<int?> RunElevatedOnWindows(string program, string arguments, CancellationToken cancellationToken)
    {
        const int Cancelled = 1223; // ERROR_CANCELLED: the person answered no
        System.Diagnostics.Process? process;
        try
        {
            process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(program, arguments)
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            });
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == Cancelled)
        {
            return null;
        }
        using (process ?? throw new InvalidOperationException($"Windows did not start {program}."))
        {
            // No time limit: the permission is passed down to every file in the folders, which takes as long as it takes.
            await Task.Run(() =>
            {
                while (!process.WaitForExit(250))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }, cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
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

    // A wineserver for this prefix: it keeps files in its prefix open (seen: drive_c/windows/system32), so one of its open
    // files is under it. Its environment does not name the prefix (Proton's and Bottles' wineservers carry no WINEPREFIX).
    // Wine in other prefixes does not count.
    internal static bool WineRunningIn(string prefix)
    {
        var resolve = new HostSystem();
        string wanted = resolve.RealPath(prefix).TrimEnd('/');
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
                    if (File.ReadAllText(Path.Combine(process, "comm")).Trim() == "wineserver"
                        && Directory.EnumerateFileSystemEntries(Path.Combine(process, "fd")).Select(resolve.RealPath)
                            .Any(open => open == wanted || open.StartsWith(wanted + "/", StringComparison.Ordinal)))
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

    private static bool SteamRunningOnWindows()
    {
        var processes = System.Diagnostics.Process.GetProcessesByName("steam");
        foreach (var process in processes)
        {
            process.Dispose();
        }
        return processes.Length > 0;
    }

    /// <summary>
    /// Whether Steam's own process runs: one whose program name (<c>/proc/&lt;pid&gt;/comm</c>) is <c>steam</c>, the
    /// client binary. Its helpers (<c>steamwebhelper</c>, <c>steam.sh</c>) and a shell that only names Steam do not count.
    /// </summary>
    internal static bool SteamRunning()
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
                    if (File.ReadAllText(Path.Combine(process, "comm")).Trim() == "steam")
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

    // Under Wine or Proton the process name is cut to 15 characters, so the program each process runs is read from its
    // command line instead.
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
                    if (RunsGame(File.ReadAllText(Path.Combine(process, "cmdline"))))
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

    /// <summary>
    /// Whether a process with this command line (<c>/proc/&lt;pid&gt;/cmdline</c>, fields split by NUL) is the game: its
    /// program, the first field, is the game's executable by a Unix or a Windows path. Under Wine that field is the path the
    /// game was started by, <c>C:\...\MultiVersus-Win64-Shipping.exe</c> or <c>./MultiVersus-Win64-Shipping.exe</c>. A
    /// process that only names the file in an argument (a shell copying it, an editor) is not the game.
    /// </summary>
    internal static bool RunsGame(string commandLine)
    {
        int end = commandLine.IndexOf('\0');
        string program = (end < 0 ? commandLine : commandLine.Substring(0, end)).Trim().Trim('"');
        string name = program.Substring(program.LastIndexOfAny(['/', '\\']) + 1);
        return string.Equals(name, Games.GameFiles.ExecutableName, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// <paramref name="path"/> with every link in it resolved: two paths to one folder give the same answer. The full path
    /// as given when it cannot be resolved (it does not exist, or the system call is not there).
    /// </summary>
    internal string RealPath(string path)
    {
        string full = Path.GetFullPath(path);
        try
        {
            if (IsWindows)
            {
                // No access asked for, all sharing, open an existing folder or file (backup semantics opens folders).
                // It's a lot faster than the equivalent dotnet calls and allows to very very quickly walk the path if
                // symlinks are involved.
                IntPtr handle = CreateFile(full, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
                if (handle == new IntPtr(-1))
                {
                    return full;
                }
                try
                {
                    var buffer = new System.Text.StringBuilder(32768);
                    uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                    if (length == 0 || length >= buffer.Capacity)
                    {
                        return full;
                    }
                    string final = buffer.ToString();
                    return final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + final.Substring(8)
                        : final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final.Substring(4) : final;
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
            IntPtr resolved = RealPathOf(full, IntPtr.Zero);
            if (resolved == IntPtr.Zero)
            {
                return full;
            }
            try
            {
                int length = 0;
                while (Marshal.ReadByte(resolved, length) != 0)
                {
                    length++;
                }
                var bytes = new byte[length];
                Marshal.Copy(resolved, bytes, 0, length);
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                Free(resolved);
            }
        }
        catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
        {
            return full;
        }
    }

    [DllImport("kernel32", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(IntPtr handle, System.Text.StringBuilder path, uint size, uint flags);

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr RealPathOf(string path, IntPtr resolved);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(IntPtr pointer);

    [DllImport("kernel32", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("libc", EntryPoint = "access", SetLastError = true)]
    private static extern int Access(string path, int mode);
}
