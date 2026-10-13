using System.Text.Json.Serialization;

namespace MvsBuddy.Catalog;

/// <summary>The parts of GitHub's release JSON the buddy reads.</summary>
internal sealed class GitHubRelease
{
    public string TagName { get; set; } = "";

    public string? Name { get; set; }

    public bool Draft { get; set; }

    public bool Prerelease { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public List<GitHubAsset> Assets { get; set; } = [];
}

/// <summary>The parts of GitHub's release asset JSON the buddy reads.</summary>
internal sealed class GitHubAsset
{
    public string Name { get; set; } = "";

    public long Size { get; set; }

    public string BrowserDownloadUrl { get; set; } = "";

    /// <summary><c>sha256:&lt;hex&gt;</c>, computed by GitHub on upload.</summary>
    public string? Digest { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(List<GitHubRelease>))]
[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext
{
}
