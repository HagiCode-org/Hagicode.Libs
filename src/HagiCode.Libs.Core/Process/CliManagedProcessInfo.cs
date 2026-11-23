namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Describes how a managed CLI subprocess came under management.
/// </summary>
public enum CliManagedProcessOrigin
{
    /// <summary>
    /// The process was started by <see cref="CliProcessManager.StartAsync" />.
    /// </summary>
    Started,

    /// <summary>
    /// The process was started by the caller and registered with <see cref="CliProcessManager.RegisterExternalProcessAsync" />.
    /// </summary>
    External,
}

/// <summary>
/// Describes one alive managed CLI subprocess.
/// </summary>
/// <param name="Identity">The process id and start time that identify the process.</param>
/// <param name="ProviderName">The provider that owns the process.</param>
/// <param name="SessionId">The session identifier supplied at registration, or <see langword="null" /> when none was supplied.</param>
/// <param name="ExecutablePath">The resolved executable path of the process.</param>
/// <param name="WorkingDirectory">The working directory, when one was set.</param>
/// <param name="Origin">Whether the manager started the process or the caller registered it.</param>
/// <param name="IsContained">Whether the process is held in a Windows Job Object that ends its members on stop.</param>
public sealed record CliManagedProcessInfo(
    ProcessIdentity Identity,
    string ProviderName,
    string? SessionId,
    string ExecutablePath,
    string? WorkingDirectory,
    CliManagedProcessOrigin Origin,
    bool IsContained)
{
    /// <summary>
    /// Gets the process id.
    /// </summary>
    public int Pid => Identity.Pid;

    /// <summary>
    /// Gets the process start time in UTC.
    /// </summary>
    public DateTimeOffset StartedAtUtc => Identity.StartedAtUtc;
}
