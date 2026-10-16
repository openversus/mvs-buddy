using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MvsBuddy.Catalog;
using MvsBuddy.Changes;
using MvsBuddy.Games;
using MvsBuddy.Installs;

namespace MvsBuddy.Tests;

[Collection("libgit2")]
public sealed class ReleaseCacheTests : IDisposable
{
    private static readonly byte[] LoaderDll = [.. Encoding.ASCII.GetBytes("MZ pinned "), .. Encoding.Unicode.GetBytes("Ultimate ASI Loader")];
    private static readonly byte[] License = Encoding.ASCII.GetBytes("MIT License\n");
    private static readonly byte[] LoaderZip = Zip(new() { ["dinput8.dll"] = Encoding.Latin1.GetString(LoaderDll) });
    private readonly TempTree tree = new();
    private readonly FakeGitHub server = new FakeGitHub()
        .Answer("/ual/Ultimate-ASI-Loader_x64.zip", () => Bytes(LoaderZip))
        .Answer("/ual/license", () => Bytes(License));

    public void Dispose() => tree.Dispose();

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static AsiLoaderPin Pin() => AsiLoaderPin.Default with
    {
        DllSha256 = Sha(LoaderDll),
        LicenseSha256 = Sha(License),
        ZipSha256 = Sha(LoaderZip),
        ZipUrl = new Uri("https://example.test/ual/Ultimate-ASI-Loader_x64.zip"),
        LicenseUrl = new Uri("https://example.test/ual/license"),
    };

    private Buddy Buddy() => new BuddyBuilder().Http(new HttpClient(server)).AsiLoader(Pin())
        .Host(new HostSystem { Home = tree.Path("home"), DataDirectory = tree.Path("data") }).Build();

    // A release whose zip carries, as CI zips do, a loader and a readme at its root beside plugins/OpenVersus/.
    // published: the .sha256 file the release carries beside its zip (sha256sum's format), for a zip GitHub gives no digest.
    private Release Release(string tag, Channel channel, string asi, bool checksum = true, Func<string, string>? published = null)
    {
        byte[] zip = Zip(new()
        {
            ["xinput1_3.dll"] = "MZ a loader from the zip",
            ["readme.txt"] = "readme",
            ["plugins/OpenVersus/" + asi] = "asi " + tag,
            ["plugins/OpenVersus/sample_config.toml"] = "[Settings]\n",
        });
        string path = $"/rel/{tag}/OpenVersus_release.zip";
        server.Answer(path, () => Bytes(zip));
        Uri? sums = null;
        if (published != null)
        {
            byte[] text = Encoding.ASCII.GetBytes(published(Sha(zip)));
            server.Answer(path + ".sha256", () => Bytes(text));
            sums = new Uri("https://example.test" + path + ".sha256");
        }
        return new Release(tag, tag, channel, null, null,
            [new ReleaseAsset("OpenVersus_release.zip", zip.Length, new Uri("https://example.test" + path), checksum ? Sha(zip) : null) { ChecksumUrl = sums }]);
    }

    private int ZipRequests(string tag) => server.Requests.Count(r => r.RequestUri!.AbsolutePath == $"/rel/{tag}/OpenVersus_release.zip");

    [Fact]
    public async Task A_release_is_downloaded_once_kept_as_its_files_and_found_again()
    {
        var buddy = Buddy();
        var release = Release("2026.10.13.01", Channel.Prod, "OpenVersus_2026.10.13.01.asi");

        var first = await buddy.Cache.GetAsync(release);
        var again = await buddy.Cache.GetAsync(release);

        Assert.True(first.Downloaded);
        Assert.False(again.Downloaded);
        Assert.Equal(first.Commit, again.Commit);
        Assert.Equal(1, ZipRequests("2026.10.13.01"));
        var store = buddy.Cache.Store();
        Assert.Equal(
            ["loader/LICENSE_Ultimate_ASI_Loader.txt", "loader/dinput8.dll", "plugins/OpenVersus/OpenVersus_2026.10.13.01.asi", "plugins/OpenVersus/sample_config.toml", "release.json"],
            store.Paths(first.Tree).Order(StringComparer.Ordinal));
        Assert.Equal(first.Commit, store.Tip("prod"));
        var info = ReleaseCache.Info(store, first.Tree)!;
        Assert.Equal((Sha(LoaderDll), "Prod"), (info.Loader!.Sha256, info.Channel));
        // The zip goes once its files are in: only the loader's download (kept for the next release) is left.
        Assert.DoesNotContain(Directory.EnumerateFiles(tree.Path("data", "downloads"), "*", SearchOption.AllDirectories), f => f.EndsWith("OpenVersus_release.zip", StringComparison.Ordinal));
        Assert.False(Directory.Exists(tree.Path("data", "stage")) && Directory.EnumerateFileSystemEntries(tree.Path("data", "stage")).Any());
    }

    [Fact]
    public async Task A_release_retagged_upstream_is_fetched_again_and_the_old_copy_is_freed()
    {
        var buddy = Buddy();
        var first = await buddy.Cache.GetAsync(Release("OVS_Season_1-testing", Channel.Testing, "OpenVersus_2026.10.12.01.asi"));

        // The same tag, moved to a new build: its zip and checksum differ.
        var moved = await buddy.Cache.GetAsync(Release("OVS_Season_1-testing", Channel.Testing, "OpenVersus_2026.10.14.01.asi"));

        Assert.True(moved.Downloaded);
        var store = buddy.Cache.Store();
        Assert.Equal(moved.Commit, store.Tagged("OVS_Season_1-testing"));
        Assert.Equal(moved.Commit, store.Tip("testing"));
        Assert.True(store.Prune().Objects > 0);
        Assert.Contains("plugins/OpenVersus/OpenVersus_2026.10.14.01.asi", store.Paths(store.TreeOf(moved.Commit)));
        Assert.ThrowsAny<Exception>(() => store.TreeOf(first.Commit));
    }

    [Fact]
    public async Task A_zip_github_gives_no_checksum_for_is_fetched_every_time()
    {
        var buddy = Buddy();
        var release = Release("2026.10.13.02", Channel.Prod, "OpenVersus_2026.10.13.02.asi", checksum: false);

        await buddy.Cache.GetAsync(release);
        var again = await buddy.Cache.GetAsync(release);

        Assert.True(again.Downloaded);
        Assert.Equal(2, ZipRequests("2026.10.13.02"));
    }

    [Fact]
    public async Task A_zip_github_gives_no_digest_for_is_checked_against_the_sha256_beside_it_and_kept()
    {
        var buddy = Buddy();
        var release = Release("2026.10.13.04", Channel.Prod, "OpenVersus_2026.10.13.04.asi", checksum: false, published: sha => $"{sha} *OpenVersus_release.zip\n");

        var first = await buddy.Cache.GetAsync(release);
        var again = await buddy.Cache.GetAsync(release);

        Assert.True(first.Downloaded);
        Assert.False(again.Downloaded);
        Assert.Equal(1, ZipRequests("2026.10.13.04"));
    }

    [Fact]
    public async Task A_zip_that_does_not_match_the_sha256_beside_it_is_not_cached()
    {
        var buddy = Buddy();
        var release = Release("2026.10.13.05", Channel.Prod, "OpenVersus_2026.10.13.05.asi", checksum: false,
            published: _ => new string('0', 64) + " *OpenVersus_release.zip\n");

        await Assert.ThrowsAsync<DownloadException>(() => buddy.Cache.GetAsync(release));

        Assert.Null(buddy.Cache.Store().Tagged("2026.10.13.05"));
    }

    [Fact]
    public async Task A_new_loader_pin_fetches_the_release_again_with_it()
    {
        var buddy = Buddy();
        var release = Release("2026.10.13.03", Channel.Prod, "OpenVersus_2026.10.13.03.asi");
        var first = await buddy.Cache.GetAsync(release);
        var store = buddy.Cache.Store();
        // The cache as an older buddy left it: the same release, with another loader recorded.
        var info = ReleaseCache.Info(store, first.Tree)!;
        info.Loader!.Sha256 = "an older pin";
        string staged = tree.File(System.Text.Json.JsonSerializer.Serialize(info, CacheJsonContext.Default.CachedReleaseInfo), "older", "release.json");
        store.Tag(release.Tag, store.Commit("prod", store.Compose(first.Tree, [(ReleaseCache.InfoFile, staged)]), "older"));

        var refreshed = await buddy.Cache.GetAsync(release);

        Assert.True(refreshed.Downloaded);
        Assert.Equal(Sha(LoaderDll), ReleaseCache.Info(store, refreshed.Tree)!.Loader!.Sha256);
    }

    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static byte[] Zip(Dictionary<string, string> entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(Encoding.Latin1.GetBytes(text));
            }
        }
        return memory.ToArray();
    }

    [Fact]
    public async Task The_pinned_loader_is_downloaded_once_when_no_cached_release_holds_it_and_read_from_the_cache_after()
    {
        var buddy = Buddy();
        int Fetches() => server.Requests.Count(r => r.RequestUri!.AbsolutePath.StartsWith("/ual/", StringComparison.Ordinal));

        var (dll, license) = buddy.Cache.PinnedLoader(null, default);
        Assert.Equal(LoaderDll, dll);
        Assert.Equal(License, license);
        Assert.Equal(2, Fetches());
        // The download is kept by checksum, so asking again fetches nothing.
        buddy.Cache.PinnedLoader(null, default);
        Assert.Equal(2, Fetches());

        // Once a release is cached, its loader is read from the cache.
        await buddy.Cache.GetAsync(Release("2026.10.14.1", Channel.Prod, "OpenVersus_2026.10.14.1.asi"));
        int after = Fetches();
        Directory.Delete(tree.Path("data", "downloads"), recursive: true);
        var (cachedDll, cachedLicense) = buddy.Cache.PinnedLoader(null, default);
        Assert.Equal(LoaderDll, cachedDll);
        Assert.Equal(License, cachedLicense);
        Assert.Equal(after, Fetches());
    }
}
