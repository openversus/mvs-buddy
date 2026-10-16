using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MvsBuddy;

/// <summary>
/// MVS Buddy's one known place on a machine: <c>bin/</c> in its own folder (<c>%LOCALAPPDATA%\\mvs-buddy\\bin</c> on Windows,
/// <c>~/.local/share/mvs-buddy/bin</c> on Linux). A copy started from anywhere else puts itself there at startup, before
/// any window, and starts that copy in its place; one already there that is as new or newer runs instead. The menu
/// entry and the desktop shortcut point there, so a downloaded file moved or deleted later breaks nothing, and deleting
/// MVS Buddy's files takes the program with them. <c>--version</c> is how one copy asks another what it is.
/// </summary>
public static class SelfInstall
{
    /// <summary>What a startup does next, as <see cref="Decide"/> found.</summary>
    public enum Outcome
    {
        /// <summary>This is the installed copy (or the person asked to run in place): carry on.</summary>
        RunHere,

        /// <summary>The installed copy is as new or newer: start it with the same arguments and exit.</summary>
        StartInstalled,

        /// <summary>No installed copy, or an older one: copy this executable there, then start it and exit.</summary>
        Install,
    }

    /// <summary>Where the installed copy lives, for this machine; null when MVS Buddy has no folder of its own.</summary>
    public static string? InstalledPath() => InstalledPath(HostSystem.Current());

    internal static string? InstalledPath(HostSystem host) => string.IsNullOrEmpty(host.DataDirectory) ? null
        : Path.Combine(host.DataDirectory, "bin", host.IsWindows ? "MvsBuddy.exe" : "MvsBuddy");

    /// <summary>
    /// What to do, given where this copy runs from, its version, and a way to ask another copy its version (null when
    /// it does not answer): the installed copy runs unless this one is newer or there is none.
    /// </summary>
    public static Outcome Decide(string running, string version, Func<string, string?> versionOf) => Decide(HostSystem.Current(), running, version, versionOf);

    internal static Outcome Decide(HostSystem host, string running, string version, Func<string, string?> versionOf)
    {
        string? installed = InstalledPath(host);
        if (installed == null || string.IsNullOrEmpty(running) || SamePath(host, running, installed))
        {
            return Outcome.RunHere;
        }
        if (!File.Exists(installed))
        {
            return Outcome.Install;
        }
        // One that does not answer is not one to hand over to; one older than this is replaced.
        return Parse(versionOf(installed)) is Version theirs && Parse(version) is Version ours && ours <= theirs ? Outcome.StartInstalled : Outcome.Install;
    }

    /// <summary>Copies <paramref name="running"/> over the installed copy, runnable and without the mark of the web; the path it is at.</summary>
    /// <exception cref="IOException">It could not be copied (the installed copy is running, on Windows).</exception>
    /// <exception cref="UnauthorizedAccessException">It could not be copied.</exception>
    public static string Install(string running) => Install(HostSystem.Current(), running);

    internal static string Install(HostSystem host, string running)
    {
        string installed = InstalledPath(host) ?? throw new IOException("MVS Buddy has no folder of its own to install into.");
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        // Beside it first, then into its place in one move: the installed copy may be running (opened from the shortcut
        // while a newer download starts), and writing over a running program's file is not safe on Linux; the move swaps
        // the file under it instead. On Windows the move fails on a running one, and the caller runs on from where it is.
        string fresh = installed + ".new";
        File.Copy(running, fresh, overwrite: true);
        try
        {
            if (!host.IsWindows)
            {
                Chmod(fresh, 0x1ED); // rwxr-xr-x
            }
            Changes.Installer.RemoveMarkOfTheWeb(fresh, null);
            if (host.IsWindows)
            {
                // No atomic replace of a file that may be running: delete (refused while it runs), then move.
                if (File.Exists(installed))
                {
                    File.Delete(installed);
                }
                File.Move(fresh, installed);
            }
            else if (rename(fresh, installed) != 0)
            {
                throw new IOException($"Could not put {fresh} in place of {installed} (error {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            if (File.Exists(fresh))
            {
                File.Delete(fresh);
            }
        }
        return installed;
    }

    /// <summary>
    /// Asks the copy at <paramref name="executable"/> its version (<c>--version</c>), waiting a few seconds; null when it
    /// does not start, does not answer, or answers something that is not a version.
    /// </summary>
    public static string? VersionOf(string executable)
    {
        try
        {
            var info = new ProcessStartInfo(executable, "--version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(info);
            if (process == null)
            {
                return null;
            }
            string output = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(10_000))
            {
                process.Kill();
                return null;
            }
            return process.ExitCode == 0 && Parse(output) != null ? output : null;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is System.ComponentModel.Win32Exception || e is InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Starts <paramref name="executable"/> with <paramref name="args"/>, detached, and returns at once.</summary>
    /// <exception cref="System.ComponentModel.Win32Exception">It could not be started.</exception>
    public static void Start(string executable, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(executable, Quote(args)) { UseShellExecute = false };
        Process.Start(info)?.Dispose();
    }

    /// <summary>
    /// On Windows, where a running program cannot delete itself: a detached shell that waits ten seconds, by when this
    /// process has exited, then deletes <paramref name="folder"/> and everything in it (MVS Buddy's folder, the program
    /// included). Nothing elsewhere: Linux deletes a running program's file like any other.
    /// </summary>
    public static void DeleteAfterExit(string folder)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || string.IsNullOrEmpty(folder))
        {
            return;
        }
        var info = new ProcessStartInfo("cmd.exe", $"/d /c \"timeout /t 10 /nobreak >nul & rmdir /s /q \"{folder}\"\"") { UseShellExecute = false, CreateNoWindow = true };
        Process.Start(info)?.Dispose();
    }

    /// <summary>
    /// <paramref name="args"/> as one command line, each quoted the way the runtime (and Windows) split it back: an
    /// argument with a space or a quote goes in double quotes, a quote inside escaped, backslashes before one doubled.
    /// </summary>
    internal static string Quote(IEnumerable<string> args) => string.Join(" ", args.Select(QuoteOne));

    private static string QuoteOne(string arg)
    {
        if (arg.Length > 0 && arg.All(c => !char.IsWhiteSpace(c) && c != '"'))
        {
            return arg;
        }
        var text = new System.Text.StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                text.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                text.Append('\\', backslashes).Append(c);
            }
            backslashes = 0;
        }
        return text.Append('\\', backslashes * 2).Append('"').ToString();
    }

    /// <summary>A build number as a <see cref="Version"/> (2026.10.16.09 is 2026.10.16.9); null for anything else.</summary>
    internal static Version? Parse(string? text) => text != null && Version.TryParse(text.Split('+')[0].Trim(), out var v) ? v : null;

    private static bool SamePath(HostSystem host, string a, string b)
    {
        string x = Path.GetFullPath(host.RealPath(a)).TrimEnd(Path.DirectorySeparatorChar);
        string y = Path.GetFullPath(host.RealPath(b)).TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(x, y, host.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static void Chmod(string path, int mode)
    {
        if (chmod(path, mode) != 0)
        {
            throw new IOException($"Could not make {path} runnable (error {Marshal.GetLastWin32Error()}).");
        }
    }

    [DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
    private static extern int chmod(string path, int mode);

    // rename(2) replaces an existing file in one step, a running one included: the old file lives on for its process.
    [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
    private static extern int rename(string from, string to);
}
