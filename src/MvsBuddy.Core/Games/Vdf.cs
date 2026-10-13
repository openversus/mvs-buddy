using System.Text;

namespace MvsBuddy.Games;

/// <summary>Valve's KeyValues text (<c>.vdf</c>, <c>.acf</c>): quoted keys, quoted values or braced children.</summary>
internal sealed class Vdf
{
    private readonly List<KeyValuePair<string, object>> entries = [];

    /// <summary>The entries in file order; a value is a string or a <see cref="Vdf"/>.</summary>
    internal IReadOnlyList<KeyValuePair<string, object>> Entries => entries;

    /// <summary>The first string value under <paramref name="key"/> (case-insensitive), or null.</summary>
    internal string? Value(string key) =>
        entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase) && e.Value is string).Value as string;

    /// <summary>The first child under <paramref name="key"/> (case-insensitive), or null.</summary>
    internal Vdf? Child(string key) =>
        entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase) && e.Value is Vdf).Value as Vdf;

    /// <summary>Parses a whole file: its top-level entries.</summary>
    /// <exception cref="FormatException">The text is not KeyValues.</exception>
    internal static Vdf Parse(string text)
    {
        int at = 0;
        var root = ParseEntries(text, ref at, nested: false);
        return root;
    }

    private static Vdf ParseEntries(string text, ref int at, bool nested)
    {
        var node = new Vdf();
        while (true)
        {
            string? key = Token(text, ref at);
            if (key == null)
            {
                return nested ? throw new FormatException("A '{' is never closed.") : node;
            }
            if (key == "}")
            {
                return nested ? node : throw new FormatException("A '}' closes nothing.");
            }
            string? value = Token(text, ref at) ?? throw new FormatException($"The key \"{key}\" has no value.");
            node.entries.Add(new KeyValuePair<string, object>(key, value == "{" ? ParseEntries(text, ref at, nested: true) : value));
        }
    }

    // The next token: a quoted string (with \\ \" \n \t escapes), an unquoted word, "{" or "}"; null at the end.
    private static string? Token(string text, ref int at)
    {
        while (at < text.Length)
        {
            char c = text[at];
            if (char.IsWhiteSpace(c))
            {
                at++;
            }
            else if (c == '/' && at + 1 < text.Length && text[at + 1] == '/')
            {
                while (at < text.Length && text[at] != '\n')
                {
                    at++;
                }
            }
            else
            {
                break;
            }
        }
        if (at >= text.Length)
        {
            return null;
        }
        char first = text[at];
        if (first == '{' || first == '}')
        {
            at++;
            return first.ToString();
        }
        var token = new StringBuilder();
        if (first == '"')
        {
            at++;
            while (at < text.Length && text[at] != '"')
            {
                char c = text[at++];
                if (c == '\\' && at < text.Length)
                {
                    char escaped = text[at++];
                    token.Append(escaped switch { 'n' => '\n', 't' => '\t', _ => escaped });
                }
                else
                {
                    token.Append(c);
                }
            }
            if (at >= text.Length)
            {
                throw new FormatException("A quoted string is never closed.");
            }
            at++;
            return token.ToString();
        }
        while (at < text.Length && !char.IsWhiteSpace(text[at]) && text[at] != '{' && text[at] != '}' && text[at] != '"')
        {
            token.Append(text[at++]);
        }
        return token.ToString();
    }
}
