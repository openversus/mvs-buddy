using MvsBuddy.Games;

namespace MvsBuddy.Tests;

// libraryfolders.vdf and appmanifest_1818750.acf are the real files from a Linux Steam install with three libraries,
// with paths replaced by {LIB0}..{LIB2}, ids zeroed and the apps lists cut to the game. The Epic manifest is NOT from a
// real install: only its InstallLocation is read, and the test says so.
public sealed class GameFinderTests : IDisposable
{
    private readonly TempTree tree = new();

    public void Dispose() => tree.Dispose();

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private GameFinder Finder(bool windows = false, Dictionary<string, string>? registry = null, Dictionary<string, string>? environment = null) =>
        new BuddyBuilder().Host(new HostSystem
        {
            IsWindows = windows,
            Home = tree.Path("home"),
            Registry = (key, value) => registry != null && registry.TryGetValue($@"{key}\{value}", out var v) ? v : null,
            Environment = name => environment != null && environment.TryGetValue(name, out var v) ? v : null,
            SearchRoots = () => [tree.Path("drive")],
        }).Build().Games;

    // Steam at ~/.local/share/Steam with the real three-library list; returns the library paths.
    private string[] Steam(string steamRoot)
    {
        string[] libraries = [steamRoot, tree.Path("libA"), tree.Path("libB")];
        string list = Fixture("libraryfolders.vdf");
        for (int i = 0; i < libraries.Length; i++)
        {
            list = list.Replace("{LIB" + i + "}", libraries[i].Replace("\\", "\\\\"));
        }
        tree.File(list, Path.GetRelativePath(tree.Root, steamRoot), "steamapps", "libraryfolders.vdf");
        return libraries;
    }

    private void Manifest(string library) =>
        tree.File(Fixture("appmanifest_1818750.acf"), Path.GetRelativePath(tree.Root, library), "steamapps", "appmanifest_1818750.acf");

    [Fact]
    public void Steam_on_Linux_finds_the_game_in_the_steam_folder_with_its_proton_prefix()
    {
        string[] libraries = Steam(tree.Path("home", ".local", "share", "Steam"));
        Manifest(libraries[0]);
        string win64 = tree.Game("home", ".local", "share", "Steam", "steamapps", "common", "MultiVersus");

        var install = Assert.Single(Finder().FindKnown());

        Assert.Equal(win64, install.Win64Directory);
        Assert.Equal(GameStore.Steam, install.Store);
        Assert.Equal(FoundBy.SteamLibrary, install.FoundBy);
        Assert.Equal(Path.Combine(libraries[0], "steamapps", "compatdata", "1818750", "pfx"), install.WinePrefix);
    }

    [Fact]
    public void Steam_on_Linux_finds_the_game_in_another_library()
    {
        string[] libraries = Steam(tree.Path("home", ".local", "share", "Steam"));
        Manifest(libraries[2]);
        string win64 = tree.Game("libB", "steamapps", "common", "MultiVersus");

        var install = Assert.Single(Finder().FindKnown());

        Assert.Equal(win64, install.Win64Directory);
        Assert.Equal(Path.Combine(libraries[2], "steamapps", "compatdata", "1818750", "pfx"), install.WinePrefix);
    }

    [Fact]
    public void The_old_steam_link_to_the_same_folder_lists_the_game_once()
    {
        string steam = tree.Path("home", ".local", "share", "Steam");
        Manifest(Steam(steam)[0]);
        tree.Game("home", ".local", "share", "Steam", "steamapps", "common", "MultiVersus");
        Directory.CreateDirectory(tree.Path("home", ".steam"));
        Directory.CreateSymbolicLink(tree.Path("home", ".steam", "steam"), steam);

        Assert.Single(Finder().FindKnown());
    }

    [Fact]
    public void A_manifest_without_the_game_on_disk_is_not_an_install()
    {
        Manifest(Steam(tree.Path("home", ".local", "share", "Steam"))[1]);

        Assert.Empty(Finder().FindKnown());
    }

    [Fact]
    public void The_library_list_from_before_2021_still_counts()
    {
        string steam = tree.Path("home", ".local", "share", "Steam");
        tree.File($"\"LibraryFolders\"\n{{\n\t\"TimeNextStatsReport\"\t\t\"1600000000\"\n\t\"ContentStatsID\"\t\t\"-123\"\n\t\"1\"\t\t\"{tree.Path("old")}\"\n}}\n",
            "home", ".local", "share", "Steam", "steamapps", "libraryfolders.vdf");
        Manifest(tree.Path("old"));
        string win64 = tree.Game("old", "steamapps", "common", "MultiVersus");

        Assert.Equal(win64, Assert.Single(Finder().FindKnown()).Win64Directory);
    }

    [Fact]
    public void On_Windows_steams_registry_record_comes_first_and_the_library_finds_the_same_install()
    {
        string steam = tree.Path("steam");
        Manifest(Steam(steam)[0]);
        string win64 = tree.Game("steam", "steamapps", "common", "MultiVersus");
        var registry = new Dictionary<string, string>
        {
            [@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 1818750\InstallLocation"] =
                tree.Path("steam", "steamapps", "common", "MultiVersus"),
            [@"HKEY_CURRENT_USER\Software\Valve\Steam\SteamPath"] = steam,
        };

        var install = Assert.Single(Finder(windows: true, registry).FindKnown());

        Assert.Equal(win64, install.Win64Directory);
        Assert.Equal(FoundBy.SteamRegistry, install.FoundBy);
        Assert.Null(install.WinePrefix);
    }

    [Fact]
    public void On_Windows_an_epic_manifest_points_at_the_install()
    {
        // Not a real Epic manifest: the finder reads InstallLocation and nothing else.
        string install = tree.Path("Epic Games", "MultiVersus");
        string win64 = tree.Game("Epic Games", "MultiVersus");
        tree.File($"{{ \"DisplayName\": \"MultiVersus\", \"InstallLocation\": {System.Text.Json.JsonSerializer.Serialize(install)} }}",
            "ProgramData", "Epic", "EpicGamesLauncher", "Data", "Manifests", "0123.item");
        tree.File("{ not json", "ProgramData", "Epic", "EpicGamesLauncher", "Data", "Manifests", "broken.item");

        var found = Finder(windows: true, environment: new() { ["ProgramData"] = tree.Path("ProgramData") }).FindKnown();

        var epic = Assert.Single(found);
        Assert.Equal(win64, epic.Win64Directory);
        Assert.Equal(GameStore.Epic, epic.Store);
    }

    [Fact]
    public void A_search_finds_copies_no_store_knows_and_skips_what_never_holds_a_game()
    {
        string loose = tree.Game("drive", "Games", "MVS");
        string inPrefix = tree.Game("drive", "prefixes", "mvs", "drive_c", "Games", "MultiVersus");
        tree.Game("drive", "projects", "node_modules", "decoy");
        tree.LoneExecutable("drive", "Downloads", "Steamless");
        // A link back up the tree: followed, it never ends.
        Directory.CreateSymbolicLink(tree.Path("drive", "Games", "loop"), tree.Path("drive"));

        var found = Finder().Search([tree.Path("drive")], null, CancellationToken.None);

        Assert.Equal([loose, inPrefix], found.Select(i => i.Win64Directory).Order(StringComparer.Ordinal));
        Assert.All(found, i => Assert.Equal(FoundBy.Search, i.FoundBy));
        Assert.Equal(tree.Path("drive", "prefixes", "mvs"), found.Single(i => i.Win64Directory == inPrefix).WinePrefix);
        Assert.Null(found.Single(i => i.Win64Directory == loose).WinePrefix);
    }

    [Fact]
    public void A_search_finds_a_steam_copy_with_its_librarys_prefix()
    {
        string win64 = tree.Game("drive", "SteamLibrary", "steamapps", "common", "MultiVersus");

        var install = Assert.Single(Finder().Search([tree.Path("drive")], null, CancellationToken.None));

        Assert.Equal(win64, install.Win64Directory);
        Assert.Equal(GameStore.Steam, install.Store);
        Assert.Equal(tree.Path("drive", "SteamLibrary", "steamapps", "compatdata", "1818750", "pfx"), install.WinePrefix);
    }

    [Fact]
    public void A_search_reports_each_install_as_it_finds_it_shallow_ones_first()
    {
        string deep = tree.Game("drive", "a", "b", "c", "d", "e", "f", "MVS");
        string shallow = tree.Game("drive", "z", "MVS");
        var reports = new List<SearchProgress>();

        Finder().Search([tree.Path("drive")], new Synchronous<SearchProgress>(reports.Add), CancellationToken.None);

        Assert.Equal([shallow, deep], reports.Where(r => r.Found != null).Select(r => r.Found!.Win64Directory));
    }

    [Fact]
    public async Task A_search_stops_when_cancelled()
    {
        tree.Game("drive", "Games", "MVS");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Finder().SearchAsync(null, cancel.Token));
    }

    [Fact]
    public void A_picked_folder_that_is_the_game_is_verified_whichever_level_was_picked()
    {
        string win64 = tree.Game("picked");
        var finder = Finder();

        foreach (string path in new[] { tree.Path("picked"), tree.Path("picked", "MultiVersus"), win64, Path.Combine(win64, GameFiles.ExecutableName) })
        {
            var install = finder.Choose(path);
            Assert.Equal(win64, install.Win64Directory);
            Assert.Equal(FoundBy.Chosen, install.FoundBy);
            Assert.True(install.Verified);
            Assert.Null(install.UnverifiedReason);
        }
    }

    [Fact]
    public void A_picked_folder_that_is_not_the_game_is_still_the_install_unverified_with_the_reason()
    {
        string lone = tree.LoneExecutable("unpacked");
        Directory.CreateDirectory(tree.Path("empty"));
        var finder = Finder();

        var exeOnly = finder.Choose(lone);
        var exeFile = finder.Choose(Path.Combine(lone, GameFiles.ExecutableName));
        var empty = finder.Choose(tree.Path("empty"));
        var missing = finder.Choose(tree.Path("not", "yet"));

        Assert.All(new[] { exeOnly, exeFile, empty, missing }, i => Assert.False(i.Verified));
        Assert.Equal(lone, exeOnly.Win64Directory);
        Assert.Equal(lone, exeFile.Win64Directory);
        Assert.Contains("Content", exeOnly.UnverifiedReason);
        Assert.Equal(tree.Path("empty"), empty.Win64Directory);
        Assert.Contains("There is no " + GameFiles.ExecutableName, empty.UnverifiedReason);
        Assert.Equal(tree.Path("not", "yet"), missing.Win64Directory);
        Assert.Contains("does not exist", missing.UnverifiedReason);
    }

    [Fact]
    public void Everything_the_finder_finds_is_verified()
    {
        Manifest(Steam(tree.Path("home", ".local", "share", "Steam"))[0]);
        tree.Game("home", ".local", "share", "Steam", "steamapps", "common", "MultiVersus");
        tree.Game("drive", "Games", "MVS");
        var finder = Finder();

        var found = finder.FindKnown().Concat(finder.Search([tree.Path("drive")], null, CancellationToken.None)).ToList();

        Assert.Equal(2, found.Count);
        Assert.All(found, i => Assert.True(i.Verified));
    }

    // Progress<T> posts to the thread pool; the test wants the reports in order, as they happen.
    private sealed class Synchronous<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
