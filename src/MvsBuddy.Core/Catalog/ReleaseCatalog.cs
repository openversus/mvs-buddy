using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;

namespace MvsBuddy.Catalog;

/// <summary>
/// The mod's releases on each channel, read from GitHub's API, and the paks that go with each. Every call asks GitHub
/// again, so a re-uploaded testing pak is seen at once.
/// </summary>
public sealed class ReleaseCatalog
{
    private const int PageSize = 100;
    private const int MaxPages = 10;
    private static readonly Uri ApiBase = new("https://api.github.com/");
    private static readonly string[] PakExtensions = [".pak", ".utoc", ".ucas", ".sig"];

    private readonly HttpClient http;
    private readonly GitHubRepository clientReleases;
    private readonly GitHubRepository paks;
    private readonly string testingPaksTag;
    private readonly ILogger<ReleaseCatalog> log;

    internal ReleaseCatalog(HttpClient http, GitHubRepository clientReleases, GitHubRepository paks, string testingPaksTag, ILogger<ReleaseCatalog> log)
    {
        this.http = http;
        this.clientReleases = clientReleases;
        this.paks = paks;
        this.testingPaksTag = testingPaksTag;
        this.log = log;
    }

    /// <summary>
    /// The releases on <paramref name="channel"/>, newest version first, a full release before a prerelease of the same
    /// version (a testing release without a version in its tag comes after those with one, newest first). Drafts and tags
    /// that belong to neither channel are left out.
    /// </summary>
    /// <exception cref="ReleaseCatalogException">GitHub could not be reached, refused, or answered something unreadable.</exception>
    public async Task<IReadOnlyList<Release>> ListAsync(Channel channel, CancellationToken cancellationToken = default)
    {
        var releases = new List<Release>();
        int seen = 0;
        for (int page = 1; page <= MaxPages; page++)
        {
            var batch = await GetAsync(
                $"repos/{clientReleases.Owner}/{clientReleases.Name}/releases?per_page={PageSize}&page={page}",
                GitHubJsonContext.Default.ListGitHubRelease, $"There is no repository {clientReleases} on GitHub.", cancellationToken).ConfigureAwait(false);
            seen += batch.Count;
            foreach (var release in batch)
            {
                if (!release.Draft && ChannelRules.Classify(release.TagName) == channel)
                {
                    releases.Add(ToRelease(release, channel));
                }
            }
            if (batch.Count < PageSize)
            {
                break;
            }
        }
        log.LogInformation("{Count} {Channel} releases among {Seen} in {Repository}", releases.Count, channel, seen, clientReleases);
        return releases.OrderByDescending(r => r.Version).ThenBy(r => r.IsPrerelease).ThenByDescending(r => r.PublishedAt).ToList();
    }

    /// <summary>
    /// The paks <paramref name="release"/> needs (<c>.pak</c>, <c>.utoc</c>, <c>.ucas</c>, <c>.sig</c>): a prod release's
    /// own, or for a testing release those currently on the testing paks release.
    /// </summary>
    /// <exception cref="ReleaseCatalogException">GitHub could not be reached, refused, or has no testing paks release.</exception>
    public async Task<IReadOnlyList<ReleaseAsset>> PaksForAsync(Release release, CancellationToken cancellationToken = default)
    {
        if (release == null)
        {
            throw new ArgumentNullException(nameof(release));
        }
        if (release.Channel == Channel.Prod)
        {
            return release.Assets.Where(IsPak).ToList();
        }
        var current = await GetAsync(
            $"repos/{paks.Owner}/{paks.Name}/releases/tags/{Uri.EscapeDataString(testingPaksTag)}",
            GitHubJsonContext.Default.GitHubRelease, $"{paks} has no release tagged {testingPaksTag}, where the testing paks live.",
            cancellationToken).ConfigureAwait(false);
        var files = ToRelease(current, Channel.Testing).Assets.Where(IsPak).ToList();
        log.LogInformation("{Count} testing paks on {Repository} {Tag}", files.Count, paks, testingPaksTag);
        return files;
    }

    /// <summary>
    /// The text of a file in the mod's repository at <paramref name="tag"/> (such as <c>sample.toml</c>, the defaults of
    /// that version), or null when it cannot be had. Nothing depends on it being there.
    /// </summary>
    public async Task<string?> FileAtTagAsync(string tag, string path, CancellationToken cancellationToken = default)
    {
        var url = new Uri($"https://raw.githubusercontent.com/{clientReleases.Owner}/{clientReleases.Name}/{Uri.EscapeDataString(tag)}/{path}");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("mvs-buddy", BuddyVersion.Current));
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync().ConfigureAwait(false) : null;
        }
        catch (Exception e) when (e is HttpRequestException || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            log.LogInformation("{Url} could not be had: {Reason}", url, e.Message);
            return null;
        }
    }

    private static bool IsPak(ReleaseAsset asset) =>
        PakExtensions.Any(extension => asset.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static Release ToRelease(GitHubRelease release, Channel channel) => new(
        release.TagName,
        string.IsNullOrEmpty(release.Name) ? release.TagName : release.Name!,
        channel,
        ChannelRules.VersionOf(release.TagName),
        release.PublishedAt,
        release.Assets.Select(asset => new ReleaseAsset(asset.Name, asset.Size, new Uri(asset.BrowserDownloadUrl), Sha256Of(asset.Digest))).ToList());

    private static string? Sha256Of(string? digest)
    {
        const string prefix = "sha256:";
        return digest != null && digest.StartsWith(prefix, StringComparison.Ordinal)
            ? digest.Substring(prefix.Length).ToLowerInvariant()
            : null;
    }

    private async Task<T> GetAsync<T>(string path, JsonTypeInfo<T> type, string notFound, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBase, path));
        // GitHub refuses a request without a User-Agent.
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("mvs-buddy", BuddyVersion.Current));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        log.LogDebug("GET {Uri}", request.RequestUri);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw Fail("GitHub could not be reached. Check the internet connection, then try again.", e);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient reports its own timeout as a cancellation nobody asked for.
            throw Fail("GitHub did not answer in time. Try again in a moment.", e);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw Fail(Refusal(response, notFound));
            }
            try
            {
                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                return await JsonSerializer.DeserializeAsync(stream, type, cancellationToken).ConfigureAwait(false)
                    ?? throw Fail("GitHub answered with nothing.");
            }
            catch (JsonException e)
            {
                throw Fail("GitHub's answer could not be read.", e);
            }
        }
    }

    private ReleaseCatalogException Fail(string message, Exception? inner = null)
    {
        log.LogWarning(inner, "{Message}", message);
        return new ReleaseCatalogException(message, inner);
    }

    private static string Refusal(HttpResponseMessage response, string notFound)
    {
        var status = response.StatusCode;
        if (status == HttpStatusCode.NotFound)
        {
            return notFound;
        }
        if ((status == HttpStatusCode.Forbidden || (int)status == 429) && Header(response, "X-RateLimit-Remaining") == "0")
        {
            string? reset = Header(response, "X-RateLimit-Reset");
            if (long.TryParse(reset, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds))
            {
                var until = DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
                return $"GitHub's limit on requests from this network is used up until {until:HH:mm}. Try again then.";
            }
            return "GitHub's limit on requests from this network is used up. Try again in an hour.";
        }
        return $"GitHub answered {(int)status} {response.ReasonPhrase}.";
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
