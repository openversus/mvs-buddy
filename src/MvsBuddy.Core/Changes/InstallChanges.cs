using Microsoft.Extensions.Logging;
using MvsBuddy.Catalog;
using MvsBuddy.Installs;

namespace MvsBuddy.Changes;

/// <summary>
/// Plans that change an install: holding mods aside (disabling OpenVersus, setting MVS Infinite aside) and restoring
/// them. Each returns a <see cref="ChangePlan"/>; nothing happens until it is applied.
/// </summary>
public sealed class InstallChanges
{
    /// <summary>The held set that is OpenVersus of no known channel (and every one held before channels were told apart).</summary>
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

    /// <summary>The held set that is OpenVersus of <paramref name="channel"/>: one each, so both can be kept.</summary>
    public static string OpenVersusSetFor(Channel channel) => $"{OpenVersusSet} {channel}";

    /// <summary>Whether <paramref name="set"/> is a held OpenVersus: of either channel, or of none.</summary>
    public static bool IsOpenVersusSet(string set) =>
        set == OpenVersusSet || set == OpenVersusSetFor(Channel.Prod) || set == OpenVersusSetFor(Channel.Testing);

    // The set OpenVersus is held as: named for the installed mod's channel.
    private static string OpenVersusSetOf(InstallState state) => state.Channel is { } c ? OpenVersusSetFor(c.Channel) : OpenVersusSet;

    /// <summary>
    /// Disables OpenVersus: holds every file of its installed layout, any older layout's files, and its paks, both in the
    /// profile and any found in the game's own folders, as the set for its channel (<see cref="OpenVersusSetFor"/>).
    /// Ultimate ASI Loader and its license go too when an install by the buddy put them there (its undo set lists them as
    /// placed) and no other ASI plugin is loaded through it; a loader put there some other way stays, since something else
    /// may use it. Restoring puts the files back, except the paks from the game's folders, which go to the profile where
    /// the mod keeps them (back where they were, when the profile's paks folder is unknown).
    /// </summary>
    public ChangePlan HoldOpenVersus(InstallState state)
    {
        Require(state);
        var files = OpenVersusFiles(state);
        if (state.OtherPlugins.Count == 0)
        {
            files.AddRange(LoaderPlacedByBuddy(state).Select(f => (f, f)));
        }
        string set = OpenVersusSetOf(state);
        return Hold(state, set, files, $"There is nothing of {set} here to hold.");
    }

    // Every file of OpenVersus, without the loader, and where each goes back to.
    private static List<(string Original, string RestoreTo)> OpenVersusFiles(InstallState state)
    {
        var files = state.Mod.Files.Concat(state.Mod.OldLayoutFiles).Select(f => (f, f))
            .Concat(state.Paks.Select(p => (p.Path, p.Path)));
        // Where the profile's paks folder is unknown, they still go: a game without the mod should not mount them.
        return files.Concat(state.PaksInGame.Select(p => (p.Path, state.SavedPaksDirectory == null ? p.Path : Path.Combine(state.SavedPaksDirectory, p.Name))))
            .ToList();
    }

    // Ultimate ASI Loader and its license, where they are now, when an install by the buddy placed them there.
    private IEnumerable<string> LoaderPlacedByBuddy(InstallState state)
    {
        var placed = new HashSet<string>(state.Held.Where(h => h.IsRecorded).SelectMany(h => h.Placed),
            host.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        return state.Loaders.Where(l => l.IsUltimateAsiLoader).Select(l => l.Path)
            .Append(Path.Combine(state.Game.Win64Directory, Installer.LicenseName))
            .Where(f => placed.Contains(f) && File.Exists(f));
    }

    /// <summary>
    /// Sets MVS Infinite aside: its plugins, and <paramref name="paks"/> from the game's <c>Content/Paks</c> (by default the
    /// ones whose names say MVS Infinite; pass the person's own choice to overrule that).
    /// </summary>
    public ChangePlan HoldMvsInfinite(InstallState state, IEnumerable<OtherPak>? paks = null)
    {
        Require(state);
        return Hold(state, MvsInfiniteSet, MvsInfiniteFiles(state, paks), $"There is nothing of {MvsInfiniteSet} here to hold.");
    }

    private static List<(string Original, string RestoreTo)> MvsInfiniteFiles(InstallState state, IEnumerable<OtherPak>? paks = null) =>
        state.OtherPlugins.Where(p => p.IsMvsInfinite).Select(p => p.Path)
            .Concat((paks ?? state.OtherPaks.Where(p => p.IsMvsInfinite)).Select(p => p.Path))
            .Select(f => (f, f)).ToList();

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
    /// A clean start: sets <paramref name="extras"/> aside (from <see cref="StockCheck"/>, all of them or the person's pick),
    /// one set for each end state, so each can be put back on its own. OpenVersus goes whole (its paks in the profile too)
    /// as the set for its channel, which "turn on" puts back; MVS Infinite whole as <see cref="MvsInfiniteSet"/>; the rest
    /// as one set named for the time. Ultimate ASI Loader (<see cref="ExtraKind.Loader"/> among the extras) goes only when
    /// no ASI plugin stays to be loaded by it, and then with OpenVersus, else MVS Infinite, else the rest. All or nothing.
    /// </summary>
    public CleanStartPlan CleanStart(InstallState state, IEnumerable<StockExtra> extras)
    {
        Require(state);
        var picked = (extras ?? throw new ArgumentNullException(nameof(extras))).ToList();
        var comparer = host.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var openVersus = picked.Any(e => e.Kind == ExtraKind.OpenVersus) ? OpenVersusFiles(state) : [];
        var mvsInfinite = picked.Any(e => e.Kind == ExtraKind.MvsInfinite) ? MvsInfiniteFiles(state) : [];
        var others = picked.Where(e => e.Kind == ExtraKind.Other).Select(e => (e.Path, e.Path)).ToList();
        // The picked extras of a kind the buddy holds whole are in its whole list; any that are not still go with it.
        AddMissing(openVersus, picked.Where(e => e.Kind == ExtraKind.OpenVersus), comparer);
        AddMissing(mvsInfinite, picked.Where(e => e.Kind == ExtraKind.MvsInfinite), comparer);

        var going = new HashSet<string>(openVersus.Concat(mvsInfinite).Concat(others).Select(f => f.Item1), comparer);
        var staying = state.Mod.Plugins.Concat(state.Mod.OldLayoutFiles.Where(f => f.EndsWith(".asi", StringComparison.OrdinalIgnoreCase)))
            .Concat(state.OtherPlugins.Select(p => p.Path)).Where(p => !going.Contains(p)).Select(Path.GetFileName).Distinct().ToList();
        var loader = picked.Where(e => e.Kind == ExtraKind.Loader).Select(e => (e.Path, e.Path)).ToList();
        string? loaderStays = null;
        if (openVersus.Count + mvsInfinite.Count + others.Count == 0)
        {
            loader.Clear();
        }
        else if (loader.Count > 0 && staying.Count > 0)
        {
            loaderStays = $"Ultimate ASI Loader stays: {string.Join(", ", staying)} still loads through it.";
            loader.Clear();
        }
        (openVersus.Count > 0 ? openVersus : mvsInfinite.Count > 0 ? mvsInfinite : others).AddRange(loader);

        var plan = new ChangePlan(host, log);
        var summary = new List<string>();
        string otherSet = $"Not stock {DateTimeOffset.Now:yyyy-MM-dd HHmmss}";
        foreach (var (set, files, what) in new[]
        {
            (OpenVersusSetOf(state), openVersus, "OpenVersus"),
            (MvsInfiniteSet, mvsInfinite, "MVS Infinite"),
            (otherSet, others, "Everything else"),
        })
        {
            if (files.Count == 0)
            {
                continue;
            }
            AddHold(plan, state, set, files);
            string withLoader = loader.Count > 0 && files.Contains(loader[0]) ? ", Ultimate ASI Loader among them, since nothing left needs it" : "";
            summary.Add($"{what}: {files.Count} {(files.Count == 1 ? "file" : "files")} set aside as \"{set}\"{withLoader}.");
        }
        if (loaderStays != null)
        {
            summary.Add(loaderStays);
        }
        if (openVersus.Count + mvsInfinite.Count + others.Count == 0)
        {
            plan.Refuse(() => "Nothing was picked to set aside.");
        }
        log.LogInformation("Clean start planned: {Summary}", string.Join(" | ", summary));
        return new CleanStartPlan(plan, summary);
    }

    private static void AddMissing(List<(string Original, string RestoreTo)> files, IEnumerable<StockExtra> extras, StringComparer comparer)
    {
        var have = new HashSet<string>(files.Select(f => f.Original), comparer);
        files.AddRange(extras.Where(e => have.Add(e.Path)).Select(e => (e.Path, e.Path)));
    }

    /// <summary>
    /// Writes down that the installed OpenVersus is <paramref name="channel"/>, as the person says: its record (see
    /// <see cref="InstallRecord"/>) is kept, or made, with that channel. The current layout only, which has a folder for it.
    /// </summary>
    public ChangePlan SetChannel(InstallState state, Channel channel)
    {
        Require(state);
        var plan = new ChangePlan(host, log);
        if (state.Mod.Layout != ModLayout.Current)
        {
            plan.Refuse(() => "Only the current OpenVersus layout keeps a record of its channel.");
            return plan;
        }
        string path = Path.Combine(state.Game.Win64Directory, "plugins", InstallReader.ModFolder, InstallRecord.FileName);
        var record = state.Record?.Copy() ?? new InstallRecord { Version = state.Mod.Version is { } v ? ModVersion.Text(v) : null };
        record.Channel = channel.ToString();
        record.ChannelFrom = "person";
        record.At = DateTimeOffset.Now;
        record.Buddy = BuddyVersion.Current;
        if (File.Exists(path))
        {
            plan.Delete(path);
        }
        plan.Write(path, record.ToJson());
        return plan;
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
        // A plugin the mod updated itself to goes too, when the install put a plugin in place; one that found its plugin
        // already there (a reinstall) left it as it was, and the mod's own update of it since stays, like its cache.
        bool placedPlugin = record.Placed.Any(p => p.EndsWith(".asi", StringComparison.OrdinalIgnoreCase));
        var takeAway = record.Placed.Count == 0 ? []
            : record.Placed.Concat(placedPlugin ? state.Mod.Plugins.Where(p => !restoreTo.Contains(p)) : []).Where(File.Exists).Distinct(comparer).ToList();
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

    /// <summary>
    /// Deletes a held set for good, recorded or left over from a hold that did not finish. Its manifest goes first, as the
    /// plan's one step; the folder is deleted once that is done, without keeping its files to undo with, since a set can
    /// hold gigabytes.
    /// </summary>
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
        string manifest = Path.Combine(record.Folder, HeldArea.ManifestFile);
        if (File.Exists(manifest))
        {
            plan.Delete(manifest);
        }
        plan.DeleteAfter(record.Folder);
        plan.CleanUp(Path.GetDirectoryName(record.Folder)!, host.DataDirectory);
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

/// <summary>A clean start ready to apply, and what it does in plain sentences.</summary>
/// <param name="Plan">The changes; nothing happens until it is applied.</param>
/// <param name="Summary">Which set each part goes to, and whether the loader goes or stays.</param>
public sealed record CleanStartPlan(ChangePlan Plan, IReadOnlyList<string> Summary);
