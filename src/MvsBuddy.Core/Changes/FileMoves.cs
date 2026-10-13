using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MvsBuddy.Changes;

/// <summary>
/// Moving one file without ever losing it: a rename when both places are on one drive, otherwise a copy that is read
/// back and compared (length and SHA-256) before the original is deleted.
/// </summary>
internal static class FileMoves
{
    private const int UnixCrossDevice = 18;       // EXDEV
    private const int WindowsNotSameDevice = 17;  // ERROR_NOT_SAME_DEVICE

    /// <summary>Moves <paramref name="from"/> to <paramref name="to"/>, which must not exist; makes its folder.</summary>
    /// <exception cref="IOException">The move failed; <paramref name="from"/> is untouched.</exception>
    internal static void Move(string from, string to, bool windows)
    {
        if (File.Exists(to) || Directory.Exists(to))
        {
            throw new IOException($"{to} is already there.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (!TryRename(from, to, windows))
        {
            CopyVerified(from, to);
            File.Delete(from);
        }
    }

    /// <summary>Copies, then reads the copy back: same length and SHA-256, or the copy is deleted and this throws.</summary>
    internal static void CopyVerified(string from, string to)
    {
        File.Copy(from, to, overwrite: false);
        bool same;
        try
        {
            same = new FileInfo(from).Length == new FileInfo(to).Length && Sha256(from).SequenceEqual(Sha256(to));
        }
        catch
        {
            File.Delete(to);
            throw;
        }
        if (!same)
        {
            File.Delete(to);
            throw new IOException($"The copy of {from} at {to} did not match the original.");
        }
    }

    // A rename that refuses to cross drives: false when it would have to, so the caller copies.
    private static bool TryRename(string from, string to, bool windows)
    {
        if (windows)
        {
            if (MoveFileEx(from, to, 0))
            {
                return true;
            }
            int error = Marshal.GetLastWin32Error();
            return error == WindowsNotSameDevice ? false : throw new IOException($"Could not move {from} to {to} (Windows error {error}).");
        }
        if (Rename(from, to) == 0)
        {
            return true;
        }
        int errno = Marshal.GetLastWin32Error();
        return errno == UnixCrossDevice ? false : throw new IOException($"Could not move {from} to {to} (error {errno}).");
    }

    private static byte[] Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return sha.ComputeHash(stream);
    }

    [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
    private static extern int Rename(string from, string to);

    [DllImport("kernel32", EntryPoint = "MoveFileExW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string from, string to, int flags);
}
