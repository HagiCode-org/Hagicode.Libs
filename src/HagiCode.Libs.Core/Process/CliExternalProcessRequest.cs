namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Describes a process that the caller started itself and wants registered with the managed-process ledger.
/// </summary>
public sealed record CliExternalProcessRequest
{
    /// <summary>
    /// Gets the CLI provider name that owns the process.
    /// </summary>
    public required string ProviderName { get; init; }

    /// <summary>
    /// Gets the session identifier that owns the process, when known.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Gets the executable path to report. When <see langword="null" />, the registration reports the process's main module name when it can be read.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Gets the working directory to report.
    /// </summary>
    public string? WorkingDirectory { get; init; }
}
