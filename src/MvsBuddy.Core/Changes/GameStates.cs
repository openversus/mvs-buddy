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
    private readonly Action<IProgress<string>?> apply;

    internal PreparedChange(IReadOnlyList<string> summary, SettingsMergeResult? settings, string? refused, Action<IProgress<string>?> apply, string? risk = null)
    {
        Summary = summary;
        Settings = settings;
        Refused = refused;
        Risk = risk;
        this.apply = apply;
    }

    /// <summary>What the change will do, in plain sentences, for the person to confirm.</summary>
    public IReadOnlyList<string> Summary { get; }

    /// <summary>How the settings were merged, or null when no settings file is written.</summary>
    public SettingsMergeResult? Settings { get; }

    /// <summary>Why the change cannot be made as it is, naming what stands in its way; null when it can.</summary>
    public string? Refused { get; }

    /// <summary>
    /// What will probably go wrong if the change is made as it is (a pak it cannot put back), and how to fix that afterwards;
    /// null when nothing is known to. The change can still be made: the person is told, asked a second time, and the rest is
    /// theirs to decide.
    /// </summary>
    public string? Risk { get; }

    /// <summary>
    /// Makes the change: the game's files, then the paks. Refused, with nothing in the game touched, while the game is
    /// running, when <see cref="Refused"/> says so, when a file that is no state's stands where the change writes one, or
    /// when this account may not change a folder it writes (<see cref="ChangeException.DeniedFolders"/>). If the paks
    /// cannot be moved, the game's files are put back as they were.
    /// </summary>
    /// <exception cref="ChangeException">Refused, or failed and put back; the message says which.</exception>
    public void Apply() => apply(null);

    /// <summary>Makes the change as <see cref="Apply()"/> does, telling <paramref name="progress"/> what it is doing as it goes.</summary>
    /// <exception cref="ChangeException">Refused, or failed and put back; the message says which.</exception>
    public void Apply(IProgress<string>? progress) => apply(progress);
}

/// <summary>What a set-aside holds: OpenVersus of a channel, MVS Infinite, or everything else a clean start set aside.</summary>
public enum SetAsideKind
{
    /// <summary>OpenVersus of one channel, as it was when that channel was last live; "turn on" brings it back.</summary>
    OpenVersus,

    /// <summary>MVS Infinite's plugins and paks.</summary>
    MvsInfinite,

    /// <summary>Everything else clean starts set aside, together.</summary>
    Rest,
}

/// <summary>Something set aside for a copy of the game, which can be put back (or, for OpenVersus, turned on).</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Channel">For OpenVersus, its channel; null otherwise.</param>
/// <param name="Files">How many files it holds (paks included).</param>
/// <param name="Bytes">Their size; 0 for OpenVersus, whose files mostly are the cached release's.</param>
/// <param name="At">When it was set aside.</param>
public sealed record SetAside(SetAsideKind Kind, Channel? Channel, int Files, long Bytes, DateTimeOffset At);

/// <summary>
/// The states of each copy of the game, as branches of a bare git repository in MVS Buddy's folder whose working tree is
/// the game's install folder (<c>states/&lt;copy&gt;.git</c>): <c>current</c>, what is there now; <c>previous</c>, what was
/// there before the last change, which undo puts back; and per channel (<c>prod</c>, <c>testing</c>) the last state that
/// channel was live in, where its own <c>OVSState.toml</c> and <c>PatternsCache.toml</c> come back from. An install makes
/// the files a state tracks match the release, so nothing of the version before stays behind. Release files are read
/// from the cache (<see cref="ReleaseCache"/>), never stored twice. The paks are never in git: the live ones are in the
/// profile, the others in MVS Buddy's pak sets (<see cref="PakStore"/>), and a change moves them in the same go.
/// </summary>
public sealed class GameStates
{
    internal const string StatesFolder = "states";
    internal const string CurrentBranch = "current";
    internal const string PreviousBranch = "previous";

    /// <summary>The pak set for paks of no known channel: there with no mod to say which.</summary>
    internal const string OtherPaks = "other";

    /// <summary>MVS Infinite's plugins (and the loader, when it went with them), set aside; its paks are the pak set of the same name.</summary>
    internal const string MvsInfiniteBranch = "mvsi";

    /// <summary>Everything else a clean start set aside: one commit, added to by the next clean start.</summary>
    internal const string CleanStartBranch = "clean-start";

    /// <summary>The pak set MVS Infinite's paks are kept in while it is set aside.</summary>
    internal const string MvsInfinitePaks = "mvsi";

    /// <summary>The copy as it was the first time MVS Buddy changed it: one commit, made then and never moved.</summary>
    internal const string InitialBranch = "initial";

    // The branches that hold what is set aside apart from OpenVersus. Each change keeps them as they were before it under
    // "previous-<name>", as it keeps the files as `previous`; undo puts them back, so undoing a clean start un-sets-aside
    // and undoing a put-back sets aside again.
    private static readonly string[] SetBranches = [MvsInfiniteBranch, CleanStartBranch];

    private const string PreviousOf = "previous-";

    private const string Home = "plugins/OpenVersus/";
    private const string StateFile = "OVSState.toml";
    private const string PatternsFile = "PatternsCache.toml";
    private const string SampleFile = "sample_config.toml";
    private const string PakLine = "pak";
    private const string LoaderLine = "loader";
    private const string CoversLine = "covers";
    private const string FirstSightLoader = "Ultimate ASI Loader stays: one was here before MVS Buddy first changed this copy.";

    // The phases of a change, as its progress lines name them; a phase that counts its files adds "(n of m files)".
    private const string FirstLookPhase = "Keeping how this copy is before MVS Buddy first changes it...";
    private const string SavingPhase = "Saving what is there now, for undo...";
    private const string ChangingPhase = "Changing the game's files...";
    private const string PaksPhase = "Moving the paks...";

    private readonly HostSystem host;
    private readonly ReleaseCatalog catalog;
    private readonly ReleaseCache cache;
    private readonly PakStore paks;
    private readonly InstallReader installs;
    private readonly StockCheck stock;
    private readonly AsiLoaderPin pin;
    private readonly ILogger<GameStates> log;

    internal GameStates(HostSystem host, ReleaseCatalog catalog, ReleaseCache cache, PakStore paks, InstallReader installs, StockCheck stock, AsiLoaderPin pin,
        ILogger<GameStates> log)
    {
        this.stock = stock;
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

    /// <summary>
    /// A pak live now: the mod's, in the profile or put in the game's own folders by hand (<paramref name="Home"/> null:
    /// it belongs in the profile); or MVS Infinite's, in the game's <c>Content/Paks</c> (<paramref name="Home"/> its folder,
    /// relative to the working tree).
    /// </summary>
    internal sealed record LivePak(string Name, long Size, string Sha256, string Path, string? Home)
    {
        /// <summary>The pak as a change's target has it: where it is now.</summary>
        public StagedPak InPlace => new(Name, Size, Sha256, null, Path, false) { Home = Home };
    }

    /// <summary>The key that names one copy of the game in MVS Buddy's folder: its Win64 path, hashed.</summary>
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
            throw new ChangeException("MVS Buddy has no folder of its own to keep states in.");
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
        if (IsOlderThanInstallable(release.Version))
        {
            string old = $"OpenVersus {release.Tag} is older than {ModVersion.Text(OldestInstallable)}, the oldest release MVS Buddy installs. Pick a newer one.";
            return new PreparedChange([old], null, old, _ => throw new ChangeException(old));
        }
        var copy = Layout(state.Game);
        var cached = await cache.GetAsync(release, progress, cancellationToken).ConfigureAwait(false);
        var cacheStore = cache.Store();
        var staged = new List<StagedPak>();
        if (state.SavedPaksDirectory != null)
        {
            var pakAssets = await catalog.PaksForAsync(release, cancellationToken).ConfigureAwait(false);
            staged.AddRange(await paks.StageAsync(pakAssets, PakSources(state, copy, []), progress, cancellationToken).ConfigureAwait(false));
        }

        string version = release.Version == null ? release.Tag : ModVersion.Text(release.Version);
        string from = state.Mod.Version != null ? ModVersion.Text(state.Mod.Version) : state.Mod.Layout == ModLayout.None ? "nothing" : $"an older layout ({state.Mod.Layout})";
        var summary = new List<string> { $"Installs OpenVersus {version} ({release.Channel}) over {from}." };
        if (!Directory.Exists(state.Game.Win64Directory))
        {
            summary.Add($"{state.Game.Win64Directory} does not exist yet; it will be made.");
        }
        summary.AddRange(ChannelLines(state, copy, release.Channel));
        // MVS Infinite cannot run with OpenVersus: an install sets it aside in the same change.
        var mvsi = LiveMvsInfinite(state, copy);
        int mvsiPaks = LivePaths(state, copy).Count(p => p.Home != null);
        bool mvsiAside = mvsi.Count + mvsiPaks > 0;
        if (mvsiAside)
        {
            summary.Add(MvsInfiniteAsideLine(mvsi.Count, mvsiPaks));
        }

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
        summary.AddRange(PakLines(state, copy, staged.Select(p => new PakAt(p.Name, p.Path, p.Home))
            .Concat(mvsiAside ? [] : LivePaths(state, copy).Where(p => p.Home != null))));
        var notChecked = (cached.ZipChecked ? Array.Empty<string>() : new[] { release.InstallZip!.Name }).Concat(staged.Where(p => !p.Checked).Select(p => p.Name)).ToList();
        if (notChecked.Count > 0)
        {
            summary.Add($"Not checked, since neither GitHub nor the release gives a checksum for them: {string.Join(", ", notChecked)}.");
        }
        // Under Wine, the loader loads only when the prefix says native first for its name.
        string loaderDll = Path.GetFileNameWithoutExtension(loaderName).ToLowerInvariant();
        bool setWine = state.WineRegistry is string registry && !WineRegistry.LoadsNative(File.ReadAllText(registry), loaderDll);
        if (setWine)
        {
            summary.AddRange(WineLines(state, [loaderDll]));
        }
        summary.Add(UndoLine(state, "Undo puts the mod, the loader and the paks back as they are now"));

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
            // MVS Buddy's loader only when it puts it there, or did before (the record says so): the same build already in
            // place is the person's, and a turn-off or uninstall leaves it.
            Loader = atName is { IsPinnedBuild: true } && !(state.Record?.Loader is { } before
                    && string.Equals(before.Name, loaderName, StringComparison.OrdinalIgnoreCase) && before.Sha256 == atName.Sha256)
                ? null
                : new RecordedFile { Name = loaderName, Version = pin.Version, Sha256 = pin.DllSha256 },
        };
        byte[]? settingsBytes = merge == null ? null : Encoding.UTF8.GetBytes(merge.Text);
        byte[] recordBytes = record.ToJson();
        log.LogInformation("Prepared {Release} for {Folder}: {Summary}", release.Tag, copy.WorkTree, string.Join(" | ", summary));

        return new PreparedChange(summary, merge, refused, report =>
        {
            Change(report, state.Game, $"before OpenVersus {version} ({release.Channel}) was installed",
                $"OpenVersus {version} ({release.Channel})", refused,
                // The release's paks; MVS Infinite's stay where they are, unless it is set aside: then they leave as its set.
                (_, live) => [.. staged, .. live.Where(p => p.Home != null && !mvsiAside).Select(p => p.InPlace)],
                (store, now, before) => store.Edit(before, InstallEdits(store, copy, now, before, cached.Tree, release.Channel, loaderName, settingsBytes, recordBytes)
                    .Concat(mvsi.Select(p => (TreeEdit)new Removal(p)))),
                (store, before) =>
                {
                    SetMvsInfiniteAside(store, before, mvsi);
                    if (release.Channel == Channel.Testing)
                    {
                        PruneTesting(store, staged);
                    }
                },
                mvsi);
            if (setWine)
            {
                try
                {
                    SetWineNative(state.WineRegistry!, [loaderDll]);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    throw new ChangeException($"OpenVersus is installed, but Wine's setting could not be written ({e.Message}); the game's screen offers to set it.", e);
                }
            }
        });
    }

    /// <summary>
    /// Gets telling Wine to load the live loader (each copy of it) from the game's folder ready: the prefix's registry gets
    /// native first for its name. Not a change of state, and nothing to undo; it does nothing without a loader there.
    /// </summary>
    public PreparedChange PrepareWineSetting(InstallState state)
    {
        if (state == null)
        {
            throw new ArgumentNullException(nameof(state));
        }
        if (state.WineRegistry is not string registry || state.WineOverridesMissing.Count == 0)
        {
            const string nothing = "Wine is already set to load the loader from the game's folder.";
            return new PreparedChange([nothing], null, nothing, _ => throw new ChangeException(nothing));
        }
        var dlls = state.WineOverridesMissing;
        return new PreparedChange(WineLines(state, dlls).ToList(), null, null, _ => SetWineNative(registry, dlls));
    }

    private static IEnumerable<string> LiveLoaderDlls(InstallState state) =>
        state.Loaders.Where(l => l.IsUltimateAsiLoader).Select(l => Path.GetFileNameWithoutExtension(l.FileName).ToLowerInvariant());

    // What setting Wine's override says; and, while Wine runs in the game's prefix, that it may write over it when it ends.
    private IEnumerable<string> WineLines(InstallState state, IEnumerable<string> dlls)
    {
        yield return $"Wine is set to load {string.Join(", ", dlls.Select(d => d + ".dll"))} from the game's folder (in the game's Wine prefix), which the loader needs.";
        if (state.Game.WinePrefix is string prefix && host.IsWineRunningIn(prefix))
        {
            yield return "Wine is running in the game's prefix right now, and may write over this setting when it ends. If MVS Buddy says it is missing later, set it again with the game closed.";
        }
    }

    // The registry with native first for each DLL, written beside it and then put in its place.
    private static void SetWineNative(string registry, IEnumerable<string> dlls)
    {
        string text = File.ReadAllText(registry);
        foreach (string dll in dlls)
        {
            text = WineRegistry.WithNative(text, dll, DateTimeOffset.Now);
        }
        string written = registry + ".mvs-buddy";
        File.WriteAllText(written, text);
        File.Replace(written, registry, null);
    }

    /// <summary>
    /// The Steam accounts on this machine whose launch options for the game MVS Buddy can read: what they say now, and
    /// whether they tell Wine to load the loader (<see cref="LaunchOptionsDlls"/>) from the game's folder. The accounts
    /// the game has run under; when it has run under none and there is one account, that one. Empty on Windows, for a
    /// copy that is not Steam's, and when Steam's folder is nowhere MVS Buddy looks.
    /// </summary>
    public IReadOnlyList<LaunchOptionsAccount> LaunchOptionsAccounts(InstallState state)
    {
        if (state == null)
        {
            throw new ArgumentNullException(nameof(state));
        }
        if (host.IsWindows || state.Game.Store != GameStore.Steam)
        {
            return [];
        }
        var dlls = LaunchOptionsDlls(state);
        var read = new List<(LaunchOptionsAccount Account, bool HasApp)>();
        foreach (string root in GameFinder.SteamRoots(host).Where(Directory.Exists).Select(host.RealPath).Distinct(StringComparer.Ordinal))
        {
            foreach (var account in SteamLaunchOptions.Accounts(root))
            {
                try
                {
                    string text = ReadText(account.LocalConfig);
                    if (SteamLaunchOptions.Read(text, GameFiles.SteamAppId) is string options)
                    {
                        read.Add((new LaunchOptionsAccount(account.Id, account.Name, options, dlls.All(d => LaunchLine.LoadsNative(options, d))) { File = account.LocalConfig },
                            SteamLaunchOptions.HasApp(text, GameFiles.SteamAppId)));
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is FormatException || e is DecoderFallbackException)
                {
                    log.LogInformation("Skipped {File}: {Reason}", account.LocalConfig, e.Message);
                }
            }
        }
        var ran = read.Where(r => r.HasApp).Select(r => r.Account).ToList();
        return ran.Count > 0 || read.Count != 1 ? ran : [read[0].Account];
    }

    /// <summary>
    /// The DLL names (without <c>.dll</c>) the launch options setting names: each live copy of Ultimate ASI Loader, or
    /// <c>version</c>, the name an install under Proton gives it, when there is none yet.
    /// </summary>
    public static IReadOnlyList<string> LaunchOptionsDlls(InstallState state) =>
        state == null ? throw new ArgumentNullException(nameof(state)) : LiveLoaderDlls(state).Distinct().DefaultIfEmpty("version").ToList();

    /// <summary>
    /// Gets turning the Steam launch options setting on or off for <paramref name="account"/> ready. On puts
    /// <c>WINEDLLOVERRIDES</c> with the loader's name native first into the game's launch options: into an assignment
    /// already there, else at the front, with <c>%command%</c> added when the line has none. Off takes the loader's entry
    /// out again, and the assignment when nothing else is in it. The rest of the line stays as it is. Refused while Steam
    /// runs, since it writes the file over when it exits; nothing to undo, the other setting puts it back.
    /// </summary>
    public PreparedChange PrepareLaunchOptions(InstallState state, LaunchOptionsAccount account, bool on)
    {
        if (state == null || account == null)
        {
            throw new ArgumentNullException(state == null ? nameof(state) : nameof(account));
        }
        var dlls = LaunchOptionsDlls(state);
        string now = account.Options;
        string then = on ? LaunchLine.TurnedOn(now, dlls) : LaunchLine.TurnedOff(now, dlls);
        string names = string.Join(", ", dlls.Select(d => d + ".dll"));
        var summary = new List<string>
        {
            on ? $"Steam's launch options for MultiVersus ({account.Name}) will tell Wine to load {names} from the game's folder."
               : $"Steam's launch options for MultiVersus ({account.Name}) will no longer tell Wine to load {names} from the game's folder.",
            now.Length == 0 ? "Now: no launch options." : $"Now: {now}",
            then.Length == 0 ? "After: no launch options." : $"After: {then}",
        };
        if (then == now)
        {
            string same = on ? "The launch options already say so." : "The launch options do not say so.";
            return new PreparedChange(summary, null, same, _ => throw new ChangeException(same));
        }
        if (host.IsSteamRunning())
        {
            return new PreparedChange(summary, null, SteamRunningText, _ => throw new ChangeException(SteamRunningText));
        }
        return new PreparedChange(summary, null, null, _ => WriteLaunchOptions(account, dlls, on));
    }

    private const string SteamRunningText = "Steam is currently running; it overwrites games' launch options when it exits, so any changes made now would be lost. "
                                            + "Close Steam first, hit the \"Back\" button, and then turn this setting on. To change this setting if you're using a Steam Deck, "
                                            + "you may need to enter Desktop Mode, close Steam, change this setting, and then return to Gaming Mode. If you are unable to "
                                            + "return to Gaming Mode, restart the Steam Deck and it will boot to Gaming Mode automatically by default.";

    // The file as it is at this moment (not as it was read for the screen), changed in one place and put in its place.
    private void WriteLaunchOptions(LaunchOptionsAccount account, IReadOnlyList<string> dlls, bool on)
    {
        if (host.IsSteamRunning())
        {
            throw new ChangeException(SteamRunningText);
        }
        string text;
        try
        {
            text = ReadText(account.File);
        }
        catch (DecoderFallbackException e)
        {
            throw new ChangeException($"{account.File} is not UTF-8 text throughout, so MVS Buddy cannot change it without changing other bytes; it is left alone.", e);
        }
        string current = SteamLaunchOptions.Read(text, GameFiles.SteamAppId)
            ?? throw new ChangeException($"{account.File} is not laid out as MVS Buddy expects (no apps block), so it is left alone.");
        string options = on ? LaunchLine.TurnedOn(current, dlls) : LaunchLine.TurnedOff(current, dlls);
        string written = account.File + ".mvs-buddy";
        File.WriteAllBytes(written, Utf8.GetBytes(SteamLaunchOptions.With(text, GameFiles.SteamAppId, options)!));
        File.Replace(written, account.File, null);
    }

    // Byte for byte: a byte-order mark, should there be one, is a character of the text and goes back as it was; a byte
    // that is not UTF-8 would not, so reading it throws rather than writing something else in its place.
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static string ReadText(string file) => Utf8.GetString(File.ReadAllBytes(file));

    /// <summary>
    /// Gets undoing the last change ready: the copy as it was before it, files and paks. Undoing again brings back what is
    /// there now. The paks come from MVS Buddy's sets or the profile, by checksum; one that is in neither is a reason the
    /// undo cannot be made, not something to fetch anew.
    /// </summary>
    public PreparedChange PrepareUndo(InstallState state, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
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
            return new PreparedChange([nothing], null, nothing, _ => throw new ChangeException(nothing));
        }
        var store = Open(copy);
        var (message, at) = store.Describe(previous);
        string what = message.Split('\n')[0];
        string tree = store.TreeOf(previous);
        var found = Find(state, copy, PaksIn(message), RecordIn(store, copy, tree), progress, cancellationToken);
        var summary = new List<string> { $"Puts this copy back as it was {what}, on {at.LocalDateTime:yyyy-MM-dd HH:mm}." };
        // An undo puts back exactly what was there, paks included; when that is Testing's last live state and Testing's paks
        // have moved on since, it says so, and turning Testing off and on gets the current ones.
        if (store.Tip(ReleaseCache.BranchOf(Channel.Testing)) == previous && TestingMovedOn(found, progress, cancellationToken))
        {
            summary.Add("Testing's paks have moved on since then: this puts back the ones it had. Turn Testing off and on again afterwards, or install it again, for the current ones.");
        }
        summary.AddRange(PakLines(state, copy, found.Paks));
        summary.AddRange(found.Lines);
        summary.Add("Undoing again brings back what is here now.");
        return new PreparedChange(summary, null, null,
            report => Change(report, state.Game, "before an undo", $"Undo: {what}", null, (_, _) => found.Paks, (_, _, _) => tree, restoresSets: true),
            RiskOf(found.Missing, "what this puts back", InstallAgainFix));
    }

    /// <summary>
    /// Deletes the undo point for good: the last change stays as it is, and there is nothing to undo until the next change.
    /// What the last change set aside stays set aside. For a person who would rather not undo without a pak that is gone.
    /// </summary>
    public void DropUndo(GameInstall game)
    {
        var copy = Layout(game ?? throw new ArgumentNullException(nameof(game)));
        if (!LibGit2Sharp.Repository.IsValid(copy.GitDir))
        {
            return;
        }
        var store = Open(copy);
        store.DeleteBranch(PreviousBranch);
        foreach (string set in SetBranches)
        {
            store.DeleteBranch(PreviousOf + set);
        }
        store.Prune();
    }

    /// <summary>
    /// Gets turning OpenVersus off ready: its files and paks set aside, and with them Ultimate ASI Loader when MVS Buddy
    /// put it there (its install record names it) and no other plugin is left to load through it; a loader put there some
    /// other way stays. "Turn on" (<see cref="PrepareTurnOn"/>) brings it back exactly as it is now.
    /// </summary>
    public PreparedChange PrepareTurnOff(InstallState state) =>
        SetAside(state ?? throw new ArgumentNullException(nameof(state)), openVersus: true, mvsInfinite: false, others: [], onlyOurLoader: true);

    /// <summary>
    /// Gets uninstalling OpenVersus ready: its files and its paks are deleted, and Ultimate ASI Loader with them when the
    /// buddy put it there (its install record names it) and no other plugin is left to load through it; a loader put there
    /// some other way stays, and so do the mod's logs. Nothing of OpenVersus is kept for this copy afterwards: the saved
    /// state and paks of every channel kept for it go too. There is no undo; to have OpenVersus again, it is installed again.
    /// </summary>
    public PreparedChange PrepareUninstall(InstallState state)
    {
        if (state == null)
        {
            throw new ArgumentNullException(nameof(state));
        }
        var copy = Layout(state.Game);
        bool keepLoaders = KeepsLoaders(state, copy);
        var (ovsPaths, _, staying, loaderPaths, live) = WhatGoes(state, copy, openVersus: true, mvsInfinite: false, others: [], onlyOurLoader: true, keepLoaders);
        // Without the profile's folder (a prefix not known) the mod's paks are left where they are, as by every change.
        var goingPaks = state.SavedPaksDirectory == null ? [] : live;
        var kept = SetAsideOf(state).Where(s => s.Kind == SetAsideKind.OpenVersus).Select(s => s.Channel!.Value).ToList();
        bool installed = state.Mod.Layout != ModLayout.None;
        bool inGame = installed || goingPaks.Count > 0 || loaderPaths.Count > 0;

        var summary = new List<string>();
        if (installed)
        {
            summary.Add($"Deletes OpenVersus{(state.Mod.Version is { } v ? " " + ModVersion.Text(v) : "")}{(state.Channel is { } c ? $" ({c.Channel})" : "")} from the game's folder.");
        }
        if (goingPaks.Count > 0)
        {
            summary.Add(goingPaks.Count == 1 ? "Deletes its pak." : $"Deletes its {goingPaks.Count} paks.");
        }
        else if (live.Count > 0)
        {
            summary.Add("Its paks stay where they are: " + (state.NeedsFirstLaunch ? InstallReader.FirstLaunchNote
                : "MVS Buddy cannot tell which Wine prefix this copy runs in."));
        }
        foreach (var channel in kept)
        {
            summary.Add($"Deletes OpenVersus {channel}, kept for this copy, with its saved state and its paks.");
        }
        bool loader = state.Loaders.Any(l => l.IsUltimateAsiLoader);
        if (loaderPaths.Count > 0)
        {
            summary.Add("Deletes Ultimate ASI Loader too: MVS Buddy put it there, and no plugin is left to load through it.");
        }
        else if (loader && staying.Count > 0)
        {
            summary.Add($"Ultimate ASI Loader stays: {string.Join(", ", staying)} still loads through it.");
        }
        else if (loader && keepLoaders)
        {
            summary.Add(FirstSightLoader);
        }
        else if (loader)
        {
            summary.Add("Ultimate ASI Loader stays: MVS Buddy did not put it there.");
        }
        if (state.Mod.Files.Any(f => f.Contains(Path.DirectorySeparatorChar + "logs" + Path.DirectorySeparatorChar)))
        {
            summary.Add("The mod's logs stay in plugins/OpenVersus/logs: MVS Buddy never touches them.");
        }
        if (state.WineRegistry is string wine && LiveLoaderDlls(state).Concat(["version"]).Distinct().FirstOrDefault(d => WineRegistry.LoadsNative(File.ReadAllText(wine), d)) is string set)
        {
            summary.Add($"Wine's setting to load {set}.dll from the game's folder stays; it does nothing without a loader there.");
        }
        string? refused = inGame || kept.Count > 0 ? null : "There is no OpenVersus here to uninstall.";
        summary.Add(refused ?? "There is no undo for this: to have OpenVersus again, install it.");
        log.LogInformation("Uninstall prepared for {Folder}: {Summary}", copy.WorkTree, string.Join(" | ", summary));

        var removed = ovsPaths.Concat(loaderPaths).Distinct(StringComparer.Ordinal).ToList();
        return new PreparedChange(summary, null, refused, report =>
        {
            if (inGame)
            {
                Change(report, state.Game, "before OpenVersus was uninstalled", "OpenVersus, uninstalled", refused,
                    // The mod's paks leave as its channel's set, which Forget deletes; MVS Infinite's stay where they are.
                    (_, live) => live.Where(p => p.Home != null).Select(p => p.InPlace).ToList(),
                    (store, _, before) => store.Edit(before, removed.Select(p => (TreeEdit)new Removal(p))),
                    (store, _) => Forget(store));
                return;
            }
            if (refused != null)
            {
                throw new ChangeException(refused);
            }
            // Only kept states: nothing in the game changes.
            var store = Open(copy);
            Forget(store);
            store.Prune();
            Retain(report);
        });
    }

    // The end of an uninstall: no channel's state or paks are kept for this copy, and there is nothing to undo.
    private void Forget(GitStore store)
    {
        foreach (var channel in new[] { Channel.Prod, Channel.Testing })
        {
            store.DeleteBranch(ReleaseCache.BranchOf(channel));
        }
        store.DeleteBranch(PreviousBranch);
        foreach (string set in SetBranches)
        {
            store.DeleteBranch(PreviousOf + set);
        }
        foreach (string set in new[] { ReleaseCache.BranchOf(Channel.Prod), ReleaseCache.BranchOf(Channel.Testing), OtherPaks })
        {
            if (Directory.Exists(paks.Folder(set)))
            {
                Directory.Delete(paks.Folder(set), recursive: true);
            }
        }
    }

    /// <summary>
    /// Gets writing down that the installed OpenVersus is <paramref name="channel"/> ready, as the person says: its record
    /// (see <see cref="InstallRecord"/>) is kept, or made, with that channel. Nothing else moves, and there is nothing to
    /// undo; the next change files what is here under that channel, and what an earlier change filed of this same install
    /// under the old one is not kept as the old one any more. The current layout only, which has a folder for the record.
    /// </summary>
    public PreparedChange PrepareSetChannel(InstallState state, Channel channel)
    {
        if (state == null)
        {
            throw new ArgumentNullException(nameof(state));
        }
        if (state.Mod.Layout != ModLayout.Current)
        {
            const string only = "Only the current OpenVersus layout keeps a record of its channel.";
            return new PreparedChange([only], null, only, _ => throw new ChangeException(only));
        }
        string path = Path.Combine(state.Game.Win64Directory, "plugins", InstallReader.ModFolder, InstallRecord.FileName);
        var record = state.Record?.Copy() ?? new InstallRecord { Version = state.Mod.Version is { } v ? ModVersion.Text(v) : null };
        record.Channel = channel.ToString();
        record.ChannelFrom = "person";
        // At stays the install's: it is half of what tells this install from another one. The correction has its own time.
        record.CorrectedAt = DateTimeOffset.Now;
        record.Buddy = BuddyVersion.Current;
        var plan = new ChangePlan(host, log);
        if (File.Exists(path))
        {
            plan.Delete(path);
        }
        plan.Write(path, record.ToJson());
        var summary = new List<string> { $"MVS Buddy counts this copy's OpenVersus as {channel} from now on." };

        // A change made before the correction filed this very install under the old channel, which is not a kept state of
        // that channel: it goes. Its pak set stays, as undo may need it.
        string? misfiled = null;
        var copy = Layout(state.Game);
        if (state.Channel is { } old && old.Channel != channel && LibGit2Sharp.Repository.IsValid(copy.GitDir))
        {
            var store = Open(copy);
            string branch = ReleaseCache.BranchOf(old.Channel);
            if (store.Tip(branch) is string tip && store.TreeOf(tip) is string tree && store.Has(tree, copy.Prefix + Home.TrimEnd('/')))
            {
                string live = Path.Combine(state.Game.Win64Directory, "plugins", InstallReader.ModFolder, StateFile);
                if (SameInstall(InstallId(File.Exists(live) ? File.ReadAllBytes(live) : null), state.Record,
                    InstallId(store.Read(tree, copy.Prefix + Home + StateFile)), RecordIn(store, copy, tree)))
                {
                    misfiled = branch;
                    summary.Add($"What MVS Buddy kept as OpenVersus {old.Channel} is this same install, so it is not kept as {old.Channel} any more.");
                }
            }
        }
        return new PreparedChange(summary, null, null, _ =>
        {
            plan.Apply();
            if (misfiled != null)
            {
                var store = Open(copy);
                store.DeleteBranch(misfiled);
                store.Prune();
            }
        });
    }

    // Whether two states are one install of the mod. The mod's own identity decides when both have run (an InstallId each,
    // made once and never crossing channels); before one of them has run, MVS Buddy's own record does (one install action:
    // the same tag at the same moment); with neither to go by, two states that never ran and were never recorded are one.
    internal static bool SameInstall(string? liveId, InstallRecord? liveRecord, string? keptId, InstallRecord? keptRecord)
    {
        if (liveId != null && keptId != null)
        {
            return liveId == keptId;
        }
        if (liveRecord != null && keptRecord != null)
        {
            return liveRecord.Tag == keptRecord.Tag && liveRecord.At == keptRecord.At;
        }
        return liveId == null && keptId == null;
    }

    // Which install of the mod a state is: the [Identity] InstallId of its OVSState.toml, which the mod makes once and which
    // never crosses channels; null when it has none (never run).
    private static string? InstallId(byte[]? state) =>
        state == null ? null : TomlSettings.Parse(Encoding.UTF8.GetString(state), StateFile).Get("Identity", "InstallId");

    /// <summary>
    /// Gets a clean start ready: <paramref name="extras"/> (from <see cref="StockCheck"/>, all of them or the person's pick)
    /// set aside, by kind, so each can come back on its own. OpenVersus as its channel's state ("turn on" brings it back);
    /// MVS Infinite's plugins in a state of its own and its paks in its pak set; everything else in one state that the next
    /// clean start adds to. Ultimate ASI Loader goes too when no plugin is left to load through it. Undo puts it all back.
    /// </summary>
    public PreparedChange PrepareSetAside(InstallState state, IEnumerable<StockExtra> extras)
    {
        if (state == null || extras == null)
        {
            throw new ArgumentNullException(state == null ? nameof(state) : nameof(extras));
        }
        var picked = extras.ToList();
        var copy = Layout(state.Game);
        return SetAside(state, picked.Any(e => e.Kind == ExtraKind.OpenVersus), picked.Any(e => e.Kind == ExtraKind.MvsInfinite),
            picked.Where(e => e.Kind == ExtraKind.Other).Select(e => Relative(copy, e.Path)).OfType<string>().ToList());
    }

    /// <summary>What a set-aside or an uninstall takes out of the game, as the store names it.</summary>
    /// <param name="OpenVersus">The mod's own files (not the loader).</param>
    /// <param name="MvsInfinite">MVS Infinite's plugins.</param>
    /// <param name="Staying">The plugins left to load through Ultimate ASI Loader, by file name.</param>
    /// <param name="Loader">Ultimate ASI Loader and its license, when they go.</param>
    /// <param name="Paks">The live paks that go.</param>
    private sealed record Going(List<string> OpenVersus, List<string> MvsInfinite, List<string> Staying, List<string> Loader, List<PakAt> Paks);

    private static Going WhatGoes(InstallState state, CopyLayout copy, bool openVersus, bool mvsInfinite, IReadOnlyList<string> others, bool onlyOurLoader,
        bool keepLoaders)
    {
        // The mod's own files only: whether the loader and its license go is decided below, by its own rule.
        var ovsPaths = openVersus
            ? new[] { copy.Prefix + Home.TrimEnd('/') }.Concat(state.Mod.Files.Concat(state.Mod.OldLayoutFiles).Select(f => Relative(copy, f)).OfType<string>()).Distinct(StringComparer.Ordinal).ToList()
            : [];
        var mvsiPaths = mvsInfinite ? state.OtherPlugins.Where(p => p.IsMvsInfinite).Select(p => Relative(copy, p.Path)).OfType<string>().ToList() : [];
        // The loader goes when no plugin stays to be loaded through it, with each part that goes, so each can come back on its own.
        var going = new HashSet<string>(ovsPaths.Concat(mvsiPaths).Concat(others), StringComparer.Ordinal);
        var staying = state.Mod.Plugins.Concat(state.Mod.OldLayoutFiles.Where(f => f.EndsWith(".asi", StringComparison.OrdinalIgnoreCase)))
            .Concat(state.OtherPlugins.Select(p => p.Path)).Where(p => !(openVersus && IsOpenVersusPlugin(state, p)) && Relative(copy, p) is string r && !going.Contains(r))
            .Select(Path.GetFileName).OfType<string>().Distinct().ToList();
        var loaderPaths = !keepLoaders && staying.Count == 0 && going.Count > 0 ? LoaderPaths(state, copy, onlyOurLoader) : [];
        var goingPaks = LivePaths(state, copy).Where(p => (openVersus && p.Home == null) || (mvsInfinite && p.Home != null)).ToList();
        return new Going(ovsPaths, mvsiPaths, staying, loaderPaths, goingPaks);
    }

    private PreparedChange SetAside(InstallState state, bool openVersus, bool mvsInfinite, IReadOnlyList<string> others, bool onlyOurLoader = false)
    {
        var copy = Layout(state.Game);
        bool keepLoaders = KeepsLoaders(state, copy);
        var (ovsPaths, mvsiPaths, staying, loaderPaths, goingPaks) = WhatGoes(state, copy, openVersus, mvsInfinite, others, onlyOurLoader, keepLoaders);
        var going = new HashSet<string>(ovsPaths.Concat(mvsiPaths).Concat(others), StringComparer.Ordinal);

        var summary = new List<string>();
        string? refused = null;
        if (going.Count == 0 && goingPaks.Count == 0)
        {
            refused = "Nothing was picked to set aside.";
            summary.Add(refused);
        }
        if (openVersus)
        {
            if (state.Channel is { } channel)
            {
                summary.Add($"OpenVersus {channel.Channel}: set aside; \"Turn OpenVersus {channel.Channel} on\" brings it back as it is now, with its paks.");
            }
            else
            {
                refused ??= "There is no OpenVersus here to set aside.";
            }
            if (state.Mod.Files.Any(f => f.Contains(Path.DirectorySeparatorChar + "logs" + Path.DirectorySeparatorChar)))
            {
                summary.Add("The mod's logs stay in plugins/OpenVersus/logs: they are never part of a state.");
            }
        }
        if (mvsInfinite)
        {
            summary.Add($"MVS Infinite: {mvsiPaths.Count} {Files(mvsiPaths.Count)} and {goingPaks.Count(p => p.Home != null)} {Paks(goingPaks.Count(p => p.Home != null))} set aside, to put back on their own.");
        }
        if (others.Count > 0)
        {
            var store = Open(copy);
            string? held = store.Tip(CleanStartBranch);
            int already = held == null ? 0 : others.Count(o => store.Has(store.TreeOf(held), o));
            summary.Add($"Everything else: {others.Count} {Files(others.Count)} set aside, to put back together"
                + (held != null ? ", with what an earlier clean start set aside" : "") + ".");
            if (already > 0)
            {
                summary.Add($"{already} of them {(already == 1 ? "was" : "were")} set aside before too; the {(already == 1 ? "copy" : "copies")} set aside now {(already == 1 ? "replaces" : "replace")} the older.");
            }
        }
        if (loaderPaths.Count > 0)
        {
            summary.Add("Ultimate ASI Loader goes too, since no plugin is left to load through it; it comes back with whatever is put back.");
        }
        else if (going.Count > 0 && staying.Count > 0)
        {
            summary.Add($"Ultimate ASI Loader stays: {string.Join(", ", staying)} still loads through it.");
        }
        else if (going.Count > 0 && keepLoaders && state.Loaders.Any(l => l.IsUltimateAsiLoader))
        {
            summary.Add(FirstSightLoader);
        }
        else if (going.Count > 0 && state.Loaders.Any(l => l.IsUltimateAsiLoader))
        {
            summary.Add("Ultimate ASI Loader stays: MVS Buddy did not put it there.");
        }
        summary.Add(UndoLine(state, "Undo puts everything back as it is now"));
        log.LogInformation("Set aside prepared for {Folder}: {Summary}", copy.WorkTree, string.Join(" | ", summary));

        var removed = ovsPaths.Concat(mvsiPaths).Concat(others).Concat(loaderPaths).Distinct(StringComparer.Ordinal).ToList();
        // What the undo of this is called: a turn-off is OpenVersus alone; anything more is a clean start.
        bool turnOff = openVersus && !mvsInfinite && others.Count == 0;
        return new PreparedChange(summary, null, refused, report => Change(report, state.Game, turnOff ? "before OpenVersus was turned off" : "before a clean start", turnOff ? "OpenVersus, turned off" : "Set aside", refused,
            (_, live) => live.Where(p => !((openVersus && p.Home == null) || (mvsInfinite && p.Home != null))).Select(p => p.InPlace).ToList(),
            (store, _, before) => store.Edit(before, removed.Select(p => (TreeEdit)new Removal(p))),
            (store, before) =>
            {
                if (mvsiPaths.Count > 0)
                {
                    store.Commit(MvsInfiniteBranch, store.Edit(null, Grafts(store, before, mvsiPaths.Concat(loaderPaths))), "MVS Infinite, set aside");
                }
                if (others.Count > 0)
                {
                    // One state for everything else, added to: what was set aside before stays, a path set aside again is the newer.
                    var held = store.Tip(CleanStartBranch) is string tip ? store.TreeOf(tip) : null;
                    store.Commit(CleanStartBranch, store.Edit(held, Grafts(store, before, others.Concat(loaderPaths))), "Set aside by a clean start");
                }
            },
            mvsiPaths.Concat(others).Concat(loaderPaths)));
    }

    /// <summary>
    /// Gets turning OpenVersus <paramref name="channel"/> on ready: the mod exactly as it was when that channel was last
    /// live here (its version, settings and saved state), and its paks from MVS Buddy's set. Nothing is downloaded; a pak
    /// that is no longer kept is a reason it cannot be turned on as it was.
    /// </summary>
    public PreparedChange PrepareTurnOn(InstallState state, Channel channel, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        TurnOn(state ?? throw new ArgumentNullException(nameof(state)), channel, progress, setMvsInfiniteAside: false, cancellationToken);

    /// <summary>
    /// Gets switching from MVS Infinite to OpenVersus <paramref name="channel"/> ready: OpenVersus turned on as in
    /// <see cref="PrepareTurnOn"/>, and MVS Infinite, which cannot run with it, set aside in the same change (its plugins
    /// and paks; an earlier one set aside is replaced). <see cref="PrepareSwitchToMvsInfinite"/> switches back.
    /// </summary>
    public PreparedChange PrepareSwitchToOpenVersus(InstallState state, Channel channel, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        TurnOn(state ?? throw new ArgumentNullException(nameof(state)), channel, progress, setMvsInfiniteAside: true, cancellationToken);

    private PreparedChange TurnOn(InstallState state, Channel channel, IProgress<string>? progress, bool setMvsInfiniteAside, CancellationToken cancellationToken)
    {
        var copy = Layout(state.Game);
        string? last = LibGit2Sharp.Repository.IsValid(copy.GitDir) ? Open(copy).Tip(ReleaseCache.BranchOf(channel)) : null;
        var store = Open(copy);
        string? lastTree = last == null ? null : store.TreeOf(last);
        if (lastTree == null || !store.Has(lastTree, copy.Prefix + Home.TrimEnd('/')))
        {
            string nothing = $"Nothing of OpenVersus {channel} is kept for this copy of the game.";
            return new PreparedChange([nothing], null, nothing, _ => throw new ChangeException(nothing));
        }
        var (message, at) = store.Describe(last!);
        var record = RecordIn(store, copy, lastTree);
        var found = Find(state, copy, PaksIn(message).Where(p => p.Home == null), record, progress, cancellationToken);
        var current = channel == Channel.Testing ? CurrentTestingPaks(state, copy, progress, cancellationToken) : null;
        if (current != null)
        {
            found = ForTesting(found, current);
        }
        var owned = store.Paths(lastTree).Where(p => IsOpenVersusOwned(copy, p)).ToList();
        string version = record?.Version ?? record?.Tag ?? "";
        var summary = new List<string> { $"Turns OpenVersus {channel}{(version.Length > 0 ? " " + version : "")} back on as it was on {at.LocalDateTime:yyyy-MM-dd HH:mm}, with its settings and saved state." };
        if (state.Channel is { } live && state.Mod.Layout != ModLayout.None)
        {
            summary.Add($"The OpenVersus here ({live.Channel}) is kept as it is, for turning it on again.");
        }
        var mvsi = setMvsInfiniteAside ? LiveMvsInfinite(state, copy) : [];
        int mvsiPaks = setMvsInfiniteAside ? LivePaths(state, copy).Count(p => p.Home != null) : 0;
        bool mvsiAside = mvsi.Count + mvsiPaks > 0;
        if (mvsiAside)
        {
            summary.Add(MvsInfiniteAsideLine(mvsi.Count, mvsiPaks));
        }
        summary.AddRange(PakLines(state, copy, found.Paks.Select(p => new PakAt(p.Name, p.Path, p.Home))
            .Concat(mvsiAside ? [] : LivePaths(state, copy).Where(p => p.Home != null))));
        summary.AddRange(found.Lines);
        summary.Add(UndoLine(state, "Undo puts everything back as it is now"));
        // A loader the copy keeps (one was here at first sight) is taken out only when the state brings one back.
        bool bringsLoader = owned.Any(p => IsLoaderPath(copy, p) && p != copy.Prefix + Installer.LicenseName);
        bool keepLoaders = KeepsLoaders(state, copy) && !bringsLoader;
        // A state that brings no loader back, on a copy where none stays: nothing would load OpenVersus. The pinned loader
        // is added, as an install adds it, and said; undo takes it out again with the rest.
        var added = new List<TreeEdit>();
        string? addedDll = null;
        string? loaderRisk = null;
        if (!bringsLoader && !(keepLoaders && state.Loaders.Any(l => l.IsUltimateAsiLoader)))
        {
            if (FreeLoaderName(state) is string name)
            {
                try
                {
                    var (dll, license) = cache.PinnedLoader(progress, cancellationToken);
                    added.Add(new Content(copy.Prefix + name, dll));
                    added.Add(new Content(copy.Prefix + Installer.LicenseName, license));
                    addedDll = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
                    summary.Add($"Ultimate ASI Loader {pin.Version} is added as {name}: nothing here would load OpenVersus otherwise.");
                }
                catch (DownloadException e)
                {
                    summary.Add($"Ultimate ASI Loader could not be downloaded ({e.Message}), so nothing here will load OpenVersus.");
                    loaderRisk = NoLoaderRisk;
                }
            }
            else
            {
                summary.Add("Nothing here will load OpenVersus: version.dll and xinput1_3.dll are both another program's files, so Ultimate ASI Loader cannot take either name.");
                loaderRisk = NoLoaderRisk;
            }
        }
        bool setWine = addedDll != null && state.WineRegistry is string registry && !WineRegistry.LoadsNative(File.ReadAllText(registry), addedDll);
        if (setWine)
        {
            summary.AddRange(WineLines(state, [addedDll!]));
        }
        string? risk = string.Join(" ", new[] { RiskOf(found.Missing, $"OpenVersus {channel}", InstallAgainFix), loaderRisk }.OfType<string>()) is { Length: > 0 } both ? both : null;
        return new PreparedChange(summary, null, null, report =>
        {
            Change(report, state.Game,
                mvsiAside ? $"before switching to OpenVersus {channel}" : $"before OpenVersus {channel} was turned on",
                mvsiAside ? $"Switched to OpenVersus {channel}" : $"OpenVersus {channel}, turned on", null,
                // MVS Infinite's paks stay where they are, unless it is set aside: then they leave as its set.
                (_, live) => [.. found.Paks, .. live.Where(p => p.Home != null && !mvsiAside).Select(p => p.InPlace)],
                (store, now, before) => store.Edit(before, OpenVersusOwned(now, copy).Where(p => !(keepLoaders && IsLoaderPath(copy, p))).Concat(mvsi)
                    .Select(p => (TreeEdit)new Removal(p)).Concat(Grafts(store, lastTree, owned)).Concat(added)),
                (store, before) =>
                {
                    SetMvsInfiniteAside(store, before, mvsi);
                    if (current?.Paks != null)
                    {
                        PruneTesting(store, current.Paks);
                    }
                },
                mvsi);
            if (setWine)
            {
                try
                {
                    SetWineNative(state.WineRegistry!, [addedDll!]);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    throw new ChangeException($"OpenVersus is on, but Wine's setting could not be written ({e.Message}); the game's screen offers to set it.", e);
                }
            }
        }, risk);
    }

    private const string NoLoaderRisk = "Without Ultimate ASI Loader, OpenVersus will not load at all: the game runs as if it were not there. To fix that afterwards, install OpenVersus again.";

    // The name the platform's loader takes, or the other when another program's file holds it; null when both are taken.
    private string? FreeLoaderName(InstallState state)
    {
        bool Taken(string name) => state.Loaders.Any(l => string.Equals(l.FileName, name, StringComparison.OrdinalIgnoreCase) && !l.IsUltimateAsiLoader);
        string first = host.IsWindows ? "xinput1_3.dll" : "version.dll";
        string other = first == "version.dll" ? "xinput1_3.dll" : "version.dll";
        return !Taken(first) ? first : !Taken(other) ? other : null;
    }

    // Testing's set holds nothing but current paks once Testing is live with them, apart from what the one undo point
    // needs: a pak pulled from the release, or folded into another, is deleted from it rather than kept for switching
    // back, as soon as the undo of this change no longer needs it.
    private void PruneTesting(GitStore store, IEnumerable<StagedPak> current)
    {
        // Runs after `previous` is this change's own undo point, so what that undo needs is what is kept.
        var undo = store.Tip(PreviousBranch) is string previous ? PaksIn(store.Describe(previous).Message).Where(p => p.Home == null) : [];
        try
        {
            paks.Prune(ReleaseCache.BranchOf(Channel.Testing), current.Where(p => p.Home == null).Select(p => new PakEntry(p.Name, p.Size, p.Sha256, p.Url)).Concat(undo));
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            // The change is made; a stale pak left in MVS Buddy's own folder is not worth a failure over it.
            log.LogWarning(e, "The testing set was not pruned");
        }
    }

    /// <summary>
    /// Gets switching from OpenVersus to MVS Infinite, which is set aside, ready: OpenVersus set aside as its channel's
    /// kept state (as by <see cref="PrepareTurnOff"/>), and MVS Infinite's plugins and paks put back in the same change.
    /// The loader stays: MVS Infinite runs on any build, so the copy it was set aside with stays out when one is live.
    /// </summary>
    public PreparedChange PrepareSwitchToMvsInfinite(InstallState state, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (state == null)
        {
            throw new ArgumentNullException(nameof(state));
        }
        var copy = Layout(state.Game);
        string? tip = LibGit2Sharp.Repository.IsValid(copy.GitDir) ? Open(copy).Tip(MvsInfiniteBranch) : null;
        var wanted = paks.Read(MvsInfinitePaks);
        if (tip == null && wanted.Count == 0)
        {
            const string nothing = "Nothing of MVS Infinite is set aside for this copy of the game.";
            return new PreparedChange([nothing], null, nothing, _ => throw new ChangeException(nothing));
        }
        var store = Open(copy);
        string? tree = tip == null ? null : store.TreeOf(tip);
        bool loaderLive = state.Loaders.Any(l => l.IsUltimateAsiLoader);
        var paths = tree == null ? [] : store.Paths(tree).Where(p => !(loaderLive && IsLoaderPath(copy, p))).ToList();
        var found = Find(state, copy, wanted, null, progress, cancellationToken);
        var ovsPaths = WhatGoes(state, copy, openVersus: true, mvsInfinite: false, others: [], onlyOurLoader: true, keepLoaders: true).OpenVersus;

        var summary = new List<string>();
        if (state.Channel is { } live && state.Mod.Layout != ModLayout.None)
        {
            summary.Add($"OpenVersus {live.Channel}: set aside; \"Switch to OpenVersus {live.Channel}\" brings it back as it is now, with its paks.");
        }
        summary.Add($"MVS Infinite: {paths.Count} {Files(paths.Count)}{(found.Paks.Count > 0 ? $" and {found.Paks.Count} {Paks(found.Paks.Count)}" : "")} go back where they were.");
        summary.AddRange(found.Lines);
        if (loaderLive)
        {
            summary.Add("Ultimate ASI Loader stays: MVS Infinite loads through it.");
        }
        if (state.Mod.Files.Any(f => f.Contains(Path.DirectorySeparatorChar + "logs" + Path.DirectorySeparatorChar)))
        {
            summary.Add("The mod's logs stay in plugins/OpenVersus/logs: they are never part of a state.");
        }
        summary.Add(UndoLine(state, "Undo puts everything back as it is now"));
        log.LogInformation("Switch to MVS Infinite prepared for {Folder}: {Summary}", copy.WorkTree, string.Join(" | ", summary));
        return new PreparedChange(summary, null, null, report => Change(report, state.Game, "before switching to MVS Infinite", "Switched to MVS Infinite", null,
            // OpenVersus's paks leave as its channel's set; MVS Infinite's come back.
            (_, live) => [.. found.Paks, .. live.Where(p => p.Home != null).Select(p => p.InPlace)],
            (store, _, before) => store.Edit(before, ovsPaths.Select(p => (TreeEdit)new Removal(p)).Concat(tree == null ? [] : Grafts(store, tree, paths))),
            (store, _) => store.DeleteBranch(MvsInfiniteBranch),
            paths), RiskOf(found.Missing, "MVS Infinite", MvsInfiniteFix));
    }

    // MVS Infinite's plugins live here, as the store names them. Its paks (by their names, or as the person said) are the
    // live paks with a home in the game's folder; either is MVS Infinite being here, which OpenVersus cannot run beside.
    private static List<string> LiveMvsInfinite(InstallState state, CopyLayout copy) =>
        state.OtherPlugins.Where(p => p.IsMvsInfinite).Select(p => Relative(copy, p.Path)).OfType<string>().ToList();

    private static string MvsInfiniteAsideLine(int files, int paks)
    {
        string what = files > 0 && paks > 0 ? $"{files} {Files(files)} and {paks} {Paks(paks)}" : files > 0 ? $"{files} {Files(files)}" : $"{paks} {Paks(paks)}";
        return $"MVS Infinite ({what}) is set aside, since it cannot run with OpenVersus; \"Switch to MVS Infinite\" brings it back.";
    }

    /// <summary>Where the person's answers about a copy's paks are kept: which are MVS Infinite's and which are not.</summary>
    internal static string PakAnswersFile(HostSystem host, string win64) =>
        Path.Combine(host.DataDirectory, StatesFolder, CopyKey(host.RealPath(Path.GetFullPath(win64).TrimEnd(Path.DirectorySeparatorChar)), host.IsWindows) + ".mvsi-paks");

    /// <summary>
    /// Writes down, for <paramref name="game"/>'s copy, which paks are MVS Infinite's (<paramref name="theirs"/>) and which
    /// are not (<paramref name="notTheirs"/>), by name, as the person says. From then on the first move with MVS Infinite
    /// as its own paks do, and neither is asked about again.
    /// </summary>
    public void AnswerMvsInfinitePaks(GameInstall game, IEnumerable<string> theirs, IEnumerable<string> notTheirs)
    {
        string file = PakAnswersFile(host, (game ?? throw new ArgumentNullException(nameof(game))).Win64Directory);
        var answers = InstallReader.PakAnswers(file);
        foreach (string pak in theirs)
        {
            answers[InstallReader.PakKey(pak)] = true;
        }
        foreach (string pak in notTheirs)
        {
            answers[InstallReader.PakKey(pak)] = false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllLines(file, answers.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => $"{(a.Value ? "mvsi" : "other")}\t{a.Key}"));
    }

    // MVS Infinite's plugins, as they were before the change, as the state set aside; one set aside before is replaced whole.
    private static void SetMvsInfiniteAside(GitStore store, string before, IReadOnlyList<string> plugins)
    {
        if (plugins.Count > 0)
        {
            store.Commit(MvsInfiniteBranch, store.Edit(null, Grafts(store, before, plugins)), "MVS Infinite, set aside");
        }
    }

    /// <summary>
    /// Gets putting back what is set aside as <paramref name="kind"/> ready: MVS Infinite (its plugins and paks) or
    /// everything else a clean start set aside. Once it is back, nothing is set aside as that any more; undo sets it aside
    /// again. For OpenVersus, see <see cref="PrepareTurnOn"/>.
    /// </summary>
    public PreparedChange PreparePutBack(InstallState state, SetAsideKind kind, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (state == null)
        {
            throw new ArgumentNullException(nameof(state));
        }
        if (kind == SetAsideKind.OpenVersus)
        {
            throw new ArgumentException("OpenVersus comes back with PrepareTurnOn.", nameof(kind));
        }
        var copy = Layout(state.Game);
        string branch = kind == SetAsideKind.MvsInfinite ? MvsInfiniteBranch : CleanStartBranch;
        string what = kind == SetAsideKind.MvsInfinite ? "MVS Infinite" : "What a clean start set aside";
        // Mid-sentence, as an undo quotes it.
        string phrase = kind == SetAsideKind.MvsInfinite ? what : "what a clean start set aside";
        string? tip = LibGit2Sharp.Repository.IsValid(copy.GitDir) ? Open(copy).Tip(branch) : null;
        var store = Open(copy);
        var wanted = kind == SetAsideKind.MvsInfinite ? paks.Read(MvsInfinitePaks) : [];
        if (tip == null && wanted.Count == 0)
        {
            string nothing = $"Nothing is set aside as {phrase} for this copy of the game.";
            return new PreparedChange([nothing], null, nothing, _ => throw new ChangeException(nothing));
        }
        string? tree = tip == null ? null : store.TreeOf(tip);
        var paths = tree == null ? [] : store.Paths(tree);
        var found = Find(state, copy, wanted, null, progress, cancellationToken);
        var summary = new List<string> { $"{what}: {paths.Count} {Files(paths.Count)}{(found.Paks.Count > 0 ? $" and {found.Paks.Count} {Paks(found.Paks.Count)}" : "")} go back where they were." };
        summary.AddRange(found.Lines);
        summary.Add("Undo sets them aside again.");
        // Safe to forget the state once they are back: the files are then tracked by the live state, so undo's snapshot has them.
        return new PreparedChange(summary, null, null, report => Change(report, state.Game, $"before {phrase} was put back", $"{what}, put back", null,
            (_, live) => [.. live.Select(p => p.InPlace), .. found.Paks],
            (store, _, before) => tree == null ? before : store.Edit(before, Grafts(store, tree, paths)),
            (store, _) => store.DeleteBranch(branch),
            paths), RiskOf(found.Missing, what, MvsInfiniteFix));
    }

    /// <summary>
    /// Deletes what is set aside as <paramref name="kind"/> for good (for OpenVersus, <paramref name="channel"/>'s state; for
    /// MVS Infinite, its plugins and paks). The game's files are not touched. When the last change was the one that set it
    /// aside, undoing that change still puts its files back in the game's folder; until the next change, so its space is
    /// freed only then. An OpenVersus channel's paks are kept (the channel's set): installing or putting it back does not
    /// download them again. Only an uninstall deletes MVS Buddy's pak sets.
    /// </summary>
    public void Discard(GameInstall game, SetAsideKind kind, Channel? channel = null)
    {
        var copy = Layout(game ?? throw new ArgumentNullException(nameof(game)));
        if (!LibGit2Sharp.Repository.IsValid(copy.GitDir))
        {
            return;
        }
        var store = Open(copy);
        string set = kind == SetAsideKind.MvsInfinite ? MvsInfinitePaks : "";
        string branch = kind switch
        {
            SetAsideKind.MvsInfinite => MvsInfiniteBranch,
            SetAsideKind.OpenVersus => ReleaseCache.BranchOf(channel ?? throw new ArgumentNullException(nameof(channel))),
            _ => CleanStartBranch,
        };
        store.DeleteBranch(branch);
        // For good: undo does not bring it back as set aside either.
        store.DeleteBranch(PreviousOf + branch);
        if (set.Length > 0 && Directory.Exists(paks.Folder(set)))
        {
            Directory.Delete(paks.Folder(set), recursive: true);
        }
        store.Prune();
    }

    /// <summary>When MVS Buddy first changed <paramref name="game"/>'s copy, which a roll-back goes back to; null when it has no first look of it.</summary>
    public DateTimeOffset? FirstLookOf(GameInstall game)
    {
        var copy = Layout(game ?? throw new ArgumentNullException(nameof(game)));
        return LibGit2Sharp.Repository.IsValid(copy.GitDir) && Open(copy).Tip(InitialBranch) is string initial ? Open(copy).Describe(initial).At : null;
    }

    /// <summary>
    /// Gets rolling back to how this copy was the first time MVS Buddy changed it ready: the files it had then come back as
    /// they were, with their paks (found by checksum, never downloaded); the mod's files, the loader and its license that
    /// were not here then go; other files added since stay. What a clean start had set aside of it is back, so it is not
    /// set aside any more. An ordinary change: undo puts everything back as it is now.
    /// </summary>
    public PreparedChange PrepareRollBack(InstallState state, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (state == null)
        {
            throw new ArgumentNullException(nameof(state));
        }
        var copy = Layout(state.Game);
        string? initial = LibGit2Sharp.Repository.IsValid(copy.GitDir) ? Open(copy).Tip(InitialBranch) : null;
        if (initial == null)
        {
            const string nothing = "MVS Buddy has no first look at this copy to roll back to: it began keeping one after this copy was first changed.";
            return new PreparedChange([nothing], null, nothing, _ => throw new ChangeException(nothing));
        }
        var store = Open(copy);
        var (message, at) = store.Describe(initial);
        string tree = store.TreeOf(initial);
        var paths = store.Paths(tree);
        var had = new HashSet<string>(paths, StringComparer.Ordinal);
        var found = Find(state, copy, PaksIn(message), RecordIn(store, copy, tree), progress, cancellationToken);
        var foundPaths = new HashSet<string>(found.Paks.Select(p => p.Path), host.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var asideNow = SetBranches.Select(store.Tip).OfType<string>().SelectMany(t => store.Paths(store.TreeOf(t)));

        var summary = new List<string> { $"Puts back the {paths.Count} {Files(paths.Count)} this copy had the first time MVS Buddy changed it, on {at.LocalDateTime:yyyy-MM-dd HH:mm}." };
        if (message.Split('\n').Contains(CoversLine + "\trecognised"))
        {
            summary.Add("The game's manifests were missing then, so that is only what MVS Buddy recognised: the mod, loaders and other plugins.");
        }
        summary.Add("The mod's files, the loader and its license that were not here then go.");
        summary.AddRange(PakLines(state, copy, found.Paks.Select(p => new PakAt(p.Name, p.Path, p.Home))
            .Concat(LivePaths(state, copy).Where(p => p.Home != null && !foundPaths.Contains(p.Path)))));
        summary.AddRange(found.Lines);
        if (asideNow.Any(had.Contains))
        {
            summary.Add("What a clean start set aside of it comes back, so it is not set aside any more.");
        }
        summary.Add("Other files added since stay.");
        summary.Add(UndoLine(state, "Undo puts everything back as it is now"));
        log.LogInformation("Roll back prepared for {Folder}: {Summary}", copy.WorkTree, string.Join(" | ", summary));

        string? risk = RiskOf(found.Missing, "the game as it was then", "To fix that afterwards, put the paks back by hand where they were, or install OpenVersus again.");
        return new PreparedChange(summary, null, null, report => Change(report, state.Game, "before a roll back to the first look", "Rolled back to the first look", null,
            // Its paks; MVS Infinite's added since stay where they are.
            (_, live) => [.. found.Paks, .. live.Where(p => p.Home != null && !foundPaths.Contains(p.Path)).Select(p => p.InPlace)],
            (store, now, before) => store.Edit(before, OpenVersusOwned(now, copy).Select(p => (TreeEdit)new Removal(p)).Concat(Grafts(store, tree, paths))),
            (store, _) =>
            {
                // What is back is not set aside any more; undo sets it aside again.
                foreach (string branch in SetBranches)
                {
                    if (store.Tip(branch) is string tip)
                    {
                        string held = store.TreeOf(tip);
                        var back = store.Paths(held).Where(had.Contains).ToList();
                        if (back.Count == store.Paths(held).Count)
                        {
                            store.DeleteBranch(branch);
                        }
                        else if (back.Count > 0)
                        {
                            store.Commit(branch, store.Edit(held, back.Select(p => (TreeEdit)new Removal(p))), "Set aside, less what a roll back brought back");
                        }
                    }
                }
            },
            // Its files that are here now untracked (MVS Infinite's plugin) are recorded first, so they are not in the way and undo has them.
            paths), risk);
    }

    /// <summary>What is set aside for <paramref name="state"/>'s copy, and can be put back or turned on.</summary>
    public IReadOnlyList<SetAside> SetAsideOf(InstallState state)
    {
        var copy = Layout((state ?? throw new ArgumentNullException(nameof(state))).Game);
        if (!LibGit2Sharp.Repository.IsValid(copy.GitDir))
        {
            return [];
        }
        var store = Open(copy);
        var sets = new List<SetAside>();
        foreach (var channel in new[] { Channel.Prod, Channel.Testing })
        {
            bool live = state.Mod.Layout != ModLayout.None && state.Channel?.Channel == channel;
            if (!live && store.Tip(ReleaseCache.BranchOf(channel)) is string tip && store.Has(store.TreeOf(tip), copy.Prefix + Home.TrimEnd('/')))
            {
                var owned = store.Paths(store.TreeOf(tip)).Where(p => IsOpenVersusOwned(copy, p)).ToList();
                sets.Add(new SetAside(SetAsideKind.OpenVersus, channel, owned.Count, 0, store.Describe(tip).At));
            }
        }
        var mvsiPaks = paks.Read(MvsInfinitePaks);
        if (store.Tip(MvsInfiniteBranch) is string mvsi || mvsiPaks.Count > 0)
        {
            var (files, bytes) = store.Tip(MvsInfiniteBranch) is string m ? store.Size(store.TreeOf(m)) : (0, 0L);
            sets.Add(new SetAside(SetAsideKind.MvsInfinite, null, files + mvsiPaks.Count, bytes + mvsiPaks.Sum(p => p.Size),
                store.Tip(MvsInfiniteBranch) is string t ? store.Describe(t).At : DateTimeOffset.MinValue));
        }
        if (store.Tip(CleanStartBranch) is string rest)
        {
            var (files, bytes) = store.Size(store.TreeOf(rest));
            sets.Add(new SetAside(SetAsideKind.Rest, null, files, bytes, store.Describe(rest).At));
        }
        return sets;
    }

    // The one way every change is made. Nothing in the game changes before the snapshot, the conflicts and the
    // permissions are all checked; `previous` moves only once the files and the paks are both in place. paksAfter: the
    // paks the target has, from the read made now and the paks live with it. after: what the change keeps once it is made
    // (a branch of what it set aside, from the state before). extraRoots: paths the snapshot must hold besides the usual
    // ones, so that undo puts them back.
    private void Change(IProgress<string>? progress, GameInstall game, string beforeWhat, string title, string? refused,
        Func<InstallState, IReadOnlyList<LivePak>, IReadOnlyList<StagedPak>> paksAfter, Func<GitStore, InstallState, string, string> target,
        Action<GitStore, string>? after = null, IEnumerable<string>? extraRoots = null, bool restoresSets = false)
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
        var clock = System.Diagnostics.Stopwatch.StartNew();
        progress?.Report("Reading the game's folder...");
        var now = installs.Read(game);
        var live = LivePaks(now, copy);
        var store = Open(copy);
        var sets = SetBranches.ToDictionary(b => b, store.Tip);
        var previousSets = SetBranches.ToDictionary(b => b, b => store.Tip(PreviousOf + b));
        log.LogInformation("{Title}: read in {Seconds:F1} s", title, clock.Elapsed.TotalSeconds);
        // The first change of this copy: no state yet. Kept even if this change is then refused, since nothing has moved.
        if (store.Tip(CurrentBranch) == null && store.Tip(InitialBranch) == null)
        {
            progress?.Report(FirstLookPhase);
            MakeInitial(store, copy, now, live, progress);
            log.LogInformation("{Title}: first look kept in {Seconds:F1} s", title, clock.Elapsed.TotalSeconds);
        }
        progress?.Report(SavingPhase);
        string before = store.Snapshot(Roots(now, copy).Concat(extraRoots ?? []), Message(beforeWhat, live), progress, SavingPhase);
        string beforeTree = store.TreeOf(before);
        if (now.Channel is { } channel)
        {
            store.SetBranch(ReleaseCache.BranchOf(channel.Channel), before);
        }
        log.LogInformation("{Title}: snapshot in {Seconds:F1} s", title, clock.Elapsed.TotalSeconds);
        progress?.Report("Checking that nothing is in the way...");
        string targetTree = target(store, now, beforeTree);
        log.LogInformation("{Title}: target in {Seconds:F1} s", title, clock.Elapsed.TotalSeconds);

        var diff = store.Diff(beforeTree, targetTree);
        var inTheWay = diff.Where(d => d.Change == PathChange.Added).Select(d => Full(copy, d.Path)).Where(File.Exists).ToList();
        if (inTheWay.Count > 0)
        {
            throw new ChangeException(inTheWay.Count == 1
                ? $"{inTheWay[0]} is already there and is not part of anything MVS Buddy keeps, so it is not replaced. Move it away, then try again."
                : $"These are already there and are not part of anything MVS Buddy keeps, so they are not replaced: {string.Join(", ", inTheWay)}. Move them away, then try again.");
        }
        var pakPlan = PakPlan(now, copy, live, paksAfter(now, live));
        foreach (var (path, _) in diff)
        {
            pakPlan.Writes(Path.GetDirectoryName(Full(copy, path))!);
        }
        pakPlan.Check();
        log.LogInformation("{Title}: checked in {Seconds:F1} s", title, clock.Elapsed.TotalSeconds);
        progress?.Report(ChangingPhase);

        store.Switch(targetTree, title, progress, ChangingPhase);
        log.LogInformation("{Title}: switched in {Seconds:F1} s", title, clock.Elapsed.TotalSeconds);
        progress?.Report(PaksPhase);
        try
        {
            pakPlan.Apply(progress, PaksPhase);
        }
        catch (ChangeException e)
        {
            progress?.Report(GitStore.PuttingBack);
            try
            {
                store.Switch(beforeTree, "Put back: " + title, progress, GitStore.PuttingBack);
            }
            catch (ChangeException back)
            {
                throw new ChangeException($"{e.Message} The game's files could not all be put back either: {back.Message}", e);
            }
            throw;
        }
        store.SetBranch(PreviousBranch, before);
        foreach (var set in sets)
        {
            Point(store, PreviousOf + set.Key, set.Value);
        }
        if (restoresSets)
        {
            foreach (var set in previousSets)
            {
                Point(store, set.Key, set.Value);
            }
        }
        progress?.Report("Tidying up MVS Buddy's folder...");
        after?.Invoke(store, beforeTree);
        store.Prune();
        Retain(progress);
        log.LogInformation("{Title}: done for {Folder} in {Seconds:F1} s", title, copy.WorkTree, clock.Elapsed.TotalSeconds);
    }

    // `initial`: the copy before MVS Buddy first changes it, for rolling back to and for the rule that a loader there then is
    // never taken away. What the game did not ship (StockCheck's extras); without the game's manifests, what MVS Buddy
    // recognises (the mod, the loaders and the license, other plugins), and the message says so. Made through an index of its
    // own, so the states do not start tracking these files. The paks of the mod and MVS Infinite are lines by checksum, never
    // in git; the loaders are lines too.
    private void MakeInitial(GitStore store, CopyLayout copy, InstallState now, IReadOnlyList<LivePak> live, IProgress<string>? progress)
    {
        var report = stock.Read(now);
        var comparer = host.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var paks = new HashSet<string>(live.Select(p => p.Path), comparer);
        var files = report.HasManifests
            ? report.Extras.Select(e => e.Path)
            : now.Mod.Files.Concat(now.Mod.OldLayoutFiles).Concat(now.Loaders.Select(l => l.Path)).Concat(now.OtherPlugins.Select(p => p.Path))
                .Append(Path.Combine(now.Game.Win64Directory, Installer.LicenseName));
        var paths = files.Where(f => !paks.Contains(f) && File.Exists(f)).Select(f => Relative(copy, f)).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        string lines = (report.HasManifests ? "" : $"{CoversLine}\trecognised\n")
            + string.Concat(now.Loaders.Where(l => l.IsUltimateAsiLoader).Select(l => $"{LoaderLine}\t{l.FileName}\t{l.Sha256}\n"));
        string message = Message("How this copy was the first time MVS Buddy changed it", live);
        int split = message.IndexOf("\n\n", StringComparison.Ordinal) + 2;
        store.Commit(InitialBranch, store.TreeOfFiles(paths, progress, FirstLookPhase), message.Substring(0, split) + lines + message.Substring(split));
    }

    // Whether the copy keeps its loaders whatever else goes: one was there the first time MVS Buddy saw it. That
    // is `initial`'s loader lines; before the first change, what is there now; for a copy whose states are older than
    // `initial`, no: the install record's rule alone.
    private bool KeepsLoaders(InstallState state, CopyLayout copy)
    {
        bool now = state.Loaders.Any(l => l.IsUltimateAsiLoader);
        if (!LibGit2Sharp.Repository.IsValid(copy.GitDir))
        {
            return now;
        }
        var store = Open(copy);
        if (store.Tip(InitialBranch) is string initial)
        {
            return store.Describe(initial).Message.Split('\n').Any(l => l.StartsWith(LoaderLine + "\t", StringComparison.Ordinal));
        }
        return store.Tip(CurrentBranch) == null && now;
    }

    // A loader beside the game, or its license, as the store names them.
    private static bool IsLoaderPath(CopyLayout copy, string path) =>
        path == copy.Prefix + Installer.LicenseName
        || (path.StartsWith(copy.Prefix, StringComparison.Ordinal) && path.IndexOf('/', copy.Prefix.Length) < 0
            && InstallReader.ProxyNames.Contains(path.Substring(copy.Prefix.Length), StringComparer.OrdinalIgnoreCase));

    /// <summary>The oldest release MVS Buddy installs; an older one is refused before anything is downloaded.</summary>
    internal static readonly Version OldestInstallable = new(2026, 10, 1, 0);

    // Older than OldestInstallable, with a missing part read as 0 (Version counts 2026.10.1 as older than 2026.10.1.0). A
    // release without a version is not known to be older.
    internal static bool IsOlderThanInstallable(Version? version) => version != null
        && new Version(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0)) < OldestInstallable;

    // After a change, the release cache keeps what the copies use and the newest of each channel. A failure here leaves
    // releases cached and nothing worse, so the change stands.
    private void Retain(IProgress<string>? progress)
    {
        try
        {
            if (TagsInUse() is { } inUse)
            {
                progress?.Report("Tidying the release cache...");
                cache.Retain(inUse);
            }
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is LibGit2Sharp.LibGit2SharpException || e is ChangeException)
        {
            log.LogWarning(e, "The release cache was not tidied");
        }
    }

    // The releases the copies of the game have installed or keep, as the install records in their states name them: every
    // copy, every state that can hold OpenVersus. Null when a copy's states cannot be read, so nothing goes on a partial count.
    private HashSet<string>? TagsInUse()
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        string folder = Path.Combine(host.DataDirectory, StatesFolder);
        if (!Directory.Exists(folder))
        {
            return tags;
        }
        foreach (string gitDir in Directory.EnumerateDirectories(folder, "*.git"))
        {
            if (!LibGit2Sharp.Repository.IsValid(gitDir))
            {
                log.LogWarning("The release cache is not tidied: {Folder} cannot be read", gitDir);
                return null;
            }
            var store = GitStore.Open(gitDir, null, [], log);
            foreach (string branch in new[] { CurrentBranch, PreviousBranch, ReleaseCache.BranchOf(Channel.Prod), ReleaseCache.BranchOf(Channel.Testing), InitialBranch })
            {
                if (store.Tip(branch) is not string tip)
                {
                    continue;
                }
                string tree = store.TreeOf(tip);
                // The record's place: in a recognised install's Win64, or at the top of a folder the person chose.
                foreach (string prefix in new[] { "MultiVersus/Binaries/Win64/", "" })
                {
                    if (store.Read(tree, prefix + Home + InstallRecord.FileName) is byte[] json && InstallRecord.FromJson(json)?.Tag is string tag)
                    {
                        tags.Add(tag);
                    }
                }
            }
        }
        return tags;
    }

    private static void Point(GitStore store, string branch, string? commit)
    {
        if (commit == null)
        {
            store.DeleteBranch(branch);
        }
        else
        {
            store.SetBranch(branch, commit);
        }
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

    // The paks, as they will be: the target's where each belongs (the mod's in the profile, MVS Infinite's in the game's
    // folder; those already there and the same stay put), and the ones live now that the target does not use kept as a
    // set, replacing that set's older paks: the mod's as the set of the channel live now, MVS Infinite's as its own.
    // Incoming paks are set aside in MVS Buddy's folder first, since one may have a leaving pak's name or come from the very
    // set the leaving ones replace.
    private ChangePlan PakPlan(InstallState now, CopyLayout copy, IReadOnlyList<LivePak> live, IReadOnlyList<StagedPak> target)
    {
        var plan = new ChangePlan(host, log);
        var comparer = host.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        string? Destination(string? home) => home == null ? now.SavedPaksDirectory : Full(copy, home);
        // Without the profile's folder (a prefix not known), the mod's paks are left as they are.
        target = target.Where(p => Destination(p.Home) != null).ToList();
        var inPlace = new HashSet<string>(target.Where(p => comparer.Equals(p.Path, Path.Combine(Destination(p.Home)!, p.Name))).Select(p => p.Path), comparer);
        var incoming = target.Where(p => !inPlace.Contains(p.Path)).ToList();
        var taken = new HashSet<string>(incoming.Select(p => p.Path), comparer);
        // A hand-placed pak the target uses comes in like any other; it does not also leave.
        var leaving = live.Where(p => Destination(p.Home) != null && !inPlace.Contains(p.Path) && !taken.Contains(p.Path)).ToList();
        if (incoming.Count + leaving.Count == 0)
        {
            return plan;
        }
        string run = Path.Combine(paks.Root, ".change-" + Guid.NewGuid().ToString("N"));
        string old = Path.Combine(run, "old");
        for (int i = 0; i < incoming.Count; i++)
        {
            plan.Move(incoming[i].Path, Path.Combine(run, "in", $"{i:000}-{incoming[i].Name}"));
        }
        string channelSet = now.Channel is { } channel ? ReleaseCache.BranchOf(channel.Channel) : OtherPaks;
        var replaced = leaving.GroupBy(p => p.Home == null ? channelSet : MvsInfinitePaks).ToList();
        var replacedFolders = new HashSet<string>(replaced.Select(g => paks.Folder(g.Key)), comparer);
        // A set the incoming paks came from, other than one being replaced, no longer holds them.
        foreach (var from in incoming.Select(p => Path.GetDirectoryName(p.Path)!).Distinct(comparer)
            .Where(f => comparer.Equals(Path.GetDirectoryName(f), paks.Root.TrimEnd(Path.DirectorySeparatorChar)) && !replacedFolders.Contains(f)))
        {
            string manifest = Path.Combine(from, PakStore.ManifestFile);
            var left = paks.Read(Path.GetFileName(from)).Where(e => !taken.Contains(Path.Combine(from, e.Name))).ToList();
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
        var urls = now.Record?.Paks.Where(p => p.Url != null).GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Url, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in replaced)
        {
            string setFolder = paks.Folder(group.Key);
            var older = Directory.Exists(setFolder) ? Directory.EnumerateFiles(setFolder).Where(f => !taken.Contains(f)).ToList() : [];
            foreach (string file in older)
            {
                plan.Move(file, Path.Combine(old, $"{setAside++:000}-{Path.GetFileName(file)}"));
            }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entries = new List<PakEntry>();
            foreach (var pak in group)
            {
                // The same name twice (in the profile and in the game's folder): the profile's is the one the game used.
                bool first = names.Add(pak.Name);
                plan.Move(pak.Path, first ? Path.Combine(setFolder, pak.Name) : Path.Combine(old, $"{setAside++:000}-{pak.Name}"));
                if (first)
                {
                    entries.Add(new PakEntry(pak.Name, pak.Size, pak.Sha256, pak.Home == null && urls.TryGetValue(pak.Name, out var url) ? url : null) { Home = pak.Home });
                }
                if (!comparer.Equals(Path.GetDirectoryName(pak.Path), now.SavedPaksDirectory))
                {
                    plan.CleanUp(Path.GetDirectoryName(pak.Path)!, StopAt(now, pak.Path));
                }
            }
            plan.Write(Path.Combine(setFolder, PakStore.ManifestFile), PakStore.Manifest(entries));
        }
        for (int i = 0; i < incoming.Count; i++)
        {
            var pak = incoming[i];
            plan.Move(Path.Combine(run, "in", $"{i:000}-{pak.Name}"), Path.Combine(Destination(pak.Home)!, pak.Name));
            if (pak.Downloaded)
            {
                plan.CleanUp(Path.GetDirectoryName(pak.Path)!, Path.Combine(host.DataDirectory, "downloads"));
            }
            else if (!comparer.Equals(Path.GetDirectoryName(pak.Path), now.SavedPaksDirectory) && !pak.Path.StartsWith(paks.Root, StringComparison.Ordinal))
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

    // Where a pak may already be: the profile first (so one there stays put), then where MVS Infinite's are (likewise),
    // then MVS Buddy's sets, then where hand-placed ones are.
    private List<string> PakSources(InstallState state, CopyLayout copy, IEnumerable<string?> homes)
    {
        var sources = new List<string>();
        if (state.SavedPaksDirectory != null)
        {
            sources.Add(state.SavedPaksDirectory);
        }
        sources.AddRange(homes.OfType<string>().Concat(LivePaths(state, copy).Select(p => p.Home).OfType<string>()).Distinct(StringComparer.Ordinal).Select(h => Full(copy, h)));
        sources.AddRange(new[] { ReleaseCache.BranchOf(Channel.Prod), ReleaseCache.BranchOf(Channel.Testing), OtherPaks, MvsInfinitePaks }.Select(paks.Folder));
        sources.AddRange(state.PaksInGame.Select(p => Path.GetDirectoryName(p.Path)!).Distinct());
        return sources;
    }

    /// <summary>The paks a state had, as a change gets them: found or downloaded again (<paramref name="Paks"/>), or neither (<paramref name="Missing"/>); a line each for the ones not simply found.</summary>
    private sealed record PaksFound(IReadOnlyList<StagedPak> Paks, IReadOnlyList<PakEntry> Missing, IReadOnlyList<string> Lines);

    // The paks a state had: found where they are kept now by checksum; downloaded again when not, from where they came
    // (the state's own record, or a set's manifest, names the address); left out when neither, named in Missing. A change
    // goes ahead without them, told and asked twice, never refused: the person's call.
    private PaksFound Find(InstallState state, CopyLayout copy, IEnumerable<PakEntry> wanted, InstallRecord? record, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var list = wanted.Where(p => p.Home != null || state.SavedPaksDirectory != null).ToList();
        var found = paks.AtHand(list, PakSources(state, copy, list.Select(p => p.Home)), out var missing, progress).ToList();
        var still = new List<PakEntry>();
        var lines = new List<string>();
        foreach (var pak in missing)
        {
            string? url = pak.Url ?? UrlOf(pak, record);
            if (url == null)
            {
                still.Add(pak);
                lines.Add($"{pak.Name} is not put back: it is nowhere MVS Buddy keeps paks, and there is no address to download it from again.");
                continue;
            }
            try
            {
                found.Add(paks.Fetch(pak with { Url = url }, progress, cancellationToken));
                lines.Add($"{pak.Name} is downloaded again: it is nowhere MVS Buddy keeps paks.");
            }
            catch (DownloadException e)
            {
                still.Add(pak);
                lines.Add($"{pak.Name} is not put back: it is nowhere MVS Buddy keeps paks, and it could not be downloaded again ({e.Message})");
            }
        }
        return new PaksFound(found, still, lines);
    }

    // Where a pak came from: the record of the state being put back (by checksum, or by name for a record without one),
    // else any set's manifest naming the same bytes.
    private string? UrlOf(PakEntry pak, InstallRecord? record)
    {
        var recorded = record?.Paks.FirstOrDefault(p => p.Url != null && (p.Sha256 != null
            ? string.Equals(p.Sha256, pak.Sha256, StringComparison.OrdinalIgnoreCase)
            : string.Equals(p.Name, pak.Name, StringComparison.OrdinalIgnoreCase)));
        if (recorded?.Url != null)
        {
            return recorded.Url;
        }
        return new[] { ReleaseCache.BranchOf(Channel.Prod), ReleaseCache.BranchOf(Channel.Testing), OtherPaks }
            .SelectMany(paks.Read).FirstOrDefault(p => p.Url != null && string.Equals(p.Sha256, pak.Sha256, StringComparison.OrdinalIgnoreCase))?.Url;
    }

    // The risk of going ahead without paks, said plainly with the way to fix it afterwards; null when none are missing.
    private static string? RiskOf(IReadOnlyList<PakEntry> missing, string what, string fix) => missing.Count == 0 ? null
        : $"Without {string.Join(", ", missing.Select(p => p.Name))}, {what} will probably not work: the game may crash, or run without what "
          + (missing.Count == 1 ? "that pak holds. " : "those paks hold. ") + fix;

    private const string InstallAgainFix = "To fix that afterwards, install OpenVersus again: that downloads its current paks.";

    private const string MvsInfiniteFix = "MVS Buddy cannot download MVS Infinite's paks. To fix that afterwards, get them from where MVS Infinite is distributed and put them in the game's Content/Paks folder.";

    /// <summary>The testing paks as GitHub has them now, staged; null with the reason when GitHub could not say, and the kept ones are used.</summary>
    private sealed record CurrentPaks(IReadOnlyList<StagedPak>? Paks, string Line);

    // Testing is the bleeding edge: the only paks valid for it are the ones on the testing paks release right now, so a
    // testing state going live gets those (each already here by checksum, or downloaded), not the ones it had. Without
    // GitHub, the ones it had, said so: a change is never refused for it.
    private CurrentPaks CurrentTestingPaks(InstallState state, CopyLayout copy, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (state.SavedPaksDirectory == null)
        {
            return new CurrentPaks(null, "");
        }
        progress?.Report("Checking the testing paks against GitHub...");
        try
        {
            var assets = catalog.TestingPaksAsync(cancellationToken).GetAwaiter().GetResult();
            var staged = paks.StageAsync(assets, PakSources(state, copy, []), progress, cancellationToken).GetAwaiter().GetResult();
            int downloaded = staged.Count(p => p.Downloaded);
            return new CurrentPaks(staged, downloaded == 0
                ? "Testing's paks are the current ones on GitHub, all already here."
                : $"Testing's paks are the current ones on GitHub: {downloaded} of {staged.Count} downloaded, the rest already here.");
        }
        catch (Exception e) when (e is ReleaseCatalogException || e is DownloadException)
        {
            log.LogWarning(e, "The current testing paks could not be had; the kept ones are used");
            return new CurrentPaks(null, $"GitHub could not say which paks Testing uses now ({e.Message}), so the ones kept here are used; they may be behind.");
        }
    }

    // Whether Testing's paks on GitHub are not the ones found for a state (any current pak missing from them, or with
    // other bytes); false when GitHub cannot say.
    private bool TestingMovedOn(PaksFound found, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report("Checking the testing paks against GitHub...");
        try
        {
            var current = catalog.TestingPaksAsync(cancellationToken).GetAwaiter().GetResult();
            var had = found.Paks.Where(p => p.Home == null).ToDictionary(p => p.Name, p => p.Sha256, StringComparer.OrdinalIgnoreCase);
            return current.Any(a => a.Sha256 != null && !(had.TryGetValue(a.Name, out var sha) && string.Equals(sha, a.Sha256, StringComparison.OrdinalIgnoreCase)));
        }
        catch (ReleaseCatalogException e)
        {
            log.LogInformation("Whether the testing paks moved on could not be checked: {Reason}", e.Message);
            return false;
        }
    }

    // The paks a testing state goes live with: the current ones when GitHub says, else the ones found for it.
    private PaksFound ForTesting(PaksFound found, CurrentPaks current) => current.Paks == null
        ? found with { Lines = [.. found.Lines, current.Line] }
        : new PaksFound([.. current.Paks, .. found.Paks.Where(p => p.Home != null)], found.Missing.Where(p => p.Home != null).ToList(), [current.Line]);

    // The record the state being put back wrote, when it has one.
    private static InstallRecord? RecordIn(GitStore store, CopyLayout copy, string? tree) =>
        tree != null && store.Read(tree, copy.Prefix + Home + InstallRecord.FileName) is byte[] json ? InstallRecord.FromJson(json) : null;

    /// <summary>The mod's paks (<paramref name="Home"/> null) and MVS Infinite's, where they are now, without reading them.</summary>
    private sealed record PakAt(string Name, string Path, string? Home);

    private static IEnumerable<PakAt> LivePaths(InstallState now, CopyLayout copy) =>
        now.Paks.Concat(now.PaksInGame).Select(p => new PakAt(p.Name, p.Path, null))
            .Concat(now.OtherPaks.Where(p => p.IsMvsInfinite).Select(p => new PakAt(p.Name, p.Path, Relative(copy, Path.GetDirectoryName(p.Path)!))));

    private static List<LivePak> LivePaks(InstallState now, CopyLayout copy) =>
        LivePaths(now, copy).Select(p => new LivePak(p.Name, new FileInfo(p.Path).Length, Downloads.Sha256Of(p.Path), p.Path, p.Home)).ToList();

    // A snapshot's message: what it is the state before, then the paks live with it, a line each (MVS Infinite's with its
    // folder), for an undo to find.
    private static string Message(string what, IEnumerable<LivePak> live) =>
        what + "\n\n" + string.Concat(live.GroupBy(p => (p.Name.ToLowerInvariant(), p.Home)).Select(g => g.First())
            .Select(p => $"{PakLine}\t{p.Name}\t{p.Size}\t{p.Sha256}{(p.Home == null ? "" : "\t" + p.Home)}\n"));

    internal static IReadOnlyList<PakEntry> PaksIn(string message) =>
        message.Split('\n').Select(l => l.Split('\t')).Where(f => (f.Length == 4 || f.Length == 5) && f[0] == PakLine && long.TryParse(f[2], out _))
            .Select(f => new PakEntry(f[1], long.Parse(f[2]), f[3], null) { Home = f.Length == 5 ? f[4] : null }).ToList();

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
            ChannelSource.Install => "MVS Buddy installed it",
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

    private IEnumerable<string> PakLines(InstallState state, CopyLayout copy, IEnumerable<StagedPak> target) =>
        PakLines(state, copy, target.Select(p => new PakAt(p.Name, p.Path, p.Home)));

    private IEnumerable<string> PakLines(InstallState state, CopyLayout copy, IEnumerable<PakAt> targetPaks)
    {
        var target = targetPaks.ToList();
        if (state.SavedPaksDirectory == null && target.Any(p => p.Home == null))
        {
            yield return state.NeedsFirstLaunch
                ? "The paks are not installed: " + InstallReader.FirstLaunchNote
                : "The paks are not installed: MVS Buddy cannot tell which Wine prefix this copy runs in.";
        }
        var comparer = host.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        string? Destination(string? home) => home == null ? state.SavedPaksDirectory : Full(copy, home);
        var mod = target.Where(p => Destination(p.Home) != null).ToList();
        int inPlace = mod.Count(p => comparer.Equals(p.Path, Path.Combine(Destination(p.Home)!, p.Name)));
        if (mod.Count > 0)
        {
            yield return $"Paks: {mod.Count - inPlace} to put in place, {inPlace} already there.";
        }
        var used = new HashSet<string>(target.Select(p => p.Path), comparer);
        var leaving = LivePaths(state, copy).Where(p => Destination(p.Home) != null && !used.Contains(p.Path)).ToList();
        int ovs = leaving.Count(p => p.Home == null);
        if (ovs > 0)
        {
            yield return (ovs == 1 ? "1 pak it does not use is" : $"{ovs} paks it does not use are")
                + (state.Channel is { } channel ? $" kept with {channel.Channel}, for switching back." : " kept aside in MVS Buddy's folder.");
        }
        int mvsi = leaving.Count - ovs;
        if (mvsi > 0)
        {
            yield return $"{mvsi} MVS Infinite {Paks(mvsi)} {(mvsi == 1 ? "is" : "are")} kept aside in MVS Buddy's folder, to put back with it.";
        }
        if (state.PaksInGame.Count > 0)
        {
            yield return $"{state.PaksInGame.Count} OpenVersus paks found in the game's own folders go where the mod keeps them, or with the set they belong to.";
        }
    }

    // What OpenVersus owns in the live state, as the store names it: its folder, an older layout's files, every copy of
    // Ultimate ASI Loader and its license.
    private static List<string> OpenVersusOwned(InstallState now, CopyLayout copy) =>
        new[] { copy.Prefix + Home.TrimEnd('/') }
            .Concat(now.Mod.Files.Concat(now.Mod.OldLayoutFiles).Concat(now.Loaders.Where(l => l.IsUltimateAsiLoader).Select(l => l.Path))
                .Select(f => Relative(copy, f)).OfType<string>())
            .Append(copy.Prefix + Installer.LicenseName).Distinct(StringComparer.Ordinal).ToList();

    // Whether a path a state holds is OpenVersus's to bring back: its folder, the license, a file under a loader's name, an
    // older layout's file beside the game or in plugins/.
    private static bool IsOpenVersusOwned(CopyLayout copy, string path)
    {
        if (path.StartsWith(copy.Prefix + Home, StringComparison.Ordinal) || path == copy.Prefix + Installer.LicenseName)
        {
            return true;
        }
        int slash = path.LastIndexOf('/');
        string folder = slash < 0 ? "" : path.Substring(0, slash + 1);
        string name = path.Substring(slash + 1);
        return (folder == copy.Prefix && InstallReader.ProxyNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            || ((folder == copy.Prefix || folder == copy.Prefix + "plugins/") && InstallReader.OldLayoutNames.Contains(name, StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsOpenVersusPlugin(InstallState state, string path) =>
        state.Mod.Plugins.Contains(path) || state.Mod.OldLayoutFiles.Contains(path);

    // Ultimate ASI Loader and its license, as the store names them; with onlyOurs, only the copy MVS Buddy's install
    // record names (by name and checksum), and the license only with it.
    private static List<string> LoaderPaths(InstallState state, CopyLayout copy, bool onlyOurs)
    {
        var loaders = state.Loaders.Where(l => l.IsUltimateAsiLoader && (!onlyOurs || (state.Record?.Loader is { } ours
            && string.Equals(ours.Name, l.FileName, StringComparison.OrdinalIgnoreCase) && ours.Sha256 == l.Sha256))).Select(l => l.Path).ToList();
        return loaders.Count == 0 ? [] : loaders.Append(Path.Combine(state.Game.Win64Directory, Installer.LicenseName)).Where(File.Exists)
            .Select(f => Relative(copy, f)).OfType<string>().ToList();
    }

    // Each path of <paramref name="from"/> that it has (a file or a folder), as a graft of the same path. Its paths are
    // read once: a look per path opens the repository per path, 40 s for 34,000.
    private static IEnumerable<TreeEdit> Grafts(GitStore store, string from, IEnumerable<string> paths)
    {
        var has = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in store.Paths(from))
        {
            for (int slash = file.Length; slash > 0; slash = file.LastIndexOf('/', slash - 1))
            {
                if (!has.Add(file.Substring(0, slash)))
                {
                    break;
                }
            }
        }
        return paths.Distinct(StringComparer.Ordinal).Where(has.Contains).Select(p => (TreeEdit)new Graft(p, from, p)).ToList();
    }

    // Undo's promise, with its one exception: the mod's paks found in the game's own folders come back where the mod keeps them.
    private static string UndoLine(InstallState state, string promise) => state.PaksInGame.Count == 0
        ? promise + "."
        : promise + ", except that the paks found in the game's own folders go where the mod keeps them.";

    private static string Files(int count) => count == 1 ? "file" : "files";

    private static string Paks(int count) => count == 1 ? "pak" : "paks";

    // The defaults of the version installed now, the base of the merge: the settings its release shipped, from the cache
    // (an install by MVS Buddy names its release), else its own sample_config.toml, else sample.toml at its tag on
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
