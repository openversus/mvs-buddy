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
}
