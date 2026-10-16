namespace MvsBuddy.Tests;

// What building MVS Buddy does to its own folder.
public sealed class BuddyBuilderTests : IDisposable
{
    private readonly TempTree tree = new();

    public void Dispose() => tree.Dispose();

    [Fact]
    public void The_old_held_layout_goes_at_startup_and_nothing_else_does()
    {
        tree.File("an old held set", "data", "held", "071a63b7056178f9", "Before", "files", "000-version.dll");
        tree.File("a set", "data", "paks", "prod", "OVS_P.pak");
        tree.File("[Menu]\nEntry = \"no\"\n", "data", "preferences.toml");

        using var buddy = new BuddyBuilder().Host(new HostSystem { Home = tree.Path("home"), DataDirectory = tree.Path("data") }).Build();

        Assert.False(Directory.Exists(tree.Path("data", "held")));
        Assert.Equal("a set", File.ReadAllText(tree.Path("data", "paks", "prod", "OVS_P.pak")));
        Assert.True(File.Exists(tree.Path("data", "preferences.toml")));
        // Nothing to do the next time, and no folder is made for it.
        using var again = new BuddyBuilder().Host(new HostSystem { Home = tree.Path("home"), DataDirectory = tree.Path("data") }).Build();
        Assert.False(Directory.Exists(tree.Path("data", "held")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Removing_the_data_deletes_everything_but_the_preferences_when_they_are_kept(bool keepPreferences)
    {
        tree.File("state", "data", "states", "abc.git", "HEAD");
        tree.File("cache", "data", "cache.git", "HEAD");
        tree.File("a set", "data", "paks", "prod", "OVS_P.pak");
        tree.File("a log", "data", "logs", "mvs-buddy-20261016.log");
        tree.File("[Menu]\nEntry = \"no\"\n", "data", "preferences.toml");
        var host = new HostSystem { Home = tree.Path("home"), DataDirectory = tree.Path("data"), Environment = _ => null };
        DesktopEntry.Install(host, "/opt/mvs/MvsBuddy", new Dictionary<int, byte[]> { [256] = [1] });
        using var buddy = new BuddyBuilder().Host(host).Build();

        var removal = buddy.RemoveData(keepPreferences);

        Assert.Empty(removal.Left);
        Assert.False(File.Exists(tree.Path("home", ".local", "share", "applications", "mvs-buddy.desktop")));
        Assert.Equal(keepPreferences, File.Exists(tree.Path("data", "preferences.toml")));
        Assert.Equal(!keepPreferences, !Directory.Exists(tree.Path("data")));
        if (keepPreferences)
        {
            Assert.Equal(["preferences.toml"], Directory.EnumerateFileSystemEntries(tree.Path("data")).Select(Path.GetFileName));
        }
        // The states went before the cache: both are among what was deleted, in that order.
        var order = removal.Deleted.ToList();
        Assert.True(order.IndexOf(tree.Path("data", "states")) < order.IndexOf(tree.Path("data", "cache.git")));
    }
}
