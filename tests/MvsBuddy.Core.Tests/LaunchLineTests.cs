using MvsBuddy.Games;

namespace MvsBuddy.Tests;

// Launch lines shaped like ones seen in a real localconfig.vdf: a wrapper with its own arguments and "--", game arguments
// after %command%, an "env" form with a quoted value, and a bare one.
public sealed class LaunchLineTests
{
    private static readonly string[] Version = ["version"];

    [Theory]
    [InlineData("WINEDLLOVERRIDES=\"version=n,b\" %command%", true)]
    [InlineData("WINEDLLOVERRIDES=version=native,builtin %command%", true)]
    [InlineData("env WINEDLLOVERRIDES='dsound=b;version=n' %command%", true)]
    [InlineData("WINEDLLOVERRIDES=\"version,xinput1_3=n,b\" %command%", true)]
    [InlineData("WINEDLLOVERRIDES=\"Version=N,B\" %command%", true)]
    [InlineData("WINEDLLOVERRIDES=\"version=b;version=n\" %command%", true)]
    [InlineData("WINEDLLOVERRIDES=\"version=n,b;version=b\" %command%", false)]
    [InlineData("WINEDLLOVERRIDES=\"version=b,n\" %command%", false)]
    [InlineData("WINEDLLOVERRIDES=\"xinput1_3=n,b\" %command%", false)]
    [InlineData("%command% WINEDLLOVERRIDES=\"version=n,b\"", false)]
    [InlineData("gamemoderun %command%", false)]
    [InlineData("", false)]
    public void The_last_entry_of_a_dll_before_the_command_is_what_wine_does(string options, bool loads) =>
        Assert.Equal(loads, LaunchLine.LoadsNative(options, "version"));

    // Each line as a person might have it, what turning the setting on makes of it, and what turning it off again gives.
    [Theory]
    [InlineData("", "WINEDLLOVERRIDES=\"version=n,b\" %command%", "")]
    [InlineData("gamemoderun %command%", "WINEDLLOVERRIDES=\"version=n,b\" gamemoderun %command%", "gamemoderun %command%")]
    [InlineData("OBS_VKCAPTURE=1 LSFGVK_DISABLE=1 gamemoderun scb -W 2560 -H 1440 --fullscreen  --  %command% -d3d12",
        "WINEDLLOVERRIDES=\"version=n,b\" OBS_VKCAPTURE=1 LSFGVK_DISABLE=1 gamemoderun scb -W 2560 -H 1440 --fullscreen  --  %command% -d3d12",
        "OBS_VKCAPTURE=1 LSFGVK_DISABLE=1 gamemoderun scb -W 2560 -H 1440 --fullscreen  --  %command% -d3d12")]
    [InlineData("env WINEDLLOVERRIDES=\"dsound=b,n\" %command%", "env WINEDLLOVERRIDES=\"dsound=b,n;version=n,b\" %command%", "env WINEDLLOVERRIDES=\"dsound=b,n\" %command%")]
    [InlineData("WINEDLLOVERRIDES='dsound=b' %command%", "WINEDLLOVERRIDES='dsound=b;version=n,b' %command%", "WINEDLLOVERRIDES='dsound=b' %command%")]
    [InlineData("%command% WINEDLLOVERRIDES=x", "WINEDLLOVERRIDES=\"version=n,b\" %command% WINEDLLOVERRIDES=x", "%command% WINEDLLOVERRIDES=x")]
    public void Turning_on_adds_one_setting_and_turning_off_gives_the_line_back(string theirs, string on, string off)
    {
        string turnedOn = LaunchLine.TurnedOn(theirs, Version);

        Assert.Equal(on, turnedOn);
        Assert.True(LaunchLine.LoadsNative(turnedOn, "version"));
        Assert.Equal(turnedOn, LaunchLine.TurnedOn(turnedOn, Version));
        Assert.Equal(off, LaunchLine.TurnedOff(turnedOn, Version));
        Assert.False(LaunchLine.LoadsNative(off, "version"));
    }

    // Shapes that cannot come back byte for byte: an entry of the DLL's that was there is replaced, so off leaves none; a
    // bare value that gains a ";" has to be quoted for the shell; a line without %command% gets one, which stays (Steam runs
    // the game and then the line's words the same either way). What comes back means the same to the shell and to Steam.
    [Theory]
    [InlineData("-d3d12", "WINEDLLOVERRIDES=\"version=n,b\" %command% -d3d12", "%command% -d3d12")]
    [InlineData("WINEDLLOVERRIDES=\"version=b\" %command%", "WINEDLLOVERRIDES=\"version=n,b\" %command%", "")]
    [InlineData("WINEDLLOVERRIDES=version=b gamemoderun %command%", "WINEDLLOVERRIDES=version=n,b gamemoderun %command%", "gamemoderun %command%")]
    [InlineData("WINEDLLOVERRIDES=dsound=b %command%", "WINEDLLOVERRIDES=\"dsound=b;version=n,b\" %command%", "WINEDLLOVERRIDES=\"dsound=b\" %command%")]
    [InlineData("WINEDLLOVERRIDES=\"version,xinput1_3=b\" %command%", "WINEDLLOVERRIDES=\"xinput1_3=b;version=n,b\" %command%", "WINEDLLOVERRIDES=\"xinput1_3=b\" %command%")]
    [InlineData("WINEDLLOVERRIDES=\"version=n,b;version=b\" %command% -dx12", "WINEDLLOVERRIDES=\"version=n,b\" %command% -dx12", "%command% -dx12")]
    public void An_entry_of_the_dll_already_there_is_replaced_and_off_takes_it_out(string theirs, string on, string off)
    {
        Assert.Equal(on, LaunchLine.TurnedOn(theirs, Version));
        Assert.Equal(off, LaunchLine.TurnedOff(on, Version));
    }

    [Fact]
    public void Several_dlls_go_in_one_assignment_and_come_out_one_at_a_time()
    {
        string on = LaunchLine.TurnedOn("gamemoderun %command%", ["version", "xinput1_3"]);

        Assert.Equal("WINEDLLOVERRIDES=\"version=n,b;xinput1_3=n,b\" gamemoderun %command%", on);
        Assert.Equal("WINEDLLOVERRIDES=\"xinput1_3=n,b\" gamemoderun %command%", LaunchLine.TurnedOff(on, Version));
        Assert.Equal("gamemoderun %command%", LaunchLine.TurnedOff(on, ["version", "xinput1_3"]));
    }

    [Fact]
    public void Off_on_a_line_without_the_setting_changes_nothing() =>
        Assert.Equal("gamemoderun %command% -d3d12", LaunchLine.TurnedOff("gamemoderun %command% -d3d12", Version));
}
