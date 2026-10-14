using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using MvsBuddy.Catalog;

namespace MvsBuddy.Changes;

/// <summary>A pak a set holds: its name, size, checksum and where it came from.</summary>
public sealed record PakEntry(string Name, long Size, string Sha256, string? Url);

/// <summary>A release's pak, checked and ready to place: where its bytes are now, and whether they were just downloaded.</summary>
internal sealed record StagedPak(string Name, long Size, string Sha256, string? Url, string Path, bool Downloaded)
{
    /// <summary>Whether it was checked against a checksum; false for a pak GitHub and the release give none for.</summary>
    public bool Checked { get; init; } = true;
}

/// <summary>
/// The channel paks, outside any git repository: <c>paks/&lt;set&gt;/</c> in the buddy's folder (prod, testing, mvsi),
/// one set each, replaced as it is when its release changes, with a <c>manifest.json</c> naming what it holds. The live
/// channel's set is in the game's paks folder instead; moving sets between the two is a change's job, not this.
/// </summary>
internal sealed class PakStore
{
    internal const string ManifestFile = "manifest.json";

    private readonly HostSystem host;
    private readonly Downloads downloads;
    private readonly ILogger<PakStore> log;

    internal PakStore(HostSystem host, Downloads downloads, ILogger<PakStore> log)
    {
        this.host = host;
        this.downloads = downloads;
        this.log = log;
    }

    /// <summary>The folder every set is in, which need not exist yet.</summary>
    internal string Root => string.IsNullOrEmpty(host.DataDirectory)
        ? throw new ChangeException("The buddy has no folder of its own to keep paks in.")
        : Path.Combine(host.DataDirectory, "paks");

    /// <summary>The set's folder, which need not exist yet.</summary>
    internal string Folder(string set) => Path.Combine(Root, set);

    /// <summary>What the set's manifest says it holds; empty when it has none or it cannot be read.</summary>
    internal IReadOnlyList<PakEntry> Read(string set)
    {
        string manifest = Path.Combine(Folder(set), ManifestFile);
        try
        {
            return File.Exists(manifest) ? JsonSerializer.Deserialize(File.ReadAllBytes(manifest), PakJsonContext.Default.ListPakEntry) ?? [] : [];
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException)
        {
            log.LogWarning(e, "Could not read {Manifest}", manifest);
            return [];
        }
    }

    /// <summary>A manifest naming <paramref name="paks"/>.</summary>
    internal static byte[] Manifest(IEnumerable<PakEntry> paks) =>
        JsonSerializer.SerializeToUtf8Bytes(paks.ToList(), PakJsonContext.Default.ListPakEntry);

    /// <summary>
    /// The release's paks, each checked: a copy already at hand in one of <paramref name="lookIn"/> (the same name, size
    /// and checksum) is used where it is; the others are downloaded. A pak GitHub gives no checksum for is downloaded every
    /// time, since a copy at hand cannot be told from it. Nothing is moved.
    /// </summary>
    internal async Task<IReadOnlyList<StagedPak>> StageAsync(IReadOnlyList<ReleaseAsset> assets, IEnumerable<string> lookIn,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var folders = lookIn.Where(Directory.Exists).ToList();
        var staged = new List<StagedPak>();
        foreach (var listed in assets)
        {
            var asset = await downloads.WithChecksumAsync(listed, cancellationToken).ConfigureAwait(false);
            string? atHand = asset.Sha256 == null ? null : folders.Select(f => Path.Combine(f, asset.Name))
                .FirstOrDefault(p => File.Exists(p) && new FileInfo(p).Length == asset.Size && Checked(p, asset.Name, asset.Sha256!, progress));
            if (atHand != null)
            {
                staged.Add(new StagedPak(asset.Name, asset.Size, asset.Sha256!, asset.DownloadUrl.ToString(), atHand, false));
                continue;
            }
            progress?.Report($"Downloading {asset.Name}");
            string path = await downloads.FetchAsync(asset, Installer.Percent.Of(progress, asset), cancellationToken).ConfigureAwait(false);
            staged.Add(new StagedPak(asset.Name, new FileInfo(path).Length, asset.Sha256 ?? Downloads.Sha256Of(path), asset.DownloadUrl.ToString(), path, true)
            {
                Checked = asset.Sha256 != null,
            });
        }
        log.LogInformation("Paks staged: {AtHand} at hand, {Downloaded} downloaded", staged.Count(p => !p.Downloaded), staged.Count(p => p.Downloaded));
        return staged;
    }

    /// <summary>
    /// The paks of <paramref name="wanted"/> found in <paramref name="lookIn"/>, the first folder first: the same name, size
    /// and checksum. Nothing is downloaded; <paramref name="missing"/> gets the ones found nowhere.
    /// </summary>
    internal IReadOnlyList<StagedPak> AtHand(IEnumerable<PakEntry> wanted, IEnumerable<string> lookIn, out IReadOnlyList<PakEntry> missing,
        IProgress<string>? progress = null)
    {
        var folders = lookIn.Where(Directory.Exists).ToList();
        var found = new List<StagedPak>();
        var notFound = new List<PakEntry>();
        foreach (var pak in wanted)
        {
            string? atHand = folders.Select(f => Path.Combine(f, pak.Name)).FirstOrDefault(p => File.Exists(p) && new FileInfo(p).Length == pak.Size
                && Checked(p, pak.Name, pak.Sha256, progress));
            if (atHand == null)
            {
                notFound.Add(pak);
            }
            else
            {
                found.Add(new StagedPak(pak.Name, pak.Size, pak.Sha256, pak.Url, atHand, false));
            }
        }
        missing = notFound;
        return found;
    }

    private static bool Checked(string path, string name, string sha256, IProgress<string>? progress)
    {
        progress?.Report($"Checking {name}");
        return Downloads.Sha256Of(path) == sha256;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(List<PakEntry>))]
internal sealed partial class PakJsonContext : JsonSerializerContext
{
}
