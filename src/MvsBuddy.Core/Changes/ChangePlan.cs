using Microsoft.Extensions.Logging;

namespace MvsBuddy.Changes;

/// <summary>
/// Changes to files, made all together or not at all: the paks a change moves, and files written outside a state. Nothing
/// happens until <see cref="Apply()"/>.
/// </summary>
public sealed class ChangePlan
{
    private readonly HostSystem host;
    private readonly ILogger log;
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
    public void Apply() => Apply(null, "Changing files...");

    /// <summary>
    /// Makes the changes as <see cref="Apply()"/> does, telling <paramref name="progress"/> before each step which it is,
    /// as <paramref name="phase"/> followed by the step's place and the file's name: "Moving the paks... (3 of 12: x.pak)".
    /// </summary>
    /// <exception cref="ChangeException">Refused, or failed and put back; the message says which.</exception>
    public void Apply(IProgress<string>? progress, string phase)
    {
        Check();
        var done = new Stack<Step>();
        foreach (var step in steps)
        {
            try
            {
                log.LogInformation("{Step}", step.Description);
                progress?.Report($"{phase} ({done.Count + 1} of {steps.Count}: {step.Name})");
                step.Do();
                done.Push(step);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                progress?.Report($"{phase} (putting back what was moved so far)");
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
                // The change is made; what is left is only in MVS Buddy's own folder, where reading the install reports it.
                log.LogWarning(e, "Could not delete {Folder} after the change", folder);
            }
        }
        foreach (var (folder, stopAt) in cleanups)
        {
            RemoveEmpty(folder, stopAt);
        }
    }

    /// <summary>
    /// What <see cref="Apply()"/> checks before it changes anything: the game is not running, no condition of the plan fails,
    /// and this account may change every folder. For a change made of this plan and more, checked before the rest.
    /// </summary>
    /// <exception cref="ChangeException">Refused; the message says why.</exception>
    internal void Check()
    {
        if (host.IsGameRunning())
        {
            throw Refuse("MultiVersus is running. Close it, then try again.");
        }
        if (DeniedFolders() is { Count: > 0 } denied)
        {
            string reason = $"This account is not allowed to change files in {string.Join(", ", denied)}, so nothing was changed.";
            log.LogInformation("Refused: {Reason}", reason);
            throw new ChangeException(reason, denied);
        }
    }

    /// <summary>Counts <paramref name="folder"/> among the folders the change writes, for <see cref="DeniedFolders"/>, when something else writes it.</summary>
    internal void Writes(string folder) => folders.Add(folder);

    /// <summary>Moves a file, which must not be at <paramref name="to"/> already, and takes the mark of the web off it where it lands (Windows); undone by moving it back.</summary>
    internal void Move(string from, string to)
    {
        folders.Add(Path.GetDirectoryName(from)!);
        folders.Add(Path.GetDirectoryName(to)!);
        steps.Add(new Step($"Move {from} to {to}", Path.GetFileName(from), () =>
        {
            FileMoves.Move(from, to, host.IsWindows);
            // A pak a person downloaded by hand carries the mark; a copy across drives carries it along. Placed, it is clean.
            Installer.RemoveMarkOfTheWeb(to, null);
        }, () => FileMoves.Move(to, from, host.IsWindows)));
    }

    /// <summary>Writes a file that is not there yet; undone by deleting it.</summary>
    internal void Write(string path, byte[] contents)
    {
        folders.Add(Path.GetDirectoryName(path)!);
        steps.Add(new Step($"Write {path}", Path.GetFileName(path), () =>
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
        steps.Add(new Step($"Delete {path}", Path.GetFileName(path), () =>
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

    private sealed class Step(string description, string name, Action doIt, Action undo)
    {
        public string Description { get; } = description;

        /// <summary>The file's name, for a progress line.</summary>
        public string Name { get; } = name;

        public void Do() => doIt();

        public void Undo() => undo();
    }
}
