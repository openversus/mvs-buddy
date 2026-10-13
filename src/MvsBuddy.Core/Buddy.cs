using System.Net.Http;
using MvsBuddy.Catalog;
using MvsBuddy.Games;

namespace MvsBuddy;

/// <summary>What the buddy can do. Made by <see cref="BuddyBuilder"/>.</summary>
public sealed class Buddy : IDisposable
{
    private readonly HttpClient? ownedHttp;

    internal Buddy(ReleaseCatalog releases, GameFinder games, HttpClient? ownedHttp)
    {
        Releases = releases;
        Games = games;
        this.ownedHttp = ownedHttp;
    }

    /// <summary>The mod's releases on each channel, and the paks that go with them.</summary>
    public ReleaseCatalog Releases { get; }

    /// <summary>Finding MultiVersus on this machine.</summary>
    public GameFinder Games { get; }

    /// <summary>Disposes the HTTP client if the buddy made it.</summary>
    public void Dispose() => ownedHttp?.Dispose();
}
