using System.Text;

namespace MvsBuddy.Games;

/// <summary>
/// A game's Steam launch options as one line: Steam hands it to a shell with <c>%command%</c> standing for the game, so
/// a word before <c>%command%</c> of the form <c>NAME=value</c> sets an environment variable for the game. The one the
/// loader needs under Proton is <c>WINEDLLOVERRIDES</c> naming the loader's DLL native first (<c>version=n,b</c>). The
/// shell keeps only the last of two assignments of one name, and Wine keeps only the last entry of one DLL in the value,
/// so the setting goes into an assignment already there, never beside it.
/// </summary>
internal static class LaunchLine
{
    internal const string Variable = "WINEDLLOVERRIDES";
    internal const string Command = "%command%";
    internal const string NativeFirst = "n,b";

    /// <summary>Whether <paramref name="options"/> tells Wine to load <paramref name="dll"/> (its name, without <c>.dll</c>) native first.</summary>
    internal static bool LoadsNative(string options, string dll)
    {
        var entries = Assignment(Words(options)) is { } word ? Entries(word.Text.Substring(Variable.Length + 1)) : [];
        var last = entries.LastOrDefault(e => e.Names.Any(n => string.Equals(n, dll, StringComparison.OrdinalIgnoreCase)));
        return last != null && last.Order.Split(',')[0].Trim().StartsWith("n", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <paramref name="options"/> with each of <paramref name="dlls"/> set native first: in the assignment already there
    /// (an entry of the DLL's replaced), or in a new one at the front, with <c>%command%</c> added when the line has none.
    /// Everything else in the line stays as it was.
    /// </summary>
    internal static string TurnedOn(string options, IEnumerable<string> dlls)
    {
        var names = dlls.ToList();
        var words = Words(options);
        if (Assignment(words) is { } word)
        {
            var entries = Without(Entries(word.Text.Substring(Variable.Length + 1)), names);
            entries.AddRange(names.Select(n => new Entry([n], NativeFirst)));
            return options.Substring(0, word.Start) + Render(entries, word.Quote) + options.Substring(word.End);
        }
        string assignment = Render(names.Select(n => new Entry([n], NativeFirst)).ToList(), '"');
        string rest = options.Trim();
        return words.Any(w => w.Text == Command)
            ? $"{assignment} {rest}"
            : rest.Length == 0 ? $"{assignment} {Command}" : $"{assignment} {Command} {rest}";
    }

    /// <summary>
    /// <paramref name="options"/> without the entries for <paramref name="dlls"/>: the assignment stays for its other
    /// entries, or goes; a <c>%command%</c> left alone goes too, as a line of only it runs the game as an empty line does.
    /// Everything else stays as it was.
    /// </summary>
    internal static string TurnedOff(string options, IEnumerable<string> dlls)
    {
        var words = Words(options);
        if (Assignment(words) is not { } word)
        {
            return options;
        }
        var entries = Without(Entries(word.Text.Substring(Variable.Length + 1)), dlls.ToList());
        if (entries.Count > 0)
        {
            return options.Substring(0, word.Start) + Render(entries, word.Quote) + options.Substring(word.End);
        }
        int end = word.End < options.Length && options[word.End] == ' ' ? word.End + 1 : word.End;
        int start = end == word.End && word.Start > 0 && options[word.Start - 1] == ' ' ? word.Start - 1 : word.Start;
        string left = options.Substring(0, start) + options.Substring(end);
        return left.Trim() == Command ? "" : left;
    }

    // A shell word: its text with the quotes and escapes resolved, where it sits in the line, and which quote its value
    // was written in ('\0' for none).
    private sealed record Word(string Text, int Start, int End, char Quote);

    private sealed record Entry(List<string> Names, string Order);

    // The words of a line as a POSIX shell splits them: whitespace between words, "..." and '...' keeping theirs, a
    // backslash keeping the next character (outside single quotes).
    private static List<Word> Words(string line)
    {
        var words = new List<Word>();
        int at = 0;
        while (at < line.Length)
        {
            while (at < line.Length && char.IsWhiteSpace(line[at]))
            {
                at++;
            }
            if (at >= line.Length)
            {
                break;
            }
            int start = at;
            var text = new StringBuilder();
            char quote = '\0';
            char inside = '\0';
            while (at < line.Length && (inside != '\0' || !char.IsWhiteSpace(line[at])))
            {
                char c = line[at++];
                if (inside == '\'')
                {
                    if (c == '\'')
                    {
                        inside = '\0';
                    }
                    else
                    {
                        text.Append(c);
                    }
                }
                else if (c == '\\' && inside != '\'' && at < line.Length)
                {
                    text.Append(line[at++]);
                }
                else if (c == '"' || c == '\'')
                {
                    if (inside == c)
                    {
                        inside = '\0';
                    }
                    else if (inside == '\0')
                    {
                        inside = c;
                        quote = c;
                    }
                    else
                    {
                        text.Append(c);
                    }
                }
                else
                {
                    text.Append(c);
                }
            }
            words.Add(new Word(text.ToString(), start, at, quote));
        }
        return words;
    }

    // The WINEDLLOVERRIDES assignment before %command%; after it, a word is an argument to the game, whatever it looks like.
    private static Word? Assignment(List<Word> words)
    {
        int command = words.FindIndex(w => w.Text == Command);
        return words.Take(command < 0 ? words.Count : command).FirstOrDefault(w => w.Text.StartsWith(Variable + "=", StringComparison.Ordinal));
    }

    // The value's entries, "name[,name]=order" separated by ";"; one without "=" is kept as it is, with no names.
    private static List<Entry> Entries(string value) =>
        value.Split(';').Where(e => e.Length > 0).Select(e =>
        {
            int equals = e.IndexOf('=');
            return equals < 0 ? new Entry([], e) : new Entry(e.Substring(0, equals).Split(',').Select(n => n.Trim()).Where(n => n.Length > 0).ToList(), e.Substring(equals + 1));
        }).ToList();

    // The entries with these DLLs taken out of their names; an entry left with no name goes, one that never had names stays.
    private static List<Entry> Without(List<Entry> entries, List<string> dlls) =>
        entries.Select(e => new Entry(e.Names.Where(n => !dlls.Any(d => string.Equals(d, n, StringComparison.OrdinalIgnoreCase))).ToList(), e.Order))
            .Where((e, i) => e.Names.Count > 0 || entries[i].Names.Count == 0)
            .ToList();

    // The assignment as a word: in the quotes it had, or, written new or bare, in double quotes when the shell would
    // otherwise split it (";" ends a command; a space ends a word).
    private static string Render(List<Entry> entries, char quote)
    {
        string value = string.Join(";", entries.Select(e => e.Names.Count == 0 ? e.Order : $"{string.Join(",", e.Names)}={e.Order}"));
        if (quote == '\0' && (value.IndexOf(';') >= 0 || value.Any(char.IsWhiteSpace)))
        {
            quote = '"';
        }
        return quote == '\0' ? $"{Variable}={value}" : $"{Variable}={quote}{value}{quote}";
    }
}

/// <summary>A Steam account on this machine with a <c>localconfig.vdf</c>, where its launch options for each game are kept.</summary>
/// <param name="Id">The account id (the folder under <c>userdata</c>).</param>
/// <param name="Name">The persona name Steam shows for it, or the id when <c>loginusers.vdf</c> does not say.</param>
/// <param name="LocalConfig">Its <c>localconfig.vdf</c>.</param>
internal sealed record SteamAccount(string Id, string Name, string LocalConfig);

/// <summary>
/// A game's launch options in Steam's <c>userdata/&lt;account&gt;/config/localconfig.vdf</c>: read, and changed in place
/// with every other byte of the file kept. Steam writes the file when it exits, over whatever is there, so a change made
/// while Steam runs is lost; the caller checks.
/// </summary>
internal static class SteamLaunchOptions
{
    private static readonly string[] AppsPath = ["UserLocalConfigStore", "Software", "Valve", "Steam", "apps"];
    private const string Key = "LaunchOptions";

    /// <summary>The launch options of <paramref name="appId"/> in <paramref name="vdf"/>: empty when there are none; null when the file has no <c>apps</c> block at all.</summary>
    internal static string? Read(string vdf, string appId)
    {
        if (Apps(vdf) is not { } apps)
        {
            return null;
        }
        var app = Find(vdf, apps.InnerStart, appId);
        return app == null ? "" : Find(vdf, app.InnerStart, Key)?.Value ?? "";
    }

    /// <summary>Whether <paramref name="vdf"/> has a block for <paramref name="appId"/> under <c>apps</c>: Steam makes one once the game has run under the account.</summary>
    internal static bool HasApp(string vdf, string appId) => Apps(vdf) is { } apps && Find(vdf, apps.InnerStart, appId) != null;

    /// <summary>
    /// <paramref name="vdf"/> with <paramref name="appId"/>'s launch options set to <paramref name="options"/>: the value
    /// replaced, or the line added to the game's block, or the game's block added under <c>apps</c>. Every other byte
    /// stays. Null when the file has no <c>apps</c> block, which is not a file the buddy knows how to change.
    /// </summary>
    internal static string? With(string vdf, string appId, string options)
    {
        if (Apps(vdf) is not { } apps)
        {
            return null;
        }
        string newline = vdf.Contains("\r\n") ? "\r\n" : "\n";
        string quoted = "\"" + options.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        var app = Find(vdf, apps.InnerStart, appId);
        if (app == null)
        {
            string indent = IndentOf(vdf, apps.InnerStart - 1) + "\t";
            string block = $"{indent}\"{appId}\"{newline}{indent}{{{newline}{indent}\t\"{Key}\"\t\t{quoted}{newline}{indent}}}{newline}";
            return Insert(vdf, apps.InnerStart, block, newline);
        }
        if (Find(vdf, app.InnerStart, Key) is { } found)
        {
            return vdf.Substring(0, found.ValueStart) + quoted + vdf.Substring(found.ValueEnd);
        }
        string line = $"{IndentOf(vdf, app.InnerStart - 1)}\t\"{Key}\"\t\t{quoted}{newline}";
        return Insert(vdf, app.InnerStart, line, newline);
    }

    /// <summary>
    /// The accounts under <paramref name="steamRoot"/> with a <c>localconfig.vdf</c> (account <c>0</c>, Steam's placeholder,
    /// is none), named from <c>config/loginusers.vdf</c>, whose keys are the 64-bit ids the account id is the low half of.
    /// </summary>
    internal static List<SteamAccount> Accounts(string steamRoot)
    {
        string userdata = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdata))
        {
            return [];
        }
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        string loginUsers = Path.Combine(steamRoot, "config", "loginusers.vdf");
        if (File.Exists(loginUsers))
        {
            try
            {
                foreach (var user in Vdf.Parse(File.ReadAllText(loginUsers)).Child("users")?.Entries ?? [])
                {
                    if (ulong.TryParse(user.Key, out ulong steamId) && user.Value is Vdf details && details.Value("PersonaName") is string persona)
                    {
                        names[(steamId & 0xFFFFFFFF).ToString()] = persona;
                    }
                }
            }
            catch (Exception e) when (e is FormatException || e is IOException || e is UnauthorizedAccessException)
            {
                // The names are a courtesy; the ids still identify the accounts.
            }
        }
        return Directory.EnumerateDirectories(userdata)
            .Select(Path.GetFileName)
            .Where(id => id != null && id != "0" && uint.TryParse(id, out _))
            .Select(id => (Id: id!, File: Path.Combine(userdata, id!, "config", "localconfig.vdf")))
            .Where(a => File.Exists(a.File))
            .OrderBy(a => a.Id, StringComparer.Ordinal)
            .Select(a => new SteamAccount(a.Id, names.TryGetValue(a.Id, out string? name) ? name : a.Id, a.File))
            .ToList();
    }

    // Where a key's value sits: a string's span, or a block's inside (just after its "{") and end (just after its "}").
    private sealed record Found(string? Value, int ValueStart, int ValueEnd, int InnerStart);

    private static Found? Apps(string vdf)
    {
        int at = 0;
        Found? found = null;
        foreach (string name in AppsPath)
        {
            found = Find(vdf, at, name);
            if (found == null || found.Value != null)
            {
                return null;
            }
            at = found.InnerStart;
        }
        return found;
    }

    // The entry named key among the entries from at to the end of their block (or the text); null when there is none.
    private static Found? Find(string text, int at, string key)
    {
        while (true)
        {
            string? name = Vdf.Token(text, ref at, out _);
            if (name == null || name == "}")
            {
                return null;
            }
            string? value = Vdf.Token(text, ref at, out int valueStart);
            if (value == null)
            {
                return null;
            }
            bool wanted = string.Equals(name, key, StringComparison.OrdinalIgnoreCase);
            if (value == "{")
            {
                int inner = at;
                SkipBlock(text, ref at);
                if (wanted)
                {
                    return new Found(null, valueStart, at, inner);
                }
            }
            else if (wanted)
            {
                return new Found(value, valueStart, at, -1);
            }
        }
    }

    // From just inside a "{" to just after its "}".
    private static void SkipBlock(string text, ref int at)
    {
        while (true)
        {
            string? token = Vdf.Token(text, ref at, out _) ?? throw new FormatException("A '{' is never closed.");
            if (token == "}")
            {
                return;
            }
            if (token == "{")
            {
                SkipBlock(text, ref at);
            }
        }
    }

    // The whitespace the line holding position at starts with.
    private static string IndentOf(string text, int at)
    {
        int lineStart = text.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
        int end = lineStart;
        while (end < text.Length && (text[end] == '\t' || text[end] == ' '))
        {
            end++;
        }
        return text.Substring(lineStart, end - lineStart);
    }

    // Inserts whole lines just after the line a block's "{" is on.
    private static string Insert(string text, int innerStart, string lines, string newline)
    {
        int lineEnd = text.IndexOf('\n', innerStart);
        int at = lineEnd < 0 ? text.Length : lineEnd + 1;
        if (lineEnd < 0)
        {
            lines = newline + lines;
        }
        return text.Substring(0, at) + lines + text.Substring(at);
    }
}
