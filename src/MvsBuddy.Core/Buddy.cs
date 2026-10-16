using System.Net.Http;
using MvsBuddy.Catalog;
using MvsBuddy.Changes;
using MvsBuddy.Games;
using MvsBuddy.Installs;

namespace MvsBuddy;

/// <summary>What MVS Buddy can do. Made by <see cref="BuddyBuilder"/>.</summary>
public sealed class Buddy : IDisposable
{
    private readonly HttpClient? ownedHttp;
    private readonly HostSystem host;

    internal Buddy(HostSystem host, ReleaseCatalog releases, GameFinder games, InstallReader installs, Downloads downloads, StockCheck stock, Permissions permissions, ReleaseCache cache, PakStore paks, GameStates states, Preferences preferences, HttpClient? ownedHttp)
    {
        this.host = host;
        Preferences = preferences;
        States = states;
        Cache = cache;
        Paks = paks;
        Permissions = permissions;
        Stock = stock;
        Downloads = downloads;
        Releases = releases;
        Games = games;
        Installs = installs;
        this.ownedHttp = ownedHttp;
    }

    /// <summary>The mod's releases on each channel, and the paks that go with them.</summary>
    public ReleaseCatalog Releases { get; }

    /// <summary>Finding MultiVersus on this machine.</summary>
    public GameFinder Games { get; }

    /// <summary>What is installed for a copy of the game.</summary>
    public InstallReader Installs { get; }

    /// <summary>The game's own files told from added ones, for a clean start.</summary>
    public StockCheck Stock { get; }

    /// <summary>Files fetched for an install, each checked against its checksum.</summary>
    public Downloads Downloads { get; }

    /// <summary>Asking Windows to let this account change files in folders it is refused in.</summary>
    public Permissions Permissions { get; }

    /// <summary>Every change to a copy of the game (install, switch channel, turn off and on, clean start, put back, uninstall, undo), as states kept in git.</summary>
    public GameStates States { get; }

    /// <summary>What the person has told MVS Buddy about itself.</summary>
    public Preferences Preferences { get; }

    /// <summary>The releases fetched so far, kept in a git repository of MVS Buddy's own.</summary>
    internal ReleaseCache Cache { get; }

    /// <summary>The channel paks, kept outside git, a set per channel.</summary>
    internal PakStore Paks { get; }

    /// <summary>Where MVS Buddy keeps everything of its own; empty when it has nowhere.</summary>
    public string DataDirectory => host.DataDirectory;

    /// <summary>
    /// Deletes everything MVS Buddy keeps, for every copy of the game: the states, what clean starts set aside, the pak
    /// sets, the release cache, downloads and logs, and the menu entry (Linux) or desktop shortcut (Windows); the preferences too unless
    /// <paramref name="keepPreferences"/>. The end of an uninstall, at the person's say-so. The states go before the
    /// cache they borrow from, and nothing after a failure, so what is left is still readable. What could not be deleted
    /// (the log MVS Buddy is writing, on Windows) is returned as <see cref="DataRemoval.Left"/>.
    /// </summary>
    public DataRemoval RemoveData(bool keepPreferences)
    {
        var deleted = new List<string>();
        var left = new List<string>();
        try
        {
            foreach (string entry in DesktopEntry.Remove(host))
            {
                deleted.Add(entry);
            }
            if (Shortcut.Remove(host) is string shortcut)
            {
                deleted.Add(shortcut);
            }
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            left.Add($"{(host.IsWindows ? "the desktop shortcut" : "the menu entry")} ({e.Message})");
        }
        if (string.IsNullOrEmpty(host.DataDirectory) || !Directory.Exists(host.DataDirectory))
        {
            return new DataRemoval(deleted, left);
        }
        // The states first, then the cache they borrow objects from; logs last, since the one being written may not go.
        var entries = Directory.EnumerateFileSystemEntries(host.DataDirectory)
            .OrderBy(p => Path.GetFileName(p) switch { "states" => 0, "cache.git" => 1, "logs" => 3, _ => 2 }).ToList();
        bool everything = !keepPreferences;
        foreach (string entry in entries)
        {
            string name = Path.GetFileName(entry);
            if (keepPreferences && name == Preferences.FileName)
            {
                continue;
            }
            try
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
                deleted.Add(entry);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                left.Add($"{entry} ({e.Message})");
                everything = false;
                if (name == "states")
                {
                    // The cache stays readable for the states that are still here.
                    left.Add(Path.Combine(host.DataDirectory, "cache.git") + " (kept: the states still here read from it)");
                    entries.Remove(Path.Combine(host.DataDirectory, "cache.git"));
                }
            }
        }
        if (everything && !Directory.EnumerateFileSystemEntries(host.DataDirectory).Any())
        {
            try
            {
                Directory.Delete(host.DataDirectory);
                deleted.Add(host.DataDirectory);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                left.Add($"{host.DataDirectory} ({e.Message})");
            }
        }
        return new DataRemoval(deleted, left);
    }

    /// <summary>Disposes the HTTP client if MVS Buddy made it.</summary>
    public void Dispose() => ownedHttp?.Dispose();
}

/// <summary>What <see cref="Buddy.RemoveData"/> did: what went, and what could not go (with why).</summary>
public sealed record DataRemoval(IReadOnlyList<string> Deleted, IReadOnlyList<string> Left);
