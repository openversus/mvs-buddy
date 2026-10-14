using System.Text;
using MvsBuddy.Catalog;
using MvsBuddy.Changes;
using MvsBuddy.Games;
using MvsBuddy.Installs;

namespace MvsBuddy.Tests;

// Installs laid out as the Season 1 zip unpacks (see InstallReaderTests), on a Linux Steam library with a Proton prefix.
public sealed class InstallChangesTests : IDisposable
{
    private readonly TempTree tree = new();
    private bool gameRunning;
    private string data;

    public InstallChangesTests() => data = tree.Path("data");

    public void Dispose() => tree.Dispose();

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private Buddy Buddy(Func<string, bool>? changeDenied = null) => new BuddyBuilder().Host(new HostSystem
    {
        Home = tree.Path("home"),
        DataDirectory = data,
        IsGameRunning = () => gameRunning,
        IsChangeDenied = changeDenied ?? (_ => false),
    }).Build();

    private string Win64 => tree.Path("steamapps", "common", "MultiVersus", "MultiVersus", "Binaries", "Win64");

    private string SavedPaks => tree.Path("steamapps", "compatdata", "1818750", "pfx", "drive_c", "users", "steamuser", "AppData", "Local", "MultiVersus", "Saved", "Paks");

    private GameInstall Game() => new(Win64, GameStore.Steam, FoundBy.SteamLibrary) { WinePrefix = tree.Path("steamapps", "compatdata", "1818750", "pfx") };

    private string[] Rel(string path) => Path.GetRelativePath(tree.Root, path).Split(Path.DirectorySeparatorChar);

    // The Season 1 install, Ultimate ASI Loader as version.dll, the mod's paks in the profile and one stray in Content/Paks.
    private Dictionary<string, string> InstallOpenVersus()
    {
        tree.Game("steamapps", "common", "MultiVersus");
        string[] home = [.. Rel(Win64), "plugins", "OpenVersus"];
        var files = new Dictionary<string, string>
        {
            [tree.File("asi 2026.10.12.01", [.. home, "OpenVersus_2026.10.12.01.asi"])] = "asi 2026.10.12.01",
            [tree.File(Fixture("OpenVersus.testing.toml"), [.. home, "OpenVersus.toml"])] = Fixture("OpenVersus.testing.toml"),
            [tree.File("linux node", [.. home, "node", "linux-x64", "appsettings.json"])] = "linux node",
            [tree.File("windows node", [.. home, "node", "win-x64", "appsettings.json"])] = "windows node",
            [tree.File("ovs pak", [.. Rel(SavedPaks), "OVS_P.pak"])] = "ovs pak",
            [tree.File("ovs ucas", [.. Rel(SavedPaks), "OVS_P.ucas"])] = "ovs ucas",
        };
        tree.Bytes([.. Encoding.ASCII.GetBytes("MZ "), .. Encoding.Unicode.GetBytes("Ultimate ASI Loader")], [.. Rel(Win64), "version.dll"]);
        tree.File("stray", "steamapps", "common", "MultiVersus", "MultiVersus", "Content", "Paks", "~mods", "OVS_Audio_P.pak");
        return files;
    }

    [Fact]
    public void Disabling_openversus_holds_its_files_and_paks_and_leaves_the_loader()
    {
        var files = InstallOpenVersus();
        var buddy = Buddy();

        buddy.Changes.HoldOpenVersus(buddy.Installs.Read(Game())).Apply();

        var state = buddy.Installs.Read(Game());
        Assert.Equal(InstallStatus.Disabled, state.Status);
        Assert.All(files.Keys, f => Assert.False(File.Exists(f), f));
        Assert.False(Directory.Exists(Path.Combine(Win64, "plugins", "OpenVersus")));
        Assert.True(Directory.Exists(Path.Combine(Win64, "plugins")));
        Assert.True(File.Exists(Path.Combine(Win64, "version.dll")));
        Assert.Empty(state.PaksInGame);
        Assert.True(Directory.Exists(SavedPaks));
        var set = Assert.Single(state.Held);
        Assert.Equal(InstallChanges.OpenVersusSetFor(Channel.Testing), set.Name);
        Assert.True(set.IsRecorded);
        Assert.Equal(7, set.Entries.Count(e => e.IsHeld));
        Assert.StartsWith(data, set.Folder);
    }

    [Fact]
    public void Enabling_again_puts_everything_back_and_the_stray_pak_into_the_profile()
    {
        var files = InstallOpenVersus();
        var buddy = Buddy();
        buddy.Changes.HoldOpenVersus(buddy.Installs.Read(Game())).Apply();

        buddy.Changes.Restore(buddy.Installs.Read(Game()), InstallChanges.OpenVersusSetFor(Channel.Testing)).Apply();

        var state = buddy.Installs.Read(Game());
        Assert.Equal(InstallStatus.Installed, state.Status);
        Assert.All(files, f => Assert.Equal(f.Value, File.ReadAllText(f.Key)));
        Assert.Equal("stray", File.ReadAllText(Path.Combine(SavedPaks, "OVS_Audio_P.pak")));
        Assert.Empty(state.PaksInGame);
        Assert.Empty(state.Held);
        Assert.False(Directory.Exists(Path.Combine(data, "held")));
    }

    [Fact]
    public void Holding_what_is_already_held_is_refused_and_touches_nothing()
    {
        InstallOpenVersus();
        var buddy = Buddy();
        buddy.Changes.HoldOpenVersus(buddy.Installs.Read(Game())).Apply();
        tree.File("reinstalled", [.. Rel(Win64), "plugins", "OpenVersus", "OpenVersus_2026.10.13.01.asi"]);
        tree.File(Fixture("OpenVersus.testing.toml"), [.. Rel(Win64), "plugins", "OpenVersus", "OpenVersus.toml"]);

        var e = Assert.Throws<ChangeException>(() => buddy.Changes.HoldOpenVersus(buddy.Installs.Read(Game())).Apply());

        Assert.Contains("already held", e.Message);
        Assert.True(File.Exists(Path.Combine(Win64, "plugins", "OpenVersus", "OpenVersus_2026.10.13.01.asi")));
    }

    [Fact]
    public void Restoring_over_a_file_that_is_back_is_refused_and_touches_nothing()
    {
        InstallOpenVersus();
        var buddy = Buddy();
        buddy.Changes.HoldOpenVersus(buddy.Installs.Read(Game())).Apply();
        string reinstalled = tree.File("reinstalled", [.. Rel(Win64), "plugins", "OpenVersus", "OpenVersus.toml"]);

        var e = Assert.Throws<ChangeException>(() => buddy.Changes.Restore(buddy.Installs.Read(Game()), InstallChanges.OpenVersusSetFor(Channel.Testing)).Apply());

        Assert.Contains(reinstalled, e.Message);
        Assert.Equal("reinstalled", File.ReadAllText(reinstalled));
        Assert.Equal(7, buddy.Installs.Read(Game()).Held.Single().Entries.Count(h => h.IsHeld));
    }

    [Fact]
    public void Nothing_changes_while_the_game_is_running()
    {
        var files = InstallOpenVersus();
        var buddy = Buddy();
        gameRunning = true;

        var e = Assert.Throws<ChangeException>(() => buddy.Changes.HoldOpenVersus(buddy.Installs.Read(Game())).Apply());

        Assert.Contains("running", e.Message);
        Assert.All(files.Keys, f => Assert.True(File.Exists(f)));
    }

    [Fact]
    public void Swapping_to_mvs_infinite_holds_openversus_and_restores_mvs_infinite_in_one_go()
    {
        InstallOpenVersus();
        string mvsi = tree.File("mvsi", [.. Rel(Win64), "plugins", "MVSIASI.asi"]);
        string mvsiPak = tree.File("mvsi pak", "steamapps", "common", "MultiVersus", "MultiVersus", "Content", "Paks", "MVSI_Content_P.pak");
        var buddy = Buddy();
        buddy.Changes.HoldMvsInfinite(buddy.Installs.Read(Game())).Apply();
        Assert.False(File.Exists(mvsi));
        Assert.False(File.Exists(mvsiPak));

        var state = buddy.Installs.Read(Game());
        var swap = buddy.Changes.HoldOpenVersus(state).Then(buddy.Changes.Restore(state, InstallChanges.MvsInfiniteSet));
        swap.Apply();

        var after = buddy.Installs.Read(Game());
        Assert.Equal(InstallStatus.Disabled, after.Status);
        Assert.True(File.Exists(mvsi));
        Assert.True(File.Exists(mvsiPak));
        Assert.Equal([InstallChanges.OpenVersusSetFor(Channel.Testing)], after.Held.Select(h => h.Name));
        Assert.Contains(after.Notes, n => n.Contains("MVS Infinite") && n.Contains("cannot run alongside"));
    }

    // The same locked folder, with the system asked first: the plan stops before its first step and names the folder,
    // where without the question it would get three steps in and put them back.
    [SkippableFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void A_folder_the_account_may_not_change_stops_the_plan_before_anything_moves()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "locks a folder with Unix file modes");
        Skip.If(Environment.UserName == "root", "root may change any folder");
        var files = InstallOpenVersus();
        string locked = Path.Combine(Win64, "plugins", "OpenVersus", "node", "win-x64");
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var buddy = Buddy(HostSystem.ChangeDeniedOnUnix);
            var plan = buddy.Changes.HoldOpenVersus(buddy.Installs.Read(Game()));
            Assert.Equal([locked], plan.DeniedFolders());

            var e = Assert.Throws<ChangeException>(plan.Apply);

            Assert.Equal([locked], e.DeniedFolders);
            Assert.Contains("nothing was changed", e.Message);
            Assert.All(files, f => Assert.Equal(f.Value, File.ReadAllText(f.Key)));
            Assert.False(Directory.Exists(Path.Combine(data, "held")));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [SkippableFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void Reading_an_install_says_which_of_its_folders_the_account_may_not_change()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "locks a folder with Unix file modes");
        Skip.If(Environment.UserName == "root", "root may change any folder");
        InstallOpenVersus();
        var buddy = Buddy(HostSystem.ChangeDeniedOnUnix);
        Assert.Empty(buddy.Installs.Read(Game()).DeniedFolders);

        File.SetUnixFileMode(Win64, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var state = buddy.Installs.Read(Game());

            Assert.Equal([Win64], state.DeniedFolders);
            Assert.Contains(state.Notes, n => n.Contains("not allowed to change files in " + Win64, StringComparison.Ordinal));
        }
        finally
        {
            File.SetUnixFileMode(Win64, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void A_folder_the_plan_would_make_is_judged_by_the_nearest_one_that_exists()
    {
        InstallOpenVersus();
        Assert.False(Directory.Exists(data));
        // Only the folder the profile's held area would be made in is refused.
        var buddy = Buddy(folder => folder == tree.Root);
        var plan = buddy.Changes.HoldOpenVersus(buddy.Installs.Read(Game()));

        var e = Assert.Throws<ChangeException>(plan.Apply);

        // The held set's folders, from the first one the plan would make: the ones inside it are covered by it.
        string files = Assert.Single(e.DeniedFolders);
        Assert.StartsWith(data + Path.DirectorySeparatorChar, files, StringComparison.Ordinal);
        Assert.False(Directory.Exists(data));
    }

    // Locks a folder with Unix file modes, which Windows does not have.
    [SkippableFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void A_step_that_fails_part_way_puts_back_every_step_before_it()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "locks a folder with Unix file modes");
        var files = InstallOpenVersus();
        // The Windows node's folder cannot be written, so its file cannot be moved out of it; it is held after the
        // plugin, the settings and the Linux node, which must all come back.
        string locked = Path.Combine(Win64, "plugins", "OpenVersus", "node", "win-x64");
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var buddy = Buddy();
            var plan = buddy.Changes.HoldOpenVersus(buddy.Installs.Read(Game()));
            int lockedStep = plan.Steps.ToList().FindIndex(s => s.Contains(Path.Combine("win-x64", "appsettings.json")));
            Assert.True(lockedStep >= 3, $"the locked file is step {lockedStep}: earlier steps must exist for the test to mean anything");

            var e = Assert.Throws<ChangeException>(plan.Apply);

            Assert.Contains("put back", e.Message);
            Assert.All(files, f => Assert.Equal(f.Value, File.ReadAllText(f.Key)));
            Assert.Empty(buddy.Installs.Read(Game()).Held);
            Assert.False(Directory.Exists(Path.Combine(data, "held")));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [SkippableFact]
    public void Holding_onto_another_drive_copies_checks_and_restores()
    {
        // /tmp and /dev/shm are separate file systems here: a rename between them fails, so the copy path runs.
        Skip.IfNot(Directory.Exists("/dev/shm") && OperatingSystem.IsLinux(), "needs /dev/shm");
        data = Path.Combine("/dev/shm", "mvs-buddy-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = InstallOpenVersus();
            var buddy = Buddy();

            buddy.Changes.HoldOpenVersus(buddy.Installs.Read(Game())).Apply();
            var held = buddy.Installs.Read(Game()).Held.Single();
            Assert.All(held.Entries, e => Assert.True(e.IsHeld));
            Assert.All(files.Keys, f => Assert.False(File.Exists(f)));

            buddy.Changes.Restore(buddy.Installs.Read(Game()), InstallChanges.OpenVersusSetFor(Channel.Testing)).Apply();
            Assert.All(files, f => Assert.Equal(f.Value, File.ReadAllText(f.Key)));
        }
        finally
        {
            if (Directory.Exists(data))
            {
                Directory.Delete(data, recursive: true);
            }
        }
    }

    [Fact]
    public void A_hold_that_never_finished_is_reported_and_can_be_discarded()
    {
        InstallOpenVersus();
        var buddy = Buddy();
        string copy = new HeldArea(new HostSystem { DataDirectory = data }).CopyFolder(Game())!;
        tree.File("half", [.. Rel(copy), "OpenVersus", "files", "000-OpenVersus_2026.10.12.01.asi"]);

        var state = buddy.Installs.Read(Game());
        var set = Assert.Single(state.Held);
        Assert.False(set.IsRecorded);
        Assert.Equal(InstallStatus.Installed, state.Status);
        Assert.Contains(state.Notes, n => n.Contains("did not finish"));

        buddy.Changes.Discard(state, set.Name).Apply();

        Assert.Empty(buddy.Installs.Read(Game()).Held);
    }

    [Fact]
    public void Deleting_a_held_set_for_good_takes_its_folder_and_leaves_the_game_alone()
    {
        InstallOpenVersus();
        var buddy = Buddy();
        buddy.Changes.HoldOpenVersus(buddy.Installs.Read(Game())).Apply();
        var off = buddy.Installs.Read(Game());
        var set = Assert.Single(off.Held);
        var game = Directory.EnumerateFiles(tree.Path("steamapps"), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();

        buddy.Changes.Discard(off, set.Name).Apply();

        var after = buddy.Installs.Read(Game());
        Assert.Empty(after.Held);
        Assert.Equal(InstallStatus.LoaderOnly, after.Status); // the loader was not the buddy's, so turning off left it
        Assert.False(Directory.Exists(Path.GetDirectoryName(set.Folder)), "the copy's folder goes when its last set does");
        Assert.Equal(game, Directory.EnumerateFiles(tree.Path("steamapps"), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
    }
}
