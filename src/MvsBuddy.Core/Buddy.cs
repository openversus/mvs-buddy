using System.Net.Http;
using MvsBuddy.Catalog;
using MvsBuddy.Changes;
using MvsBuddy.Games;
using MvsBuddy.Installs;

namespace MvsBuddy;

/// <summary>What the buddy can do. Made by <see cref="BuddyBuilder"/>.</summary>
public sealed class Buddy : IDisposable
{
    private readonly HttpClient? ownedHttp;

    internal Buddy(ReleaseCatalog releases, GameFinder games, InstallReader installs, Downloads downloads, StockCheck stock, Permissions permissions, ReleaseCache cache, PakStore paks, GameStates states, Preferences preferences, HttpClient? ownedHttp)
    {
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

    /// <summary>What the person has told the buddy about itself.</summary>
    public Preferences Preferences { get; }

    /// <summary>The releases fetched so far, kept in a git repository of the buddy's own.</summary>
    internal ReleaseCache Cache { get; }

    /// <summary>The channel paks, kept outside git, a set per channel.</summary>
    internal PakStore Paks { get; }

    /// <summary>Disposes the HTTP client if the buddy made it.</summary>
    public void Dispose() => ownedHttp?.Dispose();
}
