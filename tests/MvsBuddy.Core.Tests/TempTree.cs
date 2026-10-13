namespace MvsBuddy.Tests;

/// <summary>A temporary folder tree, deleted on dispose.</summary>
internal sealed class TempTree : IDisposable
{
    public TempTree()
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mvs-buddy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    /// <summary>Writes a file (making its folders) and returns its path.</summary>
    public string File(string text, params string[] parts)
    {
        string path = Path(parts);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    /// <summary>
    /// A game install as the stores lay it out: the executable in <c>MultiVersus/Binaries/Win64</c> under the given
    /// folder and a pak in <c>MultiVersus/Content/Paks</c>. Returns the Win64 folder.
    /// </summary>
    public string Game(params string[] installFolder)
    {
        File("not really a game", [.. installFolder, "MultiVersus", "Binaries", "Win64", Games.GameFiles.ExecutableName]);
        File("not really a pak", [.. installFolder, "MultiVersus", "Content", "Paks", "pakchunk0-Windows.pak"]);
        return Path([.. installFolder, "MultiVersus", "Binaries", "Win64"]);
    }

    /// <summary>The executable on its own, as a tool that unpacks it leaves it. Returns its folder.</summary>
    public string LoneExecutable(params string[] folder)
    {
        File("not really a game", [.. folder, Games.GameFiles.ExecutableName]);
        return Path(folder);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
