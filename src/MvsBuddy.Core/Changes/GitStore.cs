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

/// <summary>One change to a tree, for <see cref="GitStore.Edit"/>; paths relative, '/'-separated.</summary>
internal abstract record TreeEdit(string Path);

/// <summary><see cref="TreeEdit.Path"/> becomes what <paramref name="FromPath"/> is in <paramref name="FromTree"/>, a file or a folder, modes and all.</summary>
internal sealed record Graft(string Path, string FromTree, string FromPath) : TreeEdit(Path);

/// <summary><see cref="TreeEdit.Path"/> becomes an ordinary file holding <paramref name="Bytes"/>.</summary>
internal sealed record Content(string Path, byte[] Bytes) : TreeEdit(Path);

/// <summary><see cref="TreeEdit.Path"/>, a file or a folder, is taken out.</summary>
internal sealed record Removal(string Path) : TreeEdit(Path);

/// <summary>
/// A bare git repository in MVS Buddy's folder, with a folder as its working tree or none (a cache). It knows paths,
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
    /// <paramref name="workTree"/> as its working tree (null for none: a cache, or a look at a store's branches only).
    /// <paramref name="excludes"/> are gitignore lines for paths under the working tree that are never part of a state;
    /// written only with a working tree. With <paramref name="borrowFrom"/> (another store), objects that store has are
    /// read from it and never written again here; that store's <see cref="Prune"/> then has to be told about this one.
    /// </summary>
    internal static GitStore Open(string gitDir, string? workTree, IEnumerable<string> excludes, ILogger log, GitStore? borrowFrom = null)
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
        if (workTree != null)
        {
            File.WriteAllLines(Path.Combine(gitDir, "info", "exclude"), excludes);
        }
        if (borrowFrom != null)
        {
            // Relative, so MVS Buddy's folder can move as a whole; libgit2 reads it from the objects folder.
            string alternates = Path.Combine(gitDir, "objects", "info", "alternates");
            Directory.CreateDirectory(Path.GetDirectoryName(alternates)!);
            File.WriteAllText(alternates, RelativeFolder(Path.Combine(gitDir, "objects"), Path.Combine(borrowFrom.gitDir, "objects")) + "\n");
        }
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
    /// on HEAD's branch: files added, changed or gone since its last commit, not only the ones it knew. Every path the
    /// last state had is recorded as it is now too, under a root or not, so a path stops being tracked only by being gone.
    /// Returns the commit.
    /// </summary>
    internal string Snapshot(IEnumerable<string> roots, string message, IProgress<string>? progress = null, string? phase = null)
    {
        var tree = RequireWorkTree();
        using var repo = Repo();
        var index = repo.Index;
        // What the last state had and has changed since, by libgit2's own look at each file's size and times: one pass,
        // however many files are tracked. (Staging a list of paths instead matches every file against every path given,
        // which took 70 s for 36,000 unchanged files.)
        var changed = repo.RetrieveStatus(new StatusOptions
        {
            IncludeUntracked = false,
            RecurseUntrackedDirs = false,
            IncludeIgnored = false,
            DetectRenamesInIndex = false,
            DetectRenamesInWorkDir = false,
            ExcludeSubmodules = true,
        }).Where(e => (e.State & (FileStatus.ModifiedInWorkdir | FileStatus.DeletedFromWorkdir | FileStatus.TypeChangeInWorkdir)) != 0)
            .Select(e => e.FilePath).ToList();
        // And what is under the roots that the last state did not have, unless excluded.
        var tracked = new HashSet<string>(index.Select(e => e.Path), StringComparer.Ordinal);
        var added = new List<string>();
        foreach (string root in roots)
        {
            string full = Path.Combine(tree, root);
            var files = File.Exists(full) ? [root]
                : Directory.Exists(full) ? Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).Select(f => Relative(tree, f))
                : [];
            added.AddRange(files.Where(f => !tracked.Contains(f) && !repo.Ignore.IsPathIgnored(f)));
        }
        var paths = changed.Concat(added).Distinct(StringComparer.Ordinal).ToList();
        for (int i = 0; i < paths.Count; i++)
        {
            Tell(progress, phase, i, paths.Count);
            if (File.Exists(Path.Combine(tree, paths[i])))
            {
                index.Add(paths[i]);
            }
            else
            {
                index.Remove(paths[i]);
            }
        }
        index.Write();
        var commit = CommitOn(repo, repo.Head.CanonicalName, repo.ObjectDatabase.CreateTree(index), message);
        log.LogInformation("Snapshot of {Changed} changed and {Added} new paths as {Commit} on {Branch}", changed.Count, added.Count, Short(commit), repo.Head.FriendlyName);
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
    /// A tree of <paramref name="paths"/> (files under the working tree, relative, '/'-separated) as they are now, staged
    /// as libgit2 stages them (modes included) through an index of its own: the store's index, and so what its states
    /// track, is not touched. Ignored paths are left out. Returns the tree.
    /// </summary>
    internal string TreeOfFiles(IEnumerable<string> paths, IProgress<string>? progress = null, string? phase = null)
    {
        string tree = RequireWorkTree();
        string index = Path.Combine(gitDir, $"index-{Guid.NewGuid():N}");
        try
        {
            using var repo = new Repository(gitDir, new RepositoryOptions { WorkingDirectoryPath = tree, IndexPath = index });
            var kept = paths.Where(p => !repo.Ignore.IsPathIgnored(p)).ToList();
            for (int i = 0; i < kept.Count; i++)
            {
                Tell(progress, phase, i, kept.Count);
                repo.Index.Add(kept[i]);
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
    /// <paramref name="baseTree"/> (or nothing) with <paramref name="edits"/> made in order. Returns the tree. A graft
    /// from a tree this store borrows reads it from there.
    /// </summary>
    /// <exception cref="ChangeException">A graft's source is not in its tree.</exception>
    internal string Edit(string? baseTree, IEnumerable<TreeEdit> edits)
    {
        using var repo = Repo();
        var definition = baseTree == null ? new TreeDefinition() : TreeDefinition.From(repo.Lookup<Tree>(baseTree));
        var sources = new Dictionary<string, Tree>(StringComparer.Ordinal);
        foreach (var edit in edits)
        {
            switch (edit)
            {
                case Graft graft:
                    if (!sources.TryGetValue(graft.FromTree, out var source))
                    {
                        sources[graft.FromTree] = source = repo.Lookup<Tree>(graft.FromTree);
                    }
                    var entry = source[graft.FromPath]
                        ?? throw new ChangeException($"{graft.FromPath} is not in {Short(graft.FromTree)}.");
                    definition.Remove(graft.Path);
                    definition.Add(graft.Path, entry);
                    break;
                case Content content:
                    using (var bytes = new MemoryStream(content.Bytes, writable: false))
                    {
                        definition.Remove(content.Path);
                        definition.Add(content.Path, repo.ObjectDatabase.CreateBlob(bytes), Mode.NonExecutableFile);
                    }
                    break;
                case Removal removal:
                    definition.Remove(removal.Path);
                    break;
            }
        }
        return repo.ObjectDatabase.CreateTree(definition).Sha;
    }

    /// <summary>Whether <paramref name="tree"/> has a file or folder at <paramref name="path"/>.</summary>
    internal bool Has(string tree, string path)
    {
        using var repo = Repo();
        return repo.Lookup<Tree>(tree)[path] != null;
    }

    /// <summary>How many files <paramref name="tree"/> holds, and their size.</summary>
    internal (int Files, long Bytes) Size(string tree)
    {
        using var repo = Repo();
        var files = Files(repo.Lookup<Tree>(tree)).ToList();
        return (files.Count, files.Sum(e => repo.ObjectDatabase.RetrieveObjectMetadata(e.Target.Id).Size));
    }

    /// <summary>The message <paramref name="commit"/> was made with, and when.</summary>
    internal (string Message, DateTimeOffset At) Describe(string commit)
    {
        using var repo = Repo();
        var found = repo.Lookup<Commit>(commit);
        return (found.Message, found.Committer.When);
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
    internal string Switch(string tree, string message, IProgress<string>? progress = null, string? phase = null)
    {
        var root = RequireWorkTree();
        using var repo = Repo();
        var head = repo.Head.Tip ?? throw new ChangeException($"{repo.Head.FriendlyName} has no snapshot to switch from yet.");
        var target = NewCommit(repo, repo.Lookup<Tree>(tree), message);
        try
        {
            // HEAD still on the snapshot: it is the baseline that tells libgit2 which files are ours to remove.
            repo.Checkout(target.Tree, null, new CheckoutOptions { OnCheckoutProgress = Telling(progress, phase) });
        }
        catch (LibGit2SharpException e)
        {
            log.LogWarning(e, "Switching {Tree} to {Commit} failed; putting it back", root, Short(target.Sha));
            string? stuck = PutBack(repo, root, head, progress);
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
    internal const string PuttingBack = "Putting the game's files back...";

    // A phase's progress through its files, every hundredth of them or so and at the last: "<phase> (120 of 350 files)".
    private static void Tell(IProgress<string>? progress, string? phase, int done, int total)
    {
        if (progress != null && phase != null && total > 0 && (done % Math.Max(1, total / 100) == 0 || done == total - 1))
        {
            progress.Report($"{phase} ({done + 1} of {total} files)");
        }
    }

    // libgit2's checkout progress as the same kind of line; null when nobody is listening.
    private static LibGit2Sharp.Handlers.CheckoutProgressHandler? Telling(IProgress<string>? progress, string? phase) =>
        progress == null || phase == null ? null : (_, done, total) => Tell(progress, phase, Math.Max(0, done - 1), total);

    private string? PutBack(Repository repo, string tree, Commit head, IProgress<string>? progress)
    {
        try
        {
            repo.CheckoutPaths(head.Sha, ["*"], new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force, OnCheckoutProgress = Telling(progress, PuttingBack) });
            return null;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is LibGit2SharpException)
        {
            log.LogError(e, "Could not put {Tree} back to {Commit}", tree, Short(head.Sha));
            return e.Message;
        }
    }

    /// <summary>
    /// Deletes every object no branch or tag reaches any more, of this store or of <paramref name="borrowers"/> (the stores
    /// that borrow this one's objects). Loose objects only: nothing here ever packs. Returns how many objects went and
    /// their size on disk.
    /// </summary>
    internal (int Objects, long Bytes) Prune(IEnumerable<GitStore>? borrowers = null)
    {
        var reachable = new HashSet<ObjectId>();
        foreach (var store in (borrowers ?? []).Prepend(this))
        {
            using var repo = store.Repo();
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

    // The path from one folder to another, as "../.."-steps and names ('/'-separated); the full path when they share no root.
    internal static string RelativeFolder(string from, string to)
    {
        var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        char[] separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
        string[] a = Path.GetFullPath(from).TrimEnd(separators).Split(separators);
        string[] b = Path.GetFullPath(to).TrimEnd(separators).Split(separators);
        int common = 0;
        while (common < a.Length && common < b.Length && string.Equals(a[common], b[common], comparison))
        {
            common++;
        }
        if (common == 0)
        {
            return Path.GetFullPath(to);
        }
        return string.Join("/", Enumerable.Repeat("..", a.Length - common).Concat(b.Skip(common)));
    }
}
