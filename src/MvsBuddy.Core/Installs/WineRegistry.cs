namespace MvsBuddy.Installs;

/// <summary>
/// A Wine prefix's <c>user.reg</c>, for the one setting the loader needs: Wine loads a DLL of a name it has its own of
/// (version.dll) from the game's folder only when <c>Software\Wine\DllOverrides</c> says native first.
/// </summary>
internal static class WineRegistry
{
    private const string Section = @"[Software\\Wine\\DllOverrides]";

    /// <summary>The file in <paramref name="prefix"/>.</summary>
    internal static string FileIn(string prefix) => Path.Combine(prefix, "user.reg");

    /// <summary>Whether <paramref name="userReg"/> has Wine load <paramref name="dll"/> (its name, without <c>.dll</c>) native first: <c>native,builtin</c>, <c>n,b</c> or native alone.</summary>
    internal static bool LoadsNative(string userReg, string dll) =>
        Entry(Lines(userReg), dll) is { } entry && Value(entry.Line) is string value && value.Split(',')[0].Trim().StartsWith("n", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="userReg"/> with <paramref name="dll"/> set to <c>native,builtin</c>: its entry replaced, or added to
    /// the section, or the section added at the end. Every other line stays as it was.
    /// </summary>
    internal static string WithNative(string userReg, string dll, DateTimeOffset now)
    {
        string newline = userReg.Contains("\r\n") ? "\r\n" : "\n";
        var lines = Lines(userReg);
        string entry = $"\"{dll}\"=\"native,builtin\"";
        if (Entry(lines, dll) is { } found)
        {
            lines[found.Index] = entry;
        }
        else if (lines.FindIndex(IsSection) is int start and >= 0)
        {
            // After the section's own lines (#time, #class).
            int at = start + 1;
            while (at < lines.Count && lines[at].StartsWith("#", StringComparison.Ordinal))
            {
                at++;
            }
            lines.Insert(at, entry);
        }
        else
        {
            if (lines.Count > 0 && lines[^1].Length > 0)
            {
                lines.Add("");
            }
            long fileTime = now.ToFileTime();
            lines.AddRange([$"{Section} {now.ToUnixTimeSeconds()}", $"#time={fileTime:x}", entry, ""]);
        }
        return string.Join(newline, lines);
    }

    private static List<string> Lines(string text) => text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

    private static bool IsSection(string line) => line.StartsWith(Section, StringComparison.OrdinalIgnoreCase);

    // The section's entry for the DLL (a key of its name, or with the * Wine allows before it).
    private static (int Index, string Line)? Entry(List<string> lines, string dll)
    {
        int start = lines.FindIndex(IsSection);
        if (start < 0)
        {
            return null;
        }
        for (int i = start + 1; i < lines.Count && !lines[i].StartsWith("[", StringComparison.Ordinal); i++)
        {
            string line = lines[i];
            int close = line.IndexOf("\"=", StringComparison.Ordinal);
            if (line.StartsWith("\"", StringComparison.Ordinal) && close > 0
                && string.Equals(line.Substring(1, close - 1).TrimStart('*'), dll, StringComparison.OrdinalIgnoreCase))
            {
                return (i, line);
            }
        }
        return null;
    }

    private static string? Value(string line)
    {
        int equals = line.IndexOf("\"=\"", StringComparison.Ordinal);
        return equals < 0 || !line.EndsWith("\"", StringComparison.Ordinal) ? null : line.Substring(equals + 3, line.Length - equals - 4);
    }
}
