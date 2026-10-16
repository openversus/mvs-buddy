using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using MvsBuddy.Catalog;

namespace MvsBuddy;

/// <summary>A newer MVS Buddy than this one, on GitHub: its version and the archive for this platform.</summary>
public sealed record Update(Version Version, string Tag, ReleaseAsset Asset);

/// <summary>
/// Looking for a newer MVS Buddy and fetching it, each only when the person wants (<see cref="Preferences.CheckUpdates"/>,
/// <see cref="Preferences.AutoUpdate"/>): MVS Buddy never replaces itself under anyone by default. Fetched, the new copy
/// is started with this one's arguments and this one exits; being newer, it installs itself in the known place
/// (<see cref="SelfInstall"/>) and runs from there.
/// </summary>
public sealed class Updates
{
    private readonly HostSystem host;
    private readonly ReleaseCatalog catalog;
    private readonly Downloads downloads;
    private readonly ILogger<Updates> log;

    internal Updates(HostSystem host, ReleaseCatalog catalog, Downloads downloads, ILogger<Updates> log)
    {
        this.host = host;
        this.catalog = catalog;
        this.downloads = downloads;
        this.log = log;
    }

    /// <summary>This copy's version, as the comparison sees it.</summary>
    public static Version? Current => SelfInstall.Parse(BuddyVersion.Current);

    /// <summary>
    /// The newer MVS Buddy on GitHub, or null when this one is as new (or the release carries no archive for this
    /// platform, which is logged). One request.
    /// </summary>
    /// <exception cref="ReleaseCatalogException">GitHub could not be reached, refused, or has no release.</exception>
    public async Task<Update?> CheckAsync(CancellationToken cancellationToken = default)
    {
        var release = await catalog.LatestBuddyReleaseAsync(cancellationToken).ConfigureAwait(false);
        var theirs = SelfInstall.Parse(release.Tag);
        if (theirs == null || Current is not Version ours || theirs <= ours)
        {
            log.LogInformation("MVS Buddy {Ours} is as new as the latest release {Tag}", BuddyVersion.Current, release.Tag);
            return null;
        }
        string name = ArchiveName(release.Tag);
        var asset = release.Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        if (asset == null)
        {
            log.LogWarning("Release {Tag} has no {Name}; nothing to update with", release.Tag, name);
            return null;
        }
        return new Update(theirs, release.Tag, asset);
    }

    /// <summary>The archive a release carries for this platform, as scripts/package.sh names it.</summary>
    internal string ArchiveName(string tag) => host.IsWindows ? $"MvsBuddy-{tag}-win-x64.zip" : $"MvsBuddy-{tag}-linux-x64.tar.gz";

    /// <summary>
    /// Fetches <paramref name="update"/>'s archive (checked against the release's digest), unpacks the one executable in
    /// it into MVS Buddy's downloads folder, runnable and without the mark of the web, and returns its path.
    /// </summary>
    /// <exception cref="DownloadException">It could not be fetched, did not match, or held no executable.</exception>
    public async Task<string> DownloadAsync(Update update, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (update == null)
        {
            throw new ArgumentNullException(nameof(update));
        }
        progress?.Report($"Downloading {update.Asset.Name}");
        string archive = await downloads.FetchAsync(update.Asset, Changes.Installer.Percent.Of(progress, update.Asset), cancellationToken).ConfigureAwait(false);
        string folder = Path.Combine(host.DataDirectory, "downloads", "update", update.Tag);
        string executable = Path.Combine(folder, host.IsWindows ? "MvsBuddy.exe" : "MvsBuddy");
        progress?.Report($"Unpacking {update.Asset.Name}...");
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
        Directory.CreateDirectory(folder);
        if (host.IsWindows)
        {
            using var zip = ZipFile.OpenRead(archive);
            var entry = zip.GetEntry("MvsBuddy.exe") ?? throw new DownloadException($"{update.Asset.Name} holds no MvsBuddy.exe.");
            entry.ExtractToFile(executable);
        }
        else
        {
            Untar(archive, folder);
            if (!File.Exists(executable))
            {
                throw new DownloadException($"{update.Asset.Name} holds no MvsBuddy.");
            }
            SelfInstall.Chmod(executable, 0x1ED);
        }
        Changes.Installer.RemoveMarkOfTheWeb(executable, null);
        log.LogInformation("MVS Buddy {Tag} unpacked at {Path}", update.Tag, executable);
        return executable;
    }

    /// <summary>Starts the fetched <paramref name="executable"/> with <paramref name="args"/>; the caller then exits.</summary>
    public static void Start(string executable, IEnumerable<string> args) => SelfInstall.Start(executable, args);

    // The tar.gz through the system's tar, which every Linux has: .NET's own tar reader is not in netstandard2.0.
    private static void Untar(string archive, string into)
    {
        var info = new ProcessStartInfo("tar", SelfInstall.Quote(["-xzf", archive, "-C", into]))
        {
            UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true,
        };
        using var tar = Process.Start(info) ?? throw new DownloadException("tar could not be started to unpack the update.");
        string error = tar.StandardError.ReadToEnd();
        tar.WaitForExit();
        if (tar.ExitCode != 0)
        {
            throw new DownloadException($"{Path.GetFileName(archive)} could not be unpacked: {error.Trim()}");
        }
    }
}
