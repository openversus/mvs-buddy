using MvsBuddy.Catalog;
using MvsBuddy.Changes;
using MvsBuddy.Installs;

namespace MvsBuddy.Tests;

public sealed class ChannelTests
{
    private static ModFiles Mod(string? server, ModLayout layout = ModLayout.Current) =>
        new(layout, [], null, new ModSettings("OpenVersus.toml", server, null, null, []), [], [], []);

    [Theory]
    [InlineData(null, Channel.Prod)]
    [InlineData("https://prod.openversus.org/", Channel.Prod)]
    [InlineData("http://prod.openversus.org:8420/", Channel.Testing)]
    [InlineData("http://localhost:8420", Channel.Testing)]
    [InlineData("http://localhost:8080/", Channel.Prod)]
    [InlineData("not a url", Channel.Prod)]
    public void Without_a_record_the_channel_is_guessed_from_the_server_and_says_so(string? server, Channel expected)
    {
        Assert.Equal(new ModChannel(expected, ChannelSource.Server), InstallReader.ChannelOf(Mod(server), null));
    }

    [Fact]
    public void A_record_wins_over_the_server_and_says_who_wrote_it()
    {
        var testingServer = Mod("http://prod.openversus.org:8420/");

        Assert.Equal(new ModChannel(Channel.Prod, ChannelSource.Install), InstallReader.ChannelOf(testingServer, new InstallRecord { Channel = "Prod" }));
        Assert.Equal(new ModChannel(Channel.Prod, ChannelSource.Person),
            InstallReader.ChannelOf(testingServer, new InstallRecord { Channel = "Prod", ChannelFrom = "person" }));
        // A channel the buddy does not know is no record at all.
        Assert.Equal(new ModChannel(Channel.Testing, ChannelSource.Server), InstallReader.ChannelOf(testingServer, new InstallRecord { Channel = "Nightly" }));
    }

    [Fact]
    public void No_mod_has_no_channel()
    {
        Assert.Null(InstallReader.ChannelOf(Mod("http://prod.openversus.org:8420/", ModLayout.None), new InstallRecord { Channel = "Prod" }));
    }
}
