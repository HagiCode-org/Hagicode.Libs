namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Describes provider ownership metadata attached to a managed subprocess start request.
/// </summary>
public sealed record CliProcessOwnershipRegistration
{
    /// <summary>
    /// Gets or sets the CLI provider name that owns the subprocess.
    /// </summary>
    public required string ProviderName { get; init; }

    /// <summary>
    /// Gets the session identifier that owns the subprocess, when the caller has one. It is kept in memory for queries and is not written to the state file.
    /// </summary>
    public string? SessionId { get; init; }
}
