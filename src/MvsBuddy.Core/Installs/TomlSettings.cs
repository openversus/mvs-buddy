using System.Globalization;
using System.Text;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace MvsBuddy.Installs;

/// <summary>
/// Reads and edits the mod's TOML settings the way ovs-client's TomlConfig does: through Tomlyn's syntax tree, section and key
/// names compared without case, quoted parts unquoted.
/// </summary>
internal sealed class TomlSettings
{
    private readonly DocumentSyntax document;

    private TomlSettings(DocumentSyntax document, IReadOnlyList<string> errors)
    {
        this.document = document;
        Errors = errors;
    }

    /// <summary>The parse errors, as "line L, column C: message"; the mod runs on its defaults when there are any.</summary>
    internal IReadOnlyList<string> Errors { get; }

    internal static TomlSettings Parse(string text, string fileName)
    {
        var document = SyntaxParser.Parse(text, fileName, true);
        var errors = new List<string>();
        foreach (var diagnostic in document.Diagnostics ?? [])
        {
            if (diagnostic.Kind == DiagnosticMessageKind.Error)
            {
                errors.Add($"line {diagnostic.Span.Start.Line + 1}, column {diagnostic.Span.Start.Column + 1}: {diagnostic.Message}");
            }
        }
        return new TomlSettings(document, errors);
    }

    /// <summary>Every key in the file with its value, the section empty for keys before the first table.</summary>
    internal IEnumerable<(string Section, string Key, ValueSyntax Value)> Entries()
    {
        foreach (KeyValueSyntax item in document.KeyValues)
        {
            if (item.Value != null)
            {
                yield return ("", KeyText(item.Key), item.Value);
            }
        }
        foreach (TableSyntaxBase table in document.Tables)
        {
            string name = KeyText(table.Name);
            foreach (KeyValueSyntax item in table.Items)
            {
                if (item.Value != null)
                {
                    yield return (name, KeyText(item.Key), item.Value);
                }
            }
        }
    }

    /// <summary>
    /// Gives an existing key the value of <paramref name="value"/> (a string, boolean, integer or float, as typed there),
    /// keeping the key's line, spacing and trailing comment, as ovs-client's TomlConfig.Set does. False when the key is
    /// not in this file or the value is of another kind.
    /// </summary>
    internal bool Set(string section, string key, ValueSyntax value)
    {
        var item = FindKey(section, key);
        ValueSyntax? literal = value switch
        {
            StringValueSyntax s => new StringValueSyntax(s.Value ?? ""),
            BooleanValueSyntax b => new BooleanValueSyntax(b.Value),
            IntegerValueSyntax i => new IntegerValueSyntax(i.Value),
            FloatValueSyntax f => new FloatValueSyntax(f.Value),
            _ => null,
        };
        if (item?.Value == null || literal == null)
        {
            return false;
        }
        // The comment after a value and the spaces before it hang off the value's token.
        var old = TokenOf(item.Value);
        var replacement = TokenOf(literal);
        if (old != null && replacement != null)
        {
            replacement.LeadingTrivia = old.LeadingTrivia;
            replacement.TrailingTrivia = old.TrailingTrivia;
        }
        item.Value = literal;
        return true;
    }

    /// <summary>The file's text with any changes.</summary>
    public override string ToString() => document.ToString();

    /// <summary>A value as text, from <paramref name="section"/> (empty for the top level); null when it is not there.</summary>
    internal string? Get(string section, string key)
    {
        var item = FindKey(section, key);
        return item?.Value == null ? null : ValueText(item.Value);
    }

    /// <summary>A value as text, the way <see cref="Get"/> reads it.</summary>
    internal static string ValueText(ValueSyntax value) => value switch
    {
        BooleanValueSyntax b => b.Value ? "true" : "false",
        IntegerValueSyntax i => i.Value.ToString(CultureInfo.InvariantCulture),
        FloatValueSyntax f => f.Value.ToString("R", CultureInfo.InvariantCulture),
        StringValueSyntax s => s.Value ?? "",
        _ => value.ToString().Trim(),
    };

    private KeyValueSyntax? FindKey(string section, string key)
    {
        var items = section.Length == 0 ? document.KeyValues : FindTable(section)?.Items;
        if (items == null)
        {
            return null;
        }
        foreach (KeyValueSyntax item in items)
        {
            if (item.Key?.DotKeys.ChildrenCount == 0 && string.Equals(KeyText(item.Key), key, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }
        return null;
    }

    private static SyntaxToken? TokenOf(ValueSyntax value) => value switch
    {
        StringValueSyntax s => s.Token,
        BooleanValueSyntax b => b.Token,
        IntegerValueSyntax i => i.Token,
        FloatValueSyntax f => f.Token,
        _ => null,
    };

    private TableSyntaxBase? FindTable(string section)
    {
        foreach (TableSyntaxBase table in document.Tables)
        {
            if (table is TableSyntax && string.Equals(KeyText(table.Name), section, StringComparison.OrdinalIgnoreCase))
            {
                return table;
            }
        }
        return null;
    }


    private static string KeyText(KeySyntax? key)
    {
        if (key == null)
        {
            return "";
        }
        var text = new StringBuilder(PartText(key.Key));
        foreach (DottedKeyItemSyntax dotted in key.DotKeys)
        {
            text.Append('.').Append(PartText(dotted.Key));
        }
        return text.ToString();
    }

    private static string PartText(BareKeyOrStringValueSyntax? part) => part switch
    {
        BareKeySyntax bare => bare.Key?.Text ?? "",
        StringValueSyntax quoted => quoted.Value ?? "",
        null => "",
        _ => part.ToString().Trim(),
    };
}
