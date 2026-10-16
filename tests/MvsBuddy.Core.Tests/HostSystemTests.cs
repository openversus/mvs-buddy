namespace MvsBuddy.Tests;

public sealed class HostSystemTests
{
    // Command lines as /proc shows them, from processes run under Wine 11.13 with a copy of an exe given the game's name.
    [Theory]
    [InlineData("C:\\Program Files\\x y\\MultiVersus-Win64-Shipping.exe\0-n\030\0127.0.0.1\0\0\0")]
    [InlineData("./MultiVersus-Win64-Shipping.exe\0-n\015\0127.0.0.1\0\0")]
    [InlineData("Z:\\home\\someone\\Games\\MultiVersus\\MultiVersus\\Binaries\\Win64\\multiversus-win64-shipping.EXE")]
    [InlineData("\"C:\\Games\\MultiVersus\\Binaries\\Win64\\MultiVersus-Win64-Shipping.exe\"\0")]
    public void A_process_running_the_game_is_the_game(string commandLine) =>
        Assert.True(HostSystem.RunsGame(commandLine));

    [Theory]
    [InlineData("/bin/bash\0-c\0: > /tmp/game/Binaries/Win64/MultiVersus-Win64-Shipping.exe && cp a b\0")]
    [InlineData("tail\0-f\0/games/Win64/MultiVersus-Win64-Shipping.exe\0")]
    [InlineData("C:\\Games\\MultiVersus\\MultiVersus.exe\0")]
    [InlineData("/home/someone/MultiVersus-Win64-Shipping.exe.bak\0")]
    [InlineData("")]
    public void A_process_that_only_names_the_game_is_not_the_game(string commandLine) =>
        Assert.False(HostSystem.RunsGame(commandLine));

    // Steam's client process is named steam; a copy of sleep under that name stands in for it. A shell whose command line
    // names Steam (this test's own, and MVS Buddy's) does not count.
    [SkippableFact]
    public void Steam_is_running_when_a_process_named_steam_is_and_not_when_only_a_command_line_names_it()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux's /proc");
        Skip.If(HostSystem.SteamRunning(), "Steam is running on this machine, so its absence cannot be checked");
        using var mention = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sh") { ArgumentList = { "-c", "sleep 30 # steam" }, UseShellExecute = false });
        string folder = Path.Combine(Path.GetTempPath(), "mvs-buddy-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string steam = Path.Combine(folder, "steam");
            File.Copy("/bin/sleep", steam);
            Assert.False(HostSystem.SteamRunning());
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(steam, "30") { UseShellExecute = false });
            try
            {
                Assert.True(HostSystem.SteamRunning());
            }
            finally
            {
                process!.Kill();
                process.WaitForExit();
            }
            Assert.False(HostSystem.SteamRunning());
        }
        finally
        {
            mention!.Kill();
            mention.WaitForExit();
            Directory.Delete(folder, recursive: true);
        }
    }
}
