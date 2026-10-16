using System.Text;

namespace MvsBuddy;

/// <summary>
/// MVS Buddy's shortcut on a Windows desktop (<c>MVS Buddy.lnk</c>), the counterpart of <see cref="DesktopEntry"/>: offered
/// once after the person's first change, written when they say yes, and kept pointing at MVS Buddy wherever it moves.
/// The file is written here byte by byte in the Shell Link format (MS-SHLLINK), not through the shell's COM objects: a
/// trimmed single-file build cannot bind to those, and bytes that never change let a startup tell "already there" from
/// "moved" by comparing. The link names its target by path (LinkInfo), with the working folder beside it and the
/// executable's own icon; no item ID list, which Explorer does not need to open it.
/// </summary>
public static class Shortcut
{
    internal const string FileName = "MVS Buddy.lnk";

    /// <summary>Writes the shortcut for <paramref name="executable"/>, or rewrites one that points elsewhere; the path written, or null when it was already right (or this is not Windows).</summary>
    /// <exception cref="IOException">The desktop folder or the file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The desktop folder or the file could not be written.</exception>
    public static string? Install(string executable) => Install(HostSystem.Current(), executable);

    internal static string? Install(HostSystem host, string executable)
    {
        if (!host.IsWindows || string.IsNullOrEmpty(host.DesktopDirectory) || string.IsNullOrEmpty(executable))
        {
            return null;
        }
        string path = Path.Combine(host.DesktopDirectory, FileName);
        byte[] bytes = Bytes(executable);
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
        {
            return null;
        }
        Directory.CreateDirectory(host.DesktopDirectory);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Removes the shortcut; its path when there was one, else null (always null off Windows).</summary>
    /// <exception cref="IOException">It could not be deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">It could not be deleted.</exception>
    public static string? Remove() => Remove(HostSystem.Current());

    internal static string? Remove(HostSystem host)
    {
        if (!host.IsWindows || string.IsNullOrEmpty(host.DesktopDirectory))
        {
            return null;
        }
        string path = Path.Combine(host.DesktopDirectory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }
        File.Delete(path);
        return path;
    }

    // LinkFlags (MS-SHLLINK 2.1.1).
    private const uint HasLinkInfo = 0x02, HasWorkingDir = 0x10, HasIconLocation = 0x40, IsUnicode = 0x80;
    private const uint FileAttributeArchive = 0x20;
    private const uint ShowNormal = 1;

    /// <summary>The shortcut file's bytes for <paramref name="executable"/>: the same bytes for the same path, every time.</summary>
    internal static byte[] Bytes(string executable)
    {
        // The folder by Windows's separators whatever this runs on (the bytes are checked on Linux too).
        int cut = executable.LastIndexOfAny(['\\', '/']);
        string folder = cut < 0 ? executable : executable.Substring(0, cut);
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        // ShellLinkHeader: 76 bytes.
        w.Write(0x4Cu);
        w.Write(new Guid("00021401-0000-0000-C000-000000000046").ToByteArray());
        w.Write(HasLinkInfo | HasWorkingDir | HasIconLocation | IsUnicode);
        w.Write(FileAttributeArchive);
        w.Write(0L); // CreationTime
        w.Write(0L); // AccessTime
        w.Write(0L); // WriteTime
        w.Write(0u); // FileSize
        w.Write(0);  // IconIndex
        w.Write(ShowNormal);
        w.Write((ushort)0); // HotKey
        w.Write((ushort)0); // Reserved1
        w.Write(0u);        // Reserved2
        w.Write(0u);        // Reserved3
        w.Write(LinkInfo(executable));
        StringData(w, folder);
        StringData(w, executable);
        w.Flush();
        return stream.ToArray();
    }

    // LinkInfo (2.3): the target by local path, ANSI and Unicode both, offsets from the block's own start. The Unicode
    // offsets need the longer header (0x24), and Windows reads the Unicode path when it is there, so a path the ANSI
    // field cannot hold loses nothing.
    private static byte[] LinkInfo(string executable)
    {
        const uint headerSize = 0x24;
        const uint volumeIdAndLocalBasePath = 0x1;
        byte[] volumeId = VolumeId();
        byte[] basePath = Ascii(executable);
        byte[] suffix = [0];
        byte[] basePathUnicode = Utf16Terminated(executable);
        byte[] suffixUnicode = [0, 0];
        uint volumeIdOffset = headerSize;
        uint basePathOffset = volumeIdOffset + (uint)volumeId.Length;
        uint suffixOffset = basePathOffset + (uint)basePath.Length;
        uint basePathUnicodeOffset = suffixOffset + (uint)suffix.Length;
        uint suffixUnicodeOffset = basePathUnicodeOffset + (uint)basePathUnicode.Length;
        uint size = suffixUnicodeOffset + (uint)suffixUnicode.Length;
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(size);
        w.Write(headerSize);
        w.Write(volumeIdAndLocalBasePath);
        w.Write(volumeIdOffset);
        w.Write(basePathOffset);
        w.Write(0u); // CommonNetworkRelativeLinkOffset
        w.Write(suffixOffset);
        w.Write(basePathUnicodeOffset);
        w.Write(suffixUnicodeOffset);
        w.Write(volumeId);
        w.Write(basePath);
        w.Write(suffix);
        w.Write(basePathUnicode);
        w.Write(suffixUnicode);
        w.Flush();
        return stream.ToArray();
    }

    // VolumeID (2.3.1): a fixed drive, no serial, an empty label.
    private static byte[] VolumeId()
    {
        const uint driveFixed = 3;
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(0x11u);     // VolumeIDSize: 16 bytes of fields and the label's one NUL
        w.Write(driveFixed);
        w.Write(0u);        // DriveSerialNumber
        w.Write(0x10u);     // VolumeLabelOffset
        w.Write((byte)0);   // the label
        w.Flush();
        return stream.ToArray();
    }

    // StringData (2.4): a character count, then the characters in UTF-16, no terminator.
    private static void StringData(BinaryWriter w, string text)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(text);
        w.Write((ushort)(bytes.Length / 2));
        w.Write(bytes);
    }

    // ASCII with '?' for what it cannot hold, NUL-terminated: the Unicode path beside it is the one that counts.
    private static byte[] Ascii(string text) => [.. Encoding.ASCII.GetBytes(text), 0];

    private static byte[] Utf16Terminated(string text) => [.. Encoding.Unicode.GetBytes(text), 0, 0];
}
