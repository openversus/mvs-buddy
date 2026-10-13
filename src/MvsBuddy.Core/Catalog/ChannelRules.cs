using System.Text.RegularExpressions;

namespace MvsBuddy.Catalog;

/// <summary>Which channel a tag belongs to, and the version in it.</summary>
internal static class ChannelRules
{
    internal const string TestingSuffix = "-testing";

    private static readonly Regex ProdTag = new("^[0-9]{4}\\.[0-9]{2}\\.[0-9]{2}\\.[0-9]{2}$", RegexOptions.CultureInvariant);

    /// <summary>Prod for a tag that is exactly a version, testing for one ending in <c>-testing</c>, null otherwise.</summary>
    internal static Channel? Classify(string tag)
    {
        if (ProdTag.IsMatch(tag))
        {
            return Channel.Prod;
        }
        if (tag.EndsWith(TestingSuffix, StringComparison.Ordinal))
        {
            return Channel.Testing;
        }
        return null;
    }

    /// <summary>The version a tag names (a testing tag without its suffix), or null when it names none.</summary>
    internal static Version? VersionOf(string tag)
    {
        string text = tag.EndsWith(TestingSuffix, StringComparison.Ordinal) ? tag.Substring(0, tag.Length - TestingSuffix.Length) : tag;
        return ProdTag.IsMatch(text) && Version.TryParse(text, out var version) ? version : null;
    }
}
