using Microsoft.Extensions.Logging.Abstractions;
using MvsBuddy.Changes;

namespace MvsBuddy.Tests;

// The plan a change moves paks with: refused before anything moves, or put back when a step fails part way.
public sealed class ChangePlanTests : IDisposable
{
    private readonly TempTree tree = new();

    public void Dispose() => tree.Dispose();

    private ChangePlan Plan(Func<string, bool>? changeDenied = null) => new(new HostSystem
    {
        Home = tree.Path("home"),
        DataDirectory = tree.Path("data"),
        IsGameRunning = () => false,
        IsChangeDenied = changeDenied ?? (_ => false),
    }, NullLogger.Instance);

    [Fact]
    public void A_folder_the_plan_would_make_is_judged_by_the_nearest_one_that_exists()
    {
        string pak = tree.File("ovs pak", "profile", "OVS_P.pak");
        string set = tree.Path("data", "paks", "testing");
        // Only the folder MVS Buddy's own would be made in is refused.
        var plan = Plan(folder => folder == tree.Root);
        plan.Move(pak, Path.Combine(set, "OVS_P.pak"));
        plan.Write(Path.Combine(set, "inside", "manifest.json"), [1]);

        var e = Assert.Throws<ChangeException>(plan.Apply);

        // The first folder the plan would make; the one inside it is covered by it.
        Assert.Equal([set], e.DeniedFolders);
        Assert.Equal("ovs pak", File.ReadAllText(pak));
        Assert.False(Directory.Exists(tree.Path("data")));
    }

    // Locks a folder with Unix file modes, which Windows does not have.
    [SkippableFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void A_step_that_fails_part_way_puts_back_every_step_before_it()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "locks a folder with Unix file modes");
        var paks = new[] { "a", "b", "c" }.ToDictionary(name => tree.File(name, "profile", name + ".pak"), name => name);
        string stuck = tree.File("d", "locked", "d.pak");
        string locked = Path.GetDirectoryName(stuck)!;
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var plan = Plan();
            foreach (string pak in paks.Keys)
            {
                plan.Move(pak, tree.Path("data", "paks", "testing", Path.GetFileName(pak)));
            }
            // Last: its folder cannot be written, so it cannot be moved out of it.
            plan.Move(stuck, tree.Path("data", "paks", "testing", "d.pak"));

            var e = Assert.Throws<ChangeException>(plan.Apply);

            Assert.Contains("put back", e.Message);
            Assert.All(paks, p => Assert.Equal(p.Value, File.ReadAllText(p.Key)));
            Assert.Equal("d", File.ReadAllText(stuck));
            Assert.Empty(Directory.EnumerateFiles(tree.Path("data", "paks", "testing")));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [SkippableFact]
    public void A_move_onto_another_drive_copies_checks_and_comes_back()
    {
        // /tmp and /dev/shm are separate file systems here: a rename between them fails, so the copy path runs.
        Skip.IfNot(OperatingSystem.IsLinux() && Directory.Exists("/dev/shm"), "needs /dev/shm");
        string other = Path.Combine("/dev/shm", "mvs-buddy-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            string pak = tree.File("ovs pak", "profile", "OVS_P.pak");
            string there = Path.Combine(other, "paks", "testing", "OVS_P.pak");
            var away = Plan();
            away.Move(pak, there);
            away.Apply();
            Assert.False(File.Exists(pak));
            Assert.Equal("ovs pak", File.ReadAllText(there));

            var back = Plan();
            back.Move(there, pak);
            back.Apply();
            Assert.Equal("ovs pak", File.ReadAllText(pak));
            Assert.False(File.Exists(there));
        }
        finally
        {
            if (Directory.Exists(other))
            {
                Directory.Delete(other, recursive: true);
            }
        }
    }
}
