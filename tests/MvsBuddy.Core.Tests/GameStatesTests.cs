using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using LibGit2Sharp;
using MvsBuddy.Catalog;
using MvsBuddy.Changes;
using MvsBuddy.Games;
using MvsBuddy.Installs;

namespace MvsBuddy.Tests;

// Installs, channel switches and undo over the real layouts (Season 1 testing, the April layout); file contents are
// placeholders except the settings, which are the real Season 1 OpenVersus.toml (prod's: the same with prod's address).
[Collection("libgit2")]
public sealed class GameStatesTests : IDisposable
{
    private const string TestingUrl = "http://prod.openversus.org:8420/";
    private const string ProdUrl = "https://prod.openversus.org/";
    private static readonly byte[] LoaderDll = [.. Encoding.ASCII.GetBytes("MZ pinned "), .. Encoding.Unicode.GetBytes("Ultimate ASI Loader")];
    private static readonly byte[] License = Encoding.ASCII.GetBytes("MIT License\n");
    private static readonly byte[] LoaderZip = Zip(new() { ["dinput8.dll"] = (LoaderDll, false) });
    private readonly TempTree tree = new();
    private readonly FakeGitHub server = new FakeGitHub()
        .Answer("/ual/Ultimate-ASI-Loader_x64.zip", () => Bytes(LoaderZip))
        .Answer("/ual/license", () => Bytes(License));

    public void Dispose() => tree.Dispose();

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static AsiLoaderPin Pin() => AsiLoaderPin.Default with
    {
        DllSha256 = Sha(LoaderDll),
        LicenseSha256 = Sha(License),
        ZipSha256 = Sha(LoaderZip),
        ZipUrl = new Uri("https://example.test/ual/Ultimate-ASI-Loader_x64.zip"),
        LicenseUrl = new Uri("https://example.test/ual/license"),
    };

    private Buddy Buddy(Func<bool>? running = null) => new BuddyBuilder().Http(new HttpClient(server)).AsiLoader(Pin())
        .Host(new HostSystem { Home = tree.Path("home"), DataDirectory = tree.Path("data"), IsGameRunning = running ?? (() => false) }).Build();

    private string Root => tree.Path("steamapps", "common", "MultiVersus");

    private string Win64 => Path.Combine(Root, "MultiVersus", "Binaries", "Win64");

    private string Home => Path.Combine(Win64, "plugins", "OpenVersus");

    private string SavedPaks => tree.Path("steamapps", "compatdata", "1818750", "pfx", "drive_c", "users", "steamuser", "AppData", "Local", "MultiVersus", "Saved", "Paks");

    private string[] Rel(string path) => Path.GetRelativePath(tree.Root, path).Split(Path.DirectorySeparatorChar);

    private GameInstall Game()
    {
        if (!File.Exists(Path.Combine(Win64, GameFiles.ExecutableName)))
        {
            tree.Game("steamapps", "common", "MultiVersus");
            Directory.CreateDirectory(SavedPaks);
        }
        return new GameInstall(Win64, GameStore.Steam, FoundBy.SteamLibrary) { WinePrefix = tree.Path("steamapps", "compatdata", "1818750", "pfx") };
    }

    private static byte[] Zip(Dictionary<string, (byte[] Bytes, bool Executable)> files)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create))
        {
            foreach (var (name, (content, executable)) in files)
            {
                var entry = zip.CreateEntry(name);
                // As zip on Linux writes them: a regular file's mode in the high half.
                entry.ExternalAttributes = (executable ? 0x81ED : 0x81A4) << 16;
                using var stream = entry.Open();
                stream.Write(content);
            }
        }
        return bytes.ToArray();
    }

    // A release: its zip on the fake GitHub (the mod's folder with its settings, a Linux executable, and a readme the
    // install never uses), its paks (a prod release's own; testing's on paks-current, which this replaces).
    private Release Release(string version, Channel channel, Dictionary<string, string> paks)
    {
        string url = channel == Channel.Testing ? TestingUrl : ProdUrl;
        byte[] zip = Zip(new()
        {
            [$"plugins/OpenVersus/OpenVersus_{version}.asi"] = (Encoding.UTF8.GetBytes("asi " + version), false),
            ["plugins/OpenVersus/OpenVersus.toml"] = (Encoding.UTF8.GetBytes(Fixture("OpenVersus.testing.toml").Replace(TestingUrl, url)), false),
            ["plugins/OpenVersus/node/linux-x64/OVS.Rollback.Node"] = (Encoding.UTF8.GetBytes("node " + version), true),
            ["readme.txt"] = (Encoding.UTF8.GetBytes("readme"), false),
        });
        string tag = channel == Channel.Testing ? $"{version}-testing" : version;
        string path = $"/rel/{tag}/OpenVersus_release.zip";
        server.Answer(path, () => Bytes(zip));
        var pakAssets = paks.Select(p =>
        {
            byte[] content = Encoding.UTF8.GetBytes(p.Value);
            string at = $"/paks/{tag}/{p.Key}";
            server.Answer(at, () => Bytes(content));
            return new ReleaseAsset(p.Key, content.Length, new Uri("https://example.test" + at), Sha(content));
        }).ToList();
        if (channel == Channel.Testing)
        {
            string assets = string.Join(",", pakAssets.Select(a =>
                $"{{\"name\":\"{a.Name}\",\"size\":{a.Size},\"browser_download_url\":\"{a.DownloadUrl}\",\"digest\":\"sha256:{a.Sha256}\"}}"));
            server.Json("/repos/openversus/ovs-paks/releases/tags/paks-current", $"{{\"tag_name\":\"paks-current\",\"assets\":[{assets}]}}");
        }
        var zipAsset = new ReleaseAsset("OpenVersus_release.zip", zip.Length, new Uri("https://example.test" + path), Sha(zip));
        var parsed = System.Version.Parse(version);
        return new Release(tag, tag, channel, parsed, null, channel == Channel.Prod ? [zipAsset, .. pakAssets] : [zipAsset]);
    }

    private int Requests(string prefix) => server.Requests.Count(r => r.RequestUri!.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal));

    // Season 1 testing, installed by hand: its plugin, settings (one of them the person's own), a node, state, a log, an
    // older loader, and its paks.
    private void Season1Installed()
    {
        Game();
        string[] home = Rel(Home);
        tree.File("asi 2026.10.12.1", [.. home, "OpenVersus_2026.10.12.1.asi"]);
        tree.File(Fixture("OpenVersus.testing.toml").Replace("ToggleMenu = \"F1\"", "ToggleMenu = \"F2\""), [.. home, "OpenVersus.toml"]);
        string node = tree.File("node 2026.10.12.1", [.. home, "node", "linux-x64", "OVS.Rollback.Node"]);
        File.SetUnixFileMode(node, File.GetUnixFileMode(node) | UnixFileMode.UserExecute);
        tree.File("state testing", [.. home, "OVSState.toml"]);
        tree.File("a log", [.. home, "logs", "OpenVersus.log"]);
        tree.Bytes([.. Encoding.ASCII.GetBytes("MZ older "), .. Encoding.Unicode.GetBytes("Ultimate ASI Loader")], [.. Rel(Win64), "version.dll"]);
        tree.File("pak 12", [.. Rel(SavedPaks), "OVS_P.pak"]);
    }

    // Every file of the game and the profile's paks as "path sha256 [x]", sorted; x is the owner's execute bit.
    private List<string> Everything() => new[] { Root, SavedPaks }.Where(Directory.Exists)
        .SelectMany(folder => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        .Select(f => $"{Path.GetRelativePath(tree.Root, f)} {Sha(File.ReadAllBytes(f))}{((File.GetUnixFileMode(f) & UnixFileMode.UserExecute) != 0 ? " x" : "")}")
        .Order(StringComparer.Ordinal).ToList();

    private GitStore States(Buddy buddy) => buddy.States.Open(buddy.States.Layout(Game()));

    private static string Read(string path) => File.ReadAllText(path);

    [Fact]
    public async Task An_install_over_season_1_carries_what_is_the_persons_and_undo_puts_back_every_byte_and_mode()
    {
        Season1Installed();
        var buddy = Buddy();
        var before = Everything();
        var release = Release("2026.10.13.1", Channel.Testing, new() { ["OVS_P.pak"] = "pak 13" });

        var install = await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), release);
        Assert.Null(install.Refused);
        Assert.Contains("The OpenVersus here is Testing (a guess from its server address", string.Join("\n", install.Summary));
        install.Apply();

        var after = buddy.Installs.Read(Game());
        Assert.Equal([Path.Combine(Home, "OpenVersus_2026.10.13.1.asi")], after.Mod.Plugins);
        Assert.Equal("state testing", Read(Path.Combine(Home, "OVSState.toml")));
        Assert.Equal("a log", Read(Path.Combine(Home, "logs", "OpenVersus.log")));
        Assert.Equal("node 2026.10.13.1", Read(Path.Combine(Home, "node", "linux-x64", "OVS.Rollback.Node")));
        Assert.True((File.GetUnixFileMode(Path.Combine(Home, "node", "linux-x64", "OVS.Rollback.Node")) & UnixFileMode.UserExecute) != 0);
        Assert.Equal("F2", TomlSettings.Parse(Read(after.Mod.Settings!.Path), "t").Get("Settings.Keybinds", "ToggleMenu"));
        Assert.True(after.Loaders.Single().IsPinnedBuild);
        Assert.Equal((Channel.Testing, ChannelSource.Install), (after.Channel!.Channel, after.Channel.Source));
        Assert.False(File.Exists(Path.Combine(Root, "readme.txt")));
        Assert.Equal("pak 13", Read(Path.Combine(SavedPaks, "OVS_P.pak")));
        // The paks it replaced are the testing set now, for switching back or undoing.
        Assert.Equal("pak 12", Read(tree.Path("data", "paks", "testing", "OVS_P.pak")));
        Assert.Equal(["OVS_P.pak"], buddy.Paks.Read("testing").Select(p => p.Name));
        var store = States(buddy);
        foreach (string branch in new[] { "current", "previous", "testing" })
        {
            Assert.DoesNotContain(store.Paths(store.TreeOf(store.Tip(branch)!)), p => p.Contains("/logs/", StringComparison.Ordinal));
        }
        Assert.False(Directory.Exists(Path.Combine(Root, ".git")));
        var installed = Everything();

        buddy.States.PrepareUndo(buddy.Installs.Read(Game())).Apply();
        Assert.Equal(before, Everything());

        // Undoing the undo is the install again.
        buddy.States.PrepareUndo(buddy.Installs.Read(Game())).Apply();
        Assert.Equal(installed, Everything());
    }

    [Fact]
    public async Task Each_channel_gets_back_its_own_saved_state_and_paks_and_never_the_others()
    {
        Game();
        var buddy = Buddy();
        var testing = Release("2026.10.13.1", Channel.Testing, new() { ["OVS_T.pak"] = "testing pak" });
        var prod = Release("2026.10.14.1", Channel.Prod, new() { ["OVS_P.pak"] = "prod pak" });
        string state = Path.Combine(Home, "OVSState.toml");

        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), testing)).Apply();
        File.WriteAllText(state, "testing id");
        var toProd = await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), prod);
        Assert.Contains("Its saved state stays with Testing, for switching back; Prod starts with a new one.", toProd.Summary);
        Assert.Contains("1 pak it does not use is kept with Testing, for switching back.", toProd.Summary);
        toProd.Apply();
        Assert.False(File.Exists(state));
        Assert.Equal(["OVS_P.pak"], Directory.EnumerateFiles(SavedPaks).Select(Path.GetFileName));
        File.WriteAllText(state, "prod id");

        int pakDownloads = Requests("/paks/");
        var back = await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), testing);
        Assert.Contains("Its saved state stays with Prod, for switching back; Testing gets back its own from when it was last here.", back.Summary);
        back.Apply();
        Assert.Equal("testing id", Read(state));
        Assert.Equal(["OVS_T.pak"], Directory.EnumerateFiles(SavedPaks).Select(Path.GetFileName));
        Assert.Equal(TestingUrl, buddy.Installs.Read(Game()).Mod.Settings!.GameServerUrl);

        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), prod)).Apply();
        Assert.Equal("prod id", Read(state));
        Assert.Equal(ProdUrl, buddy.Installs.Read(Game()).Mod.Settings!.GameServerUrl);
        // Switching back and forth fetched no pak again: each channel's set waited in the buddy's folder.
        Assert.Equal(pakDownloads, Requests("/paks/"));
        Assert.Equal("testing pak", Read(tree.Path("data", "paks", "testing", "OVS_T.pak")));
    }

    [Fact]
    public async Task A_file_no_state_has_where_a_change_writes_one_refuses_and_nothing_changes()
    {
        Season1Installed();
        var buddy = Buddy();
        // Installed with the loader named xinput1_3.dll: the older copy under version.dll goes with the install.
        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), Release("2026.10.13.1", Channel.Testing, new() { ["OVS_P.pak"] = "pak 13" }),
            new InstallOptions { LoaderName = "xinput1_3.dll" })).Apply();
        Assert.False(File.Exists(Path.Combine(Win64, "version.dll")));
        // Then another program's version.dll, where undoing would put the older loader back.
        string theirs = tree.File("MZ another program", [.. Rel(Win64), "version.dll"]);
        var unchanged = Everything();

        var undo = buddy.States.PrepareUndo(buddy.Installs.Read(Game()));
        var e = Assert.Throws<ChangeException>(undo.Apply);

        Assert.Contains(theirs, e.Message);
        Assert.Equal(unchanged, Everything());

        // And an install over it under that name says so before anything is done.
        var install = await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), Release("2026.10.13.2", Channel.Testing, new() { ["OVS_P.pak"] = "pak 13" }));
        Assert.StartsWith($"{theirs} is another program's file", install.Refused);
        Assert.Throws<ChangeException>(install.Apply);
        Assert.Equal(unchanged, Everything());
    }

    [Fact]
    public async Task Paks_that_cannot_be_moved_put_the_files_back_and_leave_the_undo_as_it_was()
    {
        Season1Installed();
        var buddy = Buddy();
        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), Release("2026.10.13.1", Channel.Testing, new() { ["OVS_P.pak"] = "pak 13" }))).Apply();
        string previous = States(buddy).Tip("previous")!;
        var installed = Everything();

        var next = await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), Release("2026.10.13.2", Channel.Testing, new() { ["OVS_P.pak"] = "pak 14" }));
        // The downloaded pak disappears before the change is made.
        File.Delete(Assert.Single(Directory.EnumerateFiles(tree.Path("data", "downloads"), "OVS_P.pak", SearchOption.AllDirectories)));
        Assert.Throws<ChangeException>(next.Apply);

        Assert.Equal(installed, Everything());
        Assert.Equal(previous, States(buddy).Tip("previous"));
    }

    [Fact]
    public async Task A_state_keeps_no_second_copy_of_what_the_cache_has_and_the_cache_keeps_what_a_state_uses()
    {
        Season1Installed();
        var buddy = Buddy();
        var release = Release("2026.10.13.1", Channel.Testing, new() { ["OVS_P.pak"] = "pak 13" });
        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), release)).Apply();
        var layout = buddy.States.Layout(Game());

        Assert.Equal("../../../cache.git/objects\n", File.ReadAllText(Path.Combine(layout.GitDir, "objects", "info", "alternates")));
        string asi = layout.Prefix + "plugins/OpenVersus/OpenVersus_2026.10.13.1.asi";
        string current = States(buddy).TreeOf(States(buddy).Tip("current")!);
        // Undo and redo snapshot the installed files too, staging every one: still nothing the cache has is written again.
        buddy.States.PrepareUndo(buddy.Installs.Read(Game())).Apply();
        buddy.States.PrepareUndo(buddy.Installs.Read(Game())).Apply();
        current = States(buddy).TreeOf(States(buddy).Tip("current")!);
        using (var holding = new Repository(layout.GitDir))
        using (var cache = new Repository(tree.Path("data", "cache.git")))
        {
            var blob = ((Tree)holding.Lookup(current))[asi].Target.Id;
            Assert.NotNull(cache.Lookup(blob));
            var loose = Directory.EnumerateFiles(Path.Combine(layout.GitDir, "objects"), "*", SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(Path.GetDirectoryName(f))!.Length == 2)
                .Select(f => new ObjectId(Path.GetFileName(Path.GetDirectoryName(f)) + Path.GetFileName(f))).ToList();
            Assert.NotEmpty(loose);
            Assert.DoesNotContain(loose, id => cache.ObjectDatabase.Contains(id));
        }

        // The release leaves the cache (its tag and branch dropped): the state that has it keeps it.
        var cacheStore = buddy.Cache.Store();
        cacheStore.DeleteTag(release.Tag);
        cacheStore.DeleteBranch("testing");
        buddy.Cache.Prune();

        Assert.Equal(Encoding.UTF8.GetBytes("asi 2026.10.13.1"), States(buddy).Read(current, asi));
        buddy.States.PrepareUndo(buddy.Installs.Read(Game())).Apply();
        buddy.States.PrepareUndo(buddy.Installs.Read(Game())).Apply();
        Assert.Equal("asi 2026.10.13.1", Read(Path.Combine(Home, "OpenVersus_2026.10.13.1.asi")));
    }

    [Fact]
    public async Task An_update_over_the_april_layout_leaves_its_settings_and_state_where_the_mod_takes_them_over()
    {
        Game();
        string[] plugins = [.. Rel(Win64), "plugins"];
        tree.File("old asi", [.. plugins, "OpenVersus.asi"]);
        tree.File("[Settings]", [.. plugins, "OpenVersus.ini"]);
        tree.File("state", [.. plugins, "OVSState.ini"]);
        // An older one still beside the game, with its settings beside it.
        tree.File("older asi", [.. Rel(Win64), "OpenVersus.asi"]);
        tree.File("[Older]", [.. Rel(Win64), "PatternsCache.cache"]);
        tree.Bytes([.. Encoding.ASCII.GetBytes("MZ april "), .. Encoding.Unicode.GetBytes("Ultimate ASI Loader")], [.. Rel(Win64), "xinput1_3.dll"]);
        var buddy = Buddy();
        var before = Everything();

        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), Release("2026.10.13.1", Channel.Testing, []))).Apply();

        var after = buddy.Installs.Read(Game());
        Assert.Equal([Path.Combine(Home, "OpenVersus_2026.10.13.1.asi")], after.Mod.Plugins);
        Assert.Equal("state", Read(Path.Combine(Win64, "plugins", "OVSState.ini")));
        Assert.Equal("[Settings]", Read(Path.Combine(Win64, "plugins", "OpenVersus.ini")));
        Assert.Equal("[Older]", Read(Path.Combine(Win64, "plugins", "PatternsCache.cache")));
        Assert.False(File.Exists(Path.Combine(Win64, "plugins", "OpenVersus.asi")));
        Assert.False(File.Exists(Path.Combine(Win64, "OpenVersus.asi")));
        Assert.False(File.Exists(Path.Combine(Win64, "PatternsCache.cache")));

        buddy.States.PrepareUndo(buddy.Installs.Read(Game())).Apply();
        Assert.Equal(before, Everything());
    }

    [Fact]
    public async Task A_folder_the_person_chose_is_its_own_working_tree_and_is_made_when_it_is_not_there()
    {
        var buddy = Buddy();
        Directory.CreateDirectory(tree.Path("pfx", "drive_c", "users", "steamuser"));
        var game = new GameInstall(tree.Path("new", "Win64"), GameStore.Unknown, FoundBy.Chosen) { Verified = false, UnverifiedReason = "test", WinePrefix = tree.Path("pfx") };

        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(game), Release("2026.10.13.1", Channel.Testing, new() { ["OVS_P.pak"] = "pak 13" }))).Apply();

        var after = buddy.Installs.Read(game);
        Assert.Equal(InstallStatus.Installed, after.Status);
        Assert.Equal("version.dll", Assert.Single(after.Loaders).FileName);
        Assert.Equal("", buddy.States.Layout(game).Prefix);
        Assert.Equal(["OVS_P.pak"], after.Paks.Select(p => p.Name));

        buddy.States.PrepareUndo(after).Apply();
        Assert.Empty(Directory.EnumerateFiles(tree.Path("new"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task An_install_leaves_alone_what_a_state_tracks_that_is_not_openversus()
    {
        Season1Installed();
        var buddy = Buddy();
        // Another mod's file the state tracks, as an undone clean start leaves it.
        string other = tree.File("another mod", [.. Rel(Root), "MultiVersus", "Content", "Paks", "~mods", "Other_P.pak"]);
        States(buddy).Snapshot(["MultiVersus/Content/Paks/~mods/Other_P.pak"], "tracked");

        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), Release("2026.10.13.1", Channel.Testing, new() { ["OVS_P.pak"] = "pak 13" }))).Apply();

        Assert.Equal("another mod", Read(other));
        Assert.Contains("MultiVersus/Content/Paks/~mods/Other_P.pak", States(buddy).Paths(States(buddy).TreeOf(States(buddy).Tip("current")!)));
        Assert.False(File.Exists(Path.Combine(Home, "OpenVersus_2026.10.12.1.asi")), "what OpenVersus owns still goes");
    }

    [Fact]
    public async Task Installing_the_same_release_again_puts_back_what_was_deleted_since()
    {
        Game();
        var buddy = Buddy();
        var release = Release("2026.10.13.1", Channel.Testing, new() { ["OVS_P.pak"] = "pak 13" });
        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), release)).Apply();
        var installed = Everything();
        File.Delete(Path.Combine(Win64, "version.dll"));
        File.Delete(Path.Combine(Home, "node", "linux-x64", "OVS.Rollback.Node"));

        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), release)).Apply();

        // All back, the record aside: it says when this install was made.
        Assert.Equal(installed.Where(f => !f.Contains(InstallRecord.FileName, StringComparison.Ordinal)),
            Everything().Where(f => !f.Contains(InstallRecord.FileName, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Paks_go_by_checksum_and_the_ones_the_release_does_not_use_are_kept_as_the_channels_set()
    {
        Season1Installed();
        tree.File("same", [.. Rel(SavedPaks), "OVS_P.pak"]);
        tree.File("old ucas", [.. Rel(SavedPaks), "OVS_P.ucas"]);
        tree.File("retired", [.. Rel(SavedPaks), "OVS_Retired_P.pak"]);
        string mods = Path.Combine(Root, "MultiVersus", "Content", "Paks", "~mods");
        tree.File("stray", [.. Rel(mods), "OVS_P.utoc"]);
        tree.File("sig", [.. Rel(mods), "OVS_P.sig"]);
        // What an earlier switch left in the testing set: replaced as it is.
        tree.File("older yet", "data", "paks", "testing", "OVS_Older_P.pak");
        var buddy = Buddy();
        var before = Everything();
        var release = Release("2026.10.13.1", Channel.Testing, new() { ["OVS_P.pak"] = "same", ["OVS_P.ucas"] = "new ucas", ["OVS_P.sig"] = "sig" });

        var install = await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), release);
        Assert.Contains("Undo puts the mod, the loader and the paks back as they are now, except that the paks found in the game's own folders go where the mod keeps them.", install.Summary);
        install.Apply();

        Assert.Equal(["OVS_P.pak", "OVS_P.sig", "OVS_P.ucas"], Directory.EnumerateFiles(SavedPaks).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("new ucas", Read(Path.Combine(SavedPaks, "OVS_P.ucas")));
        Assert.Equal(1, Requests("/paks/"));
        Assert.False(Directory.Exists(mods), "the hand-placed paks left the game's folder, and their folder with them");
        Assert.Equal(["OVS_P.ucas", "OVS_P.utoc", "OVS_Retired_P.pak"], buddy.Paks.Read("testing").Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["OVS_P.ucas", "OVS_P.utoc", "OVS_Retired_P.pak", "manifest.json"],
            Directory.EnumerateFiles(tree.Path("data", "paks", "testing")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(["testing"], Directory.EnumerateDirectories(tree.Path("data", "paks")).Select(Path.GetFileName));

        buddy.States.PrepareUndo(buddy.Installs.Read(Game())).Apply();
        // Undone, everything is as it was, except that the hand-placed paks are where the mod keeps them, not in the game's folder.
        var handPlaced = new[] { "OVS_P.utoc", "OVS_P.sig" };
        Assert.Equal(before.Where(f => !f.Contains("~mods", StringComparison.Ordinal)),
            Everything().Where(f => !handPlaced.Any(p => f.StartsWith(Path.GetRelativePath(tree.Root, Path.Combine(SavedPaks, p)) + " ", StringComparison.Ordinal))));
        Assert.Equal(("stray", "sig"), (Read(Path.Combine(SavedPaks, "OVS_P.utoc")), Read(Path.Combine(SavedPaks, "OVS_P.sig"))));
    }

    // Season 1 by hand, plus what a clean start finds: MVS Infinite (a plugin and a pak in the game's Content/Paks), and
    // everything else (another mod's pak, a tool's folder, a loose file). Returns the extras of each kind, as StockCheck would.
    private List<StockExtra> ModdedEverywhere()
    {
        Season1Installed();
        string Add(string text, params string[] path) => tree.File(text, [.. Rel(Root), .. path]);
        var extras = new List<StockExtra>();
        void Extra(string file, ExtraKind kind) => extras.Add(new StockExtra(file, Path.GetRelativePath(Root, file), new FileInfo(file).Length, kind));
        Extra(Path.Combine(Home, "OpenVersus_2026.10.12.1.asi"), ExtraKind.OpenVersus);
        Extra(Add("mvsi asi", "MultiVersus", "Binaries", "Win64", "plugins", "MVSIASI.asi"), ExtraKind.MvsInfinite);
        Extra(Add("mvsi pak", "MultiVersus", "Content", "Paks", "MVSI_Content_P.pak"), ExtraKind.MvsInfinite);
        Extra(Add("ue4ss", "MultiVersus", "Binaries", "Win64", "plugins", "UE4SS", "ue4ss.dll"), ExtraKind.Other);
        Extra(Add("another mod", "MultiVersus", "Content", "Paks", "~mods", "Other_P.pak"), ExtraKind.Other);
        Extra(Add("notes", "notes.txt"), ExtraKind.Other);
        return extras;
    }

    private static List<string> Without(IEnumerable<string> listing, params string[] parts) =>
        listing.Where(l => !parts.Any(p => l.Contains(p, StringComparison.Ordinal))).ToList();

    [Fact]
    public void A_clean_start_sets_each_kind_aside_on_its_own_and_undo_puts_back_every_byte()
    {
        var extras = ModdedEverywhere();
        var buddy = Buddy();
        var before = Everything();

        var clean = buddy.States.PrepareSetAside(buddy.Installs.Read(Game()), extras);
        Assert.Null(clean.Refused);
        Assert.Contains("Ultimate ASI Loader stays: one was here before the buddy first changed this copy.", clean.Summary);
        clean.Apply();

        // Stock, the mod's log, which no state holds, and the loader that was here at first sight.
        Assert.Equal(["MultiVersus/Binaries/Win64/MultiVersus-Win64-Shipping.exe", "MultiVersus/Binaries/Win64/plugins/OpenVersus/logs/OpenVersus.log",
            "MultiVersus/Binaries/Win64/version.dll", "MultiVersus/Content/Paks/pakchunk0-Windows.pak"],
            Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/')).Order(StringComparer.Ordinal));
        Assert.Empty(Directory.EnumerateFiles(SavedPaks));
        Assert.Equal("pak 12", Read(tree.Path("data", "paks", "testing", "OVS_P.pak")));
        Assert.Equal("mvsi pak", Read(tree.Path("data", "paks", "mvsi", "MVSI_Content_P.pak")));
        Assert.Equal("MultiVersus/Content/Paks", Assert.Single(buddy.Paks.Read("mvsi")).Home);
        var aside = buddy.States.SetAsideOf(buddy.Installs.Read(Game()));
        Assert.Equal([(SetAsideKind.OpenVersus, (Channel?)Channel.Testing), (SetAsideKind.MvsInfinite, null), (SetAsideKind.Rest, null)], aside.Select(a => (a.Kind, a.Channel)));
        // Its three files; the loader stayed, so no copy of it rides along.
        Assert.Equal(3, aside.Single(a => a.Kind == SetAsideKind.Rest).Files);
        var cleaned = Everything();

        buddy.States.PrepareUndo(buddy.Installs.Read(Game())).Apply();
        Assert.Equal(before, Everything());
        Assert.Empty(buddy.States.SetAsideOf(buddy.Installs.Read(Game())));
        buddy.States.PrepareUndo(buddy.Installs.Read(Game())).Apply();
        Assert.Equal(cleaned, Everything());
        Assert.Equal(3, buddy.States.SetAsideOf(buddy.Installs.Read(Game())).Count);
    }

    [Fact]
    public async Task The_first_change_keeps_initial_and_the_states_do_not_start_tracking_what_it_holds()
    {
        ModdedEverywhere();
        var buddy = Buddy();
        string mvsi = "MultiVersus/Binaries/Win64/plugins/MVSIASI.asi";

        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), Release("2026.10.13.1", Channel.Testing, new() { ["OVS_P.pak"] = "pak 13" }))).Apply();

        var store = States(buddy);
        string initial = store.Tip("initial")!;
        // Without the game's manifests, what the buddy recognises: the mod as it was (not its log), the loader, other plugins.
        Assert.Equal(
        [
            "MultiVersus/Binaries/Win64/plugins/MVSIASI.asi",
            "MultiVersus/Binaries/Win64/plugins/OpenVersus/OVSState.toml",
            "MultiVersus/Binaries/Win64/plugins/OpenVersus/OpenVersus.toml",
            "MultiVersus/Binaries/Win64/plugins/OpenVersus/OpenVersus_2026.10.12.1.asi",
            "MultiVersus/Binaries/Win64/plugins/OpenVersus/node/linux-x64/OVS.Rollback.Node",
            "MultiVersus/Binaries/Win64/version.dll",
        ], store.Paths(store.TreeOf(initial)).Order(StringComparer.Ordinal));
        string message = store.Describe(initial).Message;
        Assert.Contains("\ncovers\trecognised\n", message);
        Assert.Contains("\nloader\tversion.dll\t", message);
        Assert.Equal([("OVS_P.pak", (string?)null), ("MVSI_Content_P.pak", "MultiVersus/Content/Paks")],
            GameStates.PaksIn(message).Select(p => (p.Name, p.Home)));
        // The states track what they did before: not MVS Infinite's plugin, which only `initial` holds.
        Assert.DoesNotContain(mvsi, store.Paths(store.TreeOf(store.Tip("current")!)));
        Assert.DoesNotContain(mvsi, store.Paths(store.TreeOf(store.Tip("previous")!)));

        buddy.States.PrepareTurnOff(buddy.Installs.Read(Game())).Apply();
        Assert.Equal(initial, States(buddy).Tip("initial"));
        Assert.True(File.Exists(Path.Combine(Root, mvsi)));
    }

    [Fact]
    public async Task Rolling_back_puts_back_the_first_look_with_what_a_clean_start_set_aside_and_keeps_what_was_added_since()
    {
        var extras = ModdedEverywhere();
        var buddy = Buddy();
        var first = Everything();
        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), Release("2026.10.13.1", Channel.Testing, new() { ["OVS_P.pak"] = "pak 13" }))).Apply();
        buddy.States.PrepareSetAside(buddy.Installs.Read(Game()), extras.Where(e => e.Kind == ExtraKind.MvsInfinite)).Apply();
        string later = tree.File("later", [.. Rel(Root), "later.txt"]);
        var before = Everything();

        var roll = buddy.States.PrepareRollBack(buddy.Installs.Read(Game()));
        Assert.Null(roll.Refused);
        Assert.Contains("What a clean start set aside of it comes back, so it is not set aside any more.", roll.Summary);
        roll.Apply();

        // The mod by hand, its older loader, its paks, MVS Infinite with its pak: every byte and mode; the file added since stays.
        Assert.Equal(first, Without(Everything(), "later.txt"));
        Assert.Equal("later", Read(later));
        Assert.DoesNotContain(buddy.States.SetAsideOf(buddy.Installs.Read(Game())), a => a.Kind == SetAsideKind.MvsInfinite);

        buddy.States.PrepareUndo(buddy.Installs.Read(Game())).Apply();
        Assert.Equal(before, Everything());
        Assert.Contains(buddy.States.SetAsideOf(buddy.Installs.Read(Game())), a => a.Kind == SetAsideKind.MvsInfinite);
    }

    [Fact]
    public async Task Rolling_back_right_after_an_install_is_not_stopped_by_the_first_looks_own_files_still_here()
    {
        ModdedEverywhere();
        var buddy = Buddy();
        var first = Everything();
        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), Release("2026.10.13.1", Channel.Testing, new() { ["OVS_P.pak"] = "pak 13" }))).Apply();
        Assert.NotNull(buddy.States.FirstLookOf(Game()));

        // MVS Infinite's plugin is here and no state tracks it: it is the first look's own, not in the way.
        buddy.States.PrepareRollBack(buddy.Installs.Read(Game())).Apply();

        Assert.Equal(first, Everything());
    }

    [Fact]
    public void A_roll_back_needs_a_first_look_and_its_paks()
    {
        Season1Installed();
        var buddy = Buddy();
        Assert.Null(buddy.States.FirstLookOf(Game()));
        Assert.NotNull(buddy.States.PrepareRollBack(buddy.Installs.Read(Game())).Refused);

        // An uninstall deletes the paks for good, so the hand install's cannot come back.
        buddy.States.PrepareUninstall(buddy.Installs.Read(Game())).Apply();
        var roll = buddy.States.PrepareRollBack(buddy.Installs.Read(Game()));
        Assert.Contains("OVS_P.pak", roll.Refused);
        Assert.Throws<ChangeException>(roll.Apply);
    }

    [Fact]
    public void What_a_clean_start_set_aside_comes_back_part_by_part()
    {
        var extras = ModdedEverywhere();
        var buddy = Buddy();
        var before = Everything();
        buddy.States.PrepareSetAside(buddy.Installs.Read(Game()), extras).Apply();

        var on = buddy.States.PrepareTurnOn(buddy.Installs.Read(Game()), Channel.Testing);
        Assert.Null(on.Refused);
        on.Apply();
        Assert.Equal("state testing", Read(Path.Combine(Home, "OVSState.toml")));
        Assert.Equal("pak 12", Read(Path.Combine(SavedPaks, "OVS_P.pak")));
        Assert.False(File.Exists(Path.Combine(Win64, "plugins", "MVSIASI.asi")), "turning OpenVersus on brings back OpenVersus only");

        buddy.States.PreparePutBack(buddy.Installs.Read(Game()), SetAsideKind.MvsInfinite).Apply();
        Assert.Equal("mvsi pak", Read(Path.Combine(Root, "MultiVersus", "Content", "Paks", "MVSI_Content_P.pak")));
        Assert.False(File.Exists(Path.Combine(Root, "notes.txt")));

        buddy.States.PreparePutBack(buddy.Installs.Read(Game()), SetAsideKind.Rest).Apply();
        Assert.Equal(before, Everything());
        Assert.Empty(buddy.States.SetAsideOf(buddy.Installs.Read(Game())));
        Assert.NotNull(buddy.States.PreparePutBack(buddy.Installs.Read(Game()), SetAsideKind.Rest).Refused);
    }

    [Fact]
    public async Task Files_a_clean_start_put_back_stay_through_an_install_and_through_undo()
    {
        var extras = ModdedEverywhere().Where(e => e.Kind == ExtraKind.Other).ToList();
        var buddy = Buddy();
        var before = Everything();
        buddy.States.PrepareSetAside(buddy.Installs.Read(Game()), extras).Apply();
        buddy.States.PrepareUndo(buddy.Installs.Read(Game())).Apply();
        Assert.Equal(before, Everything());

        // Undone, they are tracked by the live state; an install must leave them be.
        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), Release("2026.10.13.1", Channel.Testing, new() { ["OVS_P.pak"] = "pak 13" }))).Apply();
        Assert.Equal(("notes", "another mod"), (Read(Path.Combine(Root, "notes.txt")), Read(Path.Combine(Root, "MultiVersus", "Content", "Paks", "~mods", "Other_P.pak"))));

        // A put-back too: undo sets them aside again, and undoing that brings them back.
        buddy.States.PrepareSetAside(buddy.Installs.Read(Game()), extras).Apply();
        var putBack = buddy.States.PreparePutBack(buddy.Installs.Read(Game()), SetAsideKind.Rest);
        Assert.Contains("Undo sets them aside again.", putBack.Summary);
        putBack.Apply();
        var back = Everything();
        buddy.States.PrepareUndo(buddy.Installs.Read(Game())).Apply();
        Assert.False(File.Exists(Path.Combine(Root, "notes.txt")));
        // Set aside again, as the summary said: listed, and kept past the next change.
        Assert.Contains(buddy.States.SetAsideOf(buddy.Installs.Read(Game())), a => a.Kind == SetAsideKind.Rest);
        tree.File("e", [.. Rel(Root), "e.txt"]);
        buddy.States.PrepareSetAside(buddy.Installs.Read(Game()), [new StockExtra(Path.Combine(Root, "e.txt"), "e.txt", 1, ExtraKind.Other)]).Apply();
        buddy.States.PreparePutBack(buddy.Installs.Read(Game()), SetAsideKind.Rest).Apply();
        Assert.Equal(back.Count + 1, Everything().Count);
        Assert.Equal("notes", Read(Path.Combine(Root, "notes.txt")));
    }

    [Fact]
    public void A_second_clean_start_adds_to_the_first_and_the_newer_copy_of_a_path_wins()
    {
        var buddy = Buddy();
        Game();
        string notes = tree.File("old notes", [.. Rel(Root), "notes.txt"]);
        string a = tree.File("a", [.. Rel(Root), "a.txt"]);
        StockExtra Other(string file) => new(file, Path.GetRelativePath(Root, file), new FileInfo(file).Length, ExtraKind.Other);
        buddy.States.PrepareSetAside(buddy.Installs.Read(Game()), [Other(notes), Other(a)]).Apply();
        tree.File("new notes", [.. Rel(Root), "notes.txt"]);
        string b = tree.File("b", [.. Rel(Root), "b.txt"]);

        var again = buddy.States.PrepareSetAside(buddy.Installs.Read(Game()), [Other(notes), Other(b)]);
        Assert.Contains("1 of them was set aside before too; the copy set aside now replaces the older.", again.Summary);
        again.Apply();

        var store = States(buddy);
        Assert.Equal(["a.txt", "b.txt", "notes.txt"], store.Paths(store.TreeOf(store.Tip("clean-start")!)).Order(StringComparer.Ordinal));
        buddy.States.PreparePutBack(buddy.Installs.Read(Game()), SetAsideKind.Rest).Apply();
        Assert.Equal(("new notes", "a", "b"), (Read(notes), Read(a), Read(b)));
    }

    [Fact]
    public async Task A_loader_here_at_first_sight_stays_through_every_change()
    {
        // Season 1 by hand with its older loader: the first change sees a loader, so this copy always keeps one.
        Season1Installed();
        var buddy = Buddy();
        var handInstalled = Everything();
        var off = buddy.States.PrepareTurnOff(buddy.Installs.Read(Game()));
        Assert.Contains("Ultimate ASI Loader stays: one was here before the buddy first changed this copy.", off.Summary);
        off.Apply();
        Assert.True(File.Exists(Path.Combine(Win64, "version.dll")));
        Assert.Equal(InstallStatus.LoaderOnly, buddy.Installs.Read(Game()).Status);
        buddy.States.PrepareTurnOn(buddy.Installs.Read(Game()), Channel.Testing).Apply();
        Assert.Equal(handInstalled, Everything());

        // An install replaces it with the pinned build (undo puts the older one back); a turn-off still leaves a loader.
        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), Release("2026.10.13.1", Channel.Testing, new() { ["OVS_P.pak"] = "pak 13" }))).Apply();
        var installed = Everything();
        buddy.States.PrepareUndo(buddy.Installs.Read(Game())).Apply();
        Assert.Equal(handInstalled, Everything());
        buddy.States.PrepareUndo(buddy.Installs.Read(Game())).Apply();
        Assert.Equal(installed, Everything());
        var again = buddy.States.PrepareTurnOff(buddy.Installs.Read(Game()));
        Assert.Contains("Ultimate ASI Loader stays: one was here before the buddy first changed this copy.", again.Summary);
        again.Apply();
        Assert.True(buddy.Installs.Read(Game()).Loaders.Single().IsPinnedBuild);
        var on = buddy.States.PrepareTurnOn(buddy.Installs.Read(Game()), Channel.Testing);
        Assert.StartsWith("Turns OpenVersus Testing 2026.10.13.01 back on as it was on ", on.Summary[0]);
        on.Apply();
        Assert.Equal(installed, Everything());
    }

    [Fact]
    public async Task A_loader_the_buddy_placed_on_a_copy_that_had_none_goes_with_a_turn_off_and_a_clean_start()
    {
        Game();
        var buddy = Buddy();
        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), Release("2026.10.13.1", Channel.Testing, new() { ["OVS_T.pak"] = "testing pak" }))).Apply();
        var installed = Everything();

        buddy.States.PrepareTurnOff(buddy.Installs.Read(Game())).Apply();
        Assert.Equal(InstallStatus.NotInstalled, buddy.Installs.Read(Game()).Status);
        buddy.States.PrepareTurnOn(buddy.Installs.Read(Game()), Channel.Testing).Apply();
        Assert.Equal(installed, Everything());

        string asi = Path.Combine(Home, "OpenVersus_2026.10.13.1.asi");
        var clean = buddy.States.PrepareSetAside(buddy.Installs.Read(Game()), [new StockExtra(asi, Path.GetRelativePath(Root, asi), new FileInfo(asi).Length, ExtraKind.OpenVersus)]);
        Assert.Contains("Ultimate ASI Loader goes too, since no plugin is left to load through it; it comes back with whatever is put back.", clean.Summary);
        clean.Apply();
        Assert.Equal(InstallStatus.NotInstalled, buddy.Installs.Read(Game()).Status);
    }

    [Fact]
    public async Task Turning_off_after_a_channel_switch_keeps_the_other_channels_paks_through_undo()
    {
        Game();
        var buddy = Buddy();
        var testing = Release("2026.10.13.1", Channel.Testing, new() { ["OVS_T.pak"] = "testing pak" });
        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), testing)).Apply();
        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), Release("2026.10.14.1", Channel.Prod, new() { ["OVS_P.pak"] = "prod pak" }))).Apply();
        var onProd = Everything();

        buddy.States.PrepareTurnOff(buddy.Installs.Read(Game())).Apply();
        buddy.States.PrepareUndo(buddy.Installs.Read(Game())).Apply();

        Assert.Equal(onProd, Everything());
        Assert.Equal("testing pak", Read(tree.Path("data", "paks", "testing", "OVS_T.pak")));
        int downloads = Requests("/paks/");
        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), testing)).Apply();
        Assert.Equal(downloads, Requests("/paks/"));
    }

    [Fact]
    public void What_is_deleted_for_good_cannot_be_put_back_and_its_files_are_freed()
    {
        var buddy = Buddy();
        Game();
        StockExtra Other(string file) => new(file, Path.GetRelativePath(Root, file), new FileInfo(file).Length, ExtraKind.Other);
        string big = tree.File("only here", [.. Rel(Root), "big.bin"]);
        buddy.States.PrepareSetAside(buddy.Installs.Read(Game()), [Other(big)]).Apply();
        // A second clean start moves undo past the first, so only what is set aside still holds big.bin.
        string other = tree.File("c", [.. Rel(Root), "c.txt"]);
        buddy.States.PrepareSetAside(buddy.Installs.Read(Game()), [Other(other)]).Apply();
        var gitDir = buddy.States.Layout(Game()).GitDir;
        // Git's name for the file's contents: the SHA-1 of "blob <size>\0" and the bytes.
        string blob = Convert.ToHexString(SHA1.HashData([.. Encoding.ASCII.GetBytes("blob 9\0"), .. Encoding.UTF8.GetBytes("only here")])).ToLowerInvariant();
        string loose = Path.Combine(gitDir, "objects", blob[..2], blob[2..]);
        Assert.True(File.Exists(loose));

        buddy.States.Discard(Game(), SetAsideKind.Rest);

        Assert.False(File.Exists(loose));
        Assert.DoesNotContain(buddy.States.SetAsideOf(buddy.Installs.Read(Game())), a => a.Kind == SetAsideKind.Rest);
        Assert.NotNull(buddy.States.PreparePutBack(buddy.Installs.Read(Game()), SetAsideKind.Rest).Refused);
    }

    [Fact]
    public async Task Nothing_changes_while_the_game_runs_and_there_is_nothing_to_undo_at_first()
    {
        Season1Installed();
        bool running = true;
        var buddy = Buddy(() => running);
        var unchanged = Everything();

        Assert.False(buddy.States.CanUndo(Game()));
        Assert.NotNull(buddy.States.PrepareUndo(buddy.Installs.Read(Game())).Refused);
        var install = await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), Release("2026.10.13.1", Channel.Testing, new() { ["OVS_P.pak"] = "pak 13" }));
        Assert.Contains("running", Assert.Throws<ChangeException>(install.Apply).Message);
        Assert.Equal(unchanged, Everything());

        running = false;
        install.Apply();
        Assert.True(buddy.States.CanUndo(Game()));
    }

    // Whether the release cache still has a file of these contents (git's name for it: the SHA-1 of "blob <size>\0" and the bytes).
    private bool CacheHas(string contents)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(contents);
        string blob = Convert.ToHexString(SHA1.HashData([.. Encoding.ASCII.GetBytes($"blob {bytes.Length}\0"), .. bytes])).ToLowerInvariant();
        return File.Exists(tree.Path("data", "cache.git", "objects", blob[..2], blob[2..]));
    }

    [Fact]
    public async Task The_cache_keeps_what_is_installed_and_the_newest_of_a_channel_by_version_not_by_when_it_was_fetched()
    {
        Game();
        var buddy = Buddy();
        var cache = buddy.Cache.Store();
        var v1 = Release("2026.10.13.1", Channel.Testing, new() { ["OVS_T.pak"] = "testing pak" });
        var v2 = Release("2026.10.13.2", Channel.Testing, new() { ["OVS_T.pak"] = "testing pak" });
        var v3 = Release("2026.10.13.3", Channel.Testing, new() { ["OVS_T.pak"] = "testing pak" });
        async Task Install(Release release) => (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), release)).Apply();

        await Install(v1);
        await Install(v3);
        // v2 is fetched last, though older than v3.
        await Install(v2);
        Assert.Null(cache.Tagged(v1.Tag));
        Assert.False(CacheHas("asi 2026.10.13.1"));
        Assert.NotNull(cache.Tagged(v2.Tag));
        Assert.Equal(cache.Tagged(v3.Tag), cache.Tip("testing"));

        await Install(v3);
        Assert.NotNull(cache.Tagged(v2.Tag));
        // Off: v3 is the only one any state names, and the newest.
        buddy.States.PrepareTurnOff(buddy.Installs.Read(Game())).Apply();
        Assert.Equal([v3.Tag], cache.Tags().Keys);
        Assert.Equal(cache.Tagged(v3.Tag), cache.Tip("testing"));
        Assert.False(CacheHas("asi 2026.10.13.2"));
        Assert.True(CacheHas("asi 2026.10.13.3"));
    }

    [Fact]
    public async Task A_copys_states_that_cannot_be_read_stop_the_cache_dropping_or_deleting_anything()
    {
        Game();
        var buddy = Buddy();
        var cache = buddy.Cache.Store();
        var v1 = Release("2026.10.13.1", Channel.Testing, new() { ["OVS_T.pak"] = "testing pak" });
        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), v1)).Apply();
        Directory.CreateDirectory(tree.Path("data", "states", "unreadable.git"));

        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), Release("2026.10.13.2", Channel.Testing, new() { ["OVS_T.pak"] = "testing pak" }))).Apply();
        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), Release("2026.10.13.3", Channel.Testing, new() { ["OVS_T.pak"] = "testing pak" }))).Apply();

        Assert.NotNull(cache.Tagged(v1.Tag));
        // Nothing names v1 now; the unreadable copy might, so its files stay.
        cache.DeleteTag(v1.Tag);
        Assert.Equal((0, 0L), buddy.Cache.Prune());
        Assert.True(CacheHas("asi 2026.10.13.1"));
    }

    [Fact]
    public async Task The_cache_keeps_what_another_copy_has_installed()
    {
        Game();
        var buddy = Buddy();
        var other = new GameInstall(tree.Game("other", "MultiVersus"), GameStore.Steam, FoundBy.SteamLibrary);
        var v1 = Release("2026.10.13.1", Channel.Testing, new() { ["OVS_T.pak"] = "testing pak" });
        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(other), v1)).Apply();

        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), Release("2026.10.13.2", Channel.Testing, new() { ["OVS_T.pak"] = "testing pak" }))).Apply();
        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), Release("2026.10.13.3", Channel.Testing, new() { ["OVS_T.pak"] = "testing pak" }))).Apply();

        Assert.NotNull(buddy.Cache.Store().Tagged(v1.Tag));
        Assert.False((await buddy.Cache.GetAsync(v1)).Downloaded);
    }

    [Fact]
    public async Task The_cache_keeps_a_kept_channels_release_and_after_an_uninstall_only_the_newest_of_each()
    {
        Game();
        var buddy = Buddy();
        var cache = buddy.Cache.Store();
        var v1 = Release("2026.10.13.1", Channel.Testing, new() { ["OVS_T.pak"] = "testing pak" });
        var v2 = Release("2026.10.13.2", Channel.Testing, new() { ["OVS_T.pak"] = "testing pak" });
        var p1 = Release("2026.10.14.1", Channel.Prod, new() { ["OVS_P.pak"] = "prod pak" });
        var p2 = Release("2026.10.14.2", Channel.Prod, new() { ["OVS_P.pak"] = "prod pak" });
        async Task Install(Release release) => (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), release)).Apply();

        await Install(v2);
        await Install(v1);
        buddy.States.PrepareTurnOff(buddy.Installs.Read(Game())).Apply();
        await Install(p1);
        await Install(p2);
        // v1 is neither installed now, nor undo's, nor the newest: only the kept Testing state names it.
        Assert.NotNull(cache.Tagged(v1.Tag));

        buddy.States.PrepareUninstall(buddy.Installs.Read(Game())).Apply();

        Assert.Equal([v2.Tag, p2.Tag], cache.Tags().Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_person_can_say_which_channel_a_copy_is_and_the_next_change_files_it_there()
    {
        Season1Installed();
        var buddy = Buddy();

        buddy.States.PrepareSetChannel(buddy.Installs.Read(Game()), Channel.Prod).Apply();
        var said = buddy.Installs.Read(Game());
        Assert.Equal(new ModChannel(Channel.Prod, ChannelSource.Person), said.Channel);
        buddy.States.PrepareSetChannel(said, Channel.Testing).Apply();
        Assert.Equal(new ModChannel(Channel.Testing, ChannelSource.Person), buddy.Installs.Read(Game()).Channel);
        Assert.Equal(Channel.Prod, said.Record!.ChannelValue);
        Assert.False(buddy.States.CanUndo(Game()));

        // The guess was Testing; said Prod, the turn-off keeps it as Prod.
        buddy.States.PrepareSetChannel(buddy.Installs.Read(Game()), Channel.Prod).Apply();
        buddy.States.PrepareTurnOff(buddy.Installs.Read(Game())).Apply();
        Assert.Equal([(SetAsideKind.OpenVersus, (Channel?)Channel.Prod)], buddy.States.SetAsideOf(buddy.Installs.Read(Game())).Select(a => (a.Kind, a.Channel)));
    }

    private void InstallId(string id) => File.WriteAllText(Path.Combine(Home, "OVSState.toml"), $"[Identity]\nInstallId = \"{id}\"\n");

    private IEnumerable<(SetAsideKind, Channel?)> Kept(Buddy buddy) => buddy.States.SetAsideOf(buddy.Installs.Read(Game())).Select(a => (a.Kind, a.Channel));

    [Theory]
    [InlineData("p1")]
    [InlineData(null)]
    public async Task Saying_the_channel_after_a_change_drops_this_same_install_filed_under_the_old_one(string? id)
    {
        Game();
        var buddy = Buddy();
        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), Release("2026.10.14.1", Channel.Prod, new() { ["OVS_P.pak"] = "prod pak" }))).Apply();
        if (id != null)
        {
            InstallId(id);
        }
        buddy.States.PrepareTurnOff(buddy.Installs.Read(Game())).Apply();
        buddy.States.PrepareTurnOn(buddy.Installs.Read(Game()), Channel.Prod).Apply();

        var said = buddy.States.PrepareSetChannel(buddy.Installs.Read(Game()), Channel.Testing);
        Assert.Contains("What the buddy kept as OpenVersus Prod is this same install, so it is not kept as Prod any more.", said.Summary);
        said.Apply();

        Assert.Empty(Kept(buddy));
        // Its paks stay where the turn-on left them, and undo still has its own.
        Assert.Equal(["OVS_P.pak"], Directory.EnumerateFiles(SavedPaks).Select(Path.GetFileName));
        Assert.True(buddy.States.CanUndo(Game()));
    }

    [Fact]
    public async Task Saying_the_channel_keeps_another_install_filed_under_the_old_one()
    {
        Game();
        var buddy = Buddy();
        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), Release("2026.10.14.1", Channel.Prod, new() { ["OVS_P.pak"] = "prod pak" }))).Apply();
        InstallId("p1");
        buddy.States.PrepareTurnOff(buddy.Installs.Read(Game())).Apply();
        buddy.States.PrepareTurnOn(buddy.Installs.Read(Game()), Channel.Prod).Apply();
        // Since then, another install of the mod here: a new identity.
        InstallId("p2");

        var said = buddy.States.PrepareSetChannel(buddy.Installs.Read(Game()), Channel.Testing);
        Assert.Equal(["The buddy counts this copy's OpenVersus as Testing from now on."], said.Summary);
        said.Apply();

        Assert.Equal([(SetAsideKind.OpenVersus, (Channel?)Channel.Prod)], Kept(buddy));
    }

    [Fact]
    public async Task A_loader_already_in_place_as_the_same_build_stays_the_persons_through_turn_off_and_uninstall()
    {
        Game();
        string loader = tree.Bytes(LoaderDll, [.. Rel(Win64), "version.dll"]);
        var buddy = Buddy();
        var install = await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), Release("2026.10.13.1", Channel.Testing, new() { ["OVS_T.pak"] = "testing pak" }));
        Assert.Contains(install.Summary, s => s.EndsWith(" is already in place as version.dll.", StringComparison.Ordinal));
        install.Apply();
        Assert.Null(buddy.Installs.Read(Game()).Record!.Loader);

        var off = buddy.States.PrepareTurnOff(buddy.Installs.Read(Game()));
        Assert.Contains("Ultimate ASI Loader stays: one was here before the buddy first changed this copy.", off.Summary);
        off.Apply();
        Assert.True(File.Exists(loader));
        buddy.States.PrepareTurnOn(buddy.Installs.Read(Game()), Channel.Testing).Apply();

        var uninstall = buddy.States.PrepareUninstall(buddy.Installs.Read(Game()));
        Assert.Contains("Ultimate ASI Loader stays: one was here before the buddy first changed this copy.", uninstall.Summary);
        uninstall.Apply();
        Assert.True(File.Exists(loader));
    }

    [Fact]
    public async Task Uninstall_deletes_the_mod_its_paks_and_every_kept_channel_and_leaves_nothing_to_undo()
    {
        Game();
        var buddy = Buddy();
        var bare = Everything();
        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), Release("2026.10.13.1", Channel.Testing, new() { ["OVS_T.pak"] = "testing pak" }))).Apply();
        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), Release("2026.10.14.1", Channel.Prod, new() { ["OVS_P.pak"] = "prod pak" }))).Apply();

        var uninstall = buddy.States.PrepareUninstall(buddy.Installs.Read(Game()));
        Assert.Null(uninstall.Refused);
        Assert.Equal([
            "Deletes OpenVersus 2026.10.14.01 (Prod) from the game's folder.",
            "Deletes its pak.",
            "Deletes OpenVersus Testing, kept for this copy, with its saved state and its paks.",
            "Deletes Ultimate ASI Loader too: the buddy put it there, and no plugin is left to load through it.",
            "There is no undo for this: to have OpenVersus again, install it.",
        ], uninstall.Summary);
        uninstall.Apply();

        Assert.Equal(bare, Everything());
        Assert.False(Directory.Exists(Home));
        Assert.Empty(buddy.States.SetAsideOf(buddy.Installs.Read(Game())));
        Assert.False(buddy.States.CanUndo(Game()));
        Assert.All(new[] { "prod", "testing", "other" }, set => Assert.False(Directory.Exists(tree.Path("data", "paks", set))));
    }

    [Fact]
    public void Uninstall_leaves_a_loader_the_buddy_did_not_place_and_the_mods_logs()
    {
        Season1Installed();
        var buddy = Buddy();

        var uninstall = buddy.States.PrepareUninstall(buddy.Installs.Read(Game()));
        Assert.Contains("Ultimate ASI Loader stays: one was here before the buddy first changed this copy.", uninstall.Summary);
        Assert.Contains("The mod's logs stay in plugins/OpenVersus/logs: the buddy never touches them.", uninstall.Summary);
        uninstall.Apply();

        Assert.Equal(["a log"], Directory.EnumerateFiles(Home, "*", SearchOption.AllDirectories).Select(Read));
        Assert.True(File.Exists(Path.Combine(Win64, "version.dll")));
        Assert.Empty(Directory.EnumerateFiles(SavedPaks));
        Assert.Equal(InstallStatus.LoaderOnly, buddy.Installs.Read(Game()).Status);
        Assert.False(buddy.States.CanUndo(Game()));
        Assert.Empty(buddy.States.SetAsideOf(buddy.Installs.Read(Game())));
        Assert.False(Directory.Exists(tree.Path("data", "paks", "testing")));
    }

    [Fact]
    public void Uninstall_without_a_known_prefix_says_the_paks_stay_and_leaves_them()
    {
        Season1Installed();
        string stray = tree.File("stray", [.. Rel(Root), "MultiVersus", "Content", "Paks", "~mods", "OVS_Audio_P.pak"]);
        var buddy = Buddy();
        var noPrefix = Game() with { WinePrefix = null };
        var state = buddy.Installs.Read(noPrefix);
        Assert.Null(state.SavedPaksDirectory);

        var uninstall = buddy.States.PrepareUninstall(state);
        Assert.DoesNotContain(uninstall.Summary, s => s.StartsWith("Deletes its", StringComparison.Ordinal));
        Assert.Contains("Its paks stay where they are: the buddy cannot tell which Wine prefix this copy runs in.", uninstall.Summary);
        uninstall.Apply();

        Assert.Equal("stray", Read(stray));
        Assert.Equal("pak 12", Read(Path.Combine(SavedPaks, "OVS_P.pak")));
    }

    [Fact]
    public async Task Uninstall_after_turning_off_deletes_what_is_kept_and_changes_nothing_in_the_game()
    {
        Game();
        var buddy = Buddy();
        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), Release("2026.10.13.1", Channel.Testing, new() { ["OVS_T.pak"] = "testing pak" }))).Apply();
        tree.File("mvsi asi", [.. Rel(Win64), "plugins", "MVSIASI.asi"]);
        buddy.States.PrepareTurnOff(buddy.Installs.Read(Game())).Apply();
        var off = Everything();

        var uninstall = buddy.States.PrepareUninstall(buddy.Installs.Read(Game()));
        Assert.Equal([
            "Deletes OpenVersus Testing, kept for this copy, with its saved state and its paks.",
            "Ultimate ASI Loader stays: MVSIASI.asi still loads through it.",
            "There is no undo for this: to have OpenVersus again, install it.",
        ], uninstall.Summary);
        uninstall.Apply();

        Assert.Equal(off, Everything());
        Assert.Empty(buddy.States.SetAsideOf(buddy.Installs.Read(Game())));
        Assert.False(buddy.States.CanUndo(Game()));
        Assert.False(Directory.Exists(tree.Path("data", "paks", "testing")));
        Assert.NotNull(buddy.States.PrepareUninstall(buddy.Installs.Read(Game())).Refused);
    }
}
