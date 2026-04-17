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
}
