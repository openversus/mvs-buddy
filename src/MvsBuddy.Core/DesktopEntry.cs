using System.Text;

namespace MvsBuddy;

/// <summary>
/// MVS Buddy's entry in a Linux desktop's menu, under Games (<c>mvs-buddy.desktop</c> in the user's applications
/// folder), pointing at the executable that is running: written the first time it runs and again whenever it has moved,
/// so the menu finds it wherever the person unpacked it, with its icon in the user's icon theme. Nothing else is touched.
/// Not for Windows.
/// </summary>
public static class DesktopEntry
{
    internal const string FileName = "mvs-buddy.desktop";

    /// <summary>The icon's name in the theme and the entry's own name, which a window made with that app id is paired with.</summary>
    public const string IconName = "mvs-buddy";

    /// <summary>
    /// Makes sure the menu entry is there and points at <paramref name="executable"/>: the file is written when it is
    /// missing or says anything else, and left alone when it already says this. The file's path when it was written;
    /// null when nothing was written, on Windows always.
    /// </summary>
    /// <exception cref="IOException">The applications folder or the file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The applications folder or the file could not be written.</exception>
    public static string? Install(string executable, IReadOnlyDictionary<int, byte[]> icons) => Install(HostSystem.Current(), executable, icons);

    /// <param name="host">The machine.</param>
    /// <param name="executable">MVS Buddy's own path.</param>
    /// <param name="icons">The icon as a PNG at each size (pixels square), for the icon theme's <c>hicolor</c> folders.</param>
    internal static string? Install(HostSystem host, string executable, IReadOnlyDictionary<int, byte[]> icons)
    {
        if (host.IsWindows || string.IsNullOrEmpty(executable))
        {
            return null;
        }
        string data = DataHome(host);
        foreach (var icon in icons)
        {
            string file = Path.Combine(data, "icons", "hicolor", $"{icon.Key}x{icon.Key}", "apps", IconName + ".png");
            if (!File.Exists(file) || !File.ReadAllBytes(file).AsSpan().SequenceEqual(icon.Value))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, icon.Value);
            }
        }
        string path = Path.Combine(data, "applications", FileName);
        string text = Text(executable);
        if (File.Exists(path) && File.ReadAllText(path) == text)
        {
            return null;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Removes the menu entry and its icons; the paths removed (empty on Windows, or when there were none).</summary>
    /// <exception cref="IOException">One could not be deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">One could not be deleted.</exception>
    public static IReadOnlyList<string> Remove() => Remove(HostSystem.Current());

    internal static IReadOnlyList<string> Remove(HostSystem host)
    {
        if (host.IsWindows)
        {
            return [];
        }
        string data = DataHome(host);
        var removed = new List<string>();
        string icons = Path.Combine(data, "icons", "hicolor");
        var files = Directory.Exists(icons) ? Directory.EnumerateDirectories(icons).Select(size => Path.Combine(size, "apps", IconName + ".png")) : [];
        foreach (string file in files.Append(Path.Combine(data, "applications", FileName)).Where(File.Exists))
        {
            File.Delete(file);
            removed.Add(file);
        }
        return removed;
    }

    /// <summary>The desktop entry for <paramref name="executable"/>, in the freedesktop.org format.</summary>
    internal static string Text(string executable) =>
        "[Desktop Entry]\n"
        + "Type=Application\n"
        + "Name=MVS Buddy\n"
        + "Comment=The OpenVersus MVS mod installer and version manager\n"
        + $"Exec={Quoted(executable)}\n"
        + $"Icon={IconName}\n"
        + "Terminal=false\n"
        + "Categories=Game;\n"
        + "StartupNotify=true\n";

    // Where the menu and the icon theme look: $XDG_DATA_HOME, by default ~/.local/share (applications/ and icons/ under it).
    private static string DataHome(HostSystem host)
    {
        string? xdgData = host.Environment("XDG_DATA_HOME");
        return string.IsNullOrEmpty(xdgData) ? Path.Combine(host.Home, ".local", "share") : xdgData!;
    }

    // An Exec value is split like a shell command: a path with a space or a shell character goes in double quotes, with
    // the characters the spec reserves inside them escaped.
    private static string Quoted(string executable)
    {
        if (executable.All(c => !char.IsWhiteSpace(c) && "\"'\\`$<>~|&;*?#()".IndexOf(c) < 0))
        {
            return executable;
        }
        var text = new StringBuilder("\"");
        foreach (char c in executable)
        {
            if (c == '"' || c == '\\' || c == '`' || c == '$')
            {
                text.Append('\\');
            }
            text.Append(c);
        }
        return text.Append('"').ToString();
    }
}
