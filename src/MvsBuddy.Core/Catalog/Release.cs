namespace MvsBuddy.Catalog;

/// <summary>A release of the mod on one channel.</summary>
/// <param name="Tag">The release's git tag.</param>
/// <param name="Name">The release's title on GitHub.</param>
/// <param name="Channel">Which servers it is for, from the tag.</param>
/// <param name="Version">The version in the tag, or null when the tag has none (a testing tag may not).</param>
/// <param name="PublishedAt">When it was published.</param>
/// <param name="Assets">Its files.</param>
public sealed record Release(string Tag, string Name, Channel Channel, Version? Version, DateTimeOffset? PublishedAt, IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>The install zip (<c>OpenVersus_*.zip</c>), or null when the release has none.</summary>
    public ReleaseAsset? InstallZip => Assets.FirstOrDefault(a =>
        a.Name.StartsWith("OpenVersus_", StringComparison.Ordinal) && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
}

/// <summary>A file attached to a release.</summary>
/// <param name="Name">The file name.</param>
/// <param name="Size">Its size in bytes.</param>
/// <param name="DownloadUrl">Where to download it.</param>
/// <param name="Sha256">Its SHA-256 as GitHub computed it on upload, in lowercase hex; null when GitHub gave none.</param>
public sealed record ReleaseAsset(string Name, long Size, Uri DownloadUrl, string? Sha256);
