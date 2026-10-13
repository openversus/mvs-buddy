using Microsoft.Extensions.Logging;
using MvsBuddy.Installs;

namespace MvsBuddy.Changes;

/// <summary>
/// Plans that change an install: holding mods aside (disabling OpenVersus, setting MVS Infinite aside) and restoring
/// them. Each returns a <see cref="ChangePlan"/>; nothing happens until it is applied.
/// </summary>
public sealed class InstallChanges
{
    /// <summary>The held set that is OpenVersus.</summary>
    public const string OpenVersusSet = "OpenVersus";

    /// <summary>The held set that is MVS Infinite.</summary>
    public const string MvsInfiniteSet = "MVS Infinite";

    private readonly HostSystem host;
    private readonly HeldArea held;
    private readonly ILogger<InstallChanges> log;

    internal InstallChanges(HostSystem host, ILogger<InstallChanges> log)
    {
        this.host = host;
        held = new HeldArea(host);
        this.log = log;
    }

    /// <summary>
    /// Disables OpenVersus: holds every file of its installed layout, any older layout's files, and its paks, both in the
    /// profile and any found in the game's own folders. The loader stays: without the plugin it loads nothing of ours, and
    /// other ASI mods may need it. Restoring puts the files back, except the paks from the game's folders, which go to
    /// the profile where the mod keeps them (back where they were, when the profile's paks folder is unknown).
    /// </summary>
    public ChangePlan HoldOpenVersus(InstallState state)
    {
        Require(state);
        var files = state.Mod.Files.Concat(state.Mod.OldLayoutFiles).Select(f => (f, f))
            .Concat(state.Paks.Select(p => (p.Path, p.Path)));
        // Where the profile's paks folder is unknown, they still go: a game without the mod should not mount them.
        files = files.Concat(state.PaksInGame.Select(p => (p.Path, state.SavedPaksDirectory == null ? p.Path : Path.Combine(state.SavedPaksDirectory, p.Name))));
        return Hold(state, OpenVersusSet, files.ToList(), $"There is nothing of {OpenVersusSet} here to hold.");
    }

    /// <summary>
    /// Sets MVS Infinite aside: its plugins, and <paramref name="paks"/> from the game's <c>Content/Paks</c> (by default the
    /// ones whose names say MVS Infinite; pass the person's own choice to overrule that).
    /// </summary>
    public ChangePlan HoldMvsInfinite(InstallState state, IEnumerable<OtherPak>? paks = null)
    {
        Require(state);
        var files = state.OtherPlugins.Where(p => p.IsMvsInfinite).Select(p => p.Path)
            .Concat((paks ?? state.OtherPaks.Where(p => p.IsMvsInfinite)).Select(p => p.Path))
            .Select(f => (f, f)).ToList();
        return Hold(state, MvsInfiniteSet, files, $"There is nothing of {MvsInfiniteSet} here to hold.");
    }

    /// <summary>Holds any files under <paramref name="set"/>, such as another ASI plugin; restoring puts each back where it was.</summary>
    public ChangePlan Hold(InstallState state, string set, IEnumerable<string> files)
    {
        Require(state);
        if (string.IsNullOrEmpty(set))
        {
            throw new ArgumentException("The set needs a name.", nameof(set));
        }
        return Hold(state, set, (files ?? throw new ArgumentNullException(nameof(files))).Select(f => (f, f)).ToList(), $"There is nothing to hold as {set}.");
    }

    /// <summary>
    /// A clean start: holds <paramref name="extras"/> (from <see cref="StockCheck"/>, all of them or the person's pick) as one
    /// set named for the time, leaving the game's folder as the game shipped it. Restoring the set puts every file back.
    /// </summary>
    public ChangePlan HoldNotStock(InstallState state, IEnumerable<StockExtra> extras)
    {
        Require(state);
        var files = (extras ?? throw new ArgumentNullException(nameof(extras))).Select(e => (e.Path, e.Path)).ToList();
        return Hold(state, $"Not stock {DateTimeOffset.Now:yyyy-MM-dd HHmmss}", files, "Nothing was picked to set aside.");
    }

    /// <summary>
    /// Puts a held set back. For the set an install keeps of what it replaced, this undoes that install: first the files it
    /// put in place are taken away, and any OpenVersus plugin the set does not put back (the mod updates itself under a new
    /// file name), then the held files go back; what is taken away is deleted once everything is back. Refused, with
    /// nothing touched, when a file the buddy did not put there is where one of the set's would go.
    /// </summary>
    public ChangePlan Restore(InstallState state, string set)
    {
        Require(state);
        var plan = new ChangePlan(host, log);
        var record = state.Held.FirstOrDefault(h => h.Name == set && h.IsRecorded);
        if (record == null)
        {
            plan.Refuse(() => $"Nothing is held as {set} for this copy of the game.");
            return plan;
        }
        var entries = record.Entries.Where(e => e.IsHeld).ToList();
        var comparer = host.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var restoreTo = new HashSet<string>(entries.Select(e => e.RestoreTo), comparer);
        var takeAway = record.Placed.Count == 0 ? []
            : record.Placed.Concat(state.Mod.Plugins.Where(p => !restoreTo.Contains(p))).Where(File.Exists).Distinct(comparer).ToList();
        var takenAway = new HashSet<string>(takeAway, comparer);
        string undone = Path.Combine(record.Folder, "undone");
        for (int i = 0; i < takeAway.Count; i++)
        {
            plan.Move(takeAway[i], Path.Combine(undone, $"{i:000}-{Path.GetFileName(takeAway[i])}"));
        }
        plan.CleanUpOnFailure(undone, host.DataDirectory);
        plan.Refuse(() => entries.FirstOrDefault(e => File.Exists(e.RestoreTo) && !takenAway.Contains(e.RestoreTo)) is HeldEntry blocked
            ? $"{blocked.RestoreTo} is already there, so {set} cannot be put back over it. Move it away, or discard what is held."
            : null);
        foreach (var entry in entries)
        {
            plan.Move(entry.HeldPath, entry.RestoreTo);
        }
        plan.Delete(Path.Combine(record.Folder, HeldArea.ManifestFile));
        if (takeAway.Count > 0)
        {
            plan.DeleteAfter(undone);
            foreach (string from in takeAway.Select(f => Path.GetDirectoryName(f)!).Distinct(comparer))
            {
                plan.CleanUp(from, StopAt(state, from));
            }
        }
        plan.CleanUp(Path.Combine(record.Folder, "files"), host.DataDirectory);
        return plan;
    }

    /// <summary>Deletes a held set for good, recorded or left over from a hold that did not finish.</summary>
    public ChangePlan Discard(InstallState state, string set)
    {
        Require(state);
        var plan = new ChangePlan(host, log);
        var record = state.Held.FirstOrDefault(h => h.Name == set);
        if (record == null)
        {
            plan.Refuse(() => $"Nothing is held as {set} for this copy of the game.");
            return plan;
        }
        foreach (string file in Directory.Exists(record.Folder) ? Directory.EnumerateFiles(record.Folder, "*", SearchOption.AllDirectories) : [])
        {
            plan.Delete(file);
        }
        plan.CleanUp(Path.Combine(record.Folder, "files"), host.DataDirectory);
        return plan;
    }

    /// <summary>A plan with nothing in it yet, for other planners to fill.</summary>
    internal ChangePlan NewPlan() => new(host, log);

    private ChangePlan Hold(InstallState state, string set, IReadOnlyList<(string Original, string RestoreTo)> files, string nothing)
    {
        var plan = new ChangePlan(host, log);
        if (files.Count == 0)
        {
            plan.Refuse(() => nothing);
            return plan;
        }
        AddHold(plan, state, set, files);
        return plan;
    }

    /// <summary>
    /// Adds holding <paramref name="files"/> as <paramref name="set"/> to <paramref name="plan"/>: the moves, then the
    /// manifest, which lists <paramref name="placed"/> when an install makes the set (see <see cref="HeldSet.Placed"/>).
    /// </summary>
    internal void AddHold(ChangePlan plan, InstallState state, string set, IReadOnlyList<(string Original, string RestoreTo)> files, IReadOnlyList<string>? placed = null)
    {
        string? folder = held.SetFolder(state.Game, set);
        if (folder == null)
        {
            plan.Refuse(() => "The buddy has no folder of its own in the profile to hold files in.");
            return;
        }
        plan.Refuse(() => Directory.Exists(folder)
            ? $"{set} is already held for this copy of the game; restore or discard it first."
            : null);
        var entries = new List<(string Original, string RestoreTo, string Held)>();
        for (int i = 0; i < files.Count; i++)
        {
            string relative = Path.Combine("files", $"{i:000}-{Path.GetFileName(files[i].Original)}");
            entries.Add((files[i].Original, files[i].RestoreTo, relative));
            plan.Move(files[i].Original, Path.Combine(folder, relative));
        }
        plan.CleanUpOnFailure(Path.Combine(folder, "files"), host.DataDirectory);
        // Written last: a set folder without its manifest is a hold that never finished, and its originals are untouched.
        plan.Write(Path.Combine(folder, HeldArea.ManifestFile), HeldArea.Manifest(state.Game, set, DateTimeOffset.Now, entries, placed));
        foreach (string from in files.Select(f => Path.GetDirectoryName(f.Original)!).Distinct())
        {
            plan.CleanUp(from, StopAt(state, from));
        }
    }

    // Emptied folders are removed up to, not including: the saved paks folder, plugins/ (other mods use it), the Win64
    // folder, the game's project folder, or the game's install folder; anything elsewhere is left as it is.
    private static string StopAt(InstallState state, string folder)
    {
        string win64 = state.Game.Win64Directory;
        string plugins = Path.Combine(win64, "plugins");
        if (state.SavedPaksDirectory != null && IsAtOrUnder(folder, state.SavedPaksDirectory))
        {
            return state.SavedPaksDirectory;
        }
        if (IsAtOrUnder(folder, plugins))
        {
            return plugins;
        }
        if (IsAtOrUnder(folder, win64))
        {
            return win64;
        }
        string game = Path.GetFullPath(Path.Combine(win64, "..", ".."));
        if (IsAtOrUnder(folder, game))
        {
            return game;
        }
        string installRoot = Path.GetFullPath(Path.Combine(game, ".."));
        return IsAtOrUnder(folder, installRoot) ? installRoot : folder;
    }

    private static bool IsAtOrUnder(string path, string folder)
    {
        string root = folder.TrimEnd(Path.DirectorySeparatorChar);
        return path.TrimEnd(Path.DirectorySeparatorChar) == root || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void Require(InstallState state)
    {
        if (state == null)
        {
            throw new ArgumentNullException(nameof(state));
        }
    }
}
