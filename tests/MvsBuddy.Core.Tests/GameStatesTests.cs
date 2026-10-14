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
        Assert.Contains("Ultimate ASI Loader goes too, since no plugin is left to load through it; it comes back with whatever is put back.", clean.Summary);
        clean.Apply();

        // Stock, and the mod's log, which no state holds.
        Assert.Equal(["MultiVersus/Binaries/Win64/MultiVersus-Win64-Shipping.exe", "MultiVersus/Binaries/Win64/plugins/OpenVersus/logs/OpenVersus.log", "MultiVersus/Content/Paks/pakchunk0-Windows.pak"],
            Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/')).Order(StringComparer.Ordinal));
        Assert.Empty(Directory.EnumerateFiles(SavedPaks));
        Assert.Equal("pak 12", Read(tree.Path("data", "paks", "testing", "OVS_P.pak")));
        Assert.Equal("mvsi pak", Read(tree.Path("data", "paks", "mvsi", "MVSI_Content_P.pak")));
        Assert.Equal("MultiVersus/Content/Paks", Assert.Single(buddy.Paks.Read("mvsi")).Home);
        var aside = buddy.States.SetAsideOf(buddy.Installs.Read(Game()));
        Assert.Equal([(SetAsideKind.OpenVersus, (Channel?)Channel.Testing), (SetAsideKind.MvsInfinite, null), (SetAsideKind.Rest, null)], aside.Select(a => (a.Kind, a.Channel)));
        Assert.Equal(4, aside.Single(a => a.Kind == SetAsideKind.Rest).Files);
        var cleaned = Everything();

        buddy.States.PrepareUndo(buddy.Installs.Read(Game())).Apply();
        Assert.Equal(before, Everything());
        Assert.Empty(buddy.States.SetAsideOf(buddy.Installs.Read(Game())));
        buddy.States.PrepareUndo(buddy.Installs.Read(Game())).Apply();
        Assert.Equal(cleaned, Everything());
        Assert.Equal(3, buddy.States.SetAsideOf(buddy.Installs.Read(Game())).Count);
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
    public async Task Turning_off_takes_the_loader_the_buddy_placed_and_leaves_one_it_did_not()
    {
        Season1Installed();
        var buddy = Buddy();
        var handInstalled = Everything();
        var off = buddy.States.PrepareTurnOff(buddy.Installs.Read(Game()));
        Assert.Contains("Ultimate ASI Loader stays: the buddy did not put it there.", off.Summary);
        off.Apply();
        Assert.True(File.Exists(Path.Combine(Win64, "version.dll")));
        Assert.Equal(InstallStatus.LoaderOnly, buddy.Installs.Read(Game()).Status);
        buddy.States.PrepareTurnOn(buddy.Installs.Read(Game()), Channel.Testing).Apply();
        Assert.Equal(handInstalled, Everything());

        (await buddy.States.PrepareInstallAsync(buddy.Installs.Read(Game()), Release("2026.10.13.1", Channel.Testing, new() { ["OVS_P.pak"] = "pak 13" }))).Apply();
        var installed = Everything();
        buddy.States.PrepareTurnOff(buddy.Installs.Read(Game())).Apply();
        Assert.False(File.Exists(Path.Combine(Win64, "version.dll")));
        Assert.Equal(InstallStatus.NotInstalled, buddy.Installs.Read(Game()).Status);
        var on = buddy.States.PrepareTurnOn(buddy.Installs.Read(Game()), Channel.Testing);
        Assert.StartsWith("Turns OpenVersus Testing 2026.10.13.01 back on as it was on ", on.Summary[0]);
        on.Apply();
        Assert.Equal(installed, Everything());
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
}
