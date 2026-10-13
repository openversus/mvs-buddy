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

    internal Buddy(ReleaseCatalog releases, GameFinder games, InstallReader installs, InstallChanges changes, HttpClient? ownedHttp)
    {
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

    /// <summary>Plans that change an install: holding mods aside and restoring them.</summary>
    public InstallChanges Changes { get; }

    /// <summary>Disposes the HTTP client if the buddy made it.</summary>
    public void Dispose() => ownedHttp?.Dispose();
}
