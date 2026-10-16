namespace MvsBuddy.Tests;

public sealed class PreferencesTests : IDisposable
{
    private readonly TempTree tree = new();

    public void Dispose() => tree.Dispose();

    private string File_ => tree.Path("data", "preferences.toml");

    private Preferences Load() => new(new HostSystem { DataDirectory = tree.Path("data") });

    [Fact]
    public void The_first_answer_writes_the_buddys_file_with_its_comments_and_only_that_value_changed()
    {
        Assert.Null(Load().MenuEntry);
        Assert.False(File.Exists(File_));

        Load().SetMenuEntry(true);

        Assert.Equal(Preferences.Template.Replace("Entry = \"ask\" # \"ask\", \"yes\" or \"no\"", "Entry = \"yes\" # \"ask\", \"yes\" or \"no\""), File.ReadAllText(File_));
        Assert.True(Load().MenuEntry);
        Load().SetMenuEntry(false);
        Assert.False(Load().MenuEntry);
        Assert.False(File.Exists(File_ + ".mvs-buddy"));
    }

    [Fact]
    public void The_persons_own_file_is_edited_in_place_keeping_their_comments_and_other_keys()
    {
        tree.File("# mine\n\n[Menu]\nEntry = \"no\"   # my note\nColour = \"green\"\n", "data", "preferences.toml");

        Assert.False(Load().MenuEntry);
        Load().SetMenuEntry(true);

        Assert.Equal("# mine\n\n[Menu]\nEntry = \"yes\"   # my note\nColour = \"green\"\n", File.ReadAllText(File_));
    }

    [Fact]
    public void A_file_without_the_key_is_rebuilt_from_the_template_and_a_boolean_answer_is_read()
    {
        tree.File("[Menu]\nEntry = true\n", "data", "preferences.toml");
        Assert.True(Load().MenuEntry);

        tree.File("[Other]\nThing = 1\n", "data", "preferences.toml");
        Assert.Null(Load().MenuEntry);
        Load().SetMenuEntry(false);

        Assert.StartsWith("# MVS Buddy's own settings", File.ReadAllText(File_));
        Assert.Contains("Entry = \"no\" # \"ask\", \"yes\" or \"no\"", File.ReadAllText(File_));
        Assert.False(Load().MenuEntry);
    }

    [Fact]
    public void A_file_that_does_not_parse_means_defaults_and_is_set_aside_not_written_over()
    {
        tree.File("[Menu\nEntry = \n", "data", "preferences.toml");
        var preferences = Load();

        Assert.Null(preferences.MenuEntry);
        Assert.StartsWith("preferences.toml has a mistake (line 1", preferences.Problem);

        preferences.SetMenuEntry(true);

        Assert.Equal("[Menu\nEntry = \n", File.ReadAllText(File_ + ".unreadable"));
        Assert.True(Load().MenuEntry);
        Assert.Null(Load().Problem);
    }

    [Fact]
    public void Looking_for_updates_is_on_and_installing_them_is_off_until_the_person_says_otherwise()
    {
        var host = new HostSystem { Home = tree.Path("home"), DataDirectory = tree.Path("data") };
        var preferences = new Preferences(host);
        Assert.True(preferences.CheckUpdates);
        Assert.False(preferences.AutoUpdate);

        preferences.SetCheckUpdates(false);
        preferences.SetAutoUpdate(true);

        var again = new Preferences(host);
        Assert.False(again.CheckUpdates);
        Assert.True(again.AutoUpdate);
        string text = File.ReadAllText(tree.Path("data", "preferences.toml"));
        Assert.Contains("Check = \"no\"", text);
        Assert.Contains("Install = \"yes\"", text);
    }
}
