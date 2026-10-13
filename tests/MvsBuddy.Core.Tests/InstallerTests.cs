using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MvsBuddy.Catalog;
using MvsBuddy.Changes;
using MvsBuddy.Games;
using MvsBuddy.Installs;

namespace MvsBuddy.Tests;

// Installs over the real layouts (Season 1 testing, the April layout); file contents are placeholders except the settings,
// which are the real sample.toml and the Season 1 OpenVersus.toml.
public sealed class InstallerTests : IDisposable
{
    private static readonly byte[] LoaderDll = [.. Encoding.ASCII.GetBytes("MZ pinned "), .. Encoding.Unicode.GetBytes("Ultimate ASI Loader")];
    private static readonly byte[] License = Encoding.ASCII.GetBytes("MIT License\n");
    private readonly TempTree tree = new();

    public void Dispose() => tree.Dispose();

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static readonly Release Testing = new("2026.10.13.01-testing", "Testing", Channel.Testing, new Version(2026, 10, 13, 1), null, []);

    private Buddy Buddy(HttpMessageHandler? http = null, AsiLoaderPin? pin = null) =>
        new BuddyBuilder().Http(new HttpClient(http ?? new FakeGitHub())).AsiLoader(pin ?? Pin()).Host(new HostSystem
        {
            Home = tree.Path("home"),
            DataDirectory = tree.Path("data"),
        }).Build();

    private static AsiLoaderPin Pin() => AsiLoaderPin.Default with { DllSha256 = Sha(LoaderDll), LicenseSha256 = Sha(License) };

    private string Win64 => tree.Path("steamapps", "common", "MultiVersus", "MultiVersus", "Binaries", "Win64");

    private string SavedPaks => tree.Path("steamapps", "compatdata", "1818750", "pfx", "drive_c", "users", "steamuser", "AppData", "Local", "MultiVersus", "Saved", "Paks");

    private GameInstall Game()
    {
        if (!File.Exists(Path.Combine(Win64, GameFiles.ExecutableName)))
        {
            tree.Game("steamapps", "common", "MultiVersus");
            // Started once, so Proton has made its prefix.
            Directory.CreateDirectory(tree.Path("steamapps", "compatdata", "1818750", "pfx", "drive_c", "users"));
        }
        return new GameInstall(Win64, GameStore.Steam, FoundBy.SteamLibrary) { WinePrefix = tree.Path("steamapps", "compatdata", "1818750", "pfx") };
    }

    private string[] Rel(string path) => Path.GetRelativePath(tree.Root, path).Split(Path.DirectorySeparatorChar);

    // A release staged as PrepareAsync leaves it: its plugin folder extracted, its paks, the loader and its license.
    private StagedInstall Stage(Dictionary<string, string> plugin, Dictionary<string, string> paks, string? defaults = null)
    {
        string stage = tree.Path("data", "stage-" + Guid.NewGuid().ToString("N"));
        foreach (var (name, text) in plugin)
        {
            tree.File(text, [.. Rel(stage), "extracted", "plugins", "OpenVersus", .. name.Split('/')]);
        }
        var staged = paks.Select(p => (p.Key, (string?)tree.File(p.Value, [.. Rel(stage), "paks", p.Key]), Sha(Encoding.UTF8.GetBytes(p.Value)))).ToList();
        return new StagedInstall(Path.Combine(stage, "extracted"), staged,
            tree.Bytes(LoaderDll, [.. Rel(stage), "loader", "dinput8.dll"]), tree.Bytes(License, [.. Rel(stage), "license", Installer.LicenseName]), defaults);
    }

    private static Dictionary<string, string> NewRelease() => new()
    {
        ["OpenVersus_2026.10.13.01.asi"] = "asi 2026.10.13.01",
        ["OpenVersus.toml"] = Fixture("OpenVersus.testing.toml"),
        ["node/linux-x64/appsettings.json"] = "node 13",
    };

    private void Season1Installed()
    {
        string[] home = [.. Rel(Win64), "plugins", "OpenVersus"];
        tree.File("asi 2026.10.12.01", [.. home, "OpenVersus_2026.10.12.01.asi"]);
        tree.File(Fixture("OpenVersus.testing.toml").Replace("ToggleMenu = \"F1\"", "ToggleMenu = \"F2\""), [.. home, "OpenVersus.toml"]);
        tree.File("node 12", [.. home, "node", "linux-x64", "appsettings.json"]);
        tree.File("state", [.. home, "OVSState.toml"]);
        tree.File("a log", [.. home, "logs", "OpenVersus.log"]);
        tree.Bytes([.. Encoding.ASCII.GetBytes("MZ older "), .. Encoding.Unicode.GetBytes("Ultimate ASI Loader")], [.. Rel(Win64), "version.dll"]);
    }

    [Fact]
    public void A_fresh_install_into_a_folder_that_does_not_exist_makes_it()
    {
        var buddy = Buddy();
        Directory.CreateDirectory(tree.Path("pfx", "drive_c"));
        var game = new GameInstall(tree.Path("new", "Win64"), GameStore.Unknown, FoundBy.Chosen) { Verified = false, UnverifiedReason = "test", WinePrefix = tree.Path("pfx") };
        var state = buddy.Installs.Read(game);

        var install = buddy.Installer.Plan(state, Testing, Stage(NewRelease(), new() { ["OVS_P.pak"] = "pak 13" }), new InstallOptions());
        install.Plan.Apply();

        var after = buddy.Installs.Read(game);
        Assert.Contains(install.Summary, s => s.Contains("does not exist yet"));
        Assert.Equal(InstallStatus.Installed, after.Status);
        Assert.Equal("2026.10.13.01", ModVersion.Text(after.Mod.Version!));
        Assert.True(Assert.Single(after.Loaders).IsPinnedBuild);
        Assert.Equal("version.dll", after.Loaders[0].FileName);
        Assert.True(File.Exists(tree.Path("new", "Win64", Installer.LicenseName)));
        Assert.Equal(["OVS_P.pak"], after.Paks.Select(p => p.Name));
    }

    [Fact]
    public void An_update_over_season_1_keeps_state_and_logs_backs_up_what_it_replaces_and_merges_settings()
    {
        Season1Installed();
        var buddy = Buddy();

        var install = buddy.Installer.Plan(buddy.Installs.Read(Game()), Testing, Stage(NewRelease(), []), new InstallOptions());
        install.Plan.Apply();

        var after = buddy.Installs.Read(Game());
        string home = Path.Combine(Win64, "plugins", "OpenVersus");
        Assert.Equal([Path.Combine(home, "OpenVersus_2026.10.13.01.asi")], after.Mod.Plugins);
        Assert.Equal("state", File.ReadAllText(Path.Combine(home, "OVSState.toml")));
        Assert.Equal("a log", File.ReadAllText(Path.Combine(home, "logs", "OpenVersus.log")));
        Assert.Equal("node 13", File.ReadAllText(Path.Combine(home, "node", "linux-x64", "appsettings.json")));
        Assert.Equal("F2", after.Mod.Settings is { } s ? TomlSettings.Parse(File.ReadAllText(s.Path), "t").Get("Settings.Keybinds", "ToggleMenu") : null);
        Assert.Equal("http://prod.openversus.org:8420/", after.Mod.Settings!.GameServerUrl);
        var backup = Assert.Single(after.Held);
        Assert.StartsWith("Before 2026.10.13.01", backup.Name);
        Assert.Equal(
            ["OpenVersus.toml", "OpenVersus_2026.10.12.01.asi", "appsettings.json", "version.dll"],
            backup.Entries.Select(e => Path.GetFileName(e.Original)).Order(StringComparer.Ordinal));
        Assert.True(after.Loaders.Single().IsPinnedBuild);
    }

    [Fact]
    public void An_update_over_the_april_layout_leaves_its_settings_for_the_mod_to_take_over()
    {
        string[] plugins = [.. Rel(Win64), "plugins"];
        tree.File("old asi", [.. plugins, "OpenVersus.asi"]);
        tree.File("[Settings]", [.. plugins, "OpenVersus.ini"]);
        tree.File("state", [.. plugins, "OVSState.ini"]);
        tree.Bytes([.. Encoding.ASCII.GetBytes("MZ april "), .. Encoding.Unicode.GetBytes("Ultimate ASI Loader")], [.. Rel(Win64), "xinput1_3.dll"]);
        var buddy = Buddy();
        var release = NewRelease();
        release.Remove("OpenVersus.toml");
        release["sample_config.toml"] = Fixture("sample.toml");

        buddy.Installer.Plan(buddy.Installs.Read(Game()), Testing, Stage(release, []), new InstallOptions()).Plan.Apply();

        var after = buddy.Installs.Read(Game());
        Assert.Equal(ModLayout.Current, after.Mod.Layout);
        Assert.DoesNotContain(after.Mod.OldLayoutFiles, f => f.EndsWith(".asi", StringComparison.Ordinal));
        Assert.Equal("[Settings]", File.ReadAllText(Path.Combine(Win64, "plugins", "OpenVersus.ini")));
        Assert.Null(after.Mod.Settings);
        Assert.Equal(["version.dll"], after.Loaders.Select(l => l.FileName));
    }

    [Fact]
    public void Paks_go_by_checksum_and_stale_and_stray_ones_are_set_aside()
    {
        Season1Installed();
        tree.File("same", [.. Rel(SavedPaks), "OVS_P.pak"]);
        tree.File("old ucas", [.. Rel(SavedPaks), "OVS_P.ucas"]);
        tree.File("retired", [.. Rel(SavedPaks), "OVS_Retired_P.pak"]);
        tree.File("stray", "steamapps", "common", "MultiVersus", "MultiVersus", "Content", "Paks", "~mods", "OVS_P.utoc");
        var buddy = Buddy();
        var staged = Stage(NewRelease(), new() { ["OVS_P.pak"] = "same", ["OVS_P.ucas"] = "new ucas" });

        var install = buddy.Installer.Plan(buddy.Installs.Read(Game()), Testing, staged, new InstallOptions());
        install.Plan.Apply();

        var after = buddy.Installs.Read(Game());
        Assert.Equal(["OVS_P.pak", "OVS_P.ucas"], after.Paks.Select(p => p.Name));
        Assert.Equal("new ucas", File.ReadAllText(Path.Combine(SavedPaks, "OVS_P.ucas")));
        Assert.True(File.Exists(staged.Paks.Single(p => p.Name == "OVS_P.pak").Path), "an identical pak is left where it was downloaded");
        Assert.Empty(after.PaksInGame);
        var setAside = after.Held.Single().Entries.Select(e => Path.GetFileName(e.Original)).ToList();
        Assert.Contains("OVS_P.ucas", setAside);
        Assert.Contains("OVS_Retired_P.pak", setAside);
        Assert.Contains("OVS_P.utoc", setAside);
        Assert.DoesNotContain("OVS_P.pak", setAside);
    }

    [Fact]
    public void Another_programs_dll_under_the_loaders_name_is_refused_before_anything_moves()
    {
        Season1Installed();
        File.WriteAllText(Path.Combine(Win64, "version.dll"), "MZ some other proxy");
        var buddy = Buddy();

        var e = Assert.Throws<ChangeException>(buddy.Installer.Plan(buddy.Installs.Read(Game()), Testing, Stage(NewRelease(), []), new InstallOptions()).Plan.Apply);

        Assert.Contains("xinput1_3.dll", e.Message);
        Assert.True(File.Exists(Path.Combine(Win64, "plugins", "OpenVersus", "OpenVersus_2026.10.12.01.asi")));
        Assert.Empty(buddy.Installs.Read(Game()).Held);
    }

    // Locks a folder with Unix file modes, which Windows does not have.
    [SkippableFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void A_failure_late_in_the_install_puts_everything_back_and_leaves_no_backup()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "locks a folder with Unix file modes");
        Season1Installed();
        string locked = Path.Combine(Win64, "plugins", "OpenVersus", "node", "linux-x64");
        var buddy = Buddy();
        var install = buddy.Installer.Plan(buddy.Installs.Read(Game()), Testing, Stage(NewRelease(), []), new InstallOptions());
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.Throws<ChangeException>(install.Plan.Apply);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var after = buddy.Installs.Read(Game());
        Assert.Equal([Path.Combine(Win64, "plugins", "OpenVersus", "OpenVersus_2026.10.12.01.asi")], after.Mod.Plugins);
        Assert.False(after.Loaders.Single().IsPinnedBuild);
        Assert.Empty(after.Held);
    }

    [Fact]
    public void Uninstalling_holds_the_mod_its_paks_and_the_loader_as_one_restorable_set()
    {
        Season1Installed();
        tree.File("pak", [.. Rel(SavedPaks), "OVS_P.pak"]);
        var buddy = Buddy();

        buddy.Installer.Uninstall(buddy.Installs.Read(Game())).Apply();

        var after = buddy.Installs.Read(Game());
        Assert.Equal(InstallStatus.NotInstalled, after.Status);
        Assert.Empty(after.Loaders);
        Assert.Empty(after.Paks);
        Assert.StartsWith("Uninstalled", Assert.Single(after.Held).Name);
    }

    [Fact]
    public async Task Prepare_downloads_checks_and_takes_only_the_plugin_folder_from_the_zip()
    {
        // A prod release whose zip carries a loader and a readme at its root, as CI zips do: neither may be used.
        byte[] zip = Zip(new()
        {
            ["xinput1_3.dll"] = "MZ a loader from the zip",
            ["readme.txt"] = "readme",
            ["plugins/OpenVersus/OpenVersus_2026.10.13.01.asi"] = "asi 2026.10.13.01",
            ["plugins/OpenVersus/sample_config.toml"] = Fixture("sample.toml"),
        });
        byte[] pak = Encoding.ASCII.GetBytes("pak 13");
        byte[] loaderZip = Zip(new() { ["dinput8.dll"] = Encoding.Latin1.GetString(LoaderDll) });
        var pin = Pin() with { ZipSha256 = Sha(loaderZip), ZipUrl = new Uri("https://example.test/ual/Ultimate-ASI-Loader_x64.zip"), LicenseUrl = new Uri("https://example.test/ual/license") };
        var server = new FakeGitHub()
            .Answer("/rel/OpenVersus_v2026.10.13.01.zip", () => Bytes(zip))
            .Answer("/rel/OVS_P.pak", () => Bytes(pak))
            .Answer("/ual/Ultimate-ASI-Loader_x64.zip", () => Bytes(loaderZip))
            .Answer("/ual/license", () => Bytes(License));
        var release = new Release("2026.10.13.01", "Prod", Channel.Prod, new Version(2026, 10, 13, 1), null,
        [
            new ReleaseAsset("OpenVersus_v2026.10.13.01.zip", zip.Length, new Uri("https://example.test/rel/OpenVersus_v2026.10.13.01.zip"), Sha(zip)),
            new ReleaseAsset("OVS_P.pak", pak.Length, new Uri("https://example.test/rel/OVS_P.pak"), Sha(pak)),
        ]);
        var buddy = Buddy(server, pin);

        var install = await buddy.Installer.PrepareAsync(buddy.Installs.Read(Game()), release);
        install.Plan.Apply();

        var after = buddy.Installs.Read(Game());
        Assert.Equal(InstallStatus.Installed, after.Status);
        Assert.Equal(["version.dll"], after.Loaders.Select(l => l.FileName));
        Assert.True(after.Loaders[0].IsPinnedBuild);
        Assert.False(File.Exists(Path.Combine(Win64, "xinput1_3.dll")));
        Assert.False(File.Exists(Path.Combine(Win64, "readme.txt")));
        Assert.Null(after.Mod.Settings);
        Assert.Equal(["OVS_P.pak"], after.Paks.Select(p => p.Name));
    }

    // Every file in the library (the game) and the prefix (the paks), as "path checksum" lines.
    private List<string> Snapshot(params string[] extra) =>
        Directory.EnumerateFiles(tree.Path("steamapps"), "*", SearchOption.AllDirectories)
            .Select(f => $"{Path.GetRelativePath(tree.Root, f)} {Sha(File.ReadAllBytes(f))}")
            .Concat(extra).Order(StringComparer.Ordinal).ToList();

    [Fact]
    public void Restoring_the_set_an_update_kept_puts_the_copy_back_as_it_was_before_the_update()
    {
        Season1Installed();
        Game();
        tree.File("pak 12", [.. Rel(SavedPaks), "OVS_P.pak"]);
        tree.File("same", [.. Rel(SavedPaks), "OVS_Same_P.pak"]);
        tree.File("stale", [.. Rel(SavedPaks), "OVS_Old_P.pak"]);
        string home = Path.Combine(Win64, "plugins", "OpenVersus");
        string cache = Path.Combine(home, "PatternsCache.toml");
        var before = Snapshot($"{Path.GetRelativePath(tree.Root, cache)} {Sha(Encoding.UTF8.GetBytes("cache"))}");
        var buddy = Buddy();
        buddy.Installer.Plan(buddy.Installs.Read(Game()), Testing, Stage(NewRelease(), new() { ["OVS_P.pak"] = "pak 13", ["OVS_Same_P.pak"] = "same" }),
            new InstallOptions()).Plan.Apply();
        // Then the mod runs: it writes a cache it did not have, and updates itself to a version with a new file name.
        File.WriteAllText(cache, "cache");
        File.Move(Path.Combine(home, "OpenVersus_2026.10.13.01.asi"), Path.Combine(home, "OpenVersus_2026.10.14.01.asi"));
        var state = buddy.Installs.Read(Game());
        var undo = Assert.Single(state.Held);

        buddy.Changes.Restore(state, undo.Name).Apply();

        // As before the update, plus the cache the mod made since, which the update never touched.
        Assert.Equal(before, Snapshot());
        Assert.Empty(buddy.Installs.Read(Game()).Held);
        Assert.False(Directory.Exists(undo.Folder));
    }

    [Fact]
    public void Restoring_the_set_a_fresh_install_kept_takes_the_install_away()
    {
        Game();
        var before = Snapshot();
        var buddy = Buddy();
        var install = buddy.Installer.Plan(buddy.Installs.Read(Game()), Testing, Stage(NewRelease(), new() { ["OVS_P.pak"] = "pak 13" }), new InstallOptions());
        install.Plan.Apply();
        var state = buddy.Installs.Read(Game());
        Assert.Equal(InstallStatus.Installed, state.Status);
        Assert.Contains(install.Summary, s => s.Contains("undoes this install", StringComparison.Ordinal));

        buddy.Changes.Restore(state, Assert.Single(state.Held).Name).Apply();

        Assert.Equal(before, Snapshot());
        Assert.Equal(InstallStatus.NotInstalled, buddy.Installs.Read(Game()).Status);
    }

    [Fact]
    public void Turning_off_an_install_the_buddy_made_takes_its_loader_along_and_turning_on_brings_it_back()
    {
        Game();
        var buddy = Buddy();
        buddy.Installer.Plan(buddy.Installs.Read(Game()), Testing, Stage(NewRelease(), new() { ["OVS_P.pak"] = "pak 13" }), new InstallOptions()).Plan.Apply();
        string loader = Path.Combine(Win64, "version.dll"), license = Path.Combine(Win64, Installer.LicenseName);
        var installed = Snapshot();

        buddy.Changes.HoldOpenVersus(buddy.Installs.Read(Game())).Apply();

        var off = buddy.Installs.Read(Game());
        Assert.Equal(InstallStatus.Disabled, off.Status);
        Assert.Empty(off.Loaders);
        Assert.False(File.Exists(license));

        buddy.Changes.Restore(off, InstallChanges.OpenVersusSet).Apply();

        Assert.Equal(installed, Snapshot());
        Assert.True(File.Exists(loader));
    }

    [Fact]
    public void A_file_the_buddy_did_not_put_where_a_kept_one_goes_refuses_the_undo()
    {
        Season1Installed();
        var buddy = Buddy();
        buddy.Installer.Plan(buddy.Installs.Read(Game()), Testing, Stage(NewRelease(), []), new InstallOptions()).Plan.Apply();
        string old = Path.Combine(Win64, "plugins", "OpenVersus", "OpenVersus_2026.10.12.01.asi");
        File.WriteAllText(old, "someone else's");
        var after = Snapshot();
        var state = buddy.Installs.Read(Game());

        var e = Assert.Throws<ChangeException>(buddy.Changes.Restore(state, Assert.Single(state.Held).Name).Apply);

        Assert.Contains(old, e.Message);
        Assert.Equal(after, Snapshot());
    }

    [Fact]
    public async Task A_pak_installed_as_the_release_has_it_is_not_downloaded()
    {
        byte[] zip = Zip(new() { ["plugins/OpenVersus/OpenVersus_2026.10.13.01.asi"] = "asi 2026.10.13.01" });
        byte[] pak = Encoding.ASCII.GetBytes("pak 13");
        byte[] audio = Encoding.ASCII.GetBytes("audio 13");
        byte[] loaderZip = Zip(new() { ["dinput8.dll"] = Encoding.Latin1.GetString(LoaderDll) });
        var pin = Pin() with { ZipSha256 = Sha(loaderZip), ZipUrl = new Uri("https://example.test/ual/Ultimate-ASI-Loader_x64.zip"), LicenseUrl = new Uri("https://example.test/ual/license") };
        var server = new FakeGitHub()
            .Answer("/rel/OpenVersus_v2026.10.13.01.zip", () => Bytes(zip))
            .Answer("/rel/OVS_P.pak", () => Bytes(pak))
            .Answer("/rel/OVS_Audio_P.pak", () => Bytes(audio))
            .Answer("/ual/Ultimate-ASI-Loader_x64.zip", () => Bytes(loaderZip))
            .Answer("/ual/license", () => Bytes(License));
        var release = new Release("2026.10.13.01", "Prod", Channel.Prod, new Version(2026, 10, 13, 1), null,
        [
            new ReleaseAsset("OpenVersus_v2026.10.13.01.zip", zip.Length, new Uri("https://example.test/rel/OpenVersus_v2026.10.13.01.zip"), Sha(zip)),
            new ReleaseAsset("OVS_P.pak", pak.Length, new Uri("https://example.test/rel/OVS_P.pak"), Sha(pak)),
            new ReleaseAsset("OVS_Audio_P.pak", audio.Length, new Uri("https://example.test/rel/OVS_Audio_P.pak"), Sha(audio)),
        ]);
        Game();
        tree.Bytes(pak, [.. Rel(SavedPaks), "OVS_P.pak"]);
        var buddy = Buddy(server, pin);

        var install = await buddy.Installer.PrepareAsync(buddy.Installs.Read(Game()), release);

        Assert.DoesNotContain(server.Requests, r => r.RequestUri!.AbsolutePath == "/rel/OVS_P.pak");
        Assert.Contains(server.Requests, r => r.RequestUri!.AbsolutePath == "/rel/OVS_Audio_P.pak");
        Assert.Contains("Paks: 1 new or changed, 1 already in place.", install.Summary);
        install.Plan.Apply();
        Assert.Equal(["OVS_Audio_P.pak", "OVS_P.pak"], buddy.Installs.Read(Game()).Paks.Select(p => p.Name).Order(StringComparer.Ordinal));
        // The pak left the download cache with nothing behind it; the zip stays cached.
        Assert.False(Directory.Exists(tree.Path("data", "downloads", Sha(audio))));
        Assert.True(File.Exists(tree.Path("data", "downloads", Sha(zip), "OpenVersus_v2026.10.13.01.zip")));
    }

    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static byte[] Zip(Dictionary<string, string> entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                using var writer = archive.CreateEntry(name).Open();
                writer.Write(Encoding.Latin1.GetBytes(text));
            }
        }
        return memory.ToArray();
    }
}
