namespace MvsBuddy.Tests;

// MVS Buddy's one known place: where a startup puts itself, and when it hands over to the copy already there.
public sealed class SelfInstallTests : IDisposable
{
    private readonly TempTree tree = new();

    public void Dispose() => tree.Dispose();

    private HostSystem Host(bool windows = false) => new() { IsWindows = windows, Home = tree.Path("home"), DataDirectory = tree.Path("data") };

    private string Installed => tree.Path("data", "bin", "MvsBuddy");

    [Fact]
    public void The_installed_copy_runs_unless_this_one_is_newer_or_there_is_none()
    {
        string downloaded = tree.File("a build", "Downloads", "MvsBuddy");
        Assert.Equal(SelfInstall.Outcome.Install, SelfInstall.Decide(Host(), downloaded, "2026.10.16.09", _ => "2026.10.16.09"));

        tree.File("installed build", "data", "bin", "MvsBuddy");
        Assert.Equal(SelfInstall.Outcome.StartInstalled, SelfInstall.Decide(Host(), downloaded, "2026.10.16.09", _ => "2026.10.16.09"));
        Assert.Equal(SelfInstall.Outcome.StartInstalled, SelfInstall.Decide(Host(), downloaded, "2026.10.16.09", _ => "2026.10.17.01"));
        Assert.Equal(SelfInstall.Outcome.Install, SelfInstall.Decide(Host(), downloaded, "2026.10.16.10", _ => "2026.10.16.09"));
        // One that does not answer, or answers nonsense, is not handed over to.
        Assert.Equal(SelfInstall.Outcome.Install, SelfInstall.Decide(Host(), downloaded, "2026.10.16.09", _ => null));
        Assert.Equal(SelfInstall.Outcome.Install, SelfInstall.Decide(Host(), downloaded, "2026.10.16.09", _ => "MVS Buddy 2026"));
        // This is the installed copy: nothing to do.
        Assert.Equal(SelfInstall.Outcome.RunHere, SelfInstall.Decide(Host(), Installed, "2026.10.16.09", _ => throw new InvalidOperationException("not asked")));
        Assert.Equal(SelfInstall.Outcome.RunHere, SelfInstall.Decide(new HostSystem { Home = tree.Path("home") }, downloaded, "2026.10.16.09", _ => null));
    }

    [Fact]
    public void Installing_copies_the_executable_runnable_into_the_known_place()
    {
        string downloaded = tree.File("a build", "Downloads", "MvsBuddy");

        Assert.Equal(Installed, SelfInstall.Install(Host(), downloaded));

        Assert.Equal("a build", File.ReadAllText(Installed));
        Assert.True((File.GetUnixFileMode(Installed) & UnixFileMode.UserExecute) != 0);
        Assert.Equal("a build", File.ReadAllText(downloaded));
        // A newer copy replaces it in one move, nothing beside it left over.
        string newer = tree.File("a newer build", "Downloads", "MvsBuddy2");
        SelfInstall.Install(Host(), newer);
        Assert.Equal("a newer build", File.ReadAllText(Installed));
        Assert.Equal(["MvsBuddy"], Directory.EnumerateFiles(tree.Path("data", "bin")).Select(Path.GetFileName));
        Assert.True((File.GetUnixFileMode(Installed) & UnixFileMode.UserExecute) != 0);
    }

    [Theory]
    [InlineData("2026.10.16.09", "2026.10.16.9")]
    [InlineData("2026.10.16.09+abc123", "2026.10.16.9")]
    [InlineData("MVS Buddy", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_build_number_parses_as_a_version_and_nothing_else_does(string? text, string? expected) =>
        Assert.Equal(expected, SelfInstall.Parse(text)?.ToString());

    [Fact]
    public void Asking_a_program_its_version_reads_its_answer_and_gives_up_on_one_that_has_none()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "runs small shell scripts");
        string answers = tree.File("#!/bin/sh\necho 2026.10.16.09\n", "answers.sh");
        string silent = tree.File("#!/bin/sh\necho hello\n", "silent.sh");
        File.SetUnixFileMode(answers, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(silent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Assert.Equal("2026.10.16.09", SelfInstall.VersionOf(answers));
        Assert.Null(SelfInstall.VersionOf(silent));
        Assert.Null(SelfInstall.VersionOf(tree.Path("nothing-here")));
    }

    [Theory]
    [InlineData(new[] { "--game=/a/b" }, "--game=/a/b")]
    [InlineData(new[] { "--game=/a b/c", "--here" }, "\"--game=/a b/c\" --here")]
    [InlineData(new[] { "say \"hi\"", "" }, "\"say \\\"hi\\\"\" \"\"")]
    [InlineData(new[] { "C:\\Games\\" }, "C:\\Games\\")]
    [InlineData(new[] { "C:\\Games Dir\\" }, "\"C:\\Games Dir\\\\\"")]
    public void Arguments_are_quoted_the_way_they_are_split_back(string[] args, string expected) => Assert.Equal(expected, SelfInstall.Quote(args));

    [Fact]
    public void What_an_update_fetched_is_forgotten_once_the_installed_copy_runs_but_not_while_running_from_it()
    {
        string fetched = tree.File("new build", "data", "downloads", "update", "2026.10.17.01", "MvsBuddy");
        tree.File("a download", "data", "downloads", "abc", "x.zip");

        SelfInstall.ForgetFetchedUpdates(Host(), fetched);
        Assert.True(File.Exists(fetched));

        SelfInstall.ForgetFetchedUpdates(Host(), Installed);
        Assert.False(Directory.Exists(tree.Path("data", "downloads", "update")));
        Assert.True(File.Exists(tree.Path("data", "downloads", "abc", "x.zip")));
    }
}
