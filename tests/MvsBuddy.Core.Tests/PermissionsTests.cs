using MvsBuddy.Changes;

namespace MvsBuddy.Tests;

// Windows stood in for: the folders it refuses are a set, and "running cmd as administrator" records the command and
// lifts the refusals it is told to (or the person says no). Real folders, so that which ones exist is real.
public sealed class PermissionsTests : IDisposable
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private readonly TempTree tree = new();
    private readonly HashSet<string> denied = [];
    private readonly List<string> commands = [];
    private Func<int?> answer = () => 0;

    public void Dispose() => tree.Dispose();

    private Permissions Permissions(bool windows = true, string? sid = Sid) => new BuddyBuilder().Host(new HostSystem
    {
        IsWindows = windows,
        Home = tree.Path("home"),
        IsChangeDenied = denied.Contains,
        UserSid = () => sid,
        RunElevated = (program, arguments, _) =>
        {
            commands.Add($"{Path.GetFileName(program)} {arguments}");
            return Task.FromResult(answer());
        },
    }).Build().Permissions;

    private string Win64 => tree.Path("game", "Binaries", "Win64");

    private string Grant(string folder) => $"\"{Path.Combine(Environment.SystemDirectory, "icacls.exe")}\" \"{folder}\" /grant \"*{Sid}:(OI)(CI)M\" /q";

    [Fact]
    public async Task One_prompt_grants_the_folders_asked_about_making_the_missing_one_first()
    {
        Directory.CreateDirectory(Path.Combine(Win64, "plugins"));
        denied.UnionWith([Win64, Path.Combine(Win64, "plugins"), tree.Path("game")]);
        answer = () =>
        {
            denied.Clear();
            return 0;
        };
        // plugins is inside Win64, so Win64's permission covers it; "new" is missing, so it is made and given the
        // permission, and "game", where it is made, is not.
        string missing = tree.Path("game", "new", "deeper");

        var result = await Permissions().AskAsync([Win64, Path.Combine(Win64, "plugins"), missing]);

        Assert.Equal(PermissionOutcome.Granted, result.Outcome);
        Assert.Empty(result.StillDenied);
        string command = Assert.Single(commands);
        Assert.Equal($"cmd.exe /d /s /c \"{Grant(Win64)} & mkdir \"{tree.Path("game", "new")}\" & {Grant(tree.Path("game", "new"))}\"", command);
    }

    [Fact]
    public async Task Saying_no_changes_nothing()
    {
        Directory.CreateDirectory(Win64);
        denied.Add(Win64);
        answer = () => null;

        var result = await Permissions().AskAsync([Win64]);

        Assert.Equal(PermissionOutcome.Declined, result.Outcome);
        Assert.Equal([Win64], result.StillDenied);
        Assert.Contains(Win64, denied);
    }

    [Fact]
    public async Task What_happened_is_asked_of_the_system_afterwards_not_taken_from_the_exit_code()
    {
        string paks = tree.Path("game", "Content", "Paks");
        Directory.CreateDirectory(Win64);
        Directory.CreateDirectory(paks);
        denied.UnionWith([Win64, paks]);
        // Exit code 0, and a folder whose own permissions are not inherited keeps refusing.
        answer = () =>
        {
            denied.Remove(Win64);
            return 0;
        };

        var result = await Permissions().AskAsync([Win64, paks]);

        Assert.Equal(PermissionOutcome.StillDenied, result.Outcome);
        Assert.Equal([paks], result.StillDenied);
        Assert.Contains(paks, result.Message);
    }

    [Fact]
    public async Task A_drive_is_never_opened_up()
    {
        string drive = Path.GetPathRoot(tree.Root)!;

        var result = await Permissions().AskAsync([drive]);

        Assert.Equal(PermissionOutcome.NotAsked, result.Outcome);
        Assert.Empty(commands);
    }

    [Fact]
    public async Task Nothing_is_asked_without_windows_or_without_knowing_the_account()
    {
        Directory.CreateDirectory(Win64);
        denied.Add(Win64);

        var linux = await Permissions(windows: false).AskAsync([Win64]);
        var noSid = await Permissions(sid: null).AskAsync([Win64]);

        Assert.Equal(PermissionOutcome.NotAsked, linux.Outcome);
        Assert.Equal(PermissionOutcome.NotAsked, noSid.Outcome);
        Assert.Empty(commands);
        Assert.Contains(Win64, linux.Message);
    }
}
