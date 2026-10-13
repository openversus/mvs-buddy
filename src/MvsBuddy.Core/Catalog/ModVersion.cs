namespace MvsBuddy.Catalog;

/// <summary>The mod's versions as its tags write them.</summary>
public static class ModVersion
{
    /// <summary>
    /// <paramref name="version"/> the way tags and file names write it, two digits after the year:
    /// <c>2026.10.11.04</c>, where <see cref="Version.ToString()"/> would give <c>2026.10.11.4</c>.
    /// </summary>
    public static string Text(Version version)
    {
        if (version == null)
        {
            throw new ArgumentNullException(nameof(version));
        }
        return $"{version.Major}.{version.Minor:00}.{Math.Max(version.Build, 0):00}.{Math.Max(version.Revision, 0):00}";
    }
}
