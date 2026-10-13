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
    /// Puts a held set back. Refused, with nothing touched, when a file is already where one of the set's would go (the
    /// mod was installed again since it was held, say).
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
        plan.Refuse(() => entries.FirstOrDefault(e => File.Exists(e.RestoreTo)) is HeldEntry blocked
            ? $"{blocked.RestoreTo} is already there, so {set} cannot be put back over it. Move it away, or discard what is held."
            : null);
        foreach (var entry in entries)
        {
            plan.Move(entry.HeldPath, entry.RestoreTo);
        }
        plan.Delete(Path.Combine(record.Folder, HeldArea.ManifestFile));
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

    private ChangePlan Hold(InstallState state, string set, IReadOnlyList<(string Original, string RestoreTo)> files, string nothing)
    {
        var plan = new ChangePlan(host, log);
        string? folder = held.SetFolder(state.Game, set);
        if (folder == null)
        {
            plan.Refuse(() => "The buddy has no folder of its own in the profile to hold files in.");
            return plan;
        }
        if (files.Count == 0)
        {
            plan.Refuse(() => nothing);
            return plan;
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
        plan.Write(Path.Combine(folder, HeldArea.ManifestFile), HeldArea.Manifest(state.Game, set, DateTimeOffset.Now, entries));
        foreach (string from in files.Select(f => Path.GetDirectoryName(f.Original)!).Distinct())
        {
            plan.CleanUp(from, StopAt(state, from));
        }
        return plan;
    }

    // Emptied folders are removed up to, not including: the saved paks folder, plugins/ (other mods use it), the Win64
    // folder, or the game's folder.
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
        return IsAtOrUnder(folder, win64) ? win64 : Path.GetFullPath(Path.Combine(win64, "..", ".."));
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
