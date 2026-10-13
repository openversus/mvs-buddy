namespace MvsBuddy.Catalog;

/// <summary>A repository on GitHub, by owner and name.</summary>
public sealed record GitHubRepository
{
    /// <summary>A repository on GitHub.</summary>
    /// <exception cref="ArgumentException">The owner or name is empty.</exception>
    public GitHubRepository(string owner, string name)
    {
        Owner = string.IsNullOrEmpty(owner) ? throw new ArgumentException("The owner is empty.", nameof(owner)) : owner;
        Name = string.IsNullOrEmpty(name) ? throw new ArgumentException("The repository name is empty.", nameof(name)) : name;
    }

    /// <summary>The user or organisation.</summary>
    public string Owner { get; }

    /// <summary>The repository's name.</summary>
    public string Name { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{Owner}/{Name}";
}
