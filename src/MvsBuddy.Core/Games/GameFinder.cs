using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MvsBuddy.Games;

/// <summary>
/// Finds MultiVersus on this machine: quickly where the stores record it, or by searching the drives for its executable,
/// which also finds copies no store knows about.
/// </summary>
public sealed class GameFinder
{
    // Folders a search never enters: system and cache trees that are large and never hold a game.
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".cache", ".npm", ".nuget", ".cargo", ".rustup", "Trash", ".Trash",
        "$Recycle.Bin", "System Volume Information", "Windows", "proc", "sys", "dev",
    };

    private readonly HostSystem host;
    private readonly ILogger<GameFinder> log;

    internal GameFinder(HostSystem host, ILogger<GameFinder> log)
    {
        this.host = host;
        this.log = log;
    }

    /// <summary>
    /// The installs the stores record: Steam's (in the Windows registry, and in each Steam library's manifest) and the
    /// Epic Games Launcher's (Windows). Quick: it reads a handful of small files. Every install returned has its
    /// executable; an install found more than one way is listed once.
    /// </summary>
    public IReadOnlyList<GameInstall> FindKnown()
    {
        var found = new List<GameInstall>();
        if (host.IsWindows)
        {
            SteamFromRegistry(found);
        }
        foreach (string steam in SteamRoots(host))
        {
            SteamLibraries(steam, found);
        }
        if (host.IsWindows)
        {
            EpicManifests(found);
        }
        var installs = Distinct(found);
        log.LogInformation("{Count} installs the stores know of: {Folders}", installs.Count, installs.Select(i => i.Win64Directory));
        return installs;
    }

    /// <summary>
    /// Looks in every folder under the search roots (the fixed drives on Windows; the home folder and mounted media on
    /// Linux) for the executable, shallow folders first, skipping system and cache folders. This finds copies no store
    /// knows about, and can take minutes on big drives, so <paramref name="progress"/> hears each install as soon as it is
    /// found, and how far the search has got about ten times a second; cancel once the install wanted has turned up.
    /// </summary>
    public Task<IReadOnlyList<GameInstall>> SearchAsync(IProgress<SearchProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Search(host.SearchRoots(), progress, cancellationToken), cancellationToken);

    /// <summary>
    /// A folder someone picked, as the place to install, whatever it holds. When it is the game (the install folder, the
    /// <c>MultiVersus</c> folder in it, its <c>Binaries/Win64</c>, or the executable itself) the install is the game's
    /// <c>Win64</c> folder. Otherwise it is the picked folder as it is (the executable's folder, for a file), with
    /// <see cref="GameInstall.Verified"/> false and the reason in <see cref="GameInstall.UnverifiedReason"/>: MVS Buddy
    /// says what it could not confirm, and the person decides. Nothing refuses an unverified install.
    /// </summary>
    public GameInstall Choose(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("No folder was chosen.", nameof(path));
        }
        string? win64 = GameFiles.Win64Under(path);
        if (win64 != null)
        {
            return Unlisted(win64, FoundBy.Chosen);
        }
        string folder = Path.GetFullPath(File.Exists(path) ? Path.GetDirectoryName(Path.GetFullPath(path))! : path);
        string reason = !Directory.Exists(folder)
            ? "This folder does not exist yet."
            : !File.Exists(Path.Combine(folder, GameFiles.ExecutableName))
                ? $"There is no {GameFiles.ExecutableName} in this folder."
                : $"{GameFiles.ExecutableName} is here, but the game's own files are not where the game keeps them (Content{Path.DirectorySeparatorChar}Paks, two folders up).";
        log.LogInformation("{Folder} chosen without being verified: {Reason}", folder, reason);
        return Unlisted(folder, FoundBy.Chosen) with { Verified = false, UnverifiedReason = reason };
    }

    /// <summary>
    /// The game found inside a folder someone picked that is not the game itself (often a folder or two above it). It looks
    /// for folders named MultiVersus, the name the game's own project folder always has, and checks each; one that is not the
    /// game is walked past. Seven folders deep puts Win64 nine down (a Steam folder picked from the home folder); never more
    /// than 250,000 folders, a few seconds.
    /// </summary>
    public IReadOnlyList<GameInstall> FindUnder(string folder, CancellationToken cancellationToken = default) =>
        Directory.Exists(folder)
            ? Search([Path.GetFullPath(folder)], null, cancellationToken, maxDepth: 7, maxFolders: 250_000,
                recognise: f => string.Equals(Path.GetFileName(f), "MultiVersus", StringComparison.OrdinalIgnoreCase) ? GameFiles.Win64Under(f) : null)
            : [];

    // recognise: the game's Win64 folder for a folder that is (or holds) the game, else null; by default, the folder being Win64.
    internal IReadOnlyList<GameInstall> Search(IReadOnlyList<string> roots, IProgress<SearchProgress>? progress, CancellationToken cancellationToken,
        int maxDepth = int.MaxValue, int maxFolders = int.MaxValue, Func<string, string?>? recognise = null)
    {
        recognise ??= f => GameFiles.IsGame(f) ? f : null;
        var found = new List<GameInstall>();
        // Breadth first: a game a few folders down is found before the search sinks into a deep tree beside it.
        // Links are followed (~/.steam is links into the Steam folder); a folder reached twice, by a link or a loop, is
        // searched once. Its real path is worked out only where a link can have changed it: the roots, and the links.
        var comparer = host.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var visited = new HashSet<string>(comparer);
        var pending = new Queue<(string Folder, string Real, int Depth)>(roots.Select(r => (r, host.RealPath(r), 0)));
        var clock = Stopwatch.StartNew();
        int searched = 0;
        while (pending.Count > 0 && searched < maxFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (folder, real, depth) = pending.Dequeue();
            if (!visited.Add(real))
            {
                continue;
            }
            searched++;
            if (recognise(folder) is string win64)
            {
                var install = Unlisted(Path.GetFullPath(win64), FoundBy.Search);
                found.Add(install);
                progress?.Report(new SearchProgress(folder, searched, install));
                continue;
            }
            if (progress != null && clock.ElapsedMilliseconds >= 100)
            {
                progress.Report(new SearchProgress(folder, searched, null));
                clock.Restart();
            }
            if (depth >= maxDepth)
            {
                continue;
            }
            foreach (var (child, isLink) in Children(folder))
            {
                pending.Enqueue((child, isLink ? host.RealPath(child) : Path.Combine(real, Path.GetFileName(child)), depth + 1));
            }
        }
        var installs = Distinct(found);
        log.LogInformation("Searched {Folders} folders under {Roots}: {Count} installs", searched, roots, installs.Count);
        return installs;
    }

    private static IEnumerable<(string Folder, bool IsLink)> Children(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateDirectories()
                .Where(d => !Skipped.Contains(d.Name))
                .Select(d => (d.FullName, (d.Attributes & FileAttributes.ReparsePoint) != 0))
                .ToList();
        }
        catch (Exception e) when (e is UnauthorizedAccessException || e is IOException || e is System.Security.SecurityException)
        {
            return [];
        }
    }

    // A folder found by searching or picked: Steam's if it sits in a Steam library, with that library's Proton prefix;
    // otherwise the store is unknown, and on Linux the prefix is the one it sits inside, if any.
    private GameInstall Unlisted(string win64, FoundBy foundBy)
    {
        // By its real path: a copy reached through a link (~/.steam/steam) is the copy it points at, and MVS Buddy keeps
        // one set of everything per copy.
        win64 = Directory.Exists(win64) ? host.RealPath(win64) : win64;
        string? library = SteamLibraryOf(win64);
        if (library != null)
        {
            return new GameInstall(win64, GameStore.Steam, foundBy) { WinePrefix = ProtonPrefix(library) };
        }
        return new GameInstall(win64, GameStore.Unknown, foundBy) { WinePrefix = host.IsWindows ? null : EnclosingPrefix(win64) };
    }

    private void SteamFromRegistry(List<GameInstall> found)
    {
        foreach (string uninstall in new[] { @"SOFTWARE\WOW6432Node\Microsoft", @"SOFTWARE\Microsoft" })
        {
            string? location = host.Registry(
                $@"HKEY_LOCAL_MACHINE\{uninstall}\Windows\CurrentVersion\Uninstall\Steam App {GameFiles.SteamAppId}", "InstallLocation");
            string? win64 = string.IsNullOrEmpty(location) ? null : GameFiles.Win64Under(location!);
            if (win64 != null)
            {
                found.Add(new GameInstall(win64, GameStore.Steam, FoundBy.SteamRegistry));
            }
        }
    }

    /// <summary>Where Steam itself may be installed on <paramref name="host"/>: its registry entries and Program Files on Windows; native, the old <c>~/.steam</c> link, Flatpak and Snap on Linux. Not all exist.</summary>
    internal static IEnumerable<string> SteamRoots(HostSystem host)
    {
        if (host.IsWindows)
        {
            string? programFiles = host.Environment("ProgramFiles(x86)");
            return new[]
            {
                host.Registry(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath"),
                host.Registry(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                programFiles == null ? null : Path.Combine(programFiles, "Steam"),
            }.Where(p => !string.IsNullOrEmpty(p)).Select(p => p!);
        }
        // Native, the old ~/.steam link, Flatpak and Snap.
        return new[]
        {
            Path.Combine(host.Home, ".local", "share", "Steam"),
            Path.Combine(host.Home, ".steam", "steam"),
            Path.Combine(host.Home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
            Path.Combine(host.Home, "snap", "steam", "common", ".local", "share", "Steam"),
        };
    }

    private void SteamLibraries(string steam, List<GameInstall> found)
    {
        // Current Steam lists every library, its own folder included, as "0" { "path" "..." } under the folder's real
        // path: reading only those keeps a linked Steam folder (~/.steam/steam) from listing the game twice. Before 2021
        // the list was "1" "D:\\SteamLibrary" and left Steam's own folder out.
        var libraries = new List<string>();
        var folders = Read(Path.Combine(steam, "steamapps", "libraryfolders.vdf"))?.Child("libraryfolders");
        bool current = folders?.Entries.Any(e => e.Value is Vdf) == true;
        if (!current)
        {
            libraries.Add(steam);
        }
        foreach (var entry in folders?.Entries ?? [])
        {
            string? path = entry.Value is Vdf library ? library.Value("path") : entry.Value as string;
            if (!string.IsNullOrEmpty(path) && int.TryParse(entry.Key, out _))
            {
                libraries.Add(path!);
            }
        }
        foreach (string library in libraries)
        {
            string manifest = Path.Combine(library, "steamapps", $"appmanifest_{GameFiles.SteamAppId}.acf");
            string? installDir = Read(manifest)?.Child("AppState")?.Value("installdir");
            if (string.IsNullOrEmpty(installDir))
            {
                continue;
            }
            string install = Path.Combine(library, "steamapps", "common", installDir!);
            string? win64 = GameFiles.Win64Under(install);
            if (win64 == null)
            {
                log.LogInformation("Steam lists the game in {Library}, but {Install} has no {Executable}", library, install, GameFiles.ExecutableName);
                continue;
            }
            found.Add(new GameInstall(win64, GameStore.Steam, FoundBy.SteamLibrary) { WinePrefix = ProtonPrefix(library) });
        }
    }

    private void EpicManifests(List<GameInstall> found)
    {
        string? data = host.Registry(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher", "AppDataPath");
        string? programData = host.Environment("ProgramData");
        string? manifests = !string.IsNullOrEmpty(data)
            ? Path.Combine(data!, "Manifests")
            : programData == null ? null : Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (manifests == null || !Directory.Exists(manifests))
        {
            return;
        }
        foreach (string item in Directory.EnumerateFiles(manifests, "*.item"))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(item));
                if (json.RootElement.TryGetProperty("InstallLocation", out var location) && location.ValueKind == JsonValueKind.String)
                {
                    string? win64 = GameFiles.Win64Under(location.GetString()!);
                    if (win64 != null)
                    {
                        found.Add(new GameInstall(win64, GameStore.Epic, FoundBy.EpicManifest));
                    }
                }
            }
            catch (Exception e) when (e is JsonException || e is IOException || e is UnauthorizedAccessException)
            {
                log.LogInformation("Skipped Epic manifest {Item}: {Reason}", item, e.Message);
            }
        }
    }

    private string? ProtonPrefix(string library) =>
        host.IsWindows ? null : Path.Combine(library, "steamapps", "compatdata", GameFiles.SteamAppId, "pfx");

    // The Steam library a Win64 folder sits in: the folder above "steamapps/common/<install>".
    private static string? SteamLibraryOf(string win64)
    {
        for (var folder = new DirectoryInfo(win64); folder?.Parent?.Parent != null; folder = folder.Parent)
        {
            if (string.Equals(folder.Parent!.Name, "common", StringComparison.OrdinalIgnoreCase)
                && string.Equals(folder.Parent.Parent!.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
            {
                return folder.Parent.Parent.Parent?.FullName;
            }
        }
        return null;
    }

    // The Wine prefix a folder sits inside: the parent of an enclosing drive_c.
    private static string? EnclosingPrefix(string folder)
    {
        for (var at = new DirectoryInfo(folder); at != null; at = at.Parent)
        {
            if (at.Name == "drive_c")
            {
                return at.Parent?.FullName;
            }
        }
        return null;
    }

    private IReadOnlyList<GameInstall> Distinct(List<GameInstall> found)
    {
        var comparer = host.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        // By real path: the same copy reached through a link (~/.steam/steam, ~/.steam/root) is one copy.
        return found.Where(i => seen.Add(host.RealPath(i.Win64Directory).TrimEnd(Path.DirectorySeparatorChar))).ToList();
    }

    private Vdf? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return Vdf.Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is FormatException || e is IOException || e is UnauthorizedAccessException)
        {
            log.LogInformation("Could not read {Path}: {Reason}", path, e.Message);
            return null;
        }
    }
}
