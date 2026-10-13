namespace MvsBuddy.Changes;

/// <summary>
/// A change that was refused before it touched anything, or that failed and was put back. The message says which, in
/// words for the person; when something could not be put back, it says what and where.
/// </summary>
public sealed class ChangeException : Exception
{
    /// <summary>A refusal or failure with its reason.</summary>
    public ChangeException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }

    internal ChangeException(string message, IReadOnlyList<string> deniedFolders)
        : base(message) => DeniedFolders = deniedFolders;

    /// <summary>
    /// When the change was refused because this account may not change files in some folders, those folders; otherwise
    /// empty. Nothing was touched. On Windows, permission can be asked for and the change made again.
    /// </summary>
    public IReadOnlyList<string> DeniedFolders { get; } = [];
}
