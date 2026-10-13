using System.Net.Http;
using MvsBuddy.Catalog;

namespace MvsBuddy;

/// <summary>What the buddy can do. Made by <see cref="BuddyBuilder"/>.</summary>
public sealed class Buddy : IDisposable
{
    private readonly HttpClient? ownedHttp;

    internal Buddy(ReleaseCatalog releases, HttpClient? ownedHttp)
    {
        Releases = releases;
        this.ownedHttp = ownedHttp;
    }

    /// <summary>The mod's releases on each channel, and the paks that go with them.</summary>
    public ReleaseCatalog Releases { get; }

    /// <summary>Disposes the HTTP client if the buddy made it.</summary>
    public void Dispose() => ownedHttp?.Dispose();
}
