using System.IO.Compression;
using Microsoft.Extensions.Logging;
using MvsBuddy.Catalog;
using MvsBuddy.Installs;

namespace MvsBuddy.Changes;

/// <summary>Choices for an install.</summary>
public sealed record InstallOptions
{
    /// <summary>The name the loader takes; null for <c>xinput1_3.dll</c> on Windows and <c>version.dll</c> under Wine.</summary>
    public string? LoaderName { get; init; }
}

/// <summary>An install ready to apply: everything it needs is downloaded and checked, and nothing has changed yet.</summary>
public sealed class PreparedInstall
{
    internal PreparedInstall(Release release, ChangePlan plan, SettingsMergeResult? settings, IReadOnlyList<string> summary)
    {
        Release = release;
        Plan = plan;
        Settings = settings;
        Summary = summary;
    }

    /// <summary>The release being installed.</summary>
    public Release Release { get; }

    /// <summary>The changes; <see cref="ChangePlan.Apply"/> makes them, all or none.</summary>
    public ChangePlan Plan { get; }

    /// <summary>How the settings were merged, or null when no settings file is written.</summary>
    public SettingsMergeResult? Settings { get; }

    /// <summary>What the install will do, in plain sentences, for the person to confirm.</summary>
    public IReadOnlyList<string> Summary { get; }
}

/// <summary>
/// Files an install has staged: the release's plugin folder, its paks (a null path for one whose installed copy already
/// matches, which was not downloaded), the loader and its license.
/// </summary>
internal sealed record StagedInstall(
    string Extracted,
    IReadOnlyList<(string Name, string? Path, string Sha256)> Paks,
    string LoaderDll,
    string LoaderLicense,
    string? InstalledDefaults);

/// <summary>
/// Installs a release over whatever is there, and uninstalls. Preparing downloads and checks everything first; the plan
/// it returns backs up every file it replaces into a held set (restoring that set is how an update is rolled back) and
/// leaves alone everything the release does not ship, such as the mod's saved state and logs.
/// </summary>
public sealed class Installer
{
    internal const string LicenseName = "LICENSE_Ultimate_ASI_Loader.txt";
    private const string Home = "plugins/OpenVersus/";

    private readonly HostSystem host;
    private readonly ReleaseCatalog catalog;
    private readonly Downloads downloads;
    private readonly InstallChanges changes;
    private readonly AsiLoaderPin pin;
    private readonly ILogger<Installer> log;

    internal Installer(HostSystem host, ReleaseCatalog catalog, Downloads downloads, InstallChanges changes, AsiLoaderPin pin, ILogger<Installer> log)
    {
        this.host = host;
        this.catalog = catalog;
        this.downloads = downloads;
        this.changes = changes;
        this.pin = pin;
        this.log = log;
    }

    /// <summary>
    /// Downloads and checks everything <paramref name="release"/> needs (its zip, its paks, the loader and its license, the
    /// installed version's default settings), and plans the install. Nothing in the game or the profile changes.
    /// </summary>
    /// <exception cref="DownloadException">Something could not be downloaded or did not match its checksum.</exception>
    /// <exception cref="ReleaseCatalogException">GitHub could not be asked for the paks.</exception>
    /// <exception cref="ChangeException">The release has no install zip.</exception>
    public async Task<PreparedInstall> PrepareAsync(InstallState state, Release release, InstallOptions? options = null,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (state == null || release == null)
        {
            throw new ArgumentNullException(state == null ? nameof(state) : nameof(release));
        }
        var zipAsset = release.InstallZip ?? throw new ChangeException($"{release.Tag} has no install zip.");
        progress?.Report($"Downloading {zipAsset.Name}");
        string zip = await downloads.FetchAsync(zipAsset, null, cancellationToken).ConfigureAwait(false);
        string extracted = ExtractPluginFolder(zip);

        var paks = new List<(string, string?, string)>();
        foreach (var asset in await catalog.PaksForAsync(release, cancellationToken).ConfigureAwait(false))
        {
            // Not downloaded when the installed copy is already this one: same size, then same checksum as GitHub's.
            string? installed = state.SavedPaksDirectory == null ? null : Path.Combine(state.SavedPaksDirectory, asset.Name);
            if (asset.Sha256 != null && installed != null && File.Exists(installed) && new FileInfo(installed).Length == asset.Size)
            {
                progress?.Report($"Checking {asset.Name}");
                if (Downloads.Sha256Of(installed) == asset.Sha256)
                {
                    paks.Add((asset.Name, null, asset.Sha256));
                    continue;
                }
            }
            progress?.Report($"Downloading {asset.Name}");
            string path = await downloads.FetchAsync(asset, null, cancellationToken).ConfigureAwait(false);
            paks.Add((asset.Name, path, asset.Sha256 ?? Downloads.Sha256Of(path)));
        }

        progress?.Report($"Downloading Ultimate ASI Loader {pin.Version}");
        string loaderZip = await downloads.FetchAsync(pin.ZipUrl, Path.GetFileName(pin.ZipUrl.AbsolutePath), pin.ZipSha256, null, cancellationToken).ConfigureAwait(false);
        string loader = ExtractLoader(loaderZip);
        string license = await downloads.FetchAsync(pin.LicenseUrl, LicenseName, pin.LicenseSha256, null, cancellationToken).ConfigureAwait(false);

        string? defaults = await InstalledDefaultsAsync(state, cancellationToken).ConfigureAwait(false);
        var prepared = Plan(state, release, new StagedInstall(extracted, paks, loader, license, defaults), options ?? new InstallOptions());
        var notChecked = new[] { zipAsset }.Concat(release.Assets.Where(a => paks.Any(p => p.Item1 == a.Name))).Where(a => a.Sha256 == null).Select(a => a.Name).Distinct().ToList();
        return notChecked.Count == 0 ? prepared
            : new PreparedInstall(release, prepared.Plan, prepared.Settings,
                [.. prepared.Summary, $"Not checked, since GitHub gives no checksum for them: {string.Join(", ", notChecked)}."]);
    }

    /// <summary>
    /// Takes the mod out: everything of it, its paks, and (when <paramref name="removeLoader"/>) the loader, all held as one
    /// set named for the time, which can be restored or discarded. Keep the loader when other ASI mods use it.
    /// </summary>
    public ChangePlan Uninstall(InstallState state, bool removeLoader = true)
    {
        if (state == null)
        {
            throw new ArgumentNullException(nameof(state));
        }
        var files = state.Mod.Files.Concat(state.Mod.OldLayoutFiles).Concat(state.Paks.Select(p => p.Path)).Concat(state.PaksInGame.Select(p => p.Path));
        if (removeLoader)
        {
            string license = Path.Combine(state.Game.Win64Directory, LicenseName);
            files = files.Concat(state.Loaders.Where(l => l.IsUltimateAsiLoader).Select(l => l.Path)).Concat(File.Exists(license) ? [license] : []);
        }
        return changes.Hold(state, $"Uninstalled {DateTimeOffset.Now:yyyy-MM-dd HHmmss}", files.ToList());
    }

    internal PreparedInstall Plan(InstallState state, Release release, StagedInstall staged, InstallOptions options)
    {
        string win64 = state.Game.Win64Directory;
        string home = Path.Combine(win64, "plugins", "OpenVersus");
        var plan = changes.NewPlan();
        var summary = new List<string>();
        var backup = new List<string>();
        var moves = new List<(string From, string To)>();
        string version = release.Version == null ? release.Tag : ModVersion.Text(release.Version);
        string from = state.Mod.Version != null ? ModVersion.Text(state.Mod.Version) : state.Mod.Layout == ModLayout.None ? "nothing" : $"an older layout ({state.Mod.Layout})";
        summary.Add($"Installs OpenVersus {version} ({release.Channel}) over {from}.");
        if (!Directory.Exists(win64))
        {
            summary.Add($"{win64} does not exist yet; it will be made.");
        }

        // The release's own files, except its settings, which are merged below.
        string extractedRoot = Path.GetFullPath(staged.Extracted).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var shipped = Directory.EnumerateFiles(extractedRoot, "*", SearchOption.AllDirectories)
            .Select(f => (Path: f, Relative: f.Substring(extractedRoot.Length)))
            .ToList();
        string settingsRelative = Path.Combine("plugins", "OpenVersus", InstallReader.SettingsFile);
        string sampleRelative = Path.Combine("plugins", "OpenVersus", "sample_config.toml");
        int unchanged = 0;
        foreach (var (path, relative) in shipped.Where(f => f.Relative != settingsRelative))
        {
            string target = Path.Combine(win64, relative);
            if (File.Exists(target))
            {
                if (Downloads.Sha256Of(target) == Downloads.Sha256Of(path))
                {
                    unchanged++;
                    continue;
                }
                backup.Add(target);
            }
            moves.Add((path, target));
        }
        var shippedTargets = new HashSet<string>(shipped.Select(f => Path.Combine(win64, f.Relative)), StringComparer.Ordinal);

        // Every other OpenVersus plugin goes, or two would load: the previous version, an old layout's, one beside the game.
        var oldPlugins = state.Mod.Plugins
            .Concat(state.Mod.OldLayoutFiles.Where(f => f.EndsWith(".asi", StringComparison.OrdinalIgnoreCase)))
            .Where(p => !shippedTargets.Contains(p)).Distinct().ToList();
        backup.AddRange(oldPlugins);
        // Beside the executable, the old settings, state and cache: the mod only takes them over from plugins/.
        string plugins = Path.Combine(win64, "plugins");
        foreach (string companion in state.Mod.Files.Concat(state.Mod.OldLayoutFiles)
            .Where(f => !f.EndsWith(".asi", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(f) != plugins && !f.StartsWith(home, StringComparison.Ordinal)))
        {
            string target = Path.Combine(plugins, Path.GetFileName(companion));
            if (File.Exists(target))
            {
                backup.Add(companion);
            }
            else
            {
                moves.Add((companion, target));
            }
        }

        // Settings: written when the release ships its own or the person has some.
        SettingsMergeResult? merge = null;
        string theirsPath = Path.Combine(home, InstallReader.SettingsFile);
        string? theirs = File.Exists(theirsPath) ? File.ReadAllText(theirsPath) : null;
        string? releaseSettings = shipped.Where(f => f.Relative == settingsRelative).Select(f => f.Path).FirstOrDefault()
            ?? (theirs != null ? shipped.Where(f => f.Relative == sampleRelative).Select(f => f.Path).FirstOrDefault() : null);
        if (releaseSettings != null)
        {
            merge = SettingsMerge.Merge(staged.InstalledDefaults, theirs, File.ReadAllText(releaseSettings));
            if (theirs != null)
            {
                backup.Add(theirsPath);
            }
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

        // The loader, under the name the platform needs.
        string loaderName = options.LoaderName ?? (host.IsWindows ? "xinput1_3.dll" : "version.dll");
        var atName = state.Loaders.FirstOrDefault(l => string.Equals(l.FileName, loaderName, StringComparison.OrdinalIgnoreCase));
        bool writeLoader = atName == null || (atName.IsUltimateAsiLoader && !atName.IsPinnedBuild);
        if (atName != null && !atName.IsUltimateAsiLoader)
        {
            string other = loaderName == "version.dll" ? "xinput1_3.dll" : "version.dll";
            plan.Refuse(() => $"{atName.Path} is another program's file, not Ultimate ASI Loader, so the loader cannot take its name. Install with the loader named {other} instead.");
        }
        if (writeLoader && atName != null)
        {
            backup.Add(atName.Path);
        }
        var otherLoaders = state.Loaders.Where(l => l.IsUltimateAsiLoader && l != atName).ToList();
        backup.AddRange(otherLoaders.Select(l => l.Path));
        string licenseTarget = Path.Combine(win64, LicenseName);
        bool writeLicense = !File.Exists(licenseTarget) || Downloads.Sha256Of(licenseTarget) != Downloads.Sha256Of(staged.LoaderLicense);
        if (writeLicense && File.Exists(licenseTarget))
        {
            backup.Add(licenseTarget);
        }
        summary.Add(writeLoader
            ? $"Ultimate ASI Loader {pin.Version} as {loaderName}" + (otherLoaders.Count > 0 ? $"; the other copy ({string.Join(", ", otherLoaders.Select(l => l.FileName))}) is set aside." : ".")
            : $"Ultimate ASI Loader {pin.Version} is already in place as {loaderName}.");

        // Paks, by checksum: the same file stays; a different one, or one this release does not have, is set aside.
        int paksMoved = 0;
        if (state.SavedPaksDirectory == null)
        {
            summary.Add("The paks are not installed: the buddy cannot tell which Wine prefix this copy runs in.");
        }
        else
        {
            var names = new HashSet<string>(staged.Paks.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var (name, path, sha) in staged.Paks)
            {
                string target = Path.Combine(state.SavedPaksDirectory, name);
                if (path == null)
                {
                    continue;
                }
                if (File.Exists(target))
                {
                    if (Downloads.Sha256Of(target) == sha)
                    {
                        continue;
                    }
                    backup.Add(target);
                }
                moves.Add((path, target));
                // The paks leave the download cache as they go in; the client zip and the loader stay cached.
                plan.CleanUp(Path.GetDirectoryName(path)!, Path.Combine(host.DataDirectory, "downloads"));
                paksMoved++;
            }
            backup.AddRange(state.Paks.Where(p => !names.Contains(p.Name)).Select(p => p.Path));
            summary.Add($"Paks: {paksMoved} new or changed, {staged.Paks.Count - paksMoved} already in place.");
        }
        if (state.PaksInGame.Count > 0)
        {
            backup.AddRange(state.PaksInGame.Select(p => p.Path));
            summary.Add($"{state.PaksInGame.Count} OpenVersus paks found in the game's own folders are set aside; the checked copies go where the mod keeps them.");
        }

        // Every install keeps a set that undoes it: what it replaces or retires, and the list of what it puts in place.
        var distinctBackup = backup.Distinct().ToList();
        var placed = moves.Select(m => m.To)
            .Concat(writeLoader ? [Path.Combine(win64, loaderName)] : [])
            .Concat(writeLicense ? [licenseTarget] : [])
            .Concat(merge != null ? [theirsPath] : [])
            .Distinct().ToList();
        string set = $"Before {version} {DateTimeOffset.Now:yyyy-MM-dd HHmmss}";
        changes.AddHold(plan, state, set, distinctBackup.Select(f => (f, f)).ToList(), placed);
        summary.Add(distinctBackup.Count switch
        {
            0 => $"Restoring \"{set}\" undoes this install.",
            1 => $"1 file it replaces or retires is kept as \"{set}\"; restoring that undoes this install.",
            _ => $"{distinctBackup.Count} files it replaces or retires are kept as \"{set}\"; restoring that undoes this install.",
        });
        foreach (var (source, target) in moves)
        {
            plan.Move(source, target);
        }
        if (writeLoader)
        {
            plan.Move(staged.LoaderDll, Path.Combine(win64, loaderName));
        }
        if (writeLicense)
        {
            plan.Move(staged.LoaderLicense, licenseTarget);
        }
        if (merge != null)
        {
            plan.Write(theirsPath, System.Text.Encoding.UTF8.GetBytes(merge.Text));
        }
        if (unchanged > 0)
        {
            summary.Add(unchanged == 1 ? "1 of the release's files is already in place." : $"{unchanged} of the release's files are already in place.");
        }
        log.LogInformation("Planned {Release} over {Folder}: {Summary}", release.Tag, win64, string.Join(" | ", summary));
        return new PreparedInstall(release, plan, merge, summary);
    }

    // The release's plugins/OpenVersus/ only, extracted afresh beside the zip; nothing else in it is ever used.
    private static string ExtractPluginFolder(string zip)
    {
        string extracted = Path.Combine(Path.GetDirectoryName(zip)!, "extracted");
        if (Directory.Exists(extracted))
        {
            Directory.Delete(extracted, recursive: true);
        }
        string root = Path.GetFullPath(extracted) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith(Home, StringComparison.OrdinalIgnoreCase) || name.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }
            string target = Path.GetFullPath(Path.Combine(extracted, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.Ordinal))
            {
                throw new ChangeException($"The zip has an entry that points outside it ({entry.FullName}); it is not installed.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target);
        }
        return extracted;
    }

    // The pinned DLL out of the loader's zip, checked on its own: the zip's checksum covers the archive, not this entry.
    private string ExtractLoader(string zip)
    {
        string target = Path.Combine(Path.GetDirectoryName(zip)!, "loader", pin.DllName);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            File.Delete(target);
        }
        using (var archive = ZipFile.OpenRead(zip))
        {
            var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.FullName, pin.DllName, StringComparison.OrdinalIgnoreCase))
                ?? throw new DownloadException($"{Path.GetFileName(zip)} has no {pin.DllName}.");
            entry.ExtractToFile(target);
        }
        if (Downloads.Sha256Of(target) != pin.DllSha256)
        {
            File.Delete(target);
            throw new DownloadException($"The {pin.DllName} in {Path.GetFileName(zip)} is not the build the buddy expects; it is not installed.");
        }
        return target;
    }

    // The defaults of the version installed now: its own sample_config.toml, else sample.toml at its tag.
    private async Task<string?> InstalledDefaultsAsync(InstallState state, CancellationToken cancellationToken)
    {
        string sample = Path.Combine(state.Game.Win64Directory, "plugins", "OpenVersus", "sample_config.toml");
        if (File.Exists(sample))
        {
            return File.ReadAllText(sample);
        }
        if (state.Mod.Version == null)
        {
            return null;
        }
        foreach (var channel in new[] { Channel.Prod, Channel.Testing })
        {
            var match = (await catalog.ListAsync(channel, cancellationToken).ConfigureAwait(false)).FirstOrDefault(r => r.Version == state.Mod.Version);
            if (match != null)
            {
                return await catalog.FileAtTagAsync(match.Tag, "sample.toml", cancellationToken).ConfigureAwait(false);
            }
        }
        return null;
    }
}
