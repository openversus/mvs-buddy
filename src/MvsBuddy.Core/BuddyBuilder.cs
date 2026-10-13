using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MvsBuddy.Catalog;
using MvsBuddy.Games;

namespace MvsBuddy;

/// <summary>
/// Configures and creates a <see cref="Buddy"/>. Every setting has a default that points at OpenVersus's own
/// repositories, so <c>new BuddyBuilder().Build()</c> is a working buddy.
/// </summary>
public sealed class BuddyBuilder
{
    private ILoggerFactory loggerFactory = NullLoggerFactory.Instance;
    private HttpClient? http;
    private GitHubRepository clientReleases = new("openversus", "ovs-client");
    private GitHubRepository paks = new("openversus", "ovs-paks");
    private string testingPaksTag = "paks-current";
    private HostSystem? host;

    /// <summary>Where the buddy logs. Default: nowhere.</summary>
    public BuddyBuilder Logging(ILoggerFactory factory)
    {
        loggerFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    /// <summary>
    /// The HTTP client for GitHub and downloads. The caller keeps ownership: the buddy does not dispose it. Default: one
    /// of the buddy's own, disposed with it.
    /// </summary>
    public BuddyBuilder Http(HttpClient client)
    {
        http = client ?? throw new ArgumentNullException(nameof(client));
        return this;
    }

    /// <summary>The repository whose releases are the mod's. Default <c>openversus/ovs-client</c>.</summary>
    public BuddyBuilder ClientReleases(string owner, string repository)
    {
        clientReleases = new GitHubRepository(owner, repository);
        return this;
    }

    /// <summary>
    /// Where the testing channel's paks come from: the release tagged <paramref name="testingTag"/> in
    /// <paramref name="owner"/>/<paramref name="repository"/>, kept current by replacing its assets. Default
    /// <c>openversus/ovs-paks</c>, tag <c>paks-current</c>. Prod releases carry their own paks.
    /// </summary>
    public BuddyBuilder TestingPaks(string owner, string repository, string testingTag)
    {
        paks = new GitHubRepository(owner, repository);
        testingPaksTag = string.IsNullOrEmpty(testingTag) ? throw new ArgumentException("The tag is empty.", nameof(testingTag)) : testingTag;
        return this;
    }

    /// <summary>Stands in for this machine (tests).</summary>
    internal BuddyBuilder Host(HostSystem system)
    {
        host = system;
        return this;
    }

    /// <summary>Creates the buddy.</summary>
    public Buddy Build()
    {
        bool ownsHttp = http == null;
        var client = http ?? new HttpClient();
        var releases = new ReleaseCatalog(client, clientReleases, paks, testingPaksTag, loggerFactory.CreateLogger<ReleaseCatalog>());
        var games = new GameFinder(host ?? HostSystem.Current(), loggerFactory.CreateLogger<GameFinder>());
        return new Buddy(releases, games, ownsHttp ? client : null);
    }
}
