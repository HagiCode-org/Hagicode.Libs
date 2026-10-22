using System.Runtime.InteropServices;

namespace HagiCode.Libs.Core.Discovery;

/// <summary>
/// Describes a CLI provider's installation metadata, serving as the single source
/// of truth for npm package name, pinned version, executable candidates, and
/// public installability.
/// </summary>
/// <param name="ProviderName">The logical name of the provider (e.g., "ClaudeCode", "Codex").</param>
/// <param name="NpmPackage">The npm package name (e.g., "@anthropic-ai/claude-code").</param>
/// <param name="PinnedVersion">The pinned version string (e.g., "2.1.79").</param>
/// <param name="ExecutableCandidates">Ordered list of executable names to search on PATH.</param>
/// <param name="IsPubliclyInstallable">
/// Whether the CLI can be installed on public CI runners via npm without authentication.
/// </param>
public sealed record CliInstallDescriptor(
    string ProviderName,
    string NpmPackage,
    string PinnedVersion,
    string[] ExecutableCandidates,
    bool IsPubliclyInstallable)
{
    /// <summary>
    /// Gets the full npm package string with pinned version (e.g., "@anthropic-ai/claude-code@2.1.79").
    /// </summary>
    public string FullPackageSpecifier => $"{NpmPackage}@{PinnedVersion}";

    /// <summary>
    /// Gets the argument sets used to read the installed version, tried in order.
    /// Defaults to a single <c>--version</c> set.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> VersionArgumentSets { get; init; } = [["--version"]];

    /// <summary>
    /// Gets the known install routes for this CLI. Empty when only path-based discovery applies.
    /// </summary>
    public IReadOnlyList<CliInstallStrategy> InstallStrategies { get; init; } = [];

    /// <summary>
    /// Gets the Windows rule for choosing a native executable over a command script. Defaults to <see cref="CliWindowsExecutablePolicy.PreferNative" />.
    /// </summary>
    public CliWindowsExecutablePolicy WindowsExecutablePolicy { get; init; } = CliWindowsExecutablePolicy.PreferNative;

    /// <summary>
    /// Gets the short note that records the evidence behind <see cref="WindowsExecutablePolicy" />, including "script only" and "not examined".
    /// </summary>
    public string WindowsExecutableEvidence { get; init; } = string.Empty;

    /// <summary>
    /// Gets where the agent's packaged native binary sits inside its npm packages on Windows, or <see langword="null" /> when no layout is known.
    /// </summary>
    public CliPackagedBinaryLayout? PackagedBinaryLayout { get; init; }
}

/// <summary>
/// The Windows rule for an agent's executable.
/// </summary>
public enum CliWindowsExecutablePolicy
{
    /// <summary>
    /// A native <c>.exe</c> is tried first. A command script (<c>.cmd</c>, <c>.bat</c>) is used only when no native executable exists.
    /// </summary>
    PreferNative,

    /// <summary>
    /// Only a native <c>.exe</c> (or the packaged binary of the npm package) is accepted. A command script is never selected.
    /// </summary>
    RequireNative
}

/// <summary>
/// Where an agent's native binary sits inside its npm packages for one Windows architecture.
/// </summary>
/// <param name="Architecture">The process architecture this target serves.</param>
/// <param name="PlatformPackage">The platform npm package (for example <c>@github/copilot-win32-x64</c>).</param>
/// <param name="FileInPackage">The file path inside the platform package, with <c>\</c> or <c>/</c> separators.</param>
public sealed record CliPackagedBinaryTarget(Architecture Architecture, string PlatformPackage, string FileInPackage);

/// <summary>
/// The packaged native binary layout of an npm-distributed agent on Windows.
/// </summary>
/// <param name="MainPackage">The top-level npm package whose shim is on the search path (for example <c>@github/copilot</c>).</param>
/// <param name="Targets">The platform packages to try, in order, for the process architecture.</param>
/// <param name="MainPackageFile">An optional file inside the top-level package that is tried last, after every platform package.</param>
public sealed record CliPackagedBinaryLayout(
    string MainPackage,
    IReadOnlyList<CliPackagedBinaryTarget> Targets,
    string? MainPackageFile = null);
