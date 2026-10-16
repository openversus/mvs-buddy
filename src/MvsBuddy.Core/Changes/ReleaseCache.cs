using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using MvsBuddy.Catalog;
using MvsBuddy.Installs;

namespace MvsBuddy.Changes;

/// <summary>A release as the cache holds it.</summary>
/// <param name="Tag">The release's tag, which names it in the cache too.</param>
/// <param name="Commit">The cache's commit of it.</param>
/// <param name="Tree">Its tree: <c>release.json</c>, <c>plugins/OpenVersus/**</c> as the zip ships it, and <c>loader/</c>
/// (Ultimate ASI Loader as pinned, and its license).</param>
/// <param name="Downloaded">Whether it was downloaded just now; false when the cache already had it.</param>
public sealed record CachedRelease(string Tag, string Commit, string Tree, bool Downloaded)
{
    /// <summary>Whether the zip was checked against a checksum (GitHub's, or one the release publishes).</summary>
    public bool ZipChecked { get; init; } = true;
}

/// <summary>
/// The releases MVS Buddy has fetched, in a bare git repository of its own (<c>cache.git</c> in its folder), one for
/// every copy of the game: identical files across releases and channels are kept once. A release is a commit of its
/// extracted files (never its zip, which goes once its checksum is checked), tagged with the release's tag; a branch per
/// channel names its newest. The release's own settings file is the base a settings merge needs, with no download.
/// </summary>
internal sealed class ReleaseCache
{
    internal const string InfoFile = "release.json";
    internal const string LoaderFolder = "loader";

    private readonly HostSystem host;
    private readonly Downloads downloads;
    private readonly AsiLoaderPin pin;
    private readonly ILogger<ReleaseCache> log;

    internal ReleaseCache(HostSystem host, Downloads downloads, AsiLoaderPin pin, ILogger<ReleaseCache> log)
    {
        this.host = host;
        this.downloads = downloads;
        this.pin = pin;
        this.log = log;
    }

    // Never Prune() this store directly: the copies' states read release files from it, and only Prune() below counts them.
    internal GitStore Store() => GitStore.Open(Path.Combine(DataDirectory(), "cache.git"), null, [], log);

    /// <summary>
    /// Deletes what no tag or branch of the cache reaches, nor any state of a copy of the game (<see cref="GameStates"/>
    /// keeps its states' release files in the cache, not beside them). Returns how many objects went and their size.
    /// </summary>
    internal (int Objects, long Bytes) Prune()
    {
        string states = Path.Combine(DataDirectory(), GameStates.StatesFolder);
        var folders = Directory.Exists(states) ? Directory.EnumerateDirectories(states, "*.git").ToList() : [];
        // A copy's states that cannot be read may still use release files: then nothing is pruned.
        if (folders.FirstOrDefault(d => !LibGit2Sharp.Repository.IsValid(d)) is string unreadable)
        {
            log.LogWarning("The release cache is not pruned: {Folder} cannot be read, and its states may use the cache's files", unreadable);
            return (0, 0);
        }
        return Store().Prune(folders.Select(d => GitStore.Open(d, null, [], log)).ToList());
    }

    /// <summary>
    /// Keeps the releases <paramref name="inUse"/> names (installed or kept by a copy of the game) and the newest of each
    /// channel by version (by when it was fetched, without one); the other tags go, each channel's branch names its newest,
    /// and when a tag went, what nothing reaches any more is deleted (<see cref="Prune"/>). Returns how many tags went.
    /// </summary>
    internal int Retain(IReadOnlyCollection<string> inUse)
    {
        var store = Store();
        var tags = store.Tags();
        var releases = tags.Select(t => (Tag: t.Key, Commit: t.Value, Info: Info(store, store.TreeOf(t.Value)))).ToList();
        var keep = new HashSet<string>(inUse, StringComparer.Ordinal);
        foreach (var channel in new[] { Channel.Prod, Channel.Testing })
        {
            var newest = releases.Where(r => r.Info?.Channel == channel.ToString())
                .OrderByDescending(r => System.Version.TryParse(r.Info!.Version, out var version) ? version : null)
                .ThenByDescending(r => r.Info!.At).FirstOrDefault();
            if (newest.Tag != null)
            {
                keep.Add(newest.Tag);
                store.SetBranch(BranchOf(channel), newest.Commit);
            }
        }
        var dropped = tags.Keys.Where(t => !keep.Contains(t)).ToList();
        foreach (string tag in dropped)
        {
            store.DeleteTag(tag);
        }
        log.LogInformation("Release cache: {Kept} kept, {Dropped} dropped ({Tags})", tags.Count - dropped.Count, dropped.Count, string.Join(", ", dropped));
        if (dropped.Count > 0)
        {
            var (objects, bytes) = Prune();
            log.LogInformation("Release cache: {Objects} objects deleted, {Bytes} bytes", objects, bytes);
        }
        return dropped.Count;
    }

    /// <summary>The channel's branch in the cache.</summary>
    internal static string BranchOf(Channel channel) => channel == Channel.Prod ? "prod" : "testing";

    /// <summary>
    /// <paramref name="release"/>'s files: from the cache when it holds the release as GitHub has it now (the zip's
    /// checksum, and the loader MVS Buddy pins), else downloaded, checked, extracted and committed. A zip GitHub gives no
    /// checksum for is fetched every time, as it is for an install.
    /// </summary>
    internal async Task<CachedRelease> GetAsync(Release release, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (release == null)
        {
            throw new ArgumentNullException(nameof(release));
        }
        var zipAsset = await downloads.WithChecksumAsync(release.InstallZip ?? throw new ChangeException($"{release.Tag} has no install zip."),
            cancellationToken).ConfigureAwait(false);
        var store = Store();
        if (store.Tagged(release.Tag) is string cached)
        {
            string cachedTree = store.TreeOf(cached);
            var info = Info(store, cachedTree);
            if (zipAsset.Sha256 != null && info?.Zip?.Sha256 == zipAsset.Sha256 && info.Loader?.Sha256 == pin.DllSha256)
            {
                log.LogInformation("{Tag} is in the cache as {Commit}", release.Tag, cached.Substring(0, 8));
                return new CachedRelease(release.Tag, cached, cachedTree, false);
            }
            log.LogInformation("{Tag} is in the cache but not as GitHub has it now; fetching it again", release.Tag);
        }

        progress?.Report($"Downloading {zipAsset.Name}");
        string zip = await downloads.FetchAsync(zipAsset, Installer.Percent.Of(progress, zipAsset), cancellationToken).ConfigureAwait(false);
        progress?.Report($"Downloading Ultimate ASI Loader {pin.Version}");
        string loaderZip = await downloads.FetchAsync(pin.ZipUrl, Path.GetFileName(pin.ZipUrl.AbsolutePath), pin.ZipSha256, null, cancellationToken).ConfigureAwait(false);
        string license = await downloads.FetchAsync(pin.LicenseUrl, Installer.LicenseName, pin.LicenseSha256, null, cancellationToken).ConfigureAwait(false);

        string stage = Path.Combine(DataDirectory(), "stage", Guid.NewGuid().ToString("N"));
        try
        {
            progress?.Report($"Unpacking {zipAsset.Name}...");
            Installer.ExtractPluginFolder(zip, stage);
            string loaderFolder = Path.Combine(stage, LoaderFolder);
            Installer.ExtractLoader(loaderZip, pin, loaderFolder);
            File.Copy(license, Path.Combine(loaderFolder, Installer.LicenseName));
            var info = new CachedReleaseInfo
            {
                Tag = release.Tag,
                Channel = release.Channel.ToString(),
                Version = release.Version == null ? null : ModVersion.Text(release.Version),
                At = DateTimeOffset.Now,
                Buddy = BuddyVersion.Current,
                Zip = new RecordedFile { Name = zipAsset.Name, Url = zipAsset.DownloadUrl.ToString(), Sha256 = zipAsset.Sha256 ?? Downloads.Sha256Of(zip) },
                Loader = new RecordedFile { Name = pin.DllName, Version = pin.Version, Url = pin.ZipUrl.ToString(), Sha256 = pin.DllSha256 },
            };
            File.WriteAllBytes(Path.Combine(stage, InfoFile), JsonSerializer.SerializeToUtf8Bytes(info, CacheJsonContext.Default.CachedReleaseInfo));
            progress?.Report($"Keeping {release.Tag} in the release cache...");
            string tree = store.TreeOfFolder(stage);
            string commit = store.Commit(BranchOf(release.Channel), tree, release.Tag);
            store.Tag(release.Tag, commit);
            log.LogInformation("{Tag} cached as {Commit}", release.Tag, commit.Substring(0, 8));
            return new CachedRelease(release.Tag, commit, tree, true) { ZipChecked = zipAsset.Sha256 != null };
        }
        finally
        {
            DeleteFolder(stage);
            // The zip's job ends with its checksum: the extracted files are in the cache now.
            string downloadsFolder = Path.Combine(DataDirectory(), "downloads") + Path.DirectorySeparatorChar;
            string zipFolder = Path.GetDirectoryName(Path.GetFullPath(zip))!;
            if ((zipFolder + Path.DirectorySeparatorChar).StartsWith(downloadsFolder, StringComparison.Ordinal))
            {
                DeleteFolder(zipFolder);
            }
        }
    }

    /// <summary>
    /// The pinned Ultimate ASI Loader and its license, as bytes: out of any cached release that holds that build, else
    /// downloaded and checked. Waits for the download: the changes that need it are got ready off the window's thread.
    /// </summary>
    /// <exception cref="DownloadException">It could not be fetched or did not match.</exception>
    internal (byte[] Dll, byte[] License) PinnedLoader(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var store = Store();
        foreach (string commit in store.Tags().Values)
        {
            string tree = store.TreeOf(commit);
            if (Info(store, tree)?.Loader?.Sha256 == pin.DllSha256
                && store.Read(tree, LoaderFolder + "/" + pin.DllName) is byte[] dll && store.Read(tree, LoaderFolder + "/" + Installer.LicenseName) is byte[] license)
            {
                return (dll, license);
            }
        }
        progress?.Report($"Downloading Ultimate ASI Loader {pin.Version}");
        string zip = downloads.FetchAsync(pin.ZipUrl, Path.GetFileName(pin.ZipUrl.AbsolutePath), pin.ZipSha256, null, cancellationToken).GetAwaiter().GetResult();
        string licensePath = downloads.FetchAsync(pin.LicenseUrl, Installer.LicenseName, pin.LicenseSha256, null, cancellationToken).GetAwaiter().GetResult();
        string stage = Path.Combine(DataDirectory(), "stage", Guid.NewGuid().ToString("N"));
        try
        {
            return (File.ReadAllBytes(Installer.ExtractLoader(zip, pin, stage)), File.ReadAllBytes(licensePath));
        }
        finally
        {
            DeleteFolder(stage);
        }
    }

    /// <summary>What the cache wrote down about the release in <paramref name="tree"/>; null when it cannot be read.</summary>
    internal static CachedReleaseInfo? Info(GitStore store, string tree)
    {
        try
        {
            return store.Read(tree, InfoFile) is byte[] json ? JsonSerializer.Deserialize(json, CacheJsonContext.Default.CachedReleaseInfo) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string DataDirectory() => string.IsNullOrEmpty(host.DataDirectory)
        ? throw new ChangeException("MVS Buddy has no folder of its own to keep releases in.")
        : host.DataDirectory;

    private void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            log.LogWarning(e, "Could not delete {Folder}", folder);
        }
    }
}

/// <summary>A release's <c>release.json</c> in the cache.</summary>
internal sealed class CachedReleaseInfo
{
    public string Tag { get; set; } = "";

    public string Channel { get; set; } = "";

    public string? Version { get; set; }

    public DateTimeOffset At { get; set; }

    public string? Buddy { get; set; }

    public RecordedFile? Zip { get; set; }

    public RecordedFile? Loader { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CachedReleaseInfo))]
internal sealed partial class CacheJsonContext : JsonSerializerContext
{
}
