using MvsBuddy.Installs;

namespace MvsBuddy.Tests;

// sample.toml is ovs-client's default settings file (the prod defaults); OpenVersus.testing.toml is the one the Season 1
// testing zip ships. "Theirs" is a real file with a person's changes made to it.
public sealed class SettingsMergeTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static string Customised(string file) => Change(Change(Change(file,
        "ToggleMenu = \"F1\"", "ToggleMenu = \"F2\""),
        "NetStats = false", "NetStats = true"),
        "LabGameSpeedPercent = 100", "LabGameSpeedPercent = 150");

    private static string Change(string text, string from, string to)
    {
        Assert.Contains(from, text);
        return text.Replace(from, to);
    }

    private static string? Value(string text, string section, string key)
    {
        var parsed = TomlSettings.Parse(text, "test");
        Assert.Empty(parsed.Errors);
        return parsed.Get(section, key);
    }

    [Fact]
    public void Prod_over_a_testing_install_keeps_their_settings_and_takes_prods_servers_and_logging()
    {
        string theirs = Customised(Fixture("OpenVersus.testing.toml"));

        var merged = SettingsMerge.Merge(Fixture("sample.toml"), theirs, Fixture("sample.toml"));

        Assert.Equal("https://prod.openversus.org/", Value(merged.Text, "Server.Game", "ServerUrl"));
        Assert.Equal("https://prod.openversus.org/", Value(merged.Text, "Server.Prod", "ServerUrl"));
        Assert.Equal("info", Value(merged.Text, "Settings", "LogLevel"));
        Assert.Equal("F2", Value(merged.Text, "Settings.Keybinds", "ToggleMenu"));
        Assert.Equal("true", Value(merged.Text, "Features", "NetStats"));
        Assert.Equal("150", Value(merged.Text, "Settings.Debug", "LabGameSpeedPercent"));
        Assert.Equal(["[Settings.Keybinds] ToggleMenu", "[Features] NetStats", "[Settings.Debug] LabGameSpeedPercent"], merged.Carried);
        Assert.Empty(merged.Dropped);
        Assert.Null(merged.Note);
    }

    [Fact]
    public void Testing_over_a_prod_install_takes_the_testing_servers_and_trace_logging()
    {
        var merged = SettingsMerge.Merge(Fixture("sample.toml"), Customised(Fixture("sample.toml")), Fixture("OpenVersus.testing.toml"));

        Assert.Equal("http://prod.openversus.org:8420/", Value(merged.Text, "Server.Game", "ServerUrl"));
        Assert.Equal("trace", Value(merged.Text, "Settings", "LogLevel"));
        Assert.Equal("F2", Value(merged.Text, "Settings.Keybinds", "ToggleMenu"));
        Assert.Equal(3, merged.Carried.Count);
    }

    [Fact]
    public void Only_the_values_change_every_line_else_is_the_releases()
    {
        string release = Fixture("OpenVersus.testing.toml");

        var merged = SettingsMerge.Merge(Fixture("sample.toml"), Customised(Fixture("sample.toml")), release);

        // The three changed lines and nothing else.
        var differing = release.Split('\n').Zip(merged.Text.Split('\n')).Where(p => p.First != p.Second).ToList();
        Assert.Equal(release.Split('\n').Length, merged.Text.Split('\n').Length);
        Assert.Equal(["ToggleMenu = \"F2\"", "NetStats = true", "LabGameSpeedPercent = 150"], differing.Select(p => p.Second.Trim()));
    }

    [Fact]
    public void A_setting_the_release_no_longer_has_is_dropped_and_said()
    {
        string sample = Customised(Fixture("sample.toml"));
        string newLine = sample.Contains("\r\n") ? "\r\n" : "\n";
        string theirs = Change(sample, "[Features]" + newLine, "[Features]" + newLine + "RetiredFeature = true" + newLine);

        var merged = SettingsMerge.Merge(Fixture("sample.toml"), theirs, Fixture("sample.toml"));

        Assert.Equal(["[Features] RetiredFeature"], merged.Dropped);
        Assert.Null(Value(merged.Text, "Features", "RetiredFeature"));
    }

    [Fact]
    public void Without_the_installed_defaults_what_differs_from_the_new_defaults_is_kept()
    {
        var merged = SettingsMerge.Merge(null, Customised(Fixture("sample.toml")), Fixture("sample.toml"));

        Assert.Equal(3, merged.Carried.Count);
        Assert.Contains("were not available", merged.Note);
    }

    [Fact]
    public void A_broken_settings_file_gives_the_releases_defaults_and_says_so()
    {
        var merged = SettingsMerge.Merge(Fixture("sample.toml"), "LogLevel = \"trace\"\n[Settings\n", Fixture("sample.toml"));

        Assert.Equal(Fixture("sample.toml"), merged.Text);
        Assert.Contains("mistake", merged.Note);
    }

    [Fact]
    public void No_settings_file_is_the_releases()
    {
        Assert.Equal(Fixture("OpenVersus.testing.toml"), SettingsMerge.Merge(null, null, Fixture("OpenVersus.testing.toml")).Text);
    }
}
