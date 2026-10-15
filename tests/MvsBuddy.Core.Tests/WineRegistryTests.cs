using MvsBuddy.Installs;

namespace MvsBuddy.Tests;

// A Proton prefix's user.reg, as Wine writes it (a DXVK override already in the section).
public sealed class WineRegistryTests
{
    private const string UserReg = "WINE REGISTRY Version 2\n;; All keys relative to \\\\User\\\\S-1-5-21-0-0-0-1000\n\n#arch=win64\n\n"
        + "[Software\\\\Wine\\\\Direct3D] 1700000000\n#time=1da0000000000000\n\"MaxVersionGL\"=dword:00040006\n\n"
        + "[Software\\\\Wine\\\\DllOverrides] 1700000000\n#time=1da0000000000000\n\"*d3d11\"=\"native\"\n\"*dxgi\"=\"native\"\n\n"
        + "[Software\\\\Wine\\\\Fonts] 1700000000\n#time=1da0000000000000\n\"LogPixels\"=dword:00000060\n";

    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("\"version\"=\"native,builtin\"", true)]
    [InlineData("\"*version\"=\"n,b\"", true)]
    [InlineData("\"Version\"=\"native\"", true)]
    [InlineData("\"version\"=\"builtin,native\"", false)]
    [InlineData("\"version\"=\"builtin\"", false)]
    [InlineData("\"version\"=\"\"", false)]
    [InlineData("\"versionx\"=\"native\"", false)]
    public void Native_first_is_what_loads_the_dll_from_the_games_folder(string entry, bool loads) =>
        Assert.Equal(loads, WineRegistry.LoadsNative(UserReg.Replace("\"*dxgi\"=\"native\"", entry), "version"));

    [Fact]
    public void Another_sections_entry_of_that_name_does_not_count()
    {
        string registry = UserReg.Replace("\"MaxVersionGL\"=dword:00040006", "\"version\"=\"native\"");
        Assert.False(WineRegistry.LoadsNative(registry, "version"));
    }

    [Fact]
    public void Setting_it_adds_one_line_to_the_section_and_keeps_every_other()
    {
        string set = WineRegistry.WithNative(UserReg, "version", Now);

        Assert.True(WineRegistry.LoadsNative(set, "version"));
        Assert.Equal(UserReg.Replace("#time=1da0000000000000\n\"*d3d11\"", "#time=1da0000000000000\n\"version\"=\"native,builtin\"\n\"*d3d11\""), set);
        Assert.Equal(set, WineRegistry.WithNative(set, "version", Now));
    }

    [Fact]
    public void Setting_it_replaces_a_builtin_entry_and_keeps_windows_line_ends()
    {
        string registry = UserReg.Replace("\"*dxgi\"=\"native\"", "\"*version\"=\"builtin\"").Replace("\n", "\r\n");

        string set = WineRegistry.WithNative(registry, "version", Now);

        Assert.Equal(registry.Replace("\"*version\"=\"builtin\"", "\"version\"=\"native,builtin\""), set);
    }

    [Fact]
    public void Without_the_section_it_is_added_at_the_end()
    {
        string registry = UserReg.Substring(0, UserReg.IndexOf("[Software\\\\Wine\\\\DllOverrides]", StringComparison.Ordinal))
            + "[Software\\\\Wine\\\\Fonts] 1700000000\n#time=1da0000000000000\n\"LogPixels\"=dword:00000060\n";

        string set = WineRegistry.WithNative(registry, "version", Now);

        Assert.StartsWith(registry, set);
        Assert.EndsWith($"\n[Software\\\\Wine\\\\DllOverrides] {Now.ToUnixTimeSeconds()}\n#time={Now.ToFileTime():x}\n\"version\"=\"native,builtin\"\n", set);
        Assert.True(WineRegistry.LoadsNative(set, "version"));
    }
}
