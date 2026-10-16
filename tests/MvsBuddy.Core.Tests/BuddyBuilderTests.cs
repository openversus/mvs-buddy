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
}
