using System.Security.Cryptography;
using System.Text;
using MvsBuddy.Catalog;
using MvsBuddy.Games;
using MvsBuddy.Installs;

namespace MvsBuddy.Tests;

// Each install below is laid out as a real release unpacks: the file lists are those of OpenVersus_2026.10.12.01.zip
// (Season 1 testing), OpenVersus_v2026.10.11.04-prerelease.zip (a CI build) and OpenVersus_v2026.04.08.14.zip (the old
// layout). File contents are placeholders, except OpenVersus.toml, which is the Season 1 zip's own.
public sealed class InstallReaderTests : IDisposable
{
    private readonly TempTree tree = new();

    public void Dispose() => tree.Dispose();

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    // A DLL that is Ultimate ASI Loader as far as the reader can tell: the loader's name in UTF-16, as the real one carries it.
    private static byte[] LoaderBytes(string tag) => [.. Encoding.ASCII.GetBytes("MZ " + tag + " "), .. Encoding.Unicode.GetBytes("Ultimate ASI Loader")];

    private InstallReader Reader(bool windows = false, AsiLoaderPin? pin = null) =>
        new BuddyBuilder().AsiLoader(pin ?? AsiLoaderPin.Default).Host(new HostSystem
        {
            IsWindows = windows,
            Home = tree.Path("home"),
            Environment = name => name == "LOCALAPPDATA" ? tree.Path("AppData", "Local") : null,
        }).Build().Installs;

    // A Steam install whose Proton prefix Proton has made (the game was started once).
    private GameInstall Steam(out string win64)
    {
        win64 = tree.Game("steamapps", "common", "MultiVersus");
        Directory.CreateDirectory(tree.Path("steamapps", "compatdata", "1818750", "pfx", "drive_c", "users"));
        return new GameInstall(win64, GameStore.Steam, FoundBy.SteamLibrary) { WinePrefix = tree.Path("steamapps", "compatdata", "1818750", "pfx") };
    }

    private void Season1(string win64)
    {
        string[] home = [.. Rel(win64), "plugins", "OpenVersus"];
        tree.File("asi", [.. home, "OpenVersus_2026.10.12.01.asi"]);
        tree.File(Fixture("OpenVersus.testing.toml"), [.. home, "OpenVersus.toml"]);
        foreach (string os in new[] { "linux-x64", "win-x64" })
        {
            tree.File("{}", [.. home, "node", os, "appsettings.json"]);
        }
    }

    private string[] Rel(string path) => Path.GetRelativePath(tree.Root, path).Split(Path.DirectorySeparatorChar);

    [Fact]
    public void The_season_1_zip_alone_is_the_mod_without_its_loader()
    {
        var game = Steam(out string win64);
        Season1(win64);

        var state = Reader().Read(game);

        Assert.Equal(InstallStatus.LoaderMissing, state.Status);
        Assert.Equal(ModLayout.Current, state.Mod.Layout);
        Assert.Equal("2026.10.12.01", ModVersion.Text(state.Mod.Version!));
        Assert.Equal("http://prod.openversus.org:8420/", state.Mod.Settings?.GameServerUrl);
        Assert.Equal("http://prod.openversus.org:8420/", state.Mod.Settings?.ProdServerUrl);
        Assert.Equal("trace", state.Mod.Settings?.LogLevel);
        Assert.Empty(state.Mod.Settings!.Errors);
        Assert.Empty(state.Notes);
    }

    [Fact]
    public void A_ci_build_with_its_loader_is_installed()
    {
        var game = Steam(out string win64);
        string[] at = Rel(win64);
        tree.Bytes(LoaderBytes("ci"), [.. at, "xinput1_3.dll"]);
        foreach (string file in new[] { "LICENSE.txt", "OpenVersus_2026.10.11.04.asi", "sample_config.toml", "VERSION.txt" })
        {
            tree.File(file, [.. at, "plugins", "OpenVersus", file]);
        }

        var state = Reader().Read(game);

        Assert.Equal(InstallStatus.Installed, state.Status);
        var loader = Assert.Single(state.Loaders);
        Assert.True(loader.IsUltimateAsiLoader);
        Assert.False(loader.IsPinnedBuild);
        // No OpenVersus.toml in a CI zip: the mod writes its defaults on first launch.
        Assert.Null(state.Mod.Settings);
    }

    [Fact]
    public void The_pinned_loader_is_recognised_by_its_hash()
    {
        var game = Steam(out string win64);
        string path = tree.Bytes(LoaderBytes("pinned"), [.. Rel(win64), "version.dll"]);
        string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        var pin = AsiLoaderPin.Default with { DllSha256 = sha };

        Assert.True(Assert.Single(Reader(pin: pin).Read(game).Loaders).IsPinnedBuild);
    }

    [Fact]
    public void Another_programs_dll_under_a_proxy_name_is_not_the_loader()
    {
        var game = Steam(out string win64);
        Season1(win64);
        tree.File("MZ some other proxy", [.. Rel(win64), "version.dll"]);

        var state = Reader().Read(game);

        Assert.False(Assert.Single(state.Loaders).IsUltimateAsiLoader);
        Assert.Equal(InstallStatus.LoaderMissing, state.Status);
    }

    [Fact]
    public void The_old_layout_is_recognised_without_a_version()
    {
        var game = Steam(out string win64);
        string[] at = Rel(win64);
        tree.Bytes(LoaderBytes("old"), [.. at, "xinput1_3.dll"]);
        foreach (string file in new[] { "OpenVersus.asi", "OpenVersus.ini", "OVSState.ini", "PatternsCache.cache" })
        {
            tree.File(file, [.. at, "plugins", file]);
        }

        var state = Reader().Read(game);

        Assert.Equal(InstallStatus.Installed, state.Status);
        Assert.Equal(ModLayout.Old, state.Mod.Layout);
        Assert.Null(state.Mod.Version);
        Assert.Equal("OpenVersus.ini", Path.GetFileName(state.Mod.Settings?.Path));
    }

    [Fact]
    public void Leftovers_and_doubles_are_noted()
    {
        var game = Steam(out string win64);
        string[] at = Rel(win64);
        Season1(win64);
        tree.Bytes(LoaderBytes("a"), [.. at, "xinput1_3.dll"]);
        tree.Bytes(LoaderBytes("b"), [.. at, "version.dll"]);
        tree.File("old", [.. at, "plugins", "OpenVersus.asi"]);
        tree.File("older", [.. at, "plugins", "OpenVersus", "OpenVersus_2026.10.11.04.asi.bak"]);
        tree.File("ini", [.. at, "plugins", "OpenVersus.ini"]);
        tree.File("beside", [.. at, "OpenVersus.asi"]);

        var state = Reader().Read(game);

        Assert.Equal(ModLayout.Current, state.Mod.Layout);
        Assert.Equal(["OpenVersus.asi", "OpenVersus.ini", "OpenVersus.asi"], state.Mod.OldLayoutFiles.Select(Path.GetFileName));
        Assert.Contains("OpenVersus_2026.10.11.04.asi.bak", state.Mod.Backups.Select(Path.GetFileName));
        Assert.Equal(3, state.Notes.Count);
        Assert.Contains(state.Notes, n => n.Contains("2 copies of Ultimate ASI Loader"));
        Assert.Contains(state.Notes, n => n.Contains("old OpenVersus.asi is still loose in plugins"));
        Assert.Contains(state.Notes, n => n.Contains("still at OpenVersus.asi, where the loader loads it too"));
    }

    [Fact]
    public void A_plugin_dropped_beside_the_executable_is_the_oldest_layout()
    {
        var game = Steam(out string win64);
        string[] at = Rel(win64);
        tree.Bytes(LoaderBytes("beside"), [.. at, "xinput1_3.dll"]);
        tree.File("asi", [.. at, "OpenVersus.asi"]);
        tree.File("ini", [.. at, "OpenVersus.ini"]);

        var state = Reader().Read(game);

        Assert.Equal(InstallStatus.Installed, state.Status);
        Assert.Equal(ModLayout.BesideGame, state.Mod.Layout);
        Assert.Equal(Path.Combine(win64, "OpenVersus.asi"), Assert.Single(state.Mod.Plugins));
        Assert.Equal(Path.Combine(win64, "OpenVersus.ini"), state.Mod.Settings?.Path);
        Assert.Empty(state.OtherPlugins);
    }

    [Fact]
    public void Other_plugins_are_listed_and_mvs_infinite_is_named()
    {
        var game = Steam(out string win64);
        string[] at = Rel(win64);
        Season1(win64);
        tree.File("i", [.. at, "plugins", "MVSIASI.asi"]);
        tree.File("i", [.. at, "scripts", "deep", "mvs_infinite.asi"]);
        tree.File("i", [.. at, "mvsi.asi"]);
        tree.File("x", [.. at, "plugins", "FPSUnlock.asi"]);

        var state = Reader().Read(game);

        Assert.Equal(
            ["FPSUnlock.asi:False", "MVSIASI.asi:True", "mvs_infinite.asi:True", "mvsi.asi:True"],
            state.OtherPlugins.Select(p => $"{p.FileName}:{p.IsMvsInfinite}").Order(StringComparer.Ordinal));
        Assert.Equal(3, state.Notes.Count(n => n.Contains("MVS Infinite") && n.Contains("cannot run alongside OpenVersus")));
        Assert.Contains(state.Notes, n => n.Contains("FPSUnlock.asi is another ASI plugin"));
        Assert.Equal(ModLayout.Current, state.Mod.Layout);
    }

    [Theory]
    [InlineData("MVSIASI.asi", true)]
    [InlineData("mvsi.asi", true)]
    [InlineData("mvs_infinite.asi", true)]
    [InlineData("MVS-Infinite-v2.asi", true)]
    [InlineData("MultiVersusInfinite.asi", true)]
    [InlineData("mvsinf.asi", true)]
    [InlineData("Infinite_MVS.asi", true)]
    [InlineData("mvsinput.asi", false)]
    [InlineData("OpenVersus.asi", false)]
    [InlineData("FPSUnlock.asi", false)]
    public void MVS_Infinite_is_known_by_its_name_and_its_variations(string name, bool expected) =>
        Assert.Equal(expected, InstallReader.IsMvsInfinite(name));

    [Theory]
    [InlineData("MVSI_Content_P.pak", true)]
    [InlineData("MVSIContent_P.ucas", true)]
    [InlineData("MVS_Infinite_P.utoc", true)]
    [InlineData("mvsi.sig", true)]
    [InlineData("mvs_infinite_skins_P.pak", true)]
    [InlineData("pakchunk5-Infinite_P.ucas", false)]
    [InlineData("Infinite_P.pak", false)]
    [InlineData("My_MVSI_P.pak", false)]
    [InlineData("MVS_Skins_P.pak", false)]
    [InlineData("Skins_P.pak", false)]
    [InlineData("OVS_P.pak", false)]
    public void An_MVS_Infinite_pak_is_known_by_a_name_that_starts_with_MVSI_or_MVS_Infinite(string name, bool expected) =>
        Assert.Equal(expected, InstallReader.IsMvsInfinitePak(name));

    [Fact]
    public void OpenVersus_paks_anywhere_in_the_games_folders_are_found_and_other_mods_paks_are_told_apart()
    {
        var game = Steam(out string win64);
        string[] gameFolder = [.. Rel(win64), "..", ".."];
        tree.File("p", [.. gameFolder, "Content", "Paks", "OVS_P.pak"]);
        tree.File("p", [.. gameFolder, "Content", "Paks", "~mods", "OVS_P.ucas"]);
        tree.File("p", [.. gameFolder, "Content", "~mods", "OVS_Audio_P.pak"]);
        tree.File("p", [.. gameFolder, "Binaries", "~mods", "OVS_P.utoc"]);
        tree.File("p", [.. gameFolder, "Content", "Paks", "global.utoc"]);
        tree.File("p", [.. gameFolder, "Content", "Paks", "MVSI_Content_P.pak"]);
        tree.File("p", [.. gameFolder, "Content", "Paks", "~mods", "Skins_P.pak"]);

        var state = Reader().Read(game);

        Assert.Equal(["OVS_Audio_P.pak", "OVS_P.pak", "OVS_P.ucas", "OVS_P.utoc"], state.PaksInGame.Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["MVSI_Content_P.pak:True", "Skins_P.pak:False"], state.OtherPaks.Select(p => $"{p.Name}:{p.IsMvsInfinite}").Order(StringComparer.Ordinal));
        Assert.Contains(state.Notes, n => n.Contains("belong in the user profile"));
        Assert.Contains(state.Notes, n => n.StartsWith("MVS Infinite's paks are in the game's Content", StringComparison.Ordinal) && n.Contains("MVSI_Content_P.pak"));
        Assert.Contains(state.Notes, n => n.Contains("neither the game's nor OpenVersus's") && n.Contains("Skins_P.pak") && !n.Contains("MVS Infinite"));
    }

    [Fact]
    public void Another_mods_paks_never_bring_up_MVS_Infinite()
    {
        var game = Steam(out string win64);
        tree.File("p", [.. Rel(win64), "..", "..", "Content", "Paks", "~mods", "Skins_P.pak"]);

        var state = Reader().Read(game);

        Assert.Contains(state.Notes, n => n.Contains("Skins_P.pak"));
        Assert.DoesNotContain(state.Notes, n => n.Contains("MVS Infinite"));
        Assert.False(Assert.Single(state.OtherPaks).IsMvsInfinite);
    }

    [Fact]
    public void An_unverified_folders_surroundings_are_not_searched()
    {
        string chosen = tree.LoneExecutable("odd", "place", "Win64");
        tree.File("p", "odd", "Content", "Paks", "OVS_P.pak");
        var game = new GameInstall(chosen, GameStore.Unknown, FoundBy.Chosen) { Verified = false, UnverifiedReason = "test" };

        var state = Reader(windows: true).Read(game);

        Assert.Empty(state.PaksInGame);
        Assert.Empty(state.OtherPaks);
    }

    [Fact]
    public void A_broken_settings_file_is_noted_with_where_it_breaks()
    {
        var game = Steam(out string win64);
        Season1(win64);
        tree.File(Fixture("OpenVersus.testing.toml") + "\nLogLevel = \"twice\"\n[Settings\n", [.. Rel(win64), "plugins", "OpenVersus", "OpenVersus.toml"]);

        var state = Reader().Read(game);

        Assert.NotEmpty(state.Mod.Settings!.Errors);
        Assert.Contains(state.Notes, n => n.Contains("mistake") && n.Contains("line"));
    }

    [Fact]
    public void Proton_paks_are_in_steamusers_profile_inside_the_prefix()
    {
        var game = Steam(out _);
        string[] paks = ["steamapps", "compatdata", "1818750", "pfx", "drive_c", "users", "steamuser", "AppData", "Local", "MultiVersus", "Saved", "Paks"];
        tree.File("p", [.. paks, "OVS_P.pak"]);
        tree.File("u", [.. paks, "OVS_P.ucas"]);
        tree.File("not ours", [.. paks, "pakchunk99.pak"]);

        var state = Reader().Read(game);

        Assert.Equal(tree.Path(paks), state.SavedPaksDirectory);
        Assert.Equal(["OVS_P.pak", "OVS_P.ucas"], state.Paks.Select(p => p.Name));
    }

    [Fact]
    public void Another_wine_prefix_uses_the_user_folder_that_holds_the_games_saves()
    {
        string win64 = tree.Game("prefix", "drive_c", "Games", "MVS");
        Directory.CreateDirectory(tree.Path("prefix", "drive_c", "users", "Public"));
        Directory.CreateDirectory(tree.Path("prefix", "drive_c", "users", "someone", "AppData", "Local", "MultiVersus"));
        var game = new GameInstall(win64, GameStore.Unknown, FoundBy.Search) { WinePrefix = tree.Path("prefix") };

        Assert.Equal(tree.Path("prefix", "drive_c", "users", "someone", "AppData", "Local", "MultiVersus", "Saved", "Paks"),
            Reader().Read(game).SavedPaksDirectory);
    }

    [Fact]
    public void A_proton_prefix_not_made_yet_asks_for_the_game_to_be_started_once_first()
    {
        var game = Steam(out _);
        Directory.Delete(tree.Path("steamapps", "compatdata"), recursive: true);

        var state = Reader().Read(game);

        Assert.True(state.NeedsFirstLaunch);
        Assert.Null(state.SavedPaksDirectory);
        Assert.Contains(state.Notes, n => n.Contains("Start the game once", StringComparison.Ordinal));
        Assert.DoesNotContain(state.Notes, n => n.Contains("cannot tell which Wine prefix", StringComparison.Ordinal));
    }

    [Fact]
    public void A_proton_prefix_the_game_has_not_saved_in_yet_will_be_steamusers()
    {
        var game = Steam(out _);

        Assert.False(Reader().Read(game).NeedsFirstLaunch);
        Assert.Equal(tree.Path("steamapps", "compatdata", "1818750", "pfx", "drive_c", "users", "steamuser", "AppData", "Local", "MultiVersus", "Saved", "Paks"),
            Reader().Read(game).SavedPaksDirectory);
    }

    [Fact]
    public void On_windows_paks_are_in_local_app_data()
    {
        string win64 = tree.Game("Games", "MVS");
        var game = new GameInstall(win64, GameStore.Epic, FoundBy.EpicManifest);

        Assert.Equal(tree.Path("AppData", "Local", "MultiVersus", "Saved", "Paks"), Reader(windows: true).Read(game).SavedPaksDirectory);
    }

    [Fact]
    public void Without_a_known_prefix_on_linux_the_paks_cannot_be_seen_and_that_is_said()
    {
        var game = new GameInstall(tree.Path("chosen"), GameStore.Unknown, FoundBy.Chosen) { Verified = false, UnverifiedReason = "test" };

        var state = Reader().Read(game);

        Assert.Equal(InstallStatus.NotInstalled, state.Status);
        Assert.Null(state.SavedPaksDirectory);
        Assert.Contains(state.Notes, n => n.Contains("Wine prefix"));
    }

    [Theory]
    [InlineData(2026, 10, 11, 4, "2026.10.11.04")]
    [InlineData(2026, 4, 8, 14, "2026.04.08.14")]
    public void Versions_read_the_way_tags_write_them(int year, int month, int day, int build, string text) =>
        Assert.Equal(text, ModVersion.Text(new Version(year, month, day, build)));
}
