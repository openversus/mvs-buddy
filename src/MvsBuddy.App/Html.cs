namespace MvsBuddy.App;

/// <summary>Text made safe to put inside RML.</summary>
internal static class Html
{
    public static string Text(string? text) => (text ?? "")
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
}
