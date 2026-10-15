namespace MvsBuddy.Changes;

/// <summary>A Steam account's launch options for MultiVersus, as the buddy read them from Steam's files.</summary>
/// <param name="Id">The account id, the folder under Steam's <c>userdata</c>.</param>
/// <param name="Name">The name Steam shows for the account, or its id when Steam's files do not say.</param>
/// <param name="Options">The launch options as they are now; empty when there are none.</param>
/// <param name="LoadsLoader">Whether they tell Wine to load the loader (<see cref="GameStates.LaunchOptionsDlls"/>) from the game's folder.</param>
public sealed record LaunchOptionsAccount(string Id, string Name, string Options, bool LoadsLoader)
{
    /// <summary>The account's <c>localconfig.vdf</c>.</summary>
    internal string File { get; init; } = "";
}
