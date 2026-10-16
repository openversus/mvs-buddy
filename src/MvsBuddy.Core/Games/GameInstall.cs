namespace MvsBuddy.Games;

/// <summary>Who installed the game.</summary>
public enum GameStore
{
    /// <summary>Found some other way: a copy outside a store, or a folder the store does not list.</summary>
    Unknown,

    /// <summary>Steam.</summary>
    Steam,

    /// <summary>The Epic Games Launcher.</summary>
    Epic,
}

/// <summary>How MVS Buddy found an install.</summary>
public enum FoundBy
{
    /// <summary>Steam's own record of the install in the Windows registry.</summary>
    SteamRegistry,

    /// <summary>A Steam library's manifest for the game.</summary>
    SteamLibrary,

    /// <summary>An Epic Games Launcher manifest.</summary>
    EpicManifest,

    /// <summary>A search of the drives for the executable.</summary>
    Search,

    /// <summary>A folder the user picked (<see cref="GameFinder.Choose"/>), which may not be verified.</summary>
    Chosen,
}

/// <summary>A copy of MultiVersus on this machine.</summary>
/// <param name="Win64Directory">The folder holding the executable, where the ASI loader and <c>plugins</c> go.</param>
/// <param name="Store">Who installed it.</param>
/// <param name="FoundBy">How MVS Buddy found it.</param>
public sealed record GameInstall(string Win64Directory, GameStore Store, FoundBy FoundBy)
{
    /// <summary>The game's executable.</summary>
    public string ExecutablePath => Path.Combine(Win64Directory, GameFiles.ExecutableName);

    /// <summary>
    /// On Linux, the Wine prefix the game runs in (the folder holding <c>drive_c</c>): Steam's Proton prefix for the game,
    /// whether or not it exists yet (Proton makes it at the first launch), or the prefix the folder sits inside. Null on
    /// Windows, and on Linux when MVS Buddy cannot tell.
    /// </summary>
    public string? WinePrefix { get; init; }

    /// <summary>
    /// Whether MVS Buddy could confirm this is the game: its executable, and its own paks where the game keeps them.
    /// Every install MVS Buddy finds is; one the user picks may not be, and installing there anyway is the user's call.
    /// </summary>
    public bool Verified { get; init; } = true;

    /// <summary>Why the install is not verified, in a sentence for the user; null when it is.</summary>
    public string? UnverifiedReason { get; init; }
}
