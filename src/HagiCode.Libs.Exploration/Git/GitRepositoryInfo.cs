namespace HagiCode.Libs.Exploration.Git;

/// <summary>
/// Describes a discovered Git repository.
/// </summary>
public sealed record GitRepositoryInfo
{
    /// <summary>
    /// Gets or sets the repository path.
    /// </summary>
    public required string RepositoryPath { get; init; }

    /// <summary>
    /// Gets or sets the current branch name.
    /// </summary>
    public string? Branch { get; init; }

    /// <summary>
    /// Gets or sets the default remote URL.
    /// </summary>
    public string? RemoteUrl { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the working tree has uncommitted changes.
    /// </summary>
    public bool HasUncommittedChanges { get; init; }
}
