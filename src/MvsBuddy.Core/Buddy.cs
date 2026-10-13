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

    internal Buddy(ReleaseCatalog releases, GameFinder games, InstallReader installs, InstallChanges changes, Downloads downloads, Installer installer, StockCheck stock, Permissions permissions, HttpClient? ownedHttp)
    {
        Permissions = permissions;
        Stock = stock;
        Installer = installer;
        Downloads = downloads;
        Changes = changes;
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

    /// <summary>Installing a release, and uninstalling.</summary>
    public Installer Installer { get; }

    /// <summary>Files fetched for an install, each checked against its checksum.</summary>
    public Downloads Downloads { get; }

    /// <summary>Plans that change an install: holding mods aside and restoring them.</summary>
    public InstallChanges Changes { get; }

    /// <summary>Asking Windows to let this account change files in folders it is refused in.</summary>
    public Permissions Permissions { get; }

    /// <summary>Disposes the HTTP client if the buddy made it.</summary>
    public void Dispose() => ownedHttp?.Dispose();
}
