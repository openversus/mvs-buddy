using System.Text.Json;
using System.Text.Json.Serialization;
using MvsBuddy.Games;

namespace MvsBuddy.Changes;

/// <summary>A file held aside: where it was, where it goes back to, and whether it is still held.</summary>
/// <param name="Original">Where it was when it was held.</param>
/// <param name="RestoreTo">Where restoring puts it: where it was, except the mod's paks found in the game's folders, which go to the profile.</param>
/// <param name="HeldPath">Where it is kept meanwhile.</param>
public sealed record HeldEntry(string Original, string RestoreTo, string HeldPath)
{
    /// <summary>Whether the file is still held (a restore that stopped part way leaves some entries already back).</summary>
    public bool IsHeld => File.Exists(HeldPath);
}

/// <summary>A set of files held aside together for one copy of the game, such as OpenVersus or MVS Infinite.</summary>
/// <param name="Name">The set's name.</param>
/// <param name="Folder">Where its files are kept, under the buddy's folder in the profile.</param>
/// <param name="HeldAt">When it was held; null when <paramref name="IsRecorded"/> is false.</param>
/// <param name="Entries">Its files.</param>
/// <param name="IsRecorded">
/// False for files that were being held when something stopped the buddy before it wrote down what it held: the files at
/// their original places are the real ones, and these can be discarded.
/// </param>
public sealed record HeldSet(string Name, string Folder, DateTimeOffset? HeldAt, IReadOnlyList<HeldEntry> Entries, bool IsRecorded)
{
    /// <summary>
    /// For the set an install keeps of what it replaced: the files that install put in place. Restoring the set takes them
    /// away (with any OpenVersus plugin the set does not put back) before it puts the held files back, so the copy is as it
    /// was before the install. Empty for other sets.
    /// </summary>
    public IReadOnlyList<string> Placed { get; init; } = [];
}

/// <summary>
/// Where held files are kept: <c>&lt;the buddy's folder&gt;/held/&lt;copy&gt;/&lt;set&gt;/</c>, never anywhere the game or
/// Ultimate ASI Loader reads. Each set's <c>manifest.json</c> is written last, so a set without one never finished holding.
/// </summary>
internal sealed class HeldArea(HostSystem host)
{
    internal const string ManifestFile = "manifest.json";

    /// <summary>The held sets of <paramref name="game"/>; none when the buddy has no folder of its own.</summary>
    internal IReadOnlyList<HeldSet> Read(GameInstall game)
    {
        string? copy = CopyFolder(game);
        if (copy == null || !Directory.Exists(copy))
        {
            return [];
        }
        var sets = new List<HeldSet>();
        foreach (string folder in Directory.EnumerateDirectories(copy).OrderBy(f => f, StringComparer.Ordinal))
        {
            string manifest = Path.Combine(folder, ManifestFile);
            HeldManifest? record = null;
            if (File.Exists(manifest))
            {
                try
                {
                    record = JsonSerializer.Deserialize(File.ReadAllText(manifest), HeldJsonContext.Default.HeldManifest);
                }
                catch (JsonException)
                {
                    record = null;
                }
            }
            sets.Add(record == null
                ? new HeldSet(Path.GetFileName(folder), folder, null, [], false)
                : new HeldSet(record.Set, folder, record.HeldAt,
                    record.Entries.Select(e => new HeldEntry(e.Original, e.RestoreTo, Path.Combine(folder, e.Held))).ToList(), true)
                {
                    Placed = record.Placed,
                });
        }
        return sets;
    }

    /// <summary>The folder for one copy of the game: its <c>Win64</c> path, hashed, so two copies never share one.</summary>
    internal string? CopyFolder(GameInstall game)
    {
        if (string.IsNullOrEmpty(host.DataDirectory))
        {
            return null;
        }
        return Path.Combine(host.DataDirectory, "held", GameStates.CopyKey(game.Win64Directory, host.IsWindows));
    }

    /// <summary>A set's folder: its name with anything a file system dislikes replaced.</summary>
    internal string? SetFolder(GameInstall game, string set)
    {
        string? copy = CopyFolder(game);
        if (copy == null)
        {
            return null;
        }
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':']).ToArray();
        return Path.Combine(copy, new string(set.Select(c => invalid.Contains(c) ? '_' : c).ToArray()));
    }

    internal static byte[] Manifest(GameInstall game, string set, DateTimeOffset heldAt, IEnumerable<(string Original, string RestoreTo, string Held)> entries,
        IEnumerable<string>? placed = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new HeldManifest
        {
            Game = game.Win64Directory,
            Set = set,
            HeldAt = heldAt,
            Entries = entries.Select(e => new HeldManifestEntry { Original = e.Original, RestoreTo = e.RestoreTo, Held = e.Held }).ToList(),
            Placed = placed?.ToList() ?? [],
        }, HeldJsonContext.Default.HeldManifest);
}

internal sealed class HeldManifest
{
    public string Game { get; set; } = "";

    public string Set { get; set; } = "";

    public DateTimeOffset HeldAt { get; set; }

    public List<HeldManifestEntry> Entries { get; set; } = [];

    /// <summary>What the install this set was made by put in place; empty for other sets and in older manifests.</summary>
    public List<string> Placed { get; set; } = [];
}

internal sealed class HeldManifestEntry
{
    public string Original { get; set; } = "";

    public string RestoreTo { get; set; } = "";

    /// <summary>The held file, relative to the set's folder.</summary>
    public string Held { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(HeldManifest))]
internal sealed partial class HeldJsonContext : JsonSerializerContext
{
}
