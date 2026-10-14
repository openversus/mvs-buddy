using LibGit2Sharp;
using Microsoft.Extensions.Logging;

namespace MvsBuddy.Changes;

/// <summary>A path's change between two trees: whether the path is added, removed or modified.</summary>
internal enum PathChange
{
    Added,
    Removed,
    Modified,
}

/// <summary>
/// A bare git repository in the buddy's folder, with a folder as its working tree or none (a cache). It knows paths,
/// trees, branches and tags, not the game: which paths make up a state is the caller's. Each operation opens the
/// repository and closes it again, so nothing read before (objects, the index) is ever stale.
/// </summary>
/// <remarks>
/// The settings every repository gets, each one a failure seen without it: the person's own git configuration is not
/// read (theirs can say core.autocrlf=true, which rewrote text files on checkout), and no .gitattributes in a working
/// tree can turn on line-ending or filter rules either; core.longpaths (libgit2 on Windows refuses paths past 260
/// characters otherwise); no reflogs (they would keep dropped objects alive). Objects stay loose and are never packed, so
/// <see cref="Prune"/> can delete the ones nothing reaches. Every commit stands alone, without a parent: a branch or tag is
/// one state, not a history, so dropping it frees what only it held. File modes are kept: the mod ships a Linux executable.
/// </remarks>
internal sealed class GitStore
{
    private static readonly ConfigurationLevel[] PersonLevels =
        [ConfigurationLevel.Global, ConfigurationLevel.Xdg, ConfigurationLevel.System, ConfigurationLevel.ProgramData];

    private readonly string gitDir;
    private readonly string? workTree;
    private readonly ILogger log;

    private GitStore(string gitDir, string? workTree, ILogger log)
    {
        this.gitDir = gitDir;
        this.workTree = workTree;
        this.log = log;
    }

    /// <summary>
    /// Opens the repository at <paramref name="gitDir"/>, making it first when there is none, with
    /// <paramref name="workTree"/> as its working tree (null for none). <paramref name="excludes"/> are gitignore lines
    /// for paths under the working tree that are never part of a state.
    /// </summary>
    internal static GitStore Open(string gitDir, string? workTree, IEnumerable<string> excludes, ILogger log)
    {
        IsolateConfiguration();
        gitDir = Path.GetFullPath(gitDir);
        if (!Repository.IsValid(gitDir))
        {
            Repository.Init(gitDir, isBare: true);
            log.LogInformation("Made {GitDir}", gitDir);
        }
        var store = new GitStore(gitDir, workTree == null ? null : Path.GetFullPath(workTree), log);
        using (var repo = store.Repo())
        {
            repo.Config.Set("core.autocrlf", false);
            repo.Config.Set("core.longpaths", true);
            repo.Config.Set("core.logAllRefUpdates", false);
        }
        File.WriteAllLines(Path.Combine(gitDir, "info", "exclude"), excludes);
        // Ahead of any .gitattributes a mod leaves in the game's folder: bytes in, the same bytes out.
        File.WriteAllText(Path.Combine(gitDir, "info", "attributes"), "* -text -filter -ident -eol\n");
        return store;
    }

    /// <summary>Keeps the person's own git configuration (global, XDG, system, ProgramData) out of every repository.</summary>
    internal static void IsolateConfiguration()
    {
        foreach (var level in PersonLevels)
        {
            GlobalSettings.SetConfigSearchPaths(level, []);
        }
    }

    private Repository Repo() => workTree == null ? new(gitDir)
        : new(gitDir, new RepositoryOptions { WorkingDirectoryPath = workTree, IndexPath = Path.Combine(gitDir, "index") });

    /// <summary>The commit <paramref name="branch"/> points at; null when there is no such branch.</summary>
    internal string? Tip(string branch)
    {
        using var repo = Repo();
        return repo.Branches[branch]?.Tip?.Sha;
    }

    /// <summary>The branch HEAD names, whether or not it has a commit yet.</summary>
    internal string Head()
    {
        using var repo = Repo();
        return repo.Head.FriendlyName;
    }

    /// <summary>Points HEAD at <paramref name="branch"/>, which need not exist yet. The working tree is not touched.</summary>
    internal void SetHead(string branch)
    {
        using var repo = Repo();
        repo.Refs.UpdateTarget("HEAD", "refs/heads/" + branch);
    }

    /// <summary>
    /// Records the working tree under <paramref name="roots"/> (files or folders, relative, '/'-separated) as a new commit
    /// on HEAD's branch: files added, changed or gone since its last commit, not only the ones it knew. Returns the commit.
    /// </summary>
    internal string Snapshot(IEnumerable<string> roots, string message)
    {
        var tree = RequireWorkTree();
        using var repo = Repo();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (string root in roots)
        {
            string full = Path.Combine(tree, root);
            if (File.Exists(full))
            {
                paths.Add(root);
            }
            else if (Directory.Exists(full))
            {
                paths.UnionWith(Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).Select(f => Relative(tree, f)));
            }
            // What the last snapshot had under the root and is gone now: staging it records the removal.
            paths.UnionWith(repo.Index.Select(e => e.Path).Where(p => p == root || p.StartsWith(root + "/", StringComparison.Ordinal)));
        }
        if (paths.Count > 0)
        {
            Commands.Stage(repo, paths);
        }
        var commit = CommitOn(repo, repo.Head.CanonicalName, repo.ObjectDatabase.CreateTree(repo.Index), message);
        log.LogInformation("Snapshot of {Count} paths as {Commit} on {Branch}", paths.Count, Short(commit), repo.Head.FriendlyName);
        return commit;
    }

    /// <summary>
    /// A tree of every file in <paramref name="folder"/>, as libgit2 stages them (modes included), written without
    /// touching the store's working tree. Returns the tree.
    /// </summary>
    internal string TreeOfFolder(string folder)
    {
        folder = Path.GetFullPath(folder);
        string index = Path.Combine(gitDir, $"index-{Guid.NewGuid():N}");
        try
        {
            using var repo = new Repository(gitDir, new RepositoryOptions { WorkingDirectoryPath = folder, IndexPath = index });
            var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => Relative(folder, f)).ToList();
            if (files.Count > 0)
            {
                Commands.Stage(repo, files);
            }
            return repo.ObjectDatabase.CreateTree(repo.Index).Sha;
        }
        finally
        {
            File.Delete(index);
        }
    }

    /// <summary>
    /// <paramref name="baseTree"/> (or nothing) with <paramref name="files"/> added or replaced (relative '/'-separated
    /// path, and the file whose bytes it holds; added as ordinary files) and <paramref name="remove"/> taken out. Returns
    /// the tree.
    /// </summary>
    internal string Compose(string? baseTree, IEnumerable<(string Path, string Source)> files, IEnumerable<string>? remove = null)
    {
        using var repo = Repo();
        var definition = baseTree == null ? new TreeDefinition() : TreeDefinition.From(repo.Lookup<Tree>(baseTree));
        foreach (string path in remove ?? [])
        {
            definition.Remove(path);
        }
        foreach (var (path, source) in files)
        {
            definition.Add(path, repo.ObjectDatabase.CreateBlob(Path.GetFullPath(source)), Mode.NonExecutableFile);
        }
        return repo.ObjectDatabase.CreateTree(definition).Sha;
    }

    /// <summary>
    /// A new commit of <paramref name="tree"/> on <paramref name="branch"/>, which is made or moved to it. Returns the commit. Not HEAD's branch while there is a working tree: that one moves
    /// only with <see cref="Switch"/>, after the working tree has.
    /// </summary>
    internal string Commit(string branch, string tree, string message)
    {
        using var repo = Repo();
        if (workTree != null && repo.Head.CanonicalName == "refs/heads/" + branch)
        {
            throw new InvalidOperationException($"{branch} is HEAD's branch: it moves with Switch, once the working tree has.");
        }
        return CommitOn(repo, "refs/heads/" + branch, repo.Lookup<Tree>(tree), message);
    }

    private static string CommitOn(Repository repo, string branchRef, Tree tree, string message)
    {
        var commit = NewCommit(repo, tree, message);
        repo.Refs.Add(branchRef, commit.Id, allowOverwrite: true);
        return commit.Sha;
    }

    private static Commit NewCommit(Repository repo, Tree tree, string message)
    {
        var signature = new Signature("MVS Buddy", "buddy@localhost", DateTimeOffset.Now);
        return repo.ObjectDatabase.CreateCommit(signature, signature, message, tree, [], false);
    }

    /// <summary>Points <paramref name="branch"/> at <paramref name="commit"/>, made or moved.</summary>
    internal void SetBranch(string branch, string commit)
    {
        using var repo = Repo();
        repo.Refs.Add("refs/heads/" + branch, new ObjectId(commit), allowOverwrite: true);
    }

    /// <summary>Removes <paramref name="branch"/> when there is one; its objects stay until <see cref="Prune"/>.</summary>
    internal void DeleteBranch(string branch)
    {
        using var repo = Repo();
        if (repo.Branches[branch] != null)
        {
            repo.Branches.Remove(branch);
        }
    }

    /// <summary>Tags <paramref name="commit"/> as <paramref name="tag"/>, moving the tag when it names another commit already.</summary>
    internal void Tag(string tag, string commit)
    {
        using var repo = Repo();
        repo.Refs.Add("refs/tags/" + tag, new ObjectId(commit), allowOverwrite: true);
    }

    /// <summary>The commit <paramref name="tag"/> names; null when there is no such tag.</summary>
    internal string? Tagged(string tag)
    {
        using var repo = Repo();
        return (repo.Tags[tag]?.Target as Commit)?.Sha;
    }

    /// <summary>The tags, by name, and the commits they name.</summary>
    internal IReadOnlyDictionary<string, string> Tags()
    {
        using var repo = Repo();
        return repo.Tags.Where(t => t.Target is Commit).ToDictionary(t => t.FriendlyName, t => t.Target.Sha, StringComparer.Ordinal);
    }

    /// <summary>Removes <paramref name="tag"/> when there is one.</summary>
    internal void DeleteTag(string tag)
    {
        using var repo = Repo();
        if (repo.Tags[tag] != null)
        {
            repo.Tags.Remove(tag);
        }
    }

    /// <summary>The tree of <paramref name="commit"/>.</summary>
    internal string TreeOf(string commit)
    {
        using var repo = Repo();
        return repo.Lookup<Commit>(commit).Tree.Sha;
    }

    /// <summary>Every file path in <paramref name="tree"/>, '/'-separated.</summary>
    internal IReadOnlyList<string> Paths(string tree)
    {
        using var repo = Repo();
        return Files(repo.Lookup<Tree>(tree)).Select(e => e.Path).ToList();
    }

    /// <summary>The bytes of the file at <paramref name="path"/> in <paramref name="tree"/>; null when the tree has none. For small files.</summary>
    internal byte[]? Read(string tree, string path)
    {
        using var repo = Repo();
        if (repo.Lookup<Tree>(tree)[path]?.Target is not Blob blob)
        {
            return null;
        }
        using var source = blob.GetContentStream();
        using var bytes = new MemoryStream();
        source.CopyTo(bytes);
        return bytes.ToArray();
    }

    /// <summary>Copies the file at <paramref name="path"/> in <paramref name="tree"/> to <paramref name="to"/>; false when the tree has none.</summary>
    internal bool CopyOut(string tree, string path, string to)
    {
        using var repo = Repo();
        if (repo.Lookup<Tree>(tree)[path]?.Target is not Blob blob)
        {
            return false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(to))!);
        using var source = blob.GetContentStream();
        using var target = File.Create(to);
        source.CopyTo(target);
        return true;
    }

    /// <summary>The paths that differ between two trees, and how.</summary>
    internal IReadOnlyList<(string Path, PathChange Change)> Diff(string fromTree, string toTree)
    {
        using var repo = Repo();
        var from = Files(repo.Lookup<Tree>(fromTree)).ToDictionary(e => e.Path, e => e.Target.Id, StringComparer.Ordinal);
        var to = Files(repo.Lookup<Tree>(toTree)).ToDictionary(e => e.Path, e => e.Target.Id, StringComparer.Ordinal);
        return from.Keys.Where(p => !to.ContainsKey(p)).Select(p => (p, PathChange.Removed))
            .Concat(to.Where(p => !from.ContainsKey(p.Key)).Select(p => (p.Key, PathChange.Added)))
            .Concat(to.Where(p => from.TryGetValue(p.Key, out var id) && id != p.Value).Select(p => (p.Key, PathChange.Modified)))
            .OrderBy(c => c.Item1, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Makes the working tree <paramref name="tree"/> and moves HEAD's branch to a new commit of it: what the tree lacks
    /// of HEAD's files is removed, everything else of the working tree is left as it is. HEAD must have a commit that
    /// matches the working tree (a <see cref="Snapshot"/> just before). If writing fails part way, the working tree is put
    /// back as HEAD had it and the branch stays; the exception says what failed. Returns the new commit.
    /// </summary>
    /// <exception cref="ChangeException">The switch failed, and was put back (or could not be, which it says).</exception>
    internal string Switch(string tree, string message)
    {
        var root = RequireWorkTree();
        using var repo = Repo();
        var head = repo.Head.Tip ?? throw new ChangeException($"{repo.Head.FriendlyName} has no snapshot to switch from yet.");
        var target = NewCommit(repo, repo.Lookup<Tree>(tree), message);
        try
        {
            // HEAD still on the snapshot: it is the baseline that tells libgit2 which files are ours to remove.
            repo.Checkout(target.Tree, null, new CheckoutOptions());
        }
        catch (LibGit2SharpException e)
        {
            log.LogWarning(e, "Switching {Tree} to {Commit} failed; putting it back", root, Short(target.Sha));
            string? stuck = PutBack(repo, root, head);
            throw new ChangeException(stuck == null ? $"{e.Message} Everything was put back as it was." : $"{e.Message} It could not all be put back: {stuck}", e);
        }
        repo.Refs.Add(repo.Head.CanonicalName, target.Id, allowOverwrite: true);
        log.LogInformation("Switched {Tree} from {From} to {To}", root, Short(head.Sha), Short(target.Sha));
        return target.Sha;
    }

    // After a failed checkout: HEAD's files, forced back. libgit2 has put every file it wrote in the index, so forcing
    // HEAD's tree also removes those (and the folders they emptied). Deleting "the paths only the target has" by hand
    // would be wrong: when a file of the person's stands where the target wants one, libgit2 refuses before writing
    // anything, and that file is theirs.
    private string? PutBack(Repository repo, string tree, Commit head)
    {
        try
        {
            repo.CheckoutPaths(head.Sha, ["*"], new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
            return null;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is LibGit2SharpException)
        {
            log.LogError(e, "Could not put {Tree} back to {Commit}", tree, Short(head.Sha));
            return e.Message;
        }
    }

    /// <summary>
    /// Deletes every object no branch or tag reaches any more. Loose objects only: nothing here ever packs. Returns how many
    /// objects went and their size on disk.
    /// </summary>
    internal (int Objects, long Bytes) Prune()
    {
        var reachable = new HashSet<ObjectId>();
        using (var repo = Repo())
        {
            foreach (var reference in repo.Refs)
            {
                if (reference.ResolveToDirectReference()?.Target is GitObject target)
                {
                    Reach(target, reachable);
                }
            }
        }
        int objects = 0;
        long bytes = 0;
        foreach (string folder in Directory.EnumerateDirectories(Path.Combine(gitDir, "objects")).Where(d => Path.GetFileName(d).Length == 2))
        {
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                if (ObjectId.TryParse(Path.GetFileName(folder) + Path.GetFileName(file), out var id) && !reachable.Contains(id))
                {
                    bytes += new FileInfo(file).Length;
                    // Git writes objects read-only, and Windows does not delete a read-only file.
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                    objects++;
                }
            }
            if (!Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        log.LogInformation("Pruned {Objects} objects ({Bytes} bytes) from {GitDir}", objects, bytes, gitDir);
        return (objects, bytes);
    }

    private static void Reach(GitObject o, HashSet<ObjectId> reachable)
    {
        if (!reachable.Add(o.Id))
        {
            return;
        }
        if (o is Commit commit)
        {
            Reach(commit.Tree, reachable);
            foreach (var parent in commit.Parents)
            {
                Reach(parent, reachable);
            }
        }
        else if (o is Tree tree)
        {
            foreach (var entry in tree)
            {
                Reach(entry.Target, reachable);
            }
        }
        else if (o is TagAnnotation annotation)
        {
            Reach(annotation.Target, reachable);
        }
    }

    private static IEnumerable<TreeEntry> Files(Tree tree) =>
        tree.SelectMany(e => e.TargetType == TreeEntryTargetType.Tree ? Files((Tree)e.Target) : [e]);

    private string RequireWorkTree() => workTree ?? throw new InvalidOperationException($"{gitDir} has no working tree.");

    private static string Relative(string root, string file) =>
        file.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace(Path.DirectorySeparatorChar, '/');

    private static string Short(string sha) => sha.Substring(0, 8);
}
