namespace HagiCode.Libs.Git;

/// <summary>
/// Options for <see cref="IGitClient" />. Every value has a default that suits a desktop or server host.
/// </summary>
public sealed class GitClientOptions
{
    /// <summary>
    /// Gets or sets an explicit <c>git</c> executable path. When unset, <c>git</c> is resolved from <c>PATH</c>.
    /// </summary>
    public string? GitExecutablePath { get; set; }

    /// <summary>
    /// Gets or sets the time limit for local commands (inspection, staging, commit). Default 2 minutes.
    /// </summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets or sets the time limit for network commands (push, pull, rebase, clone). Default 10 minutes.
    /// </summary>
    public TimeSpan RemoteCommandTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Gets or sets environment variables applied on top of the current process environment for every git command.
    /// A <see langword="null" /> value removes the variable. Used by tests for hermetic configuration; production
    /// hosts normally leave it empty. The client never writes git configuration itself.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? EnvironmentVariables { get; set; }
}
