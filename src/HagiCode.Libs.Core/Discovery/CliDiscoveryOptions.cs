namespace HagiCode.Libs.Core.Discovery;

/// <summary>
/// Options that control one <see cref="CliDiscoveryService" /> lookup.
/// </summary>
public sealed record CliDiscoveryOptions
{
    /// <summary>
    /// Gets the explicitly configured executable path or command name. It is tried first and falls through when it does not resolve.
    /// </summary>
    public string? ConfiguredExecutablePath { get; init; }

    /// <summary>
    /// Gets a value indicating whether discovery starts the CLI to read its version.
    /// <see langword="false" /> resolves the path only and never starts the CLI process.
    /// </summary>
    public bool VerifyVersion { get; init; } = true;

    /// <summary>
    /// Gets the environment that supplies <c>PATH</c>, <c>HAGICODE_AGENT_CLI_PATH</c>, <c>PATHEXT</c>, home directories, and the version probe environment.
    /// <see langword="null" /> uses the runtime environment resolver when one is registered, otherwise the current process environment.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? EnvironmentVariables { get; init; }

    /// <summary>
    /// Gets the time limit for each version command. <see langword="null" /> uses <see cref="CliVersionProbe.DefaultTimeout" />.
    /// </summary>
    public TimeSpan? VersionProbeTimeout { get; init; }
}
