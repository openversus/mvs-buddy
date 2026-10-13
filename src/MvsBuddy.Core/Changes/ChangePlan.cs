using Microsoft.Extensions.Logging;

namespace MvsBuddy.Changes;

/// <summary>
/// Changes to files, made all together or not at all. Nothing happens until <see cref="Apply"/>; <see cref="Steps"/> says
/// what would, for a confirmation. Plans combine with <see cref="Then"/> into one: swapping two held sets is holding one,
/// then restoring the other, with one way back if either fails.
/// </summary>
public sealed class ChangePlan
{
    private readonly HostSystem host;
    private readonly ILogger log;
    private readonly List<Func<string?>> refusals = [];
    private readonly List<Step> steps = [];
    private readonly List<string> folders = [];
    private readonly List<(string Folder, string StopAt)> cleanups = [];
    private readonly List<(string Folder, string StopAt)> failureCleanups = [];
    private readonly List<string> deleteAfter = [];

    internal ChangePlan(HostSystem host, ILogger log)
    {
        this.host = host;
        this.log = log;
    }

    /// <summary>What the plan would do, a line per step, in order.</summary>
    public IReadOnlyList<string> Steps => steps.Select(s => s.Description).ToList();

    /// <summary>This plan followed by <paramref name="next"/>, as one: either both happen or neither does.</summary>
    public ChangePlan Then(ChangePlan next)
    {
        if (next == null)
        {
            throw new ArgumentNullException(nameof(next));
        }
        var both = new ChangePlan(host, log);
        both.refusals.AddRange(refusals.Concat(next.refusals));
        both.steps.AddRange(steps.Concat(next.steps));
        both.folders.AddRange(folders.Concat(next.folders));
        both.cleanups.AddRange(cleanups.Concat(next.cleanups));
        both.failureCleanups.AddRange(failureCleanups.Concat(next.failureCleanups));
        both.deleteAfter.AddRange(deleteAfter.Concat(next.deleteAfter));
        return both;
    }

    /// <summary>
    /// The folders the plan changes that this account may not change, as the system answers now; empty when it may change
    /// them all. A folder the plan would make is judged by the nearest one above it that exists. A folder inside another
    /// one listed is left out: permission for the outer one covers it.
    /// </summary>
    public IReadOnlyList<string> DeniedFolders()
    {
        var comparison = host.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var denied = folders.Distinct(host.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Where(host.ChangeDeniedIn).ToList();
        return denied.Where(f => !denied.Any(outer => f.StartsWith(outer + Path.DirectorySeparatorChar, comparison))).ToList();
    }

    /// <summary>
    /// Makes the changes. Refused, with nothing touched, while the game is running, when a condition of the plan fails (a
    /// file already where another would go), or when this account may not change one of the folders (see
    /// <see cref="ChangeException.DeniedFolders"/>). If a step fails, every earlier one is undone.
    /// </summary>
    /// <exception cref="ChangeException">Refused, or failed and put back; the message says which.</exception>
    public void Apply()
    {
        if (host.IsGameRunning())
        {
            throw Refuse("MultiVersus is running. Close it, then try again.");
        }
        foreach (var refusal in refusals)
        {
            if (refusal() is string reason)
            {
                throw Refuse(reason);
            }
        }
        if (DeniedFolders() is { Count: > 0 } denied)
        {
            string reason = $"This account is not allowed to change files in {string.Join(", ", denied)}, so nothing was changed.";
            log.LogInformation("Refused: {Reason}", reason);
            throw new ChangeException(reason, denied);
        }
        var done = new Stack<Step>();
        foreach (var step in steps)
        {
            try
            {
                log.LogInformation("{Step}", step.Description);
                step.Do();
                done.Push(step);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                var stuck = new List<string>();
                while (done.Count > 0)
                {
                    var back = done.Pop();
                    try
                    {
                        back.Undo();
                    }
                    catch (Exception undo) when (undo is IOException || undo is UnauthorizedAccessException)
                    {
                        stuck.Add($"{back.Description} ({undo.Message})");
                    }
                }
                foreach (var (folder, stopAt) in failureCleanups)
                {
                    RemoveEmpty(folder, stopAt);
                }
                string message = stuck.Count == 0
                    ? $"{e.Message} Everything was put back as it was."
                    : $"{e.Message} These could not be put back: {string.Join("; ", stuck)}.";
                log.LogWarning(e, "{Message}", message);
                throw new ChangeException(message, e);
            }
        }
        foreach (string folder in deleteAfter)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // The change is made; what is left is only in the buddy's own folder, where reading the install reports it.
                log.LogWarning(e, "Could not delete {Folder} after the change", folder);
            }
        }
        foreach (var (folder, stopAt) in cleanups)
        {
            RemoveEmpty(folder, stopAt);
        }
    }

    internal void Refuse(Func<string?> reason) => refusals.Add(reason);

    /// <summary>Moves a file, which must not be at <paramref name="to"/> already; undone by moving it back.</summary>
    internal void Move(string from, string to)
    {
        folders.Add(Path.GetDirectoryName(from)!);
        folders.Add(Path.GetDirectoryName(to)!);
        steps.Add(new Step($"Move {from} to {to}", () => FileMoves.Move(from, to, host.IsWindows), () => FileMoves.Move(to, from, host.IsWindows)));
    }

    /// <summary>Writes a file that is not there yet; undone by deleting it.</summary>
    internal void Write(string path, byte[] contents)
    {
        folders.Add(Path.GetDirectoryName(path)!);
        steps.Add(new Step($"Write {path}", () =>
        {
            if (File.Exists(path))
            {
                throw new IOException($"{path} is already there.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, contents);
        }, () => File.Delete(path)));
    }

    /// <summary>Deletes a small file; undone by writing it back.</summary>
    internal void Delete(string path)
    {
        byte[]? kept = null;
        folders.Add(Path.GetDirectoryName(path)!);
        steps.Add(new Step($"Delete {path}", () =>
        {
            kept = File.ReadAllBytes(path);
            File.Delete(path);
        }, () => File.WriteAllBytes(path, kept!)));
    }

    /// <summary>After success, deletes <paramref name="folder"/> and everything in it; a failure then is logged, not undone.</summary>
    internal void DeleteAfter(string folder) => deleteAfter.Add(folder);

    /// <summary>After success, removes <paramref name="folder"/> and the folders above it while they are empty, up to <paramref name="stopAt"/>.</summary>
    internal void CleanUp(string folder, string stopAt) => cleanups.Add((folder, stopAt));

    /// <summary>After a failure has been undone, removes <paramref name="folder"/> and the folders above it while they are empty, up to <paramref name="stopAt"/>.</summary>
    internal void CleanUpOnFailure(string folder, string stopAt) => failureCleanups.Add((folder, stopAt));

    private static void RemoveEmpty(string folder, string stopAt)
    {
        string stop = Path.GetFullPath(stopAt).TrimEnd(Path.DirectorySeparatorChar);
        for (var at = new DirectoryInfo(folder); at != null && at.Exists && at.FullName.TrimEnd(Path.DirectorySeparatorChar) != stop
            && at.FullName.StartsWith(stop + Path.DirectorySeparatorChar, StringComparison.Ordinal); at = at.Parent)
        {
            try
            {
                foreach (var child in at.EnumerateDirectories("*", SearchOption.AllDirectories).OrderByDescending(d => d.FullName.Length))
                {
                    if (!child.EnumerateFileSystemInfos().Any())
                    {
                        child.Delete();
                    }
                }
                if (at.EnumerateFileSystemInfos().Any())
                {
                    return;
                }
                at.Delete();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    private ChangeException Refuse(string reason)
    {
        log.LogInformation("Refused: {Reason}", reason);
        return new ChangeException(reason);
    }

    private sealed class Step(string description, Action doIt, Action undo)
    {
        public string Description { get; } = description;

        public void Do() => doIt();

        public void Undo() => undo();
    }
}
