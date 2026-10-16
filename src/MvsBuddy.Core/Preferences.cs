using MvsBuddy.Installs;
using Tomlyn.Syntax;

namespace MvsBuddy;

/// <summary>
/// What the person has told MVS Buddy about itself: <c>preferences.toml</c> in MVS Buddy's own folder, in the same form as
/// the mod's settings file and handled the same way. MVS Buddy's copy of the file (<see cref="Template"/>, with a comment
/// on every key) stands in until something is answered; after that the person's file is edited in place, so their
/// comments and spacing stay, and rebuilt from the template only when a key MVS Buddy needs is not in it (an older file, or
/// one edited down), carrying their other values over. Nothing here is about a copy of the game.
/// </summary>
public sealed class Preferences
{
    /// <summary>The preferences file's name, in MVS Buddy's folder.</summary>
    public const string FileName = "preferences.toml";

    /// <summary>The file as MVS Buddy writes it the first time, every key with its default and a comment.</summary>
    internal const string Template = """
        # MVS Buddy's own settings, in the same form as OpenVersus.toml. MVS Buddy writes a line here when you answer one of
        # its questions and keeps the rest of the file as you have it. A line that starts with # is a comment.

        [Menu]
        # An entry for MVS Buddy in the Games section of your menu (Linux). "ask" means MVS Buddy asks once, after your
        # first change; "yes" keeps the entry pointing at MVS Buddy wherever it is; "no" leaves your menu alone.
        Entry = "ask" # "ask", "yes" or "no"

        """;

    private readonly string file;
    private TomlSettings? settings;

    internal Preferences(HostSystem host)
    {
        file = Path.Combine(host.DataDirectory, FileName);
        if (!File.Exists(file))
        {
            return;
        }
        var parsed = TomlSettings.Parse(File.ReadAllText(file), FileName);
        if (parsed.Errors.Count > 0)
        {
            Problem = $"{FileName} has a mistake ({parsed.Errors[0]}), so MVS Buddy runs on its default settings; the file is set aside as {FileName}.unreadable when MVS Buddy next writes one.";
            return;
        }
        settings = parsed;
    }

    /// <summary>Why the person's file is not in use, for them to read; null when it is, or there is none.</summary>
    public string? Problem { get; private set; }

    /// <summary>
    /// Whether the person wants MVS Buddy in the Games section of the menu (Linux): true or false once answered, null until
    /// then. Asked after their first change, never before: a question about a program nobody has used yet has no answer.
    /// </summary>
    public bool? MenuEntry => Get("Menu", "Entry")?.ToLowerInvariant() switch
    {
        "yes" or "true" => true,
        "no" or "false" => false,
        _ => null,
    };

    /// <summary>Records the answer to <see cref="MenuEntry"/>.</summary>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be written.</exception>
    public void SetMenuEntry(bool wanted) => Set("Menu", "Entry", new StringValueSyntax(wanted ? "yes" : "no"));

    /// <summary>A value as text from the person's file, else from the template; null when neither has it.</summary>
    internal string? Get(string section, string key) => (settings ?? TomlSettings.Parse(Template, FileName)).Get(section, key);

    // The person's file with the one value changed, written whole beside it and put in its place. A file that lacks the key
    // is rebuilt from the template with the person's values carried over, as a release's settings file is.
    internal void Set(string section, string key, ValueSyntax value)
    {
        var document = settings ?? TomlSettings.Parse(Template, FileName);
        if (!document.Set(section, key, value))
        {
            document = TomlSettings.Parse(SettingsMerge.Merge(null, settings?.ToString(), Template).Text, FileName);
            if (!document.Set(section, key, value))
            {
                throw new InvalidOperationException($"MVS Buddy's own {FileName} template has no [{section}] {key}.");
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (Problem != null && File.Exists(file))
        {
            string aside = file + ".unreadable";
            File.Delete(aside);
            File.Move(file, aside);
            Problem = null;
        }
        string written = file + ".mvs-buddy";
        File.WriteAllText(written, document.ToString());
        if (File.Exists(file))
        {
            File.Replace(written, file, null);
        }
        else
        {
            File.Move(written, file);
        }
        settings = document;
    }
}
