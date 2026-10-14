using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MvsBuddy.Catalog;
using MvsBuddy.Changes;
using MvsBuddy.Games;
using MvsBuddy.Installs;

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
    private AsiLoaderPin loaderPin = AsiLoaderPin.Default;

    /// <summary>
    /// The buddy's own folder on this machine (<c>%LOCALAPPDATA%\mvs-buddy</c>, or <c>~/.local/share/mvs-buddy</c>), where
    /// it keeps downloads and held files, and where an app can put its logs.
    /// </summary>
    public static string DefaultDataDirectory => HostSystem.Current().DataDirectory;

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

    /// <summary>The Ultimate ASI Loader build to install and to recognise. Default <see cref="AsiLoaderPin.Default"/>.</summary>
    public BuddyBuilder AsiLoader(AsiLoaderPin pin)
    {
        loaderPin = pin ?? throw new ArgumentNullException(nameof(pin));
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
        var system = host ?? HostSystem.Current();
        var games = new GameFinder(system, loggerFactory.CreateLogger<GameFinder>());
        var installs = new InstallReader(system, loaderPin, loggerFactory.CreateLogger<InstallReader>());
        var changes = new InstallChanges(system, loggerFactory.CreateLogger<InstallChanges>());
        var downloads = new Downloads(client, system, loggerFactory.CreateLogger<Downloads>());
        var installer = new Installer(system, releases, downloads, changes, loaderPin, loggerFactory.CreateLogger<Installer>());
        var stock = new StockCheck(system, loggerFactory.CreateLogger<StockCheck>());
        var permissions = new Permissions(system, loggerFactory.CreateLogger<Permissions>());
        var cache = new ReleaseCache(system, downloads, loaderPin, loggerFactory.CreateLogger<ReleaseCache>());
        var pakStore = new PakStore(system, downloads, loggerFactory.CreateLogger<PakStore>());
        return new Buddy(releases, games, installs, changes, downloads, installer, stock, permissions, cache, pakStore, ownsHttp ? client : null);
    }
}
