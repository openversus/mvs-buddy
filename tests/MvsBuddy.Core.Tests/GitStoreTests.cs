using System.Security.Cryptography;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using MvsBuddy.Changes;

namespace MvsBuddy.Tests;

// libgit2's configuration search paths are process-wide: these never run beside another class that uses them.
[CollectionDefinition("libgit2", DisableParallelization = true)]
public sealed class Libgit2Collection
{
}

[Collection("libgit2")]
public sealed class GitStoreTests : IDisposable
{
    private readonly TempTree tree = new();

    public void Dispose() => tree.Dispose();

    private string Work => tree.Path("game");

    private string GitDir => tree.Path("data", "states.git");

    private GitStore Store() => GitStore.Open(GitDir, Work, ["/mod/logs/"], NullLogger.Instance);

    private string W(params string[] parts) => Path.Combine([Work, .. parts]);

    // Every file under the working tree as "path sha256 [x]", sorted: the x is the owner's execute bit, where there is one.
    private List<string> Snapshot() => Directory.EnumerateFiles(Work, "*", SearchOption.AllDirectories)
        .Select(f => $"{Path.GetRelativePath(Work, f).Replace('\\', '/')} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))}{(Executable(f) ? " x" : "")}")
        .Order(StringComparer.Ordinal).ToList();

    private static bool Executable(string file) => !OperatingSystem.IsWindows() && (File.GetUnixFileMode(file) & UnixFileMode.UserExecute) != 0;

    // A mod with a Linux executable, a settings file with LF endings, a log, and a file of the game's that is never ours.
    private void Installed()
    {
        tree.File("asi 1", "game", "mod", "Mod_1.asi");
        tree.File("[Settings]\nKey = 1\n", "game", "mod", "Mod.toml");
        tree.File("a log", "game", "mod", "logs", "Mod.log");
        tree.File("the game's", "game", "Game.exe");
        string node = tree.File("node", "game", "mod", "node", "Node");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(node, File.GetUnixFileMode(node) | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void The_repository_lives_in_the_buddys_folder_and_nothing_of_git_lands_in_the_working_tree()
    {
        Installed();
        var store = Store();
        store.SetHead("current");

        store.Snapshot(["mod"], "installed");

        Assert.False(File.Exists(W(".git")) || Directory.Exists(W(".git")));
        Assert.True(Repository.IsValid(GitDir));
        Assert.Equal("current", store.Head());
    }

    [Fact]
    public void The_persons_git_configuration_is_not_read_and_text_comes_back_as_it_went_in()
    {
        Installed();
        string planted = tree.File("[core]\n\tautocrlf = true\n[spike]\n\tleak = yes\n", "home", ".gitconfig");
        GlobalSettings.SetConfigSearchPaths(ConfigurationLevel.Global, Path.GetDirectoryName(planted)!);
        // The control: with the planted path in place, a repository opened directly does read it.
        Repository.Init(tree.Path("control.git"), isBare: true);
        using (var control = new Repository(tree.Path("control.git")))
        {
            Assert.Equal("yes", control.Config.Get<string>("spike.leak")?.Value);
        }
        var before = Snapshot();

        var store = Store();
        store.SetHead("current");
        string installed = store.Snapshot(["mod"], "installed");
        store.Switch(store.Compose(null, []), "empty");
        store.Switch(store.TreeOf(installed), "back");

        using (var repo = new Repository(GitDir))
        {
            Assert.Null(repo.Config.Get<string>("spike.leak"));
        }
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void A_snapshot_records_files_added_changed_and_gone_under_its_roots_and_skips_what_is_excluded()
    {
        Installed();
        var store = Store();
        store.SetHead("current");
        string first = store.Snapshot(["mod"], "installed");
        // The mod updates itself under a new name and rewrites its settings.
        File.Move(W("mod", "Mod_1.asi"), W("mod", "Mod_2.asi"));
        File.WriteAllText(W("mod", "Mod.toml"), "[Settings]\nKey = 2\n");

        string second = store.Snapshot(["mod"], "after a session");

        Assert.Equal(["mod/Mod.toml", "mod/Mod_1.asi", "mod/node/Node"], store.Paths(store.TreeOf(first)).Order(StringComparer.Ordinal));
        Assert.Equal(["mod/Mod.toml", "mod/Mod_2.asi", "mod/node/Node"], store.Paths(store.TreeOf(second)).Order(StringComparer.Ordinal));
        Assert.Equal([("mod/Mod.toml", PathChange.Modified), ("mod/Mod_1.asi", PathChange.Removed), ("mod/Mod_2.asi", PathChange.Added)],
            store.Diff(store.TreeOf(first), store.TreeOf(second)));
        Assert.Equal(first, store.Tip("previous") ?? first); // no previous yet: the caller moves it
    }

    [Fact]
    public void Switching_removes_what_the_target_lacks_writes_what_it_has_and_leaves_everything_else()
    {
        Installed();
        var store = Store();
        store.SetHead("current");
        string installed = store.Snapshot(["mod"], "installed");
        var before = Snapshot();
        string other = tree.File("other mod", "staging", "other.asi");

        string switched = store.Switch(store.Compose(null, [("other/other.asi", other)]), "the other mod");

        Assert.Equal(switched, store.Tip("current"));
        Assert.False(File.Exists(W("mod", "Mod_1.asi")));
        Assert.False(Directory.Exists(W("mod", "node")), "an emptied folder goes with its files");
        Assert.True(File.Exists(W("mod", "logs", "Mod.log")), "excluded files are never the store's to remove");
        Assert.True(File.Exists(W("Game.exe")));
        Assert.Equal("other mod", File.ReadAllText(W("other", "other.asi")));

        store.Switch(store.TreeOf(installed), "back");

        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void A_folder_becomes_a_tree_with_its_files_modes_kept()
    {
        Installed();
        var store = Store();
        store.SetHead("current");
        store.Snapshot(["mod"], "installed");
        string release = tree.File("node 2", "release", "mod", "node", "Node");
        tree.File("asi 2", "release", "mod", "Mod_2.asi");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(release, File.GetUnixFileMode(release) | UnixFileMode.UserExecute);
        }

        store.Switch(store.TreeOfFolder(tree.Path("release")), "release 2");

        Assert.Equal("node 2", File.ReadAllText(W("mod", "node", "Node")));
        Assert.Equal(!OperatingSystem.IsWindows(), Executable(W("mod", "node", "Node")));
        Assert.False(Executable(W("mod", "Mod_2.asi")));
    }

    [Fact]
    public void Heads_branch_moves_only_by_switching_so_the_working_tree_always_matches_it()
    {
        Installed();
        var store = Store();
        store.SetHead("current");
        string installed = store.Snapshot(["mod"], "installed");

        // Committing the target on HEAD's branch first would leave libgit2 nothing to compare the working tree with.
        Assert.Throws<InvalidOperationException>(() => store.Commit("current", store.Compose(null, []), "wrong order"));
        Assert.Equal(installed, store.Tip("current"));

        string elsewhere = store.Commit("prod", store.Compose(null, []), "a branch HEAD is not on");
        Assert.Equal(elsewhere, store.Tip("prod"));
    }

    [Fact]
    public void Switching_before_any_snapshot_is_refused_with_a_sentence()
    {
        Installed();
        var store = Store();
        store.SetHead("current");

        var e = Assert.Throws<ChangeException>(() => store.Switch(store.Compose(null, []), "too soon"));

        Assert.Contains("no snapshot", e.Message);
    }

    [SkippableFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void A_switch_that_fails_part_way_is_put_back_as_it_was()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "makes a folder unwritable with Unix file modes");
        Installed();
        tree.File("someone else's", "game", "paks", "Kept.pak");
        var store = Store();
        store.SetHead("current");
        string installed = store.Snapshot(["mod"], "installed");
        var before = Snapshot();
        // Written in path order: a/ before paks/, so a/First.asi is already there when paks/ refuses.
        string target = store.Compose(null, [("a/First.asi", tree.File("first", "staging", "First.asi")), ("paks/New.pak", tree.File("new", "staging", "New.pak"))]);
        File.SetUnixFileMode(W("paks"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var e = Assert.Throws<ChangeException>(() => store.Switch(target, "cannot finish"));
            Assert.Contains("put back", e.Message);
        }
        finally
        {
            File.SetUnixFileMode(W("paks"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Equal(before, Snapshot());
        Assert.False(Directory.Exists(W("a")), "the target's folders go with its files");
        Assert.Equal(installed, store.Tip("current"));
    }

    [Fact]
    public void A_file_of_the_persons_where_the_target_wants_one_stops_the_switch_and_stays_theirs()
    {
        Installed();
        var store = Store();
        store.SetHead("current");
        string installed = store.Snapshot(["mod"], "installed");
        tree.File("the person's own", "game", "other", "other.asi");
        var before = Snapshot();

        Assert.Throws<ChangeException>(() => store.Switch(store.Compose(null, [("other/other.asi", tree.File("ours", "staging", "other.asi"))]), "in the way"));

        Assert.Equal(before, Snapshot());
        Assert.Equal("the person's own", File.ReadAllText(W("other", "other.asi")));
        Assert.Equal(installed, store.Tip("current"));
    }

    [Fact]
    public void Prune_deletes_what_no_branch_or_tag_reaches_and_everything_else_still_reads()
    {
        Installed();
        var store = Store();
        store.SetHead("current");
        string installed = store.Snapshot(["mod"], "installed");
        string dropped = store.Commit("mvsi", store.Compose(null, [("mvsi/MVSI.asi", tree.File("only mvsi has this", "staging", "MVSI.asi"))]), "mvsi");
        string tagged = store.Commit("cache", store.Compose(null, [("release/r.asi", tree.File("a release", "staging", "r.asi"))]), "release");
        store.Tag("2026.10.13.01", tagged);
        store.DeleteBranch("cache");
        store.DeleteBranch("mvsi");

        var (objects, bytes) = store.Prune();

        Assert.True(objects >= 3 && bytes > 0, $"{objects} objects, {bytes} bytes"); // the commit, its trees, its blob
        using var repo = new Repository(GitDir);
        Assert.Null(repo.Lookup(dropped));
        Assert.NotNull(repo.Lookup(tagged));
        foreach (var path in store.Paths(store.TreeOf(installed)))
        {
            var blob = (Blob)repo.Lookup<Commit>(installed)[path].Target;
            using var content = blob.GetContentStream();
            Assert.Equal(blob.Size, content.CopyToCount());
        }
        Assert.Equal(0, store.Prune().Objects);
    }

    [Fact]
    public void Every_repository_gets_the_settings_its_failures_called_for()
    {
        Installed();
        Store();

        using var repo = new Repository(GitDir);
        Assert.False(repo.Config.Get<bool>("core.autocrlf").Value);
        Assert.True(repo.Config.Get<bool>("core.longpaths").Value);
        Assert.False(repo.Config.Get<bool>("core.logAllRefUpdates").Value);
        Assert.Contains("-text", File.ReadAllText(Path.Combine(GitDir, "info", "attributes")));
    }
}

internal static class StreamCount
{
    public static long CopyToCount(this Stream stream)
    {
        long count = 0;
        var buffer = new byte[1 << 16];
        for (int read; (read = stream.Read(buffer, 0, buffer.Length)) > 0;)
        {
            count += read;
        }
        return count;
    }
}
