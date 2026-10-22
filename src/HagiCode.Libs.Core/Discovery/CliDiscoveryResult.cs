namespace HagiCode.Libs.Core.Discovery;

/// <summary>
/// Identifies the lookup step that produced a discovered executable.
/// </summary>
public enum CliDiscoverySource
{
    /// <summary>No step produced an executable.</summary>
    None,

    /// <summary>The caller's configured executable path resolved to a file.</summary>
    Configured,

    /// <summary>The executable was found in a <c>HAGICODE_AGENT_CLI_PATH</c> directory.</summary>
    AgentCliPath,

    /// <summary>The executable was found by the standard lookup (<c>PATH</c>, the process directory, or the working directory).</summary>
    Path,

    /// <summary>The executable was found in a well-known install directory of one of the provider's strategies.</summary>
    WellKnownLocation
}

/// <summary>
/// An executable that matched discovery but was not the selected install.
/// </summary>
/// <param name="Path">The absolute path of the shadowed executable.</param>
/// <param name="Source">The lookup step that found it.</param>
/// <param name="InstallKind">The best-effort install kind.</param>
/// <param name="VersionFailure">The failure category of its version probe, or <see langword="null" /> when it was not probed.</param>
public sealed record CliShadowedInstall(
    string Path,
    CliDiscoverySource Source,
    CliInstallKind InstallKind,
    CliVersionProbeFailure? VersionFailure);

/// <summary>
/// The structured result of discovering one CLI provider.
/// </summary>
/// <param name="ProviderName">The provider whose CLI was discovered.</param>
public sealed record CliDiscoveryResult(string ProviderName)
{
    /// <summary>
    /// Gets a value indicating whether an executable was found.
    /// </summary>
    public bool IsFound => ResolvedPath is not null;

    /// <summary>
    /// Gets the absolute path of the selected executable, or <see langword="null" /> when not found.
    /// </summary>
    public string? ResolvedPath { get; init; }

    /// <summary>
    /// Gets the lookup step that produced the selected executable.
    /// </summary>
    public CliDiscoverySource Source { get; init; } = CliDiscoverySource.None;

    /// <summary>
    /// Gets the best-effort install kind of the selected executable. <see cref="CliInstallKind.Unknown" /> when attribution is inconclusive.
    /// </summary>
    public CliInstallKind InstallKind { get; init; } = CliInstallKind.Unknown;

    /// <summary>
    /// Gets the verified version, or <see langword="null" /> when the version was not verified or could not be read.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Gets the failure category of the version probe. <see langword="null" /> when the version was not probed or no executable was found.
    /// When a version is found this is <see cref="CliVersionProbeFailure.None" />. When every probe failed, it is the category of the last attempt.
    /// </summary>
    public CliVersionProbeFailure? VersionFailure { get; init; }

    /// <summary>
    /// Gets the locations that discovery searched, in search order.
    /// </summary>
    public IReadOnlyList<string> ProbedLocations { get; init; } = [];

    /// <summary>
    /// Gets the other executables that matched but were not selected, in probe order.
    /// </summary>
    public IReadOnlyList<CliShadowedInstall> ShadowedInstalls { get; init; } = [];

    /// <summary>
    /// Gets why nothing was found. <see langword="null" /> when an executable was found.
    /// <see cref="CliExecutableNotFoundReason.NativeExecutableMissing" /> means the agent requires its native executable on Windows and only command scripts exist.
    /// </summary>
    public CliExecutableNotFoundReason? NotFoundReason { get; init; }

    /// <summary>
    /// Gets the command scripts that were found but not selected because the agent requires its native executable, in probe order.
    /// </summary>
    public IReadOnlyList<string> SkippedCommandScripts { get; init; } = [];
}
