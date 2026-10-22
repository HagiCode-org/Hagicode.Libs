namespace HagiCode.Libs.Core.Discovery;

/// <summary>
/// Why an executable lookup found nothing.
/// </summary>
public enum CliExecutableNotFoundReason
{
    /// <summary>No executable and no command script was found for the agent.</summary>
    NotInstalled,

    /// <summary>Only command scripts were found, and the agent requires its native executable on Windows.</summary>
    NativeExecutableMissing
}

/// <summary>
/// The outcome of resolving an agent's executable under its registry policy.
/// </summary>
/// <param name="ResolvedPath">The absolute path of the selected executable, or <see langword="null" /> when not found.</param>
/// <param name="NotFoundReason">The reason nothing was found. <see langword="null" /> when a path was resolved.</param>
/// <param name="SkippedCommandScripts">The command scripts that were found but not selected, in probe order.</param>
public sealed record CliExecutableLookupResult(
    string? ResolvedPath,
    CliExecutableNotFoundReason? NotFoundReason,
    IReadOnlyList<string> SkippedCommandScripts)
{
    /// <summary>
    /// Gets a value indicating whether an executable was resolved.
    /// </summary>
    public bool IsFound => ResolvedPath is not null;

    /// <summary>
    /// Gets every path the policy accepts, in precedence order. The first item equals <see cref="ResolvedPath" />.
    /// Callers that choose among several installs, such as the Copilot shim preference, read this list.
    /// </summary>
    public IReadOnlyList<string> AcceptedPaths { get; init; } = [];
}
