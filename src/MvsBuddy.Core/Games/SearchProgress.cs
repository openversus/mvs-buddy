namespace MvsBuddy.Games;

/// <summary>How far a search of the drives has got.</summary>
/// <param name="Folder">The folder being searched.</param>
/// <param name="FoldersSearched">How many folders have been searched so far.</param>
/// <param name="Found">The install just found in <paramref name="Folder"/>, or null when this is only news of progress.</param>
public sealed record SearchProgress(string Folder, int FoldersSearched, GameInstall? Found);
