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
public sealed record CachedRelease(string Tag, string Commit, string Tree, bool Downloaded);

/// <summary>
/// The releases the buddy has fetched, in a bare git repository of its own (<c>cache.git</c> in its folder), one for
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

    internal GitStore Store() => GitStore.Open(Path.Combine(DataDirectory(), "cache.git"), null, [], log);

    /// <summary>The channel's branch in the cache.</summary>
    internal static string BranchOf(Channel channel) => channel == Channel.Prod ? "prod" : "testing";

    /// <summary>
    /// <paramref name="release"/>'s files: from the cache when it holds the release as GitHub has it now (the zip's
    /// checksum, and the loader the buddy pins), else downloaded, checked, extracted and committed. A zip GitHub gives no
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
            string tree = store.TreeOfFolder(stage);
            string commit = store.Commit(BranchOf(release.Channel), tree, release.Tag);
            store.Tag(release.Tag, commit);
            log.LogInformation("{Tag} cached as {Commit}", release.Tag, commit.Substring(0, 8));
            return new CachedRelease(release.Tag, commit, tree, true);
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
        ? throw new ChangeException("The buddy has no folder of its own to keep releases in.")
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
