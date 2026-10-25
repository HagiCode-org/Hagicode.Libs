using System.ComponentModel;
using System.Text.RegularExpressions;
using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.Core.Discovery;

/// <summary>
/// Classifies why a version probe did not produce a version.
/// </summary>
public enum CliVersionProbeFailure
{
    /// <summary>The probe produced a version.</summary>
    None,

    /// <summary>The executable could not be started.</summary>
    ProcessStartFailed,

    /// <summary>The version command exited with a non-zero code.</summary>
    CommandFailed,

    /// <summary>The command output contained no semantic-version token.</summary>
    InvalidOutput,

    /// <summary>The command did not exit within the probe time limit.</summary>
    TimedOut
}

/// <summary>
/// The outcome of a version probe.
/// </summary>
/// <param name="Version">The extracted version token without a leading <c>v</c>, or <see langword="null" /> when the probe failed.</param>
/// <param name="Failure">The failure category, or <see cref="CliVersionProbeFailure.None" /> when a version was found.</param>
public sealed record CliVersionProbeResult(string? Version, CliVersionProbeFailure Failure);

/// <summary>
/// Runs a CLI version command and extracts a clean semantic-version token from its output.
/// Built on <see cref="CliProcessManager" /> so Windows shims, process-tree termination, and cancellation follow the same rules as other CLI executions.
/// </summary>
public sealed partial class CliVersionProbe
{
    /// <summary>
    /// The number of characters retained per output stream. Output past this cap is drained but not parsed.
    /// </summary>
    public const int MaxOutputCharacters = 32 * 1024;

    /// <summary>
    /// The default time limit for one version command.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private const string JpackageLauncherVariable = "_JPACKAGE_LAUNCHER";

    private readonly CliProcessManager _processManager;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Initializes a new instance of the <see cref="CliVersionProbe" /> class with a default process manager and time limit.
    /// </summary>
    public CliVersionProbe()
        : this(new CliProcessManager())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CliVersionProbe" /> class.
    /// </summary>
    /// <param name="processManager">The process manager that runs the version commands.</param>
    /// <param name="timeout">The time limit for one version command. Defaults to <see cref="DefaultTimeout" />.</param>
    public CliVersionProbe(CliProcessManager processManager, TimeSpan? timeout = null)
    {
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The probe time limit must be positive.");
        }
    }

    /// <summary>
    /// Gets the version argument sets offered for GitHub Copilot, in the order they are tried.
    /// </summary>
    /// <returns>The argument sets <c>version</c>, <c>--version</c>, and <c>-v</c>.</returns>
    public static IReadOnlyList<IReadOnlyList<string>> GetCopilotVersionArgumentSets() =>
        [["version"], ["--version"], ["-v"]];

    /// <summary>
    /// Probes one executable with each argument set in order and returns the first version found.
    /// </summary>
    /// <param name="executablePath">The executable path or command name.</param>
    /// <param name="argumentSets">The argument sets to try, in order.</param>
    /// <param name="environmentVariables">Optional environment overrides for the probe process. A <see langword="null" /> value removes the variable.</param>
    /// <param name="cancellationToken">Cancels the probe. Caller cancellation is surfaced as an exception.</param>
    /// <returns>The probe result. When no argument set yields a version, the category of the last attempt.</returns>
    public async Task<CliVersionProbeResult> ProbeVersionAsync(
        string executablePath,
        IEnumerable<IReadOnlyList<string>> argumentSets,
        IReadOnlyDictionary<string, string?>? environmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(argumentSets);

        var argumentsToTry = argumentSets.ToArray();
        var lastFailure = CliVersionProbeFailure.InvalidOutput;

        foreach (var arguments in argumentsToTry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await RunVersionCommandAsync(executablePath, arguments, environmentVariables, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result.Version))
            {
                return result;
            }

            lastFailure = result.Failure;
        }

        return new CliVersionProbeResult(null, lastFailure);
    }

    /// <summary>
    /// Probes one executable and returns only its version.
    /// </summary>
    /// <param name="executablePath">The executable path or command name.</param>
    /// <param name="argumentSets">The argument sets to try, in order.</param>
    /// <param name="environmentVariables">Optional environment overrides for the probe process.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>The version, or <see langword="null" /> when no version was found.</returns>
    public async Task<string?> TryGetVersionAsync(
        string executablePath,
        IEnumerable<IReadOnlyList<string>> argumentSets,
        IReadOnlyDictionary<string, string?>? environmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        var result = await ProbeVersionAsync(executablePath, argumentSets, environmentVariables, cancellationToken).ConfigureAwait(false);
        return result.Version;
    }

    /// <summary>
    /// Probes an ordered list of executable candidates and returns the first version that any candidate yields.
    /// </summary>
    /// <param name="executableCandidates">The candidate paths. Blank entries are ignored and duplicates are probed once.</param>
    /// <param name="argumentSets">The argument sets to try for each candidate, in order.</param>
    /// <param name="environmentVariables">Optional environment overrides for the probe processes.</param>
    /// <param name="cancellationToken">Cancels the probes.</param>
    /// <returns>The first version found, or <see langword="null" /> when no candidate yields one.</returns>
    public async Task<string?> TryGetVersionAsync(
        IEnumerable<string?> executableCandidates,
        IEnumerable<IReadOnlyList<string>> argumentSets,
        IReadOnlyDictionary<string, string?>? environmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executableCandidates);
        ArgumentNullException.ThrowIfNull(argumentSets);

        var candidates = executableCandidates
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(static candidate => candidate!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var argumentsToTry = argumentSets.ToArray();

        foreach (var executablePath in candidates)
        {
            var result = await ProbeVersionAsync(executablePath, argumentsToTry, environmentVariables, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result.Version))
            {
                return result.Version;
            }
        }

        return null;
    }

    private async Task<CliVersionProbeResult> RunVersionCommandAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environmentVariables,
        CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await _processManager.ExecuteAsync(
                new ProcessStartContext
                {
                    ExecutablePath = executablePath,
                    Arguments = arguments,
                    EnvironmentVariables = BuildProbeEnvironment(environmentVariables),
                    Timeout = _timeout,
                    MaxCapturedOutputCharacters = MaxOutputCharacters,
                    ApplyResourceLimits = false
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsProcessStartFailure(exception))
        {
            return new CliVersionProbeResult(null, CliVersionProbeFailure.ProcessStartFailed);
        }

        if (result.TimedOut)
        {
            return new CliVersionProbeResult(null, CliVersionProbeFailure.TimedOut);
        }

        if (result.ExitCode != 0)
        {
            return new CliVersionProbeResult(null, CliVersionProbeFailure.CommandFailed);
        }

        var version = ExtractVersion(result.StandardOutput) ?? ExtractVersion(result.StandardError);
        return version is null
            ? new CliVersionProbeResult(null, CliVersionProbeFailure.InvalidOutput)
            : new CliVersionProbeResult(version, CliVersionProbeFailure.None);
    }

    private static IReadOnlyDictionary<string, string?> BuildProbeEnvironment(IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var merged = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (environmentVariables is not null)
        {
            foreach (var entry in environmentVariables)
            {
                merged[entry.Key] = entry.Value;
            }
        }

        // The jpackage launcher variable makes a child JVM-based CLI relaunch itself; the probe must not inherit it.
        merged[JpackageLauncherVariable] = null;
        return merged;
    }

    private static bool IsProcessStartFailure(Exception exception)
    {
        return exception is Win32Exception
            or FileNotFoundException
            or DirectoryNotFoundException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException;
    }

    private static string? ExtractVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("Node.js ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("npm ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Usage:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var versionMatch = VersionTokenRegex().Match(line);
            if (versionMatch.Success)
            {
                return versionMatch.Groups["version"].Value;
            }
        }

        return null;
    }

    // A trailing sentence period is allowed (Copilot prints "GitHub Copilot CLI 1.0.60."); a period followed by more version text is not.
    [GeneratedRegex(
        @"(?<![0-9A-Za-z])v?(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?(?:\+[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?)(?![0-9A-Za-z+-])(?!\.[0-9A-Za-z])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionTokenRegex();
}
