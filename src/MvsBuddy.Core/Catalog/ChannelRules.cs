using System.Text.RegularExpressions;

namespace MvsBuddy.Catalog;

/// <summary>Which channel a tag belongs to, and the version in it.</summary>
internal static class ChannelRules
{
    internal const string TestingSuffix = "-testing";
    internal const string PrereleaseSuffix = "-prerelease";

    private static readonly Regex ProdTag = new("^[0-9]{4}\\.[0-9]{2}\\.[0-9]{2}\\.[0-9]{2}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Prod for a tag that is exactly a version, or a version followed by <c>-prerelease</c>; testing for one ending in
    /// <c>-testing</c>; null otherwise.
    /// </summary>
    internal static Channel? Classify(string tag)
    {
        if (ProdTag.IsMatch(tag) || (IsPrerelease(tag) && ProdTag.IsMatch(Strip(tag, PrereleaseSuffix))))
        {
            return Channel.Prod;
        }
        if (tag.EndsWith(TestingSuffix, StringComparison.Ordinal))
        {
            return Channel.Testing;
        }
        return null;
    }

    /// <summary>Whether a tag is a prod prerelease: a version followed by <c>-prerelease</c>.</summary>
    internal static bool IsPrerelease(string tag) => tag.EndsWith(PrereleaseSuffix, StringComparison.Ordinal);

    /// <summary>The version a tag names (without a <c>-testing</c> or <c>-prerelease</c> suffix), or null when it names none.</summary>
    internal static Version? VersionOf(string tag)
    {
        string text = Strip(Strip(tag, TestingSuffix), PrereleaseSuffix);
        return ProdTag.IsMatch(text) && Version.TryParse(text, out var version) ? version : null;
    }

    private static string Strip(string tag, string suffix) =>
        tag.EndsWith(suffix, StringComparison.Ordinal) ? tag.Substring(0, tag.Length - suffix.Length) : tag;
}
