namespace HagiCode.Libs.Git;

/// <summary>Where a configuration value was read from.</summary>
public enum GitConfigScope
{
    /// <summary>The value is not set in any scope.</summary>
    None,

    /// <summary>The value comes from the repository's local configuration.</summary>
    Local,

    /// <summary>The value comes from the user's global configuration.</summary>
    Global
}

/// <summary>
/// The effective <c>user.name</c> and <c>user.email</c> for a repository, read-only. Each value resolves independently:
/// local configuration wins over global configuration. Values are <see langword="null" /> when no scope provides them.
/// </summary>
/// <param name="Name">The user name, or <see langword="null" />.</param>
/// <param name="NameScope">The scope the name came from.</param>
/// <param name="Email">The user email, or <see langword="null" />.</param>
/// <param name="EmailScope">The scope the email came from.</param>
public sealed record GitIdentity(
    string? Name,
    GitConfigScope NameScope,
    string? Email,
    GitConfigScope EmailScope)
{
    /// <summary>Gets a value indicating whether both a name and an email are available.</summary>
    public bool IsComplete => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Email);
}
