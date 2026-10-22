using System.Collections;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.Core.Discovery;

/// <summary>
/// Finds a provider's CLI executable, attributes how it was installed, and verifies its version.
/// Resolution order: the configured path, the standard lookup (<c>HAGICODE_AGENT_CLI_PATH</c> before <c>PATH</c>),
/// then the well-known install directories of the provider's strategies on the current platform.
/// Expected failures become result data. Only caller cancellation is thrown.
/// </summary>
public sealed class CliDiscoveryService
{
    private const string AgentCliPathVariable = "HAGICODE_AGENT_CLI_PATH";
    private const string PathVariable = "PATH";
    private const string CopilotProviderName = "Copilot";
    private const string KiroProviderName = "Kiro";

    private readonly CliExecutableResolver _executableResolver;
    private readonly CliProcessManager _processManager;
    private readonly IRuntimeEnvironmentResolver? _runtimeEnvironmentResolver;
    private readonly Func<CliPlatform> _platform;

    /// <summary>
    /// Initializes a new instance of the <see cref="CliDiscoveryService" /> class.
    /// </summary>
    /// <param name="executableResolver">The executable resolver used for the standard lookup.</param>
    /// <param name="processManager">The process manager that runs version commands.</param>
    /// <param name="runtimeEnvironmentResolver">Optional runtime environment, used when a discovery call supplies none.</param>
    public CliDiscoveryService(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null)
        : this(executableResolver, processManager, runtimeEnvironmentResolver, static () => CurrentPlatform())
    {
    }

    internal CliDiscoveryService(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver,
        Func<CliPlatform> platform)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _runtimeEnvironmentResolver = runtimeEnvironmentResolver;
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    /// <summary>
    /// Discovers one provider's CLI.
    /// </summary>
    /// <param name="descriptor">The provider's install descriptor.</param>
    /// <param name="options">Optional lookup options. <see langword="null" /> verifies the version using the process environment.</param>
    /// <param name="cancellationToken">Cancels discovery. Stops any running version probe.</param>
    /// <returns>The discovery result. Expected failures are reported as data.</returns>
    public async Task<CliDiscoveryResult> DiscoverAsync(
        CliInstallDescriptor descriptor,
        CliDiscoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        options ??= new CliDiscoveryOptions();
        cancellationToken.ThrowIfCancellationRequested();

        var platform = _platform();
        var comparer = platform == CliPlatform.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var environment = await ResolveEnvironmentAsync(options, comparer, cancellationToken).ConfigureAwait(false);
        var executableNames = GetExecutableNames(descriptor);
        var strategies = GetApplicableStrategies(descriptor, platform, environment);
        var probedLocations = BuildProbedLocations(options, executableNames, environment, strategies, comparer);

        var candidates = CollectCandidates(options, executableNames, environment, strategies, comparer);
        var skippedScripts = new List<string>();
        candidates = ApplyWindowsExecutablePolicy(descriptor, platform, environment, candidates, comparer, skippedScripts);
        candidates = ApplyProviderPolicy(descriptor.ProviderName, options, candidates, comparer);
        if (candidates.Count == 0)
        {
            return new CliDiscoveryResult(descriptor.ProviderName)
            {
                ProbedLocations = probedLocations,
                NotFoundReason = skippedScripts.Count > 0
                    ? CliExecutableNotFoundReason.NativeExecutableMissing
                    : CliExecutableNotFoundReason.NotInstalled,
                SkippedCommandScripts = skippedScripts,
            };
        }

        var attributed = candidates
            .Select(candidate => new Candidate(
                candidate.Path,
                candidate.Source,
                ClassifyInstallKind(candidate.Path, descriptor, strategies, comparer, platform)))
            .ToArray();

        if (!options.VerifyVersion)
        {
            return BuildResult(descriptor, attributed, winnerIndex: 0, version: null, versionFailure: null, failures: new CliVersionProbeFailure?[attributed.Length], probedLocations, skippedScripts);
        }

        var probe = new CliVersionProbe(_processManager, options.VersionProbeTimeout);
        var failures = new CliVersionProbeFailure?[attributed.Length];
        for (var index = 0; index < attributed.Length; index++)
        {
            var probeResult = await probe.ProbeVersionAsync(
                attributed[index].Path,
                descriptor.VersionArgumentSets,
                environment,
                cancellationToken).ConfigureAwait(false);

            failures[index] = probeResult.Failure;
            if (!string.IsNullOrWhiteSpace(probeResult.Version))
            {
                return BuildResult(descriptor, attributed, index, probeResult.Version, CliVersionProbeFailure.None, failures, probedLocations, skippedScripts);
            }
        }

        // Every probe failed: the first candidate is reported as the path, and the last attempt decides the category.
        return BuildResult(descriptor, attributed, winnerIndex: 0, version: null, versionFailure: failures[^1], failures, probedLocations, skippedScripts);
    }

    /// <summary>
    /// Discovers several providers. One provider's failure does not affect the others.
    /// </summary>
    /// <param name="descriptors">The provider descriptors to discover.</param>
    /// <param name="options">Options applied to every provider.</param>
    /// <param name="cancellationToken">Cancels discovery.</param>
    /// <returns>One result per descriptor, in input order.</returns>
    public async Task<IReadOnlyList<CliDiscoveryResult>> DiscoverAllAsync(
        IEnumerable<CliInstallDescriptor> descriptors,
        CliDiscoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        var results = new List<CliDiscoveryResult>();
        foreach (var descriptor in descriptors)
        {
            results.Add(await DiscoverAsync(descriptor, options, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    internal static CliPlatform CurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return CliPlatform.Windows;
        }

        return OperatingSystem.IsMacOS() ? CliPlatform.MacOS : CliPlatform.Linux;
    }

    private async Task<IReadOnlyDictionary<string, string?>> ResolveEnvironmentAsync(
        CliDiscoveryOptions options,
        StringComparer comparer,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string?> source;
        if (options.EnvironmentVariables is not null)
        {
            source = options.EnvironmentVariables;
        }
        else if (_runtimeEnvironmentResolver is not null)
        {
            source = await _runtimeEnvironmentResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            source = ReadProcessEnvironment();
        }

        // Keys are copied under the platform's rule so PATH and Path resolve the same way on Windows.
        var normalized = new Dictionary<string, string?>(comparer);
        foreach (var entry in source)
        {
            normalized[entry.Key] = entry.Value;
        }

        return normalized;
    }

    private static Dictionary<string, string?> ReadProcessEnvironment()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key)
            {
                environment[key] = entry.Value as string;
            }
        }

        return environment;
    }

    private static IReadOnlyList<string> GetExecutableNames(CliInstallDescriptor descriptor)
    {
        return descriptor.ExecutableCandidates
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<ApplicableStrategy> GetApplicableStrategies(
        CliInstallDescriptor descriptor,
        CliPlatform platform,
        IReadOnlyDictionary<string, string?> environment)
    {
        return descriptor.InstallStrategies
            .Where(strategy => strategy.AppliesTo(platform))
            .Select(strategy => new ApplicableStrategy(strategy, strategy.GetWellKnownDirectories(platform, environment)))
            .ToArray();
    }

    private List<Candidate> CollectCandidates(
        CliDiscoveryOptions options,
        IReadOnlyList<string> executableNames,
        IReadOnlyDictionary<string, string?> environment,
        IReadOnlyList<ApplicableStrategy> strategies,
        StringComparer comparer)
    {
        var candidates = new List<Candidate>();
        var seen = new HashSet<string>(comparer);
        var agentCliDirectories = SplitDirectories(GetValue(environment, AgentCliPathVariable));

        // Step 1: the configured path. A bare name resolves through the standard lookup.
        if (!string.IsNullOrWhiteSpace(options.ConfiguredExecutablePath))
        {
            var configured = _executableResolver.ResolveExecutablePath(options.ConfiguredExecutablePath, environment);
            AddCandidate(candidates, seen, configured, CliDiscoverySource.Configured);
        }

        // Step 2: the standard lookup (process directory, working directory, HAGICODE_AGENT_CLI_PATH, PATH) for each name.
        foreach (var executableName in executableNames)
        {
            foreach (var path in _executableResolver.ResolveExecutablePaths(executableName, environment))
            {
                var source = IsInDirectories(path, agentCliDirectories, comparer)
                    ? CliDiscoverySource.AgentCliPath
                    : CliDiscoverySource.Path;
                AddCandidate(candidates, seen, path, source);
            }
        }

        // Step 3: the strategies' well-known directories on this platform, in strategy order.
        foreach (var applicable in strategies)
        {
            foreach (var executableName in executableNames)
            {
                foreach (var path in _executableResolver.ResolveExecutablePathsInDirectories(executableName, applicable.Directories, environment))
                {
                    AddCandidate(candidates, seen, path, CliDiscoverySource.WellKnownLocation);
                }
            }
        }

        return candidates;
    }

    private static void AddCandidate(
        List<Candidate> candidates,
        HashSet<string> seen,
        string? path,
        CliDiscoverySource source)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        if (seen.Add(fullPath))
        {
            candidates.Add(new Candidate(fullPath, source, CliInstallKind.Unknown));
        }
    }

    /// <summary>
    /// Applies the Windows executable policy of a descriptor. Only command scripts found by the standard lookup or the install
    /// directories are affected. An explicitly configured path is kept as given.
    /// </summary>
    private List<Candidate> ApplyWindowsExecutablePolicy(
        CliInstallDescriptor descriptor,
        CliPlatform platform,
        IReadOnlyDictionary<string, string?> environment,
        List<Candidate> candidates,
        StringComparer comparer,
        List<string> skippedScripts)
    {
        if (platform != CliPlatform.Windows || !CliExecutableResolver.HasWindowsPolicy(descriptor))
        {
            return candidates;
        }

        var scripts = candidates
            .Where(static candidate => candidate.Source != CliDiscoverySource.Configured && !IsNativeExecutable(candidate.Path))
            .ToList();
        var requireNative = descriptor.WindowsExecutablePolicy == CliWindowsExecutablePolicy.RequireNative;
        var result = requireNative
            ? candidates.Where(candidate => !scripts.Contains(candidate)).ToList()
            : candidates.ToList();

        if (requireNative)
        {
            skippedScripts.AddRange(scripts.Select(static candidate => candidate.Path));
        }

        if (descriptor.PackagedBinaryLayout is not { } layout)
        {
            return result;
        }

        // A packaged binary is looked for beside every shim that was found, and in the npm global prefix.
        var packaged = new List<Candidate>();
        var seenPackaged = new HashSet<string>(comparer);
        foreach (var script in scripts)
        {
            var directory = Path.GetDirectoryName(script.Path);
            if (directory is not null && _executableResolver.FindPackagedBinary(layout, directory) is { } binary)
            {
                AddCandidate(packaged, seenPackaged, binary, script.Source);
            }
        }

        foreach (var npmDirectory in _executableResolver.GetNpmPrefixDirectories(descriptor, environment))
        {
            if (_executableResolver.FindPackagedBinary(layout, npmDirectory) is { } binary)
            {
                AddCandidate(packaged, seenPackaged, binary, CliDiscoverySource.WellKnownLocation);
            }
        }

        // A packaged binary is native, so it is listed with the native candidates. For a PreferNative agent it goes before the scripts.
        var insertAt = requireNative
            ? result.Count
            : result.FindIndex(static candidate => candidate.Source != CliDiscoverySource.Configured && !IsNativeExecutable(candidate.Path));
        if (insertAt < 0)
        {
            insertAt = result.Count;
        }

        var existing = result.Select(static candidate => candidate.Path).ToHashSet(comparer);
        result.InsertRange(insertAt, packaged.Where(candidate => !existing.Contains(candidate.Path)));
        return result;
    }

    private static bool IsNativeExecutable(string path)
    {
        return string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static List<Candidate> ApplyProviderPolicy(
        string providerName,
        CliDiscoveryOptions options,
        List<Candidate> candidates,
        StringComparer comparer)
    {
        if (string.Equals(providerName, KiroProviderName, StringComparison.Ordinal))
        {
            var accepted = KiroExecutablePathPolicy.SelectAcceptedPaths(candidates.Select(static candidate => candidate.Path)).ToHashSet(comparer);
            return candidates.Where(candidate => accepted.Contains(candidate.Path)).ToList();
        }

        if (string.Equals(providerName, CopilotProviderName, StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(options.ConfiguredExecutablePath))
        {
            var preferred = CopilotExecutablePathPolicy.SelectPreferredPath(candidates.Select(static candidate => candidate.Path));
            var preferredIndex = preferred is null
                ? -1
                : candidates.FindIndex(candidate => comparer.Equals(candidate.Path, preferred));
            if (preferredIndex > 0)
            {
                var preferredCandidate = candidates[preferredIndex];
                candidates.RemoveAt(preferredIndex);
                candidates.Insert(0, preferredCandidate);
            }
        }

        return candidates;
    }

    private static CliInstallKind ClassifyInstallKind(
        string path,
        CliInstallDescriptor descriptor,
        IReadOnlyList<ApplicableStrategy> strategies,
        StringComparer comparer,
        CliPlatform platform)
    {
        var comparison = comparer == StringComparer.OrdinalIgnoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var separator = platform == CliPlatform.Windows ? '\\' : '/';
        var linkTarget = ResolveFinalLinkTarget(path);

        // A packaged binary of an npm layout sits under node_modules/<platform package>, which the main-package check below does not match.
        if (descriptor.PackagedBinaryLayout is { } layout && IsInsideLayoutPlatformPackage(path, layout, comparison))
        {
            return CliInstallKind.Npm;
        }

        foreach (var applicable in strategies)
        {
            foreach (var directory in applicable.Directories)
            {
                if (IsUnderDirectory(path, directory, separator, comparison)
                    || (linkTarget is not null && IsUnderDirectory(linkTarget, directory, separator, comparison)))
                {
                    return applicable.Strategy.Kind;
                }
            }

            if (applicable.Strategy.Kind == CliInstallKind.Npm
                && IsNpmInstall(path, linkTarget, descriptor.NpmPackage, separator, comparison))
            {
                return applicable.Strategy.Kind;
            }
        }

        return CliInstallKind.Unknown;
    }

    private static bool IsInsideLayoutPlatformPackage(string path, CliPackagedBinaryLayout layout, StringComparison comparison)
    {
        return layout.Targets.Any(target =>
        {
            var segments = target.PlatformPackage.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var marker = "node_modules" + Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, segments) + Path.DirectorySeparatorChar;
            return path.Contains(marker, comparison);
        });
    }

    private static bool IsNpmInstall(
        string path,
        string? linkTarget,
        string npmPackage,
        char separator,
        StringComparison comparison)
    {
        if (string.IsNullOrWhiteSpace(npmPackage))
        {
            return false;
        }

        var packageSegments = npmPackage.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var packageRelativePath = "node_modules" + separator + string.Join(separator, packageSegments);

        // Unix npm links point into <prefix>/lib/node_modules/<package>.
        if (linkTarget is not null && linkTarget.Contains(packageRelativePath, comparison))
        {
            return true;
        }

        // Windows npm shims sit next to <prefix>/node_modules/<package>.
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var packageDirectory = Path.Combine(
            new[] { directory, "node_modules" }.Concat(packageSegments).ToArray());
        return Directory.Exists(packageDirectory);
    }

    private static string? ResolveFinalLinkTarget(string path)
    {
        try
        {
            return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsUnderDirectory(string path, string directory, char separator, StringComparison comparison)
    {
        var normalizedDirectory = directory.TrimEnd('/', '\\');
        if (normalizedDirectory.Length == 0)
        {
            return false;
        }

        return path.StartsWith(normalizedDirectory + separator, comparison);
    }

    private static bool IsInDirectories(string path, IReadOnlyList<string> directories, StringComparer comparer)
    {
        if (directories.Count == 0)
        {
            return false;
        }

        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return false;
        }

        foreach (var directory in directories)
        {
            var normalized = NormalizeDirectory(directory);
            if (normalized is not null && comparer.Equals(normalized, Path.TrimEndingDirectorySeparator(parent)))
            {
                return true;
            }
        }

        return false;
    }

    private static string? NormalizeDirectory(string directory)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> SplitDirectories(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string? GetValue(IReadOnlyDictionary<string, string?> environment, string name)
    {
        return environment.TryGetValue(name, out var value) ? value : null;
    }

    private static IReadOnlyList<string> BuildProbedLocations(
        CliDiscoveryOptions options,
        IReadOnlyList<string> executableNames,
        IReadOnlyDictionary<string, string?> environment,
        IReadOnlyList<ApplicableStrategy> strategies,
        StringComparer comparer)
    {
        var locations = new List<string>();
        var seen = new HashSet<string>(comparer);

        void AddLocation(string location)
        {
            if (seen.Add(location))
            {
                locations.Add(location);
            }
        }

        if (!string.IsNullOrWhiteSpace(options.ConfiguredExecutablePath))
        {
            AddLocation(options.ConfiguredExecutablePath);
        }

        var directories = new List<string>();
        var processDirectory = System.Environment.ProcessPath is { } processPath ? Path.GetDirectoryName(processPath) : null;
        if (!string.IsNullOrWhiteSpace(processDirectory))
        {
            directories.Add(processDirectory);
        }

        directories.Add(Directory.GetCurrentDirectory());
        directories.AddRange(SplitDirectories(GetValue(environment, AgentCliPathVariable)));
        directories.AddRange(SplitDirectories(GetValue(environment, PathVariable)));

        foreach (var directory in directories)
        {
            foreach (var executableName in executableNames)
            {
                AddLocation(Path.Combine(directory, executableName));
            }
        }

        foreach (var applicable in strategies)
        {
            foreach (var directory in applicable.Directories)
            {
                foreach (var executableName in executableNames)
                {
                    AddLocation(Path.Combine(directory, executableName));
                }
            }
        }

        return locations;
    }

    private static CliDiscoveryResult BuildResult(
        CliInstallDescriptor descriptor,
        IReadOnlyList<Candidate> candidates,
        int winnerIndex,
        string? version,
        CliVersionProbeFailure? versionFailure,
        IReadOnlyList<CliVersionProbeFailure?> failures,
        IReadOnlyList<string> probedLocations,
        IReadOnlyList<string> skippedScripts)
    {
        var winner = candidates[winnerIndex];
        var shadowed = new List<CliShadowedInstall>();
        for (var index = 0; index < candidates.Count; index++)
        {
            if (index == winnerIndex)
            {
                continue;
            }

            var candidate = candidates[index];
            shadowed.Add(new CliShadowedInstall(candidate.Path, candidate.Source, candidate.InstallKind, failures[index]));
        }

        return new CliDiscoveryResult(descriptor.ProviderName)
        {
            ResolvedPath = winner.Path,
            Source = winner.Source,
            InstallKind = winner.InstallKind,
            Version = version,
            VersionFailure = versionFailure,
            ProbedLocations = probedLocations,
            ShadowedInstalls = shadowed,
            SkippedCommandScripts = skippedScripts
        };
    }

    private sealed record Candidate(string Path, CliDiscoverySource Source, CliInstallKind InstallKind);

    private sealed record ApplicableStrategy(CliInstallStrategy Strategy, IReadOnlyList<string> Directories);
}
