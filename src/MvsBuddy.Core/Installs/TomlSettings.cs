using System.Globalization;
using System.Text;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace MvsBuddy.Installs;

/// <summary>
/// Reads the mod's TOML settings the way ovs-client's TomlConfig does: through Tomlyn's syntax tree, section and key
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

    /// <summary>A value as text, from <paramref name="section"/> (empty for the top level); null when it is not there.</summary>
    internal string? Get(string section, string key)
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
                return item.Value == null ? null : ValueText(item.Value);
            }
        }
        return null;
    }

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

    private static string ValueText(ValueSyntax value) => value switch
    {
        BooleanValueSyntax b => b.Value ? "true" : "false",
        IntegerValueSyntax i => i.Value.ToString(CultureInfo.InvariantCulture),
        FloatValueSyntax f => f.Value.ToString("R", CultureInfo.InvariantCulture),
        StringValueSyntax s => s.Value ?? "",
        _ => value.ToString().Trim(),
    };

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
