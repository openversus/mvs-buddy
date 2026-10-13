using Tomlyn.Syntax;

namespace MvsBuddy.Installs;

/// <summary>What a settings merge did.</summary>
/// <param name="Text">The new settings file.</param>
/// <param name="Carried">The person's own settings carried over, as <c>[Section] Key</c>.</param>
/// <param name="Dropped">Their settings the new release no longer has, or could not take (a value of another kind).</param>
/// <param name="Note">How the merge went when it could not do it the usual way; null when it could.</param>
public sealed record SettingsMergeResult(string Text, IReadOnlyList<string> Carried, IReadOnlyList<string> Dropped, string? Note);

/// <summary>
/// The new release's settings with the person's own carried over: a three-way merge of the defaults of the version they
/// have, their file, and the new release's file. A value of theirs that differs from their version's default is theirs, and
/// goes into the new file; everything else, comments included, is the new release's. The keys that decide which servers
/// and how much logging always come from the release, whichever way the channel changes.
/// </summary>
public static class SettingsMerge
{
    /// <summary>The keys that come from the release, never from the person's file.</summary>
    public static readonly IReadOnlyList<(string Section, string Key)> ChannelKeys =
    [
        ("Settings", "LogLevel"),
        ("Server.Game", "ServerUrl"),
        ("Server.Game", "P2PRegistry"),
        ("Server.Game", "P2PRegistryPort"),
        ("Server.Prod", "ServerUrl"),
    ];

    /// <summary>Merges; see the class.</summary>
    /// <param name="installedDefaults">The defaults of the version installed now; null when unknown, and then a value of
    /// theirs counts as theirs when it differs from the new release's default.</param>
    /// <param name="current">Their settings file; null when there is none.</param>
    /// <param name="release">The new release's settings file (its own, or its defaults).</param>
    /// <exception cref="FormatException">The release's file does not parse.</exception>
    public static SettingsMergeResult Merge(string? installedDefaults, string? current, string release)
    {
        if (release == null)
        {
            throw new ArgumentNullException(nameof(release));
        }
        var result = TomlSettings.Parse(release, "release");
        if (result.Errors.Count > 0)
        {
            throw new FormatException($"The release's settings do not parse: {result.Errors[0]}");
        }
        if (current == null)
        {
            return new SettingsMergeResult(release, [], [], null);
        }
        var theirs = TomlSettings.Parse(current, "current");
        if (theirs.Errors.Count > 0)
        {
            return new SettingsMergeResult(release, [], [],
                $"The settings file had a mistake ({theirs.Errors[0]}), so the new release's defaults were used; the old file is kept as a backup.");
        }
        var defaults = installedDefaults == null ? null : TomlSettings.Parse(installedDefaults, "defaults");
        if (defaults?.Errors.Count > 0)
        {
            defaults = null;
        }
        var baseline = defaults ?? result;
        var carried = new List<string>();
        var dropped = new List<string>();
        foreach (var (section, key, value) in theirs.Entries().ToList())
        {
            if (ChannelKeys.Any(c => string.Equals(c.Section, section, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            string? original = baseline.Get(section, key);
            if (original == TomlSettings.ValueText(value))
            {
                continue;
            }
            string name = section.Length == 0 ? key : $"[{section}] {key}";
            (result.Set(section, key, value) ? carried : dropped).Add(name);
        }
        string? note = defaults == null && installedDefaults != null
            ? "The installed version's defaults did not parse, so every setting that differs from the new defaults was kept."
            : defaults == null
                ? "The installed version's defaults were not available, so every setting that differs from the new defaults was kept."
                : null;
        return new SettingsMergeResult(result.ToString(), carried, dropped, note);
    }
}
