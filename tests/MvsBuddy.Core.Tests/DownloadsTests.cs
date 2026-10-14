using System.Net;
using System.Security.Cryptography;
using System.Text;
using MvsBuddy.Catalog;

namespace MvsBuddy.Tests;

public sealed class DownloadsTests : IDisposable
{
    private readonly TempTree tree = new();

    public void Dispose() => tree.Dispose();

    private static readonly byte[] Pak = Encoding.ASCII.GetBytes("pretend this is OVS_P.pak");

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private (Downloads Downloads, FakeGitHub Server) Setup(byte[] served)
    {
        var server = new FakeGitHub().Answer("/paks/OVS_P.pak", () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(served) });
        var buddy = new BuddyBuilder().Http(new HttpClient(server)).Host(new HostSystem { DataDirectory = tree.Path("data") }).Build();
        return (buddy.Downloads, server);
    }

    private static ReleaseAsset Asset(string? sha256) => new("OVS_P.pak", Pak.Length, new Uri("https://example.test/paks/OVS_P.pak"), sha256);

    [Fact]
    public async Task A_matching_download_is_kept_under_its_checksum()
    {
        var (downloads, _) = Setup(Pak);

        string path = await downloads.FetchAsync(Asset(Sha(Pak)));

        Assert.Equal(tree.Path("data", "downloads", Sha(Pak), "OVS_P.pak"), path);
        Assert.Equal(Pak, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task A_download_that_does_not_match_is_refused_and_nothing_is_kept()
    {
        var (downloads, _) = Setup(Encoding.ASCII.GetBytes("something else entirely"));

        var e = await Assert.ThrowsAsync<DownloadException>(() => downloads.FetchAsync(Asset(Sha(Pak))));

        Assert.Contains("did not match", e.Message);
        Assert.Empty(Directory.EnumerateFiles(tree.Path("data", "downloads"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_file_already_downloaded_and_matching_is_not_fetched_again()
    {
        var (downloads, server) = Setup(Pak);
        await downloads.FetchAsync(Asset(Sha(Pak)));

        await downloads.FetchAsync(Asset(Sha(Pak)));

        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task An_asset_without_a_checksum_is_fetched_unchecked_and_fetched_again_next_time()
    {
        var (downloads, server) = Setup(Pak);

        string path = await downloads.FetchAsync(Asset(null));
        await downloads.FetchAsync(Asset(null));

        Assert.Equal(Pak, File.ReadAllBytes(path));
        Assert.Contains(Path.Combine("downloads", "unchecked"), path);
        Assert.Equal(2, server.Requests.Count);
    }

    [Theory]
    [InlineData("{0} *OpenVersus_x.zip", "OpenVersus_x.zip", true)]
    [InlineData("{0}  OpenVersus_x.zip\r\n", "OpenVersus_x.zip", true)]
    [InlineData("{1} *OpenVersus_x.asi\n{0} *OpenVersus_x.zip\n", "OpenVersus_x.zip", true)]
    [InlineData("{0} *OpenVersus_x.zip.old", "OpenVersus_x.zip", false)]
    [InlineData("{0}x *OpenVersus_x.zip", "OpenVersus_x.zip", false)]
    [InlineData("not a checksum at all", "OpenVersus_x.zip", false)]
    public void A_checksum_file_as_sha256sum_writes_it_gives_the_named_files_hash(string format, string name, bool found)
    {
        string mine = new('a', 64), other = new('b', 64);

        string? sha = Downloads.ChecksumIn(string.Format(format, mine.ToUpperInvariant(), other), name);

        Assert.Equal(found ? mine : null, sha);
    }
}
