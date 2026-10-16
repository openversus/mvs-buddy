namespace MvsBuddy.Tests;

public sealed class DesktopEntryTests : IDisposable
{
    private readonly TempTree tree = new();

    public void Dispose() => tree.Dispose();

    private HostSystem Host(bool windows = false, string? xdgData = null) => new()
    {
        IsWindows = windows,
        Home = tree.Path("home"),
        Environment = name => name == "XDG_DATA_HOME" ? xdgData : null,
    };

    [Fact]
    public void The_entry_runs_the_executable_from_where_it_is()
    {
        string text = DesktopEntry.Text("/home/someone/Games/MvsBuddy");

        Assert.Equal("[Desktop Entry]\nType=Application\nName=MVS Buddy\nComment=The OpenVersus MVS mod installer and version manager\n"
            + "Exec=/home/someone/Games/MvsBuddy\nIcon=mvs-buddy\nTerminal=false\nCategories=Game;\nStartupNotify=true\n", text);
    }

    [Theory]
    [InlineData("/home/some one/MvsBuddy", "\"/home/some one/MvsBuddy\"")]
    [InlineData("/run/media/deck/SD Card/mvs \"buddy\"/MvsBuddy", "\"/run/media/deck/SD Card/mvs \\\"buddy\\\"/MvsBuddy\"")]
    [InlineData("/home/someone/$HOME-ish/MvsBuddy", "\"/home/someone/\\$HOME-ish/MvsBuddy\"")]
    public void A_path_with_a_space_or_a_shell_character_is_quoted_for_exec(string executable, string exec) =>
        Assert.Contains($"\nExec={exec}\n", DesktopEntry.Text(executable));

    private static readonly Dictionary<int, byte[]> Icons = new() { [256] = [1, 2, 3], [512] = [4, 5, 6, 7] };

    [Fact]
    public void It_is_written_once_with_its_icons_rewritten_when_the_executable_moves_and_left_alone_otherwise()
    {
        string expected = tree.Path("home", ".local", "share", "applications", "mvs-buddy.desktop");

        Assert.Equal(expected, DesktopEntry.Install(Host(), "/opt/mvs/MvsBuddy", Icons));
        Assert.Equal(DesktopEntry.Text("/opt/mvs/MvsBuddy"), File.ReadAllText(expected));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(tree.Path("home", ".local", "share", "icons", "hicolor", "256x256", "apps", "mvs-buddy.png")));
        Assert.Equal([4, 5, 6, 7], File.ReadAllBytes(tree.Path("home", ".local", "share", "icons", "hicolor", "512x512", "apps", "mvs-buddy.png")));
        Assert.Null(DesktopEntry.Install(Host(), "/opt/mvs/MvsBuddy", Icons));
        Assert.Equal(expected, DesktopEntry.Install(Host(), "/home/someone/MvsBuddy", Icons));
        Assert.Equal(DesktopEntry.Text("/home/someone/MvsBuddy"), File.ReadAllText(expected));
    }

    [Fact]
    public void The_data_home_variable_moves_it_and_windows_gets_none()
    {
        Assert.Equal(tree.Path("data", "applications", "mvs-buddy.desktop"), DesktopEntry.Install(Host(xdgData: tree.Path("data")), "/opt/mvs/MvsBuddy", Icons));
        Assert.True(File.Exists(tree.Path("data", "icons", "hicolor", "512x512", "apps", "mvs-buddy.png")));
        Assert.Null(DesktopEntry.Install(Host(windows: true), "C:\\Games\\MvsBuddy.exe", Icons));
        Assert.False(Directory.Exists(tree.Path("home")));
    }
}
