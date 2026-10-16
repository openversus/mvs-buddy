using System.Text;

namespace MvsBuddy.Tests;

// The Windows desktop shortcut, written as Shell Link bytes: read back by the format, not by length.
public sealed class ShortcutTests : IDisposable
{
    private readonly TempTree tree = new();

    public void Dispose() => tree.Dispose();

    private HostSystem Host(bool windows = true) => new() { IsWindows = windows, Home = tree.Path("home"), DesktopDirectory = tree.Path("home", "Desktop") };

    [Fact]
    public void The_bytes_are_a_shell_link_naming_the_executable_its_folder_and_its_icon()
    {
        const string exe = @"C:\Games\MVS Buddy\MvsBuddy.exe";
        byte[] bytes = Shortcut.Bytes(exe);

        // ShellLinkHeader.
        Assert.Equal(0x4C, BitConverter.ToInt32(bytes, 0));
        Assert.Equal(new Guid("00021401-0000-0000-C000-000000000046"), new Guid(bytes[4..20]));
        uint flags = BitConverter.ToUInt32(bytes, 20);
        Assert.Equal(0x02u | 0x10u | 0x40u | 0x80u, flags);
        Assert.Equal(1, BitConverter.ToInt32(bytes, 60)); // ShowCommand: normal
        // LinkInfo: offsets from its own start lead to the target, ANSI and Unicode.
        int info = 0x4C;
        int infoSize = BitConverter.ToInt32(bytes, info);
        Assert.Equal(0x24, BitConverter.ToInt32(bytes, info + 4));
        Assert.Equal(1, BitConverter.ToInt32(bytes, info + 8)); // VolumeIDAndLocalBasePath
        int basePath = info + BitConverter.ToInt32(bytes, info + 16);
        int basePathUnicode = info + BitConverter.ToInt32(bytes, info + 28);
        Assert.Equal(exe, Encoding.ASCII.GetString(bytes, basePath, Array.IndexOf(bytes, (byte)0, basePath) - basePath));
        int end = basePathUnicode;
        while (!(bytes[end] == 0 && bytes[end + 1] == 0))
        {
            end += 2;
        }
        Assert.Equal(exe, Encoding.Unicode.GetString(bytes, basePathUnicode, end - basePathUnicode));
        // StringData after LinkInfo: the working folder, then the icon's location.
        int at = info + infoSize;
        (string workingDir, at) = StringData(bytes, at);
        (string icon, at) = StringData(bytes, at);
        Assert.Equal(@"C:\Games\MVS Buddy", workingDir);
        Assert.Equal(exe, icon);
        Assert.Equal(bytes.Length, at);
        // The same path gives the same bytes, so a startup can tell "already there" by comparing.
        Assert.Equal(bytes, Shortcut.Bytes(exe));
    }

    private static (string Text, int Next) StringData(byte[] bytes, int at)
    {
        int count = BitConverter.ToUInt16(bytes, at);
        return (Encoding.Unicode.GetString(bytes, at + 2, count * 2), at + 2 + count * 2);
    }

    [Fact]
    public void It_is_written_once_rewritten_when_the_executable_moves_and_removed_on_request()
    {
        string path = tree.Path("home", "Desktop", "MVS Buddy.lnk");
        Assert.Equal(path, Shortcut.Install(Host(), @"C:\a\MvsBuddy.exe"));
        Assert.Equal(Shortcut.Bytes(@"C:\a\MvsBuddy.exe"), File.ReadAllBytes(path));
        Assert.Null(Shortcut.Install(Host(), @"C:\a\MvsBuddy.exe"));
        Assert.Equal(path, Shortcut.Install(Host(), @"C:\b\MvsBuddy.exe"));
        Assert.Equal(Shortcut.Bytes(@"C:\b\MvsBuddy.exe"), File.ReadAllBytes(path));

        Assert.Equal(path, Shortcut.Remove(Host()));
        Assert.False(File.Exists(path));
        Assert.Null(Shortcut.Remove(Host()));
    }

    [Fact]
    public void Nothing_is_written_or_removed_off_windows()
    {
        Assert.Null(Shortcut.Install(Host(windows: false), "/opt/mvs/MvsBuddy"));
        Assert.Null(Shortcut.Remove(Host(windows: false)));
        Assert.False(Directory.Exists(tree.Path("home", "Desktop")));
    }
}
