using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MvsBuddy.Catalog;

namespace MvsBuddy.Tests;

// A newer MVS Buddy on GitHub: found only when newer, fetched only when asked, unpacked runnable.
public sealed class UpdatesTests : IDisposable
{
    private readonly TempTree tree = new();
    private readonly FakeGitHub server = new();

    public void Dispose() => tree.Dispose();

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private Buddy Buddy(bool windows = false) => new BuddyBuilder().Http(new HttpClient(server))
        .Host(new HostSystem { IsWindows = windows, Home = tree.Path("home"), DataDirectory = tree.Path("data") }).Build();

    // A release on the fake GitHub with the archives for both platforms (bytes given), as the release workflow attaches them.
    private void Latest(string tag, byte[]? tgz = null, byte[]? zip = null)
    {
        var assets = new List<string>();
        void Asset(string name, byte[] bytes)
        {
            string at = $"/dl/{tag}/{name}";
            server.Answer(at, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            assets.Add($"{{\"name\":\"{name}\",\"size\":{bytes.Length},\"browser_download_url\":\"https://example.test{at}\",\"digest\":\"sha256:{Sha(bytes)}\"}}");
        }
        if (tgz != null)
        {
            Asset($"MvsBuddy-{tag}-linux-x64.tar.gz", tgz);
        }
        if (zip != null)
        {
            Asset($"MvsBuddy-{tag}-win-x64.zip", zip);
        }
        server.Json("/repos/openversus/mvs-buddy/releases/latest", $"{{\"tag_name\":\"{tag}\",\"name\":\"MVS Buddy {tag}\",\"assets\":[{string.Join(",", assets)}]}}");
    }

    private static string Newer()
    {
        var v = Updates.Current!;
        return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision + 1:00}";
    }

    private static string Older()
    {
        var v = Updates.Current!;
        return $"{v.Major - 1}.{v.Minor}.{v.Build}.{v.Revision:00}";
    }

    [Fact]
    public async Task A_newer_release_is_found_with_this_platforms_archive_and_an_older_or_equal_one_is_not()
    {
        Latest(Newer(), tgz: [1], zip: [2]);
        var found = await Buddy().Updates.CheckAsync();
        Assert.NotNull(found);
        Assert.Equal($"MvsBuddy-{Newer()}-linux-x64.tar.gz", found.Asset.Name);
        Assert.Equal($"MvsBuddy-{Newer()}-win-x64.zip", (await Buddy(windows: true).Updates.CheckAsync())!.Asset.Name);

        Latest(BuddyVersion.Current, tgz: [1], zip: [2]);
        Assert.Null(await Buddy().Updates.CheckAsync());
        Latest(Older(), tgz: [1], zip: [2]);
        Assert.Null(await Buddy().Updates.CheckAsync());
        // Newer, but nothing for this platform: nothing to update with.
        Latest(Newer(), zip: [2]);
        Assert.Null(await Buddy().Updates.CheckAsync());
    }

    [Fact]
    public async Task The_archive_is_fetched_checked_and_its_executable_unpacked_runnable()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "unpacks with tar");
        byte[] tgz;
        using (var bytes = new MemoryStream())
        {
            using (var gzip = new GZipStream(bytes, CompressionLevel.Fastest, leaveOpen: true))
            using (var tar = new TarWriter(gzip))
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, "MvsBuddy") { DataStream = new MemoryStream(Encoding.ASCII.GetBytes("the new build")) };
                tar.WriteEntry(entry);
            }
            tgz = bytes.ToArray();
        }
        Latest(Newer(), tgz: tgz);
        var buddy = Buddy();
        var found = (await buddy.Updates.CheckAsync())!;

        string executable = await buddy.Updates.DownloadAsync(found);

        Assert.Equal(tree.Path("data", "downloads", "update", Newer(), "MvsBuddy"), executable);
        Assert.Equal("the new build", File.ReadAllText(executable));
        Assert.True((File.GetUnixFileMode(executable) & UnixFileMode.UserExecute) != 0);
    }

    [Fact]
    public async Task A_windows_archive_is_unpacked_from_its_zip()
    {
        byte[] zip;
        using (var bytes = new MemoryStream())
        {
            using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
            using (var entry = archive.CreateEntry("MvsBuddy.exe").Open())
            {
                entry.Write(Encoding.ASCII.GetBytes("MZ the new build"));
            }
            zip = bytes.ToArray();
        }
        Latest(Newer(), zip: zip);
        var buddy = Buddy(windows: true);
        var found = (await buddy.Updates.CheckAsync())!;

        string executable = await buddy.Updates.DownloadAsync(found);

        Assert.Equal(tree.Path("data", "downloads", "update", Newer(), "MvsBuddy.exe"), executable);
        Assert.Equal("MZ the new build", File.ReadAllText(executable));
    }

    [Fact]
    public async Task An_archive_that_does_not_match_the_releases_digest_is_refused()
    {
        Latest(Newer(), tgz: [1, 2, 3]);
        server.Answer($"/dl/{Newer()}/MvsBuddy-{Newer()}-linux-x64.tar.gz", () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([9, 9, 9]) });
        var buddy = Buddy();
        var found = (await buddy.Updates.CheckAsync())!;

        await Assert.ThrowsAsync<DownloadException>(() => buddy.Updates.DownloadAsync(found));
    }
}
