using MvsBuddy.Games;

namespace MvsBuddy.Tests;

// A localconfig.vdf as Steam writes it: tabs, "Key"<tab><tab>"Value", a quote in a value as \", the game's block among
// other games' under UserLocalConfigStore > Software > Valve > Steam > apps.
public sealed class SteamLaunchOptionsTests : IDisposable
{
    private const string App = "1818750";

    private const string Game = "\t\t\t\t\t\"1818750\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"LastPlayed\"\t\t\"1792037227\"\n\t\t\t\t\t\t\"Playtime\"\t\t\"184928\"\n"
        + "\t\t\t\t\t\t\"LaunchOptions\"\t\t\"gamemoderun %command% -d3d12\"\n\t\t\t\t\t\t\"BadgeData\"\t\t\"020000000813\"\n\t\t\t\t\t}\n";

    private const string LocalConfig = "\"UserLocalConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n\t\t\t\t\"apps\"\n\t\t\t\t{\n"
        + "\t\t\t\t\t\"440\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"LaunchOptions\"\t\t\"env WINEDLLOVERRIDES=\\\"dsound=b,n\\\" %command%\"\n\t\t\t\t\t\t\"cloud\"\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\t\"last_sync_state\"\t\t\"synchronized\"\n\t\t\t\t\t\t}\n\t\t\t\t\t}\n"
        + Game
        + "\t\t\t\t\t\"620\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"LastPlayed\"\t\t\"1681267032\"\n\t\t\t\t\t}\n"
        + "\t\t\t\t}\n\t\t\t\t\"LastPlayedTimesSyncTime\"\t\t\"1792037000\"\n\t\t\t}\n\t\t}\n\t}\n\t\"friends\"\n\t{\n\t\t\"PersonaName\"\t\t\"Someone\"\n\t}\n}\n";

    private readonly TempTree tree = new();

    public void Dispose() => tree.Dispose();

    [Fact]
    public void The_games_options_are_read_with_quotes_unescaped()
    {
        Assert.Equal("gamemoderun %command% -d3d12", SteamLaunchOptions.Read(LocalConfig, App));
        Assert.Equal("env WINEDLLOVERRIDES=\"dsound=b,n\" %command%", SteamLaunchOptions.Read(LocalConfig, "440"));
        Assert.True(SteamLaunchOptions.HasApp(LocalConfig, App));
    }

    [Fact]
    public void A_game_without_the_line_or_without_a_block_has_no_options_and_a_file_without_apps_cannot_be_read()
    {
        Assert.Equal("", SteamLaunchOptions.Read(LocalConfig, "620"));
        Assert.Equal("", SteamLaunchOptions.Read(LocalConfig, "730"));
        Assert.False(SteamLaunchOptions.HasApp(LocalConfig, "730"));
        Assert.Null(SteamLaunchOptions.Read("\"UserLocalConfigStore\"\n{\n\t\"friends\"\n\t{\n\t}\n}\n", App));
        Assert.Null(SteamLaunchOptions.With("\"UserLocalConfigStore\"\n{\n}\n", App, "x"));
    }

    [Fact]
    public void Setting_the_options_changes_only_the_value_and_escapes_quotes_as_steam_does()
    {
        string set = SteamLaunchOptions.With(LocalConfig, App, "WINEDLLOVERRIDES=\"version=n,b\" gamemoderun %command% -d3d12")!;

        Assert.Equal(LocalConfig.Replace("\"gamemoderun %command% -d3d12\"", "\"WINEDLLOVERRIDES=\\\"version=n,b\\\" gamemoderun %command% -d3d12\""), set);
        Assert.Equal("WINEDLLOVERRIDES=\"version=n,b\" gamemoderun %command% -d3d12", SteamLaunchOptions.Read(set, App));
        Assert.Equal("WINEDLLOVERRIDES=\"version=n,b\" gamemoderun %command% -d3d12",
            Vdf.Parse(set).Child("UserLocalConfigStore")!.Child("Software")!.Child("Valve")!.Child("Steam")!.Child("apps")!.Child(App)!.Value("LaunchOptions"));
        Assert.Equal(LocalConfig, SteamLaunchOptions.With(set, App, "gamemoderun %command% -d3d12"));
    }

    [Fact]
    public void A_backslash_in_the_options_survives_the_round_trip()
    {
        string set = SteamLaunchOptions.With(LocalConfig, App, "PROTON_LOG_DIR=\"C:\\logs\" %command%")!;

        Assert.Equal("PROTON_LOG_DIR=\"C:\\logs\" %command%", SteamLaunchOptions.Read(set, App));
    }

    [Fact]
    public void A_game_block_without_the_line_gets_one_first_with_its_neighbours_indentation()
    {
        string set = SteamLaunchOptions.With(LocalConfig, "620", "%command% -novid")!;

        Assert.Equal(LocalConfig.Replace("\t\t\t\t\t\"620\"\n\t\t\t\t\t{\n", "\t\t\t\t\t\"620\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"LaunchOptions\"\t\t\"%command% -novid\"\n"), set);
        Assert.Equal("%command% -novid", SteamLaunchOptions.Read(set, "620"));
    }

    [Fact]
    public void A_game_never_run_gets_a_block_first_under_apps()
    {
        string set = SteamLaunchOptions.With(LocalConfig, "730", "-novid")!;

        Assert.Equal(LocalConfig.Replace("\t\t\t\t\"apps\"\n\t\t\t\t{\n", "\t\t\t\t\"apps\"\n\t\t\t\t{\n\t\t\t\t\t\"730\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"LaunchOptions\"\t\t\"-novid\"\n\t\t\t\t\t}\n"), set);
        Assert.Equal("-novid", SteamLaunchOptions.Read(set, "730"));
        Assert.True(SteamLaunchOptions.HasApp(set, "730"));
        Assert.Equal("gamemoderun %command% -d3d12", SteamLaunchOptions.Read(set, App));
    }

    [Fact]
    public void Windows_line_ends_are_kept()
    {
        string windows = LocalConfig.Replace("\n", "\r\n");

        string set = SteamLaunchOptions.With(windows, "620", "-novid")!;

        Assert.Equal(windows.Replace("\t\t\t\t\t\"620\"\r\n\t\t\t\t\t{\r\n", "\t\t\t\t\t\"620\"\r\n\t\t\t\t\t{\r\n\t\t\t\t\t\t\"LaunchOptions\"\t\t\"-novid\"\r\n"), set);
    }

    // userdata holds Steam's placeholder account 0 and the real ones; loginusers.vdf names them by their 64-bit id, whose
    // low half is the folder's name.
    [Fact]
    public void Accounts_are_the_userdata_folders_with_a_localconfig_named_from_loginusers()
    {
        string steam = tree.Path("Steam");
        tree.File(LocalConfig, "Steam", "userdata", "55555", "config", "localconfig.vdf");
        tree.File(LocalConfig, "Steam", "userdata", "12345", "config", "localconfig.vdf");
        Directory.CreateDirectory(tree.Path("Steam", "userdata", "0", "config"));
        Directory.CreateDirectory(tree.Path("Steam", "userdata", "99", "config"));
        tree.File("\"users\"\n{\n\t\"76561197960321283\"\n\t{\n\t\t\"AccountName\"\t\t\"someone@example.test\"\n\t\t\"PersonaName\"\t\t\"Someone\"\n\t}\n}\n", "Steam", "config", "loginusers.vdf");

        var accounts = SteamLaunchOptions.Accounts(steam);

        Assert.Equal([("12345", "12345"), ("55555", "Someone")], accounts.Select(a => (a.Id, a.Name)));
        Assert.Equal(tree.Path("Steam", "userdata", "55555", "config", "localconfig.vdf"), accounts[1].LocalConfig);
        Assert.Empty(SteamLaunchOptions.Accounts(tree.Path("nowhere")));
    }
}
