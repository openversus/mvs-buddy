using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using MvsBuddy.Catalog;
using MvsBuddy.Games;
using MvsBuddy.Installs;

namespace MvsBuddy.Changes;

/// <summary>A change to a copy of the game, ready to apply: what it needs is downloaded and checked, and nothing in the game has changed.</summary>
public sealed class PreparedChange
{
    private readonly Action apply;

    internal PreparedChange(IReadOnlyList<string> summary, SettingsMergeResult? settings, string? refused, Action apply)
    {
        Summary = summary;
        Settings = settings;
        Refused = refused;
        this.apply = apply;
    }

    /// <summary>What the change will do, in plain sentences, for the person to confirm.</summary>
    public IReadOnlyList<string> Summary { get; }

    /// <summary>How the settings were merged, or null when no settings file is written.</summary>
    public SettingsMergeResult? Settings { get; }

    /// <summary>Why the change cannot be made as it is, naming what stands in its way; null when it can.</summary>
    public string? Refused { get; }

    /// <summary>
    /// Makes the change: the game's files, then the paks. Refused, with nothing in the game touched, while the game is
    /// running, when <see cref="Refused"/> says so, when a file that is no state's stands where the change writes one, or
    /// when this account may not change a folder it writes (<see cref="ChangeException.DeniedFolders"/>). If the paks
    /// cannot be moved, the game's files are put back as they were.
    /// </summary>
    /// <exception cref="ChangeException">Refused, or failed and put back; the message says which.</exception>
    public void Apply() => apply();
}

/// <summary>
/// The states of each copy of the game, as branches of a bare git repository in the buddy's folder whose working tree is
/// the game's install folder (<c>states/&lt;copy&gt;.git</c>): <c>current</c>, what is there now; <c>previous</c>, what was
/// there before the last change, which undo puts back; and per channel (<c>prod</c>, <c>testing</c>) the last state that
/// channel was live in, where its own <c>OVSState.toml</c> and <c>PatternsCache.toml</c> come back from. An install makes
/// the files a state tracks match the release, so nothing of the version before stays behind. Release files are read
/// from the cache (<see cref="ReleaseCache"/>), never stored twice. The paks are never in git: the live ones are in the
/// profile, the others in the buddy's pak sets (<see cref="PakStore"/>), and a change moves them in the same go.
/// </summary>
public sealed class GameStates
{
    internal const string StatesFolder = "states";
    internal const string CurrentBranch = "current";
    internal const string PreviousBranch = "previous";

    /// <summary>The pak set for paks of no known channel: there with no mod to say which.</summary>
    internal const string OtherPaks = "other";

    private const string Home = "plugins/OpenVersus/";
    private const string StateFile = "OVSState.toml";
    private const string PatternsFile = "PatternsCache.toml";
    private const string SampleFile = "sample_config.toml";
    private const string PakLine = "pak";

    private readonly HostSystem host;
    private readonly ReleaseCatalog catalog;
    private readonly ReleaseCache cache;
    private readonly PakStore paks;
    private readonly InstallReader installs;
    private readonly AsiLoaderPin pin;
    private readonly ILogger<GameStates> log;

    internal GameStates(HostSystem host, ReleaseCatalog catalog, ReleaseCache cache, PakStore paks, InstallReader installs, AsiLoaderPin pin, ILogger<GameStates> log)
    {
        this.host = host;
        this.catalog = catalog;
        this.cache = cache;
        this.paks = paks;
        this.installs = installs;
        this.pin = pin;
        this.log = log;
    }

    /// <summary>Where a copy's states are, and how its paths read in them.</summary>
    /// <param name="GitDir">The repository.</param>
    /// <param name="WorkTree">Its working tree: the install folder of a verified copy; for a folder the person chose without it being recognised, that folder.</param>
    /// <param name="Prefix">The Win64 folder relative to <paramref name="WorkTree"/>, '/'-separated and ending in '/'; empty when they are one.</param>
    internal sealed record CopyLayout(string GitDir, string WorkTree, string Prefix);

    /// <summary>A pak live now: in the profile, or put in the game's own folders by hand.</summary>
    internal sealed record LivePak(string Name, long Size, string Sha256, string Path);

    /// <summary>The key that names one copy of the game in the buddy's folder: its Win64 path, hashed.</summary>
    internal static string CopyKey(string win64, bool windows)
    {
        string path = Path.GetFullPath(win64).TrimEnd(Path.DirectorySeparatorChar);
        if (windows)
        {
            path = path.ToLowerInvariant();
        }
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(path)).Take(8).Select(b => b.ToString("x2")));
    }

    /// <summary>Where <paramref name="game"/>'s states are kept, and how its paths read there.</summary>
    internal CopyLayout Layout(GameInstall game)
    {
        if (string.IsNullOrEmpty(host.DataDirectory))
        {
            throw new ChangeException("The buddy has no folder of its own to keep states in.");
        }
        string win64 = Path.GetFullPath(game.Win64Directory).TrimEnd(Path.DirectorySeparatorChar);
        string workTree = game.Verified ? Path.GetFullPath(Path.Combine(win64, "..", "..", "..")) : win64;
        string prefix = game.Verified ? win64.Substring(workTree.TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace(Path.DirectorySeparatorChar, '/') + "/" : "";
        // One copy, one key, whichever link it was reached through.
        string key = CopyKey(host.RealPath(win64), host.IsWindows);
        return new CopyLayout(Path.Combine(host.DataDirectory, StatesFolder, key + ".git"), workTree, prefix);
    }

    /// <summary>The copy's store, made when there is none, with HEAD on <c>current</c>.</summary>
    internal GitStore Open(CopyLayout copy)
    {
        var store = GitStore.Open(copy.GitDir, copy.WorkTree, ["/" + copy.Prefix + Home + "logs/"], log, cache.Store());
        if (store.Head() != CurrentBranch)
        {
            store.SetHead(CurrentBranch);
        }
        return store;
    }

    /// <summary>Whether there is a change to undo for <paramref name="game"/>.</summary>
    public bool CanUndo(GameInstall game)
    {
        var copy = Layout(game ?? throw new ArgumentNullException(nameof(game)));
        return LibGit2Sharp.Repository.IsValid(copy.GitDir) && Open(copy).Tip(PreviousBranch) != null;
    }

    /// <summary>
    /// Gets <paramref name="release"/> ready to install: its files into the cache, its paks checked or downloaded, the
    /// person's settings merged onto the release's. Installing it, or switching channels to it, makes the copy that release:
    /// its files, the loader, the merged settings, and the channel's own saved state from when it was last live here
    /// (never the other channel's). What is there now is kept as the state undo puts back, and, for an installed channel,
    /// as that channel's state, for switching back.
    /// </summary>
    /// <exception cref="DownloadException">Something could not be downloaded or did not match its checksum.</exception>
    /// <exception cref="ReleaseCatalogException">GitHub could not be asked for the paks.</exception>
    /// <exception cref="ChangeException">The release has no install zip.</exception>
    public async Task<PreparedChange> PrepareInstallAsync(InstallState state, Release release, InstallOptions? options = null,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (state == null || release == null)
        {
            throw new ArgumentNullException(state == null ? nameof(state) : nameof(release));
        }
        options ??= new InstallOptions();
        var copy = Layout(state.Game);
        var cached = await cache.GetAsync(release, progress, cancellationToken).ConfigureAwait(false);
        var cacheStore = cache.Store();
        var staged = new List<StagedPak>();
        if (state.SavedPaksDirectory != null)
        {
            var pakAssets = await catalog.PaksForAsync(release, cancellationToken).ConfigureAwait(false);
            staged.AddRange(await paks.StageAsync(pakAssets, PakSources(state), progress, cancellationToken).ConfigureAwait(false));
        }

        string version = release.Version == null ? release.Tag : ModVersion.Text(release.Version);
        string from = state.Mod.Version != null ? ModVersion.Text(state.Mod.Version) : state.Mod.Layout == ModLayout.None ? "nothing" : $"an older layout ({state.Mod.Layout})";
        var summary = new List<string> { $"Installs OpenVersus {version} ({release.Channel}) over {from}." };
        if (!Directory.Exists(state.Game.Win64Directory))
        {
            summary.Add($"{state.Game.Win64Directory} does not exist yet; it will be made.");
        }
        summary.AddRange(ChannelLines(state, copy, release.Channel));

        // Settings: written when the release ships its own or the person has some.
        string theirsPath = Path.Combine(state.Game.Win64Directory, "plugins", InstallReader.ModFolder, InstallReader.SettingsFile);
        string? theirs = File.Exists(theirsPath) ? File.ReadAllText(theirsPath) : null;
        string? releaseSettings = Text(cacheStore, cached.Tree, Home + InstallReader.SettingsFile)
            ?? (theirs != null ? Text(cacheStore, cached.Tree, Home + SampleFile) : null);
        SettingsMergeResult? merge = null;
        if (releaseSettings != null)
        {
            merge = SettingsMerge.Merge(await InstalledDefaultsAsync(state, cacheStore, cancellationToken).ConfigureAwait(false), theirs, releaseSettings);
            summary.Add(merge.Carried.Count == 0
                ? "Settings: the release's."
                : $"Settings: the release's, keeping yours for {string.Join(", ", merge.Carried)}.");
            if (merge.Dropped.Count > 0)
            {
                summary.Add($"Settings this version no longer has, dropped: {string.Join(", ", merge.Dropped)}.");
            }
            if (merge.Note != null)
            {
                summary.Add(merge.Note);
            }
        }

        // The loader, under the name the platform needs; a file of another program's under that name is not replaced.
        string loaderName = options.LoaderName ?? (host.IsWindows ? "xinput1_3.dll" : "version.dll");
        string? refused = null;
        var atName = state.Loaders.FirstOrDefault(l => string.Equals(l.FileName, loaderName, StringComparison.OrdinalIgnoreCase));
        if (atName != null && !atName.IsUltimateAsiLoader)
        {
            string other = loaderName == "version.dll" ? "xinput1_3.dll" : "version.dll";
            refused = $"{atName.Path} is another program's file, not Ultimate ASI Loader, so the loader cannot take its name. Install with the loader named {other} instead.";
        }
        var otherLoaders = state.Loaders.Where(l => l.IsUltimateAsiLoader && l != atName).ToList();
        summary.Add(atName is { IsPinnedBuild: true }
            ? $"Ultimate ASI Loader {pin.Version} is already in place as {loaderName}."
            : $"Ultimate ASI Loader {pin.Version} as {loaderName}"
                + (otherLoaders.Count > 0 ? $"; the other copy ({string.Join(", ", otherLoaders.Select(l => l.FileName))}) goes, as one is enough." : "."));
        summary.AddRange(PakLines(state, staged));
        var notChecked = (cached.ZipChecked ? Array.Empty<string>() : new[] { release.InstallZip!.Name }).Concat(staged.Where(p => !p.Checked).Select(p => p.Name)).ToList();
        if (notChecked.Count > 0)
        {
            summary.Add($"Not checked, since neither GitHub nor the release gives a checksum for them: {string.Join(", ", notChecked)}.");
        }
        summary.Add(state.PaksInGame.Count == 0
            ? "Undo puts the mod, the loader and the paks back as they are now."
            : "Undo puts the mod, the loader and the paks back as they are now, except that the paks found in the game's own folders go where the mod keeps them.");

        string? asi = cacheStore.Paths(cached.Tree).FirstOrDefault(p => p.StartsWith(Home, StringComparison.Ordinal)
            && p.IndexOf('/', Home.Length) < 0 && p.EndsWith(".asi", StringComparison.OrdinalIgnoreCase));
        var record = new InstallRecord
        {
            Channel = release.Channel.ToString(),
            Tag = release.Tag,
            Version = release.Version == null ? null : ModVersion.Text(release.Version),
            At = DateTimeOffset.Now,
            Buddy = BuddyVersion.Current,
            Zip = release.InstallZip is { } zip
                ? new RecordedFile { Name = zip.Name, Url = zip.DownloadUrl.ToString(), Sha256 = ReleaseCache.Info(cacheStore, cached.Tree)?.Zip?.Sha256 ?? zip.Sha256 }
                : null,
            Plugin = asi == null ? null : new RecordedFile { Name = asi.Substring(Home.Length), Sha256 = Sha256(cacheStore.Read(cached.Tree, asi)!) },
            Paks = staged.Select(p => new RecordedFile { Name = p.Name, Url = p.Url, Sha256 = p.Sha256 }).ToList(),
            Loader = new RecordedFile { Name = loaderName, Version = pin.Version, Sha256 = pin.DllSha256 },
        };
        byte[]? settingsBytes = merge == null ? null : Encoding.UTF8.GetBytes(merge.Text);
        byte[] recordBytes = record.ToJson();
        log.LogInformation("Prepared {Release} for {Folder}: {Summary}", release.Tag, copy.WorkTree, string.Join(" | ", summary));

        return new PreparedChange(summary, merge, refused, () => Change(state.Game, $"before OpenVersus {version} ({release.Channel}) was installed",
            $"OpenVersus {version} ({release.Channel})", refused, staged,
            (store, now, before) => store.Edit(before, InstallEdits(store, copy, now, before, cached.Tree, release.Channel, loaderName, settingsBytes, recordBytes))));
    }

    /// <summary>
    /// Gets undoing the last change ready: the copy as it was before it, files and paks. Undoing again brings back what is
    /// there now. The paks come from the buddy's sets or the profile, by checksum; one that is in neither is a reason the
    /// undo cannot be made, not something to fetch anew.
    /// </summary>
    public PreparedChange PrepareUndo(InstallState state, IProgress<string>? progress = null)
    {
        if (state == null)
        {
            throw new ArgumentNullException(nameof(state));
        }
        var copy = Layout(state.Game);
        string? previous = LibGit2Sharp.Repository.IsValid(copy.GitDir) ? Open(copy).Tip(PreviousBranch) : null;
        if (previous == null)
        {
            const string nothing = "There is nothing to undo for this copy of the game yet.";
            return new PreparedChange([nothing], null, nothing, () => throw new ChangeException(nothing));
        }
        var store = Open(copy);
        var (message, at) = store.Describe(previous);
        string what = message.Split('\n')[0];
        var wanted = PaksIn(message);
        IReadOnlyList<StagedPak> found = [];
        IReadOnlyList<PakEntry> missing = [];
        if (state.SavedPaksDirectory != null)
        {
            found = paks.AtHand(wanted, PakSources(state), out missing, progress);
        }
        string? refused = missing.Count == 0 ? null
            : $"The paks that went with it are no longer kept ({string.Join(", ", missing.Select(p => p.Name))}), so it cannot be put back as it was.";
        var summary = new List<string> { $"Puts this copy back as it was {what}, on {at.LocalDateTime:yyyy-MM-dd HH:mm}." };
        summary.AddRange(PakLines(state, found));
        summary.Add("Undoing again brings back what is here now.");
        string tree = store.TreeOf(previous);
        return new PreparedChange(summary, null, refused,
            () => Change(state.Game, "before an undo", $"Undo: {what}", refused, found, (_, _, _) => tree));
    }

    // The one way every change is made. Nothing in the game changes before the snapshot, the conflicts and the
    // permissions are all checked; `previous` moves only once the files and the paks are both in place.
    private void Change(GameInstall game, string beforeWhat, string title, string? refused, IReadOnlyList<StagedPak> targetPaks,
        Func<GitStore, InstallState, string, string> target)
    {
        if (host.IsGameRunning())
        {
            throw new ChangeException("MultiVersus is running. Close it, then try again.");
        }
        if (refused != null)
        {
            throw new ChangeException(refused);
        }
        var copy = Layout(game);
        if (!Directory.Exists(copy.WorkTree))
        {
            var make = new ChangePlan(host, log);
            make.Writes(copy.WorkTree);
            make.Check();
            Directory.CreateDirectory(copy.WorkTree);
        }
        var now = installs.Read(game);
        var live = LivePaks(now);
        var store = Open(copy);
        string before = store.Snapshot(Roots(now, copy), Message(beforeWhat, live));
        string beforeTree = store.TreeOf(before);
        if (now.Channel is { } channel)
        {
            store.SetBranch(ReleaseCache.BranchOf(channel.Channel), before);
        }
        string targetTree = target(store, now, beforeTree);

        var diff = store.Diff(beforeTree, targetTree);
        var inTheWay = diff.Where(d => d.Change == PathChange.Added).Select(d => Full(copy, d.Path)).Where(File.Exists).ToList();
        if (inTheWay.Count > 0)
        {
            throw new ChangeException(inTheWay.Count == 1
                ? $"{inTheWay[0]} is already there and is not part of anything the buddy keeps, so it is not replaced. Move it away, then try again."
                : $"These are already there and are not part of anything the buddy keeps, so they are not replaced: {string.Join(", ", inTheWay)}. Move them away, then try again.");
        }
        var pakPlan = PakPlan(now, live, targetPaks);
        foreach (var (path, _) in diff)
        {
            pakPlan.Writes(Path.GetDirectoryName(Full(copy, path))!);
        }
        pakPlan.Check();

        store.Switch(targetTree, title);
        try
        {
            pakPlan.Apply();
        }
        catch (ChangeException e)
        {
            try
            {
                store.Switch(beforeTree, "Put back: " + title);
            }
            catch (ChangeException back)
            {
                throw new ChangeException($"{e.Message} The game's files could not all be put back either: {back.Message}", e);
            }
            throw;
        }
        store.SetBranch(PreviousBranch, before);
        store.Prune();
        log.LogInformation("{Title}: done for {Folder}", title, copy.WorkTree);
    }

    // An install's edits to the state before it. Only what OpenVersus owns is taken out: an older layout's files and every
    // copy of Ultimate ASI Loader (the grafts replace the mod's folder and the license whole); anything else the state
    // tracks stays, since a clean start's undo or put-back leaves other files tracked. Then the release's mod folder and
    // loader as the cache has them (modes kept), the merged settings, the target channel's own saved state, the record,
    // and an old layout's settings and state where the mod takes them over from.
    private List<TreeEdit> InstallEdits(GitStore store, CopyLayout copy, InstallState now, string before, string releaseTree, Channel channel,
        string loaderName, byte[]? settings, byte[] record)
    {
        string home = copy.Prefix + Home;
        var edits = new List<TreeEdit>();
        edits.AddRange(now.Mod.Files.Concat(now.Mod.OldLayoutFiles).Concat(now.Loaders.Where(l => l.IsUltimateAsiLoader).Select(l => l.Path))
            .Select(f => Relative(copy, f)).OfType<string>().Distinct(StringComparer.Ordinal).Select(f => new Removal(f)));
        edits.AddRange(new TreeEdit[]
        {
            new Graft(home.TrimEnd('/'), releaseTree, Home.TrimEnd('/')),
            new Graft(copy.Prefix + loaderName, releaseTree, ReleaseCache.LoaderFolder + "/" + pin.DllName),
            new Graft(copy.Prefix + Installer.LicenseName, releaseTree, ReleaseCache.LoaderFolder + "/" + Installer.LicenseName),
        });
        if (settings != null)
        {
            edits.Add(new Content(home + InstallReader.SettingsFile, settings));
        }
        // The channel's own, never the other's: OVSState.toml holds an install id that must not cross channels.
        if (store.Tip(ReleaseCache.BranchOf(channel)) is string last)
        {
            string lastTree = store.TreeOf(last);
            edits.AddRange(new[] { StateFile, PatternsFile }.Where(f => store.Has(lastTree, home + f)).Select(f => new Graft(home + f, lastTree, home + f)));
        }
        edits.Add(new Content(home + InstallRecord.FileName, record));
        edits.AddRange(Companions(now, copy).Where(c => store.Has(before, c.From)).Select(c => new Graft(c.To, before, c.From)));
        return edits;
    }

    // An older layout's files besides its plugin (settings, state, cache): the mod takes them over from plugins/, so they
    // go there (when plugins/ has no file of that name) and stay. Its plugins go, or two would load.
    private static IEnumerable<(string From, string To)> Companions(InstallState now, CopyLayout copy)
    {
        string win64 = now.Game.Win64Directory;
        string plugins = Path.Combine(win64, "plugins");
        string home = Path.Combine(plugins, InstallReader.ModFolder);
        foreach (string file in now.Mod.Files.Concat(now.Mod.OldLayoutFiles).Distinct())
        {
            if (file.EndsWith(".asi", StringComparison.OrdinalIgnoreCase) || file.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Relative(copy, file) is not string from)
            {
                continue;
            }
            string inPlugins = Path.Combine(plugins, Path.GetFileName(file));
            if (Path.GetDirectoryName(file) == plugins)
            {
                yield return (from, from);
            }
            else if (!File.Exists(inPlugins) && Relative(copy, inPlugins) is string to)
            {
                yield return (from, to);
            }
        }
    }

    // Every path a state can hold, so the only files in a change's way are ones no state has: the mod's folder, an older
    // layout's files, every copy of Ultimate ASI Loader (by content, under any name) and its license. Paths a state held
    // before are recorded too, by the store.
    internal static List<string> Roots(InstallState now, CopyLayout copy)
    {
        var roots = new List<string> { copy.Prefix + Home.TrimEnd('/'), copy.Prefix + Installer.LicenseName };
        roots.AddRange(now.Mod.Files.Concat(now.Mod.OldLayoutFiles).Concat(now.Loaders.Where(l => l.IsUltimateAsiLoader).Select(l => l.Path))
            .Select(f => Relative(copy, f)).OfType<string>());
        return roots.Distinct(StringComparer.Ordinal).ToList();
    }

    // The paks, as they will be: the target's in the profile (those already there and the same stay put), and the ones
    // live now that the target does not use kept as the set of the channel live now, replacing that set's older paks.
    // Incoming paks are set aside in the buddy's folder first, since one may have a leaving pak's name or come from the very
    // set the leaving ones replace.
    private ChangePlan PakPlan(InstallState now, IReadOnlyList<LivePak> live, IReadOnlyList<StagedPak> target)
    {
        var plan = new ChangePlan(host, log);
        if (now.SavedPaksDirectory == null)
        {
            return plan;
        }
        var comparer = host.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        string saved = now.SavedPaksDirectory;
        var inPlace = new HashSet<string>(target.Where(p => comparer.Equals(p.Path, Path.Combine(saved, p.Name))).Select(p => p.Path), comparer);
        var incoming = target.Where(p => !inPlace.Contains(p.Path)).ToList();
        var taken = new HashSet<string>(incoming.Select(p => p.Path), comparer);
        // A hand-placed pak the target uses comes in like any other; it does not also leave.
        var leaving = live.Where(p => !inPlace.Contains(p.Path) && !taken.Contains(p.Path)).ToList();
        if (incoming.Count + leaving.Count == 0)
        {
            return plan;
        }
        string run = Path.Combine(paks.Root, ".change-" + Guid.NewGuid().ToString("N"));
        string old = Path.Combine(run, "old");
        foreach (var pak in incoming)
        {
            plan.Move(pak.Path, Path.Combine(run, "in", pak.Name));
        }
        string set = now.Channel is { } channel ? ReleaseCache.BranchOf(channel.Channel) : OtherPaks;
        string setFolder = paks.Folder(set);
        // A set the incoming paks came from, other than the one being replaced, no longer holds them.
        foreach (var from in incoming.Select(p => Path.GetDirectoryName(p.Path)!).Distinct(comparer)
            .Where(f => comparer.Equals(Path.GetDirectoryName(f), paks.Root.TrimEnd(Path.DirectorySeparatorChar)) && (leaving.Count == 0 || !comparer.Equals(f, setFolder))))
        {
            string name = Path.GetFileName(from);
            string manifest = Path.Combine(from, PakStore.ManifestFile);
            var left = paks.Read(name).Where(e => !taken.Contains(Path.Combine(from, e.Name))).ToList();
            if (File.Exists(manifest))
            {
                plan.Delete(manifest);
            }
            if (left.Count > 0)
            {
                plan.Write(manifest, PakStore.Manifest(left));
            }
        }
        int setAside = 0;
        if (leaving.Count > 0)
        {
            var older = Directory.Exists(setFolder) ? Directory.EnumerateFiles(setFolder).Where(f => !taken.Contains(f)).ToList() : [];
            foreach (string file in older)
            {
                plan.Move(file, Path.Combine(old, $"{setAside++:000}-{Path.GetFileName(file)}"));
            }
            var urls = now.Record?.Paks.Where(p => p.Url != null).GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Url, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entries = new List<PakEntry>();
            foreach (var pak in leaving)
            {
                // The same name twice (in the profile and in the game's folder): the profile's is the one the game used.
                bool first = names.Add(pak.Name);
                plan.Move(pak.Path, first ? Path.Combine(setFolder, pak.Name) : Path.Combine(old, $"{setAside++:000}-{pak.Name}"));
                if (first)
                {
                    entries.Add(new PakEntry(pak.Name, pak.Size, pak.Sha256, urls.TryGetValue(pak.Name, out var url) ? url : null));
                }
                if (!comparer.Equals(Path.GetDirectoryName(pak.Path), saved))
                {
                    plan.CleanUp(Path.GetDirectoryName(pak.Path)!, StopAt(now, pak.Path));
                }
            }
            plan.Write(Path.Combine(setFolder, PakStore.ManifestFile), PakStore.Manifest(entries));
        }
        foreach (var pak in incoming)
        {
            plan.Move(Path.Combine(run, "in", pak.Name), Path.Combine(saved, pak.Name));
            if (pak.Downloaded)
            {
                plan.CleanUp(Path.GetDirectoryName(pak.Path)!, Path.Combine(host.DataDirectory, "downloads"));
            }
            else if (!comparer.Equals(Path.GetDirectoryName(pak.Path), saved) && !pak.Path.StartsWith(paks.Root, StringComparison.Ordinal))
            {
                plan.CleanUp(Path.GetDirectoryName(pak.Path)!, StopAt(now, pak.Path));
            }
        }
        if (setAside > 0)
        {
            plan.DeleteAfter(old);
        }
        plan.CleanUp(Path.Combine(run, "in"), paks.Root);
        plan.CleanUpOnFailure(Path.Combine(run, "in"), paks.Root);
        plan.CleanUpOnFailure(old, paks.Root);
        return plan;
    }

    // Emptied folders of the game's that hand-placed paks leave go up to, not including, Content (or Binaries).
    private static string StopAt(InstallState now, string pak)
    {
        string game = Path.GetFullPath(Path.Combine(now.Game.Win64Directory, "..", ".."));
        string content = Path.Combine(game, "Content");
        return pak.StartsWith(content + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? content : Path.Combine(game, "Binaries");
    }

    // Where a pak may already be: the profile first (so one there stays put), then the buddy's sets, then where hand-placed ones are.
    private List<string> PakSources(InstallState state)
    {
        var sources = new List<string>();
        if (state.SavedPaksDirectory != null)
        {
            sources.Add(state.SavedPaksDirectory);
        }
        sources.AddRange(new[] { ReleaseCache.BranchOf(Channel.Prod), ReleaseCache.BranchOf(Channel.Testing), OtherPaks }.Select(paks.Folder));
        sources.AddRange(state.PaksInGame.Select(p => Path.GetDirectoryName(p.Path)!).Distinct());
        return sources;
    }

    private static List<LivePak> LivePaks(InstallState now) =>
        now.Paks.Concat(now.PaksInGame).Select(p => new LivePak(p.Name, p.Size, Downloads.Sha256Of(p.Path), p.Path)).ToList();

    // A snapshot's message: what it is the state before, then the paks live with it, a line each, for an undo to find.
    private static string Message(string what, IEnumerable<LivePak> live) =>
        what + "\n\n" + string.Concat(live.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .Select(p => $"{PakLine}\t{p.Name}\t{p.Size}\t{p.Sha256}\n"));

    internal static IReadOnlyList<PakEntry> PaksIn(string message) =>
        message.Split('\n').Select(l => l.Split('\t')).Where(f => f.Length == 4 && f[0] == PakLine && long.TryParse(f[2], out _))
            .Select(f => new PakEntry(f[1], long.Parse(f[2]), f[3], null)).ToList();

    private IEnumerable<string> ChannelLines(InstallState state, CopyLayout copy, Channel target)
    {
        string? kept = null;
        if (LibGit2Sharp.Repository.IsValid(copy.GitDir))
        {
            var store = Open(copy);
            if (store.Tip(ReleaseCache.BranchOf(target)) is string last)
            {
                kept = store.Has(store.TreeOf(last), copy.Prefix + Home + StateFile) ? last : null;
            }
        }
        if (state.Channel is not { } live)
        {
            if (kept != null)
            {
                yield return $"{target} gets back its saved state from when it was last here.";
            }
            yield break;
        }
        string how = live.Source switch
        {
            ChannelSource.Install => "the buddy installed it",
            ChannelSource.Person => "as you said",
            _ => "a guess from its server address; if it is the other one, say so before going on, or its saved state is filed under the wrong channel",
        };
        yield return $"The OpenVersus here is {live.Channel} ({how}).";
        if (live.Channel == target)
        {
            yield return "Its saved state carries over.";
        }
        else
        {
            yield return $"Its saved state stays with {live.Channel}, for switching back; {target} "
                + (kept != null ? "gets back its own from when it was last here." : "starts with a new one.");
        }
    }

    private IEnumerable<string> PakLines(InstallState state, IReadOnlyList<StagedPak> target)
    {
        if (state.SavedPaksDirectory == null)
        {
            yield return state.NeedsFirstLaunch
                ? "The paks are not installed: " + InstallReader.FirstLaunchNote
                : "The paks are not installed: the buddy cannot tell which Wine prefix this copy runs in.";
            yield break;
        }
        int inPlace = target.Count(p => p.Path == Path.Combine(state.SavedPaksDirectory, p.Name));
        yield return $"Paks: {target.Count - inPlace} to put in place, {inPlace} already there.";
        var used = new HashSet<string>(target.Select(p => p.Path), host.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        int leaving = state.Paks.Concat(state.PaksInGame).Count(p => !used.Contains(p.Path));
        if (leaving > 0)
        {
            yield return (leaving == 1 ? "1 pak it does not use is" : $"{leaving} paks it does not use are")
                + (state.Channel is { } channel ? $" kept with {channel.Channel}, for switching back." : " kept aside in the buddy's folder.");
        }
        if (state.PaksInGame.Count > 0)
        {
            yield return $"{state.PaksInGame.Count} OpenVersus paks found in the game's own folders go where the mod keeps them, or with the set they belong to.";
        }
    }

    // The defaults of the version installed now, the base of the merge: the settings its release shipped, from the cache
    // (an install by the buddy names its release), else its own sample_config.toml, else sample.toml at its tag on
    // GitHub. Null when none can be had; the merge then judges by the new release's defaults.
    private async Task<string?> InstalledDefaultsAsync(InstallState state, GitStore cacheStore, CancellationToken cancellationToken)
    {
        if (state.Record?.Tag is string tag && cacheStore.Tagged(tag) is string commit)
        {
            string tree = cacheStore.TreeOf(commit);
            if ((Text(cacheStore, tree, Home + InstallReader.SettingsFile) ?? Text(cacheStore, tree, Home + SampleFile)) is string shipped)
            {
                return shipped;
            }
        }
        string sample = Path.Combine(state.Game.Win64Directory, "plugins", InstallReader.ModFolder, SampleFile);
        if (File.Exists(sample))
        {
            return File.ReadAllText(sample);
        }
        if (state.Mod.Version == null)
        {
            return null;
        }
        try
        {
            foreach (var channel in new[] { Channel.Prod, Channel.Testing })
            {
                var match = (await catalog.ListAsync(channel, cancellationToken).ConfigureAwait(false)).FirstOrDefault(r => r.Version == state.Mod.Version);
                if (match != null)
                {
                    return await catalog.FileAtTagAsync(match.Tag, "sample.toml", cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (ReleaseCatalogException e)
        {
            log.LogInformation("The defaults of {Version} could not be had: {Reason}", state.Mod.Version, e.Message);
        }
        return null;
    }

    private static string? Text(GitStore store, string tree, string path) => store.Read(tree, path) is byte[] bytes ? Encoding.UTF8.GetString(bytes) : null;

    private static string Full(CopyLayout copy, string path) => Path.Combine(copy.WorkTree, path.Replace('/', Path.DirectorySeparatorChar));

    // A path under the working tree as the store names it; null for one outside it.
    private static string? Relative(CopyLayout copy, string path)
    {
        string root = copy.WorkTree.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);
        return full.StartsWith(root, StringComparison.Ordinal) ? full.Substring(root.Length).Replace(Path.DirectorySeparatorChar, '/') : null;
    }

    private static string Sha256(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
    }
}
