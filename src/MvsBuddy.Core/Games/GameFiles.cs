namespace MvsBuddy.Games;

/// <summary>Names that identify MultiVersus on disk.</summary>
public static class GameFiles
{
    /// <summary>The game's executable, in <c>MultiVersus/Binaries/Win64</c> under its install folder.</summary>
    public const string ExecutableName = "MultiVersus-Win64-Shipping.exe";

    /// <summary>The game's Steam app id.</summary>
    public const string SteamAppId = "1818750";

    /// <summary>The folder holding the executable, from the install folder (Steam's <c>installdir</c>, Epic's install location).</summary>
    internal static readonly string Win64FromInstall = Path.Combine("MultiVersus", "Binaries", "Win64");

    /// <summary>
    /// Whether <paramref name="win64"/> is the game's: it holds the executable, and the game's own paks are where the
    /// engine looks for them, in <c>Content/Paks</c> beside <c>Binaries</c>. The executable alone is not enough: copies
    /// of it turn up on their own (unpacked by a tool, kept for reverse engineering).
    /// </summary>
    internal static bool IsGame(string win64)
    {
        if (!File.Exists(Path.Combine(win64, ExecutableName)))
        {
            return false;
        }
        string paks = Path.Combine(win64, "..", "..", "Content", "Paks");
        try
        {
            return Directory.Exists(paks) && Directory.EnumerateFiles(paks, "*.pak").Any();
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The game's folder holding the executable under <paramref name="folder"/>: the folder itself, its
    /// <c>Binaries/Win64</c>, or its <c>MultiVersus/Binaries/Win64</c>; or <paramref name="folder"/>'s folder when it
    /// is the executable. Null when none of those is the game (<see cref="IsGame"/>).
    /// </summary>
    internal static string? Win64Under(string folder)
    {
        if (string.Equals(Path.GetFileName(folder), ExecutableName, StringComparison.OrdinalIgnoreCase) && File.Exists(folder))
        {
            string? parent = Path.GetDirectoryName(Path.GetFullPath(folder));
            return parent != null && IsGame(parent) ? parent : null;
        }
        foreach (string candidate in new[] { folder, Path.Combine(folder, "Binaries", "Win64"), Path.Combine(folder, Win64FromInstall) })
        {
            if (IsGame(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }
        return null;
    }
}
