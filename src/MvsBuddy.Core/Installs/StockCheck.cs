using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MvsBuddy.Games;

namespace MvsBuddy.Installs;

/// <summary>Whose a file the game did not ship is, as far as the buddy can tell.</summary>
public enum ExtraKind
{
    /// <summary>OpenVersus: its plugin and files, the loader, its paks.</summary>
    OpenVersus,

    /// <summary>MVS Infinite: its plugin, and paks whose names say so.</summary>
    MvsInfinite,

    /// <summary>Anything else: another mod, a tool, a backup someone made.</summary>
    Other,
}

/// <summary>A file in the game's folder that the game did not ship.</summary>
/// <param name="Path">The file.</param>
/// <param name="RelativePath">Its path inside the game's folder.</param>
/// <param name="Size">Its size in bytes.</param>
/// <param name="Kind">Whose it seems to be.</param>
public sealed record StockExtra(string Path, string RelativePath, long Size, ExtraKind Kind);

/// <summary>How a copy of the game compares with what the game shipped.</summary>
/// <param name="InstallRoot">The game's folder (Steam's <c>steamapps/common/MultiVersus</c>); null when unknown.</param>
/// <param name="HasManifests">
/// Whether the game's <c>Manifest_*.txt</c> lists were there and readable. Without them nothing can be told, and a clean
/// start is not offered; <paramref name="Notes"/> says why.
/// </param>
/// <param name="Extras">Files the game did not ship.</param>
/// <param name="MissingStock">Files the game shipped that are gone, relative to <paramref name="InstallRoot"/>.</param>
/// <param name="Notes">Plain sentences about the result.</param>
public sealed record StockReport(string? InstallRoot, bool HasManifests, IReadOnlyList<StockExtra> Extras, IReadOnlyList<string> MissingStock, IReadOnlyList<string> Notes);

/// <summary>
/// Tells the game's own files from everything added to its folder, for a clean start. What the game shipped is what its
/// <c>Manifest_*.txt</c> files list (the loose files, the debug files, and the files inside its paks, any of which may also
/// lie loose), the manifests themselves, and the engine's and game's own paks, which no manifest lists.
/// </summary>
public sealed class StockCheck
{
    private static readonly Regex OwnPak = new(
        "^(pakchunk[0-9]+(optional)?-Windows|global|CrashReportClient)\\.(pak|sig|ucas|utoc)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly HostSystem host;
    private readonly ILogger<StockCheck> log;

    internal StockCheck(HostSystem host, ILogger<StockCheck> log)
    {
        this.host = host;
        this.log = log;
    }

    /// <summary>The files in <paramref name="state"/>'s game folder the game did not ship, and the shipped ones that are gone.</summary>
    public StockReport Read(InstallState state)
    {
        if (state == null)
        {
            throw new ArgumentNullException(nameof(state));
        }
        if (!state.Game.Verified)
        {
            return new StockReport(null, false, [], [], ["This folder is not recognisably the game, so the buddy cannot tell where the game's folder starts."]);
        }
        string root = Path.GetFullPath(Path.Combine(state.Game.Win64Directory, "..", "..", ".."));
        var comparer = host.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var manifests = Directory.Exists(root) ? Directory.EnumerateFiles(root, "Manifest_*.txt").ToList() : [];
        if (manifests.Count == 0)
        {
            return new StockReport(root, false, [], [], ["This copy has no Manifest_*.txt files, so the buddy cannot tell the game's own files from added ones."]);
        }
        var stock = new HashSet<string>(comparer);
        var loose = new List<string>();
        try
        {
            ReadManifests(manifests, stock, loose);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            log.LogWarning(e, "Could not read the manifests in {Root}", root);
            return new StockReport(root, false, [], [], [$"The game's Manifest_*.txt files could not be read ({e.Message}), so the buddy cannot tell the game's own files from added ones."]);
        }


        var openVersus = new HashSet<string>(
            state.Mod.Files.Concat(state.Mod.OldLayoutFiles).Concat(state.PaksInGame.Select(p => p.Path))
                .Concat(state.Loaders.Where(l => l.IsUltimateAsiLoader).Select(l => l.Path))
                .Append(Path.Combine(state.Game.Win64Directory, Changes.Installer.LicenseName)), comparer);
        var mvsInfinite = new HashSet<string>(
            state.OtherPlugins.Where(p => p.IsMvsInfinite).Select(p => p.Path).Concat(state.OtherPaks.Where(p => p.IsMvsInfinite).Select(p => p.Path)), comparer);

        var extras = new List<StockExtra>();
        var present = new HashSet<string>(comparer);
        foreach (string file in Files(root))
        {
            string relative = file.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length + 1);
            present.Add(relative);
            if (stock.Contains(relative) || IsOwnPak(file))
            {
                continue;
            }
            var kind = openVersus.Contains(file) ? ExtraKind.OpenVersus : mvsInfinite.Contains(file) ? ExtraKind.MvsInfinite : ExtraKind.Other;
            extras.Add(new StockExtra(file, relative, new FileInfo(file).Length, kind));
        }
        var missing = loose.Where(f => !present.Contains(f)).Distinct(comparer).ToList();

        var notes = new List<string>();
        if (missing.Count > 0)
        {
            notes.Add($"{missing.Count} of the game's own files are missing; Steam's \"Verify integrity of game files\" puts them back.");
        }
        log.LogInformation("{Root}: {Extras} files the game did not ship ({OpenVersus} OpenVersus, {Mvsi} MVS Infinite), {Missing} shipped files missing",
            root, extras.Count, extras.Count(e => e.Kind == ExtraKind.OpenVersus), extras.Count(e => e.Kind == ExtraKind.MvsInfinite), missing.Count);
        return new StockReport(root, true, extras, missing, notes);
    }

    // Every entry of every manifest (relative paths, OS separators), and the loose files' manifest's entries apart.
    private static void ReadManifests(IEnumerable<string> manifests, HashSet<string> stock, List<string> loose)
    {
        foreach (string manifest in manifests)
        {
            bool listsLooseFiles = Path.GetFileName(manifest).IndexOf("NonUFS", StringComparison.OrdinalIgnoreCase) >= 0;
            foreach (string line in File.ReadLines(manifest))
            {
                string entry = line.Split('\t')[0].Trim().Replace('/', Path.DirectorySeparatorChar);
                if (entry.Length > 0)
                {
                    stock.Add(entry);
                    if (listsLooseFiles)
                    {
                        loose.Add(entry);
                    }
                }
            }
            stock.Add(Path.GetFileName(manifest));
        }
    }

    // The engine's and game's own paks, directly in a Content/Paks folder; never one in a folder under it (~mods).
    private static bool IsOwnPak(string file)
    {
        var folder = new DirectoryInfo(Path.GetDirectoryName(file)!);
        return folder.Name == "Paks" && folder.Parent?.Name == "Content" && OwnPak.IsMatch(Path.GetFileName(file));
    }

    // Every file under the folder, not following links (a link can loop, and what it points at is not the game's).
    private static IEnumerable<string> Files(string root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            List<FileSystemInfo> entries;
            try
            {
                entries = folder.EnumerateFileSystemInfos().ToList();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                }
                else
                {
                    yield return entry.FullName;
                }
            }
        }
    }
}
