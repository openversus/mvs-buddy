using System.Net;
using System.Text.Json.Nodes;
using MvsBuddy.Catalog;

namespace MvsBuddy.Tests;

// The fixtures are GitHub's own answers for openversus/ovs-client and openversus/ovs-paks on 2026-10-13, cut down to
// the fields the buddy reads. Where a test needs the future (a -testing tag, paks-current), it renames a real release.
public sealed class ReleaseCatalogTests
{
    private const string ClientPage1 = "/repos/openversus/ovs-client/releases?per_page=100&page=1";
    private const string ClientPage2 = "/repos/openversus/ovs-client/releases?per_page=100&page=2";
    private const string PaksCurrent = "/repos/openversus/ovs-paks/releases/tags/paks-current";

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static ReleaseCatalog Catalog(FakeGitHub github, TimeSpan? timeout = null)
    {
        var http = new HttpClient(github);
        if (timeout != null)
        {
            http.Timeout = timeout.Value;
        }
        return new BuddyBuilder().Http(http).Build().Releases;
    }

    private static string Retag(string json, string from, string to)
    {
        var releases = JsonNode.Parse(json)!.AsArray();
        var release = releases.Single(r => (string)r!["tag_name"]! == from)!;
        release["tag_name"] = to;
        return releases.ToJsonString();
    }

    [Fact]
    public async Task Prod_is_the_numbered_tags_and_their_prereleases()
    {
        var github = new FakeGitHub().Json(ClientPage1, Fixture("ovs-client-releases.json"));

        var prod = await Catalog(github).ListAsync(Channel.Prod);

        // Not OVS_Season_1 (no version), not the 2026-02-03 draft.
        Assert.Equal(["2026.10.11.04-prerelease", "2026.04.08.14"], prod.Select(r => r.Tag));
        Assert.True(prod[0].IsPrerelease);
        Assert.Equal(new Version(2026, 10, 11, 4), prod[0].Version);
        Assert.Equal("OpenVersus_v2026.10.11.04-prerelease.zip", prod[0].InstallZip?.Name);
        Assert.False(prod[1].IsPrerelease);
        Assert.Equal(new Version(2026, 4, 8, 14), prod[1].Version);
        Assert.Equal("OpenVersus_v2026.04.08.14.zip", prod[1].InstallZip?.Name);
    }

    [Fact]
    public async Task A_full_release_comes_before_the_prerelease_of_its_version()
    {
        // The real prerelease, and the real April release renamed to the prerelease's version, published earlier.
        var github = new FakeGitHub().Json(ClientPage1, Retag(Fixture("ovs-client-releases.json"), "2026.04.08.14", "2026.10.11.04"));

        var prod = await Catalog(github).ListAsync(Channel.Prod);

        Assert.Equal(["2026.10.11.04", "2026.10.11.04-prerelease"], prod.Select(r => r.Tag));
    }

    [Fact]
    public async Task Testing_is_tags_ending_in_testing()
    {
        string json = Retag(Fixture("ovs-client-releases.json"), "OVS_Season_1", "2026.10.12.01-testing");
        var github = new FakeGitHub().Json(ClientPage1, json);

        var testing = await Catalog(github).ListAsync(Channel.Testing);

        var release = Assert.Single(testing);
        Assert.Equal("2026.10.12.01-testing", release.Tag);
        Assert.Equal(Channel.Testing, release.Channel);
        Assert.Equal(new Version(2026, 10, 12, 1), release.Version);
        Assert.Equal("OpenVersus_2026.10.12.01.zip", release.InstallZip?.Name);
    }

    [Fact]
    public async Task A_draft_is_never_listed_even_with_a_prod_tag()
    {
        // The real draft, under a tag that would otherwise make it prod.
        var github = new FakeGitHub().Json(ClientPage1, Retag(Fixture("ovs-client-releases.json"), "2026-02-03", "2026.10.13.01"));

        var prod = await Catalog(github).ListAsync(Channel.Prod);

        Assert.Equal(["2026.10.11.04-prerelease", "2026.04.08.14"], prod.Select(r => r.Tag));
    }

    [Fact]
    public async Task A_testing_tag_without_a_version_comes_after_those_with_one()
    {
        string json = Retag(Fixture("ovs-client-releases.json"), "OVS_Season_1", "OVS_Season_1-testing");
        json = Retag(json, "2026.10.11.04-prerelease", "2026.10.11.04-testing");
        var github = new FakeGitHub().Json(ClientPage1, json);

        var testing = await Catalog(github).ListAsync(Channel.Testing);

        Assert.Equal(["2026.10.11.04-testing", "OVS_Season_1-testing"], testing.Select(r => r.Tag));
        Assert.Null(testing[1].Version);
    }

    [Fact]
    public async Task Today_nothing_is_tagged_for_testing()
    {
        var github = new FakeGitHub().Json(ClientPage1, Fixture("ovs-client-releases.json"));

        Assert.Empty(await Catalog(github).ListAsync(Channel.Testing));
    }

    [Fact]
    public async Task An_assets_sha256_is_githubs_digest()
    {
        var github = new FakeGitHub().Json(ClientPage1, Fixture("ovs-client-releases.json"));
        string digest = (string)JsonNode.Parse(Fixture("ovs-client-releases.json"))!.AsArray()
            .Single(r => (string)r!["tag_name"]! == "2026.04.08.14")!["assets"]!.AsArray()
            .Single(a => ((string)a!["name"]!).EndsWith(".zip", StringComparison.Ordinal))!["digest"]!;

        var zip = (await Catalog(github).ListAsync(Channel.Prod)).Single(r => r.Tag == "2026.04.08.14").InstallZip!;

        Assert.Equal(digest, "sha256:" + zip.Sha256);
        Assert.Matches("^[0-9a-f]{64}$", zip.Sha256);
    }

    [Fact]
    public async Task Without_githubs_digest_an_asset_points_at_its_own_sha256_else_at_SHA256SUMS()
    {
        var releases = JsonNode.Parse(Fixture("ovs-client-releases.json"))!.AsArray();
        var assets = releases.Single(r => (string)r!["tag_name"]! == "2026.04.08.14")!["assets"]!.AsArray();
        var zip = assets.Single(a => ((string)a!["name"]!).EndsWith(".zip", StringComparison.Ordinal))!;
        string name = (string)zip["name"]!;
        zip.AsObject().Remove("digest");
        assets.Add(new JsonObject { ["name"] = name + ".sha256", ["size"] = 90, ["browser_download_url"] = "https://example.test/own.sha256" });
        assets.Add(new JsonObject { ["name"] = "SHA256SUMS", ["size"] = 200, ["browser_download_url"] = "https://example.test/SHA256SUMS" });
        assets.Add(new JsonObject { ["name"] = "OpenVersus_extra.asi", ["size"] = 5, ["browser_download_url"] = "https://example.test/extra.asi" });
        var github = new FakeGitHub().Json(ClientPage1, releases.ToJsonString());

        var release = (await Catalog(github).ListAsync(Channel.Prod)).Single(r => r.Tag == "2026.04.08.14");

        Assert.Null(release.InstallZip!.Sha256);
        Assert.Equal("https://example.test/own.sha256", release.InstallZip.ChecksumUrl!.ToString());
        Assert.Equal("https://example.test/SHA256SUMS", release.Assets.Single(a => a.Name == "OpenVersus_extra.asi").ChecksumUrl!.ToString());
        Assert.All((await Catalog(new FakeGitHub().Json(ClientPage1, Fixture("ovs-client-releases.json"))).ListAsync(Channel.Prod)).SelectMany(r => r.Assets),
            a => Assert.True(a.Sha256 == null || a.ChecksumUrl == null));
    }

    [Fact]
    public async Task Testing_paks_are_the_current_paks_release()
    {
        string clients = Retag(Fixture("ovs-client-releases.json"), "OVS_Season_1", "2026.10.12.01-testing");
        var current = JsonNode.Parse(Fixture("ovs-paks-releases.json"))!.AsArray()[0]!;
        current["tag_name"] = "paks-current";
        var github = new FakeGitHub().Json(ClientPage1, clients).Json(PaksCurrent, current.ToJsonString());
        var catalog = Catalog(github);

        var paks = await catalog.PaksForAsync((await catalog.ListAsync(Channel.Testing))[0]);

        Assert.Equal(10, paks.Count);
        Assert.Contains(paks, p => p.Name == "OVS_P.ucas" && p.Size == 344597072);
        Assert.All(paks, p => Assert.NotNull(p.Sha256));
    }

    [Fact]
    public async Task A_prod_releases_paks_are_its_own_and_today_there_are_none()
    {
        var github = new FakeGitHub().Json(ClientPage1, Fixture("ovs-client-releases.json"));
        var catalog = Catalog(github);

        var paks = await catalog.PaksForAsync((await catalog.ListAsync(Channel.Prod)).Single(r => r.Tag == "2026.04.08.14"));

        Assert.Empty(paks);
        Assert.DoesNotContain(github.Requests, r => r.RequestUri!.AbsolutePath.Contains("ovs-paks"));
    }

    [Fact]
    public async Task No_testing_paks_release_says_so()
    {
        string clients = Retag(Fixture("ovs-client-releases.json"), "OVS_Season_1", "2026.10.12.01-testing");
        var github = new FakeGitHub().Json(ClientPage1, clients);
        var catalog = Catalog(github);
        var release = (await catalog.ListAsync(Channel.Testing))[0];

        var e = await Assert.ThrowsAsync<ReleaseCatalogException>(() => catalog.PaksForAsync(release));

        Assert.Contains("paks-current", e.Message);
    }

    [Fact]
    public async Task A_used_up_rate_limit_says_until_when()
    {
        long reset = DateTimeOffset.UtcNow.AddMinutes(37).ToUnixTimeSeconds();
        var github = new FakeGitHub().Answer(ClientPage1, () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"message\":\"API rate limit exceeded for 203.0.113.7.\"}"),
            };
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", reset.ToString());
            return response;
        });

        var e = await Assert.ThrowsAsync<ReleaseCatalogException>(() => Catalog(github).ListAsync(Channel.Prod));

        Assert.Contains(DateTimeOffset.FromUnixTimeSeconds(reset).ToLocalTime().ToString("HH:mm"), e.Message);
    }

    [Fact]
    public async Task A_timeout_is_github_not_answering_not_a_cancellation()
    {
        // HttpClient's own timeout, for real: it surfaces as a cancellation nobody requested.
        var github = new FakeGitHub { Delay = ct => Task.Delay(Timeout.Infinite, ct) };

        var e = await Assert.ThrowsAsync<ReleaseCatalogException>(() => Catalog(github, TimeSpan.FromMilliseconds(200)).ListAsync(Channel.Prod));

        Assert.Contains("did not answer in time", e.Message);
    }

    [Fact]
    public async Task A_cancellation_the_caller_asked_for_stays_a_cancellation()
    {
        var github = new FakeGitHub { Delay = ct => Task.Delay(Timeout.Infinite, ct) };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Catalog(github).ListAsync(Channel.Prod, cancel.Token));
    }

    [Fact]
    public async Task A_full_page_means_another_page()
    {
        // A first page of exactly 100 releases (copies of the real prod one under other tags), then the real list.
        var prod = JsonNode.Parse(Fixture("ovs-client-releases.json"))!.AsArray().Single(r => (string)r!["tag_name"]! == "2026.04.08.14")!;
        var page = new JsonArray();
        for (int i = 0; i < 100; i++)
        {
            var copy = prod.DeepClone();
            copy["tag_name"] = $"2025.01.01.{i:00}";
            page.Add(copy);
        }
        var github = new FakeGitHub().Json(ClientPage1, page.ToJsonString()).Json(ClientPage2, Fixture("ovs-client-releases.json"));

        var releases = await Catalog(github).ListAsync(Channel.Prod);

        Assert.Equal(102, releases.Count);
        Assert.Equal("2026.10.11.04-prerelease", releases[0].Tag);
        Assert.Equal(2, github.Requests.Count);
    }

    [Fact]
    public async Task Every_request_names_the_buddy()
    {
        var github = new FakeGitHub().Json(ClientPage1, Fixture("ovs-client-releases.json"));

        await Catalog(github).ListAsync(Channel.Prod);

        string version = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "VERSION")).Trim();
        Assert.Contains(github.Requests[0].Headers.UserAgent, p => p.Product?.Name == "mvs-buddy" && p.Product.Version == version);
    }

    [Theory]
    [InlineData("2026.04.08.14", Channel.Prod)]
    [InlineData("2026.10.12.01-testing", Channel.Testing)]
    [InlineData("OVS_Season_1-testing", Channel.Testing)]
    [InlineData("2026.10.11.04-prerelease", Channel.Prod)]
    [InlineData("OVS_Season_1-prerelease", null)]
    [InlineData("2026.10.11.04-prerelease-testing", Channel.Testing)]
    [InlineData("2026.10.11.04-test1", null)]
    [InlineData("OVS_Season_1", null)]
    [InlineData("v2026.04.08.14", null)]
    [InlineData("2026.4.8.14", null)]
    [InlineData("2026.10.12.01-testing-2", null)]
    public void Tags_belong_to_a_channel_by_their_shape(string tag, Channel? expected) =>
        Assert.Equal(expected, ChannelRules.Classify(tag));
}
