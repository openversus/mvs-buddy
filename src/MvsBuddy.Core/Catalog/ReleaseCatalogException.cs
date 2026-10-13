namespace MvsBuddy.Catalog;

/// <summary>GitHub could not be asked, or refused. The message says why in words a player can act on.</summary>
public sealed class ReleaseCatalogException : Exception
{
    /// <summary>A failure with its reason.</summary>
    public ReleaseCatalogException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
