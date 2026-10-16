namespace MvsBuddy.Changes;

/// <summary>
/// The lines of progress an operation has reported, as a screen shows them: a line that only updates the last one (the
/// same words before a parenthesis: "Downloading x.zip" then "Downloading x.zip (42%)", or "Moving the paks..." then
/// "Moving the paks... (3 of 12: x.pak)") takes its place, and any other line is added after it. So each phase stays one
/// line however many times it reports, and the phases before the current one stay on the screen.
/// </summary>
public sealed class ProgressLines
{
    private readonly List<string> lines = [];

    /// <summary>The lines so far, the current one last.</summary>
    public IReadOnlyList<string> Lines => lines;

    /// <summary>The line reported last; null before the first.</summary>
    public string? Current => lines.Count == 0 ? null : lines[lines.Count - 1];

    /// <summary>Takes in <paramref name="line"/>: in place of the last when it updates it, after it otherwise.</summary>
    public void Add(string line)
    {
        if (line == null)
        {
            throw new ArgumentNullException(nameof(line));
        }
        if (lines.Count > 0 && KeyOf(lines[lines.Count - 1]) == KeyOf(line))
        {
            lines[lines.Count - 1] = line;
        }
        else
        {
            lines.Add(line);
        }
    }

    /// <summary>What a line is about: its words before a parenthesis.</summary>
    internal static string KeyOf(string line)
    {
        int at = line.IndexOf(" (", StringComparison.Ordinal);
        return (at < 0 ? line : line.Substring(0, at)).TrimEnd();
    }
}
