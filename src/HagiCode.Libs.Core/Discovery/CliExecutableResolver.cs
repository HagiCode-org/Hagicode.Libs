using System.Runtime.InteropServices;

namespace HagiCode.Libs.Core.Discovery;

/// <summary>
/// Resolves CLI executable paths from PATH-like environments.
/// </summary>
public class CliExecutableResolver
{
    private const string AgentCliPathEnvironmentVariable = "HAGICODE_AGENT_CLI_PATH";
    private const string PathEnvironmentVariable = "PATH";
    private const string PathExtEnvironmentVariable = "PATHEXT";
    private const string ExecutableExtension = ".exe";
    private static readonly string[] NativeWindowsExtensions = [".exe", ".cmd", ".bat"];
    private readonly Func<bool> _isWindows;
    private readonly Func<Architecture> _processArchitecture;

    /// <summary>
    /// Initializes a new instance of the <see cref="CliExecutableResolver" /> class.
    /// </summary>
    public CliExecutableResolver()
        : this(static () => OperatingSystem.IsWindows(), static () => RuntimeInformation.ProcessArchitecture)
    {
    }

    internal CliExecutableResolver(Func<bool> isWindows)
        : this(isWindows, static () => RuntimeInformation.ProcessArchitecture)
    {
    }

    internal CliExecutableResolver(Func<bool> isWindows, Func<Architecture> processArchitecture)
    {
        _isWindows = isWindows ?? throw new ArgumentNullException(nameof(isWindows));
        _processArchitecture = processArchitecture ?? throw new ArgumentNullException(nameof(processArchitecture));
    }

    /// <summary>
    /// Resolves a single executable path.
    /// </summary>
    /// <param name="executableName">The executable name or path.</param>
    /// <param name="environmentVariables">Optional environment variables to search.</param>
    /// <returns>The resolved absolute path, or <see langword="null" /> when not found.</returns>
    public virtual string? ResolveExecutablePath(
        string? executableName,
        IReadOnlyDictionary<string, string?>? environmentVariables = null)
    {
        return ResolveExecutablePaths(executableName, environmentVariables).FirstOrDefault();
    }

    /// <summary>
    /// Resolves all matching executable paths in probe order.
    /// On Windows the probe runs tier by tier (process and working directories, then <c>HAGICODE_AGENT_CLI_PATH</c>, then <c>PATH</c>),
    /// and inside a tier each extension is tried across all of its directories before the next extension.
    /// </summary>
    /// <param name="executableName">The executable name or path.</param>
    /// <param name="environmentVariables">Optional environment variables to search.</param>
    /// <returns>The ordered resolved absolute paths.</returns>
    public virtual IReadOnlyList<string> ResolveExecutablePaths(
        string? executableName,
        IReadOnlyDictionary<string, string?>? environmentVariables = null)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return [];
        }

        if (ShouldResolveDirectly(executableName))
        {
            var directPath = ResolveDirectPath(executableName);
            return directPath is null ? [] : [directPath];
        }

        var resolvedPaths = new List<string>();
        var comparer = _isWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);

        foreach (var tier in GetProbeTiers(environmentVariables))
        {
            foreach (var candidate in EnumerateTierCandidates(tier, executableName, environmentVariables))
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                var fullPath = Path.GetFullPath(candidate);
                if (seen.Add(fullPath))
                {
                    resolvedPaths.Add(fullPath);
                }
            }
        }

        return resolvedPaths;
    }

    /// <summary>
    /// Resolves the first available executable from an ordered candidate list.
    /// </summary>
    /// <param name="executableNames">The ordered executable candidates.</param>
    /// <param name="environmentVariables">Optional environment variables to search.</param>
    /// <returns>The first resolved executable path, or <see langword="null" /> when none are found.</returns>
    public virtual string? ResolveFirstAvailablePath(
        IEnumerable<string> executableNames,
        IReadOnlyDictionary<string, string?>? environmentVariables = null)
    {
        ArgumentNullException.ThrowIfNull(executableNames);

        foreach (var executableName in executableNames)
        {
            var resolved = ResolveExecutablePath(executableName, environmentVariables);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves an agent's executable under its install-registry policy, which is the lookup every provider uses for availability and launch.
    /// On Windows, an agent whose policy is <see cref="CliWindowsExecutablePolicy.RequireNative" />, or that has a packaged-binary layout, resolves
    /// to a native executable only. On other platforms, and for agents without a Windows policy, this is <see cref="ResolveFirstAvailablePath" />.
    /// </summary>
    /// <param name="providerName">The provider name that keys the install descriptor, such as <c>ClaudeCode</c>.</param>
    /// <param name="executableNames">The ordered executable candidates.</param>
    /// <param name="environmentVariables">Optional environment variables to search.</param>
    /// <returns>The resolved path, or the reason nothing was found together with the command scripts that were skipped.</returns>
    public virtual CliExecutableLookupResult ResolveProviderExecutable(
        string providerName,
        IEnumerable<string> executableNames,
        IReadOnlyDictionary<string, string?>? environmentVariables = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(executableNames);

        var candidateNames = executableNames.ToArray();
        var descriptor = CliInstallRegistry.FindByProviderName(providerName);
        if (!_isWindows() || descriptor is null || !HasWindowsPolicy(descriptor))
        {
            var resolved = ResolveFirstAvailablePath(candidateNames, environmentVariables);
            var accepted = candidateNames
                .SelectMany(name => ResolveExecutablePaths(name, environmentVariables))
                .Distinct(_isWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .ToArray();
            return resolved is null
                ? new CliExecutableLookupResult(null, CliExecutableNotFoundReason.NotInstalled, []) { AcceptedPaths = accepted }
                : new CliExecutableLookupResult(resolved, null, []) { AcceptedPaths = accepted };
        }

        var comparer = StringComparer.OrdinalIgnoreCase;
        var acceptedPaths = new List<string>();
        var skippedScripts = new List<string>();
        foreach (var executableName in candidateNames.Where(static name => !string.IsNullOrWhiteSpace(name)).Select(static name => name.Trim()))
        {
            if (ShouldResolveDirectly(executableName))
            {
                // An explicit name or path is an explicit choice, so it is returned as named.
                var directPath = ResolveDirectPath(executableName);
                if (directPath is not null)
                {
                    acceptedPaths.Add(directPath);
                }

                continue;
            }

            acceptedPaths.AddRange(CollectWindowsPolicyPaths(descriptor, executableName, environmentVariables, skippedScripts));
        }

        acceptedPaths = acceptedPaths.Distinct(comparer).ToList();
        if (acceptedPaths.Count > 0)
        {
            return new CliExecutableLookupResult(acceptedPaths[0], null, []) { AcceptedPaths = acceptedPaths };
        }

        var reason = skippedScripts.Count > 0
            ? CliExecutableNotFoundReason.NativeExecutableMissing
            : CliExecutableNotFoundReason.NotInstalled;
        return new CliExecutableLookupResult(null, reason, skippedScripts.Distinct(comparer).ToArray());
    }

    /// <summary>
    /// Determines whether an executable is available.
    /// </summary>
    /// <param name="executableName">The executable name or path.</param>
    /// <param name="environmentVariables">Optional environment variables to search.</param>
    /// <returns><see langword="true" /> when the executable can be resolved; otherwise <see langword="false" />.</returns>
    public virtual bool IsExecutableAvailable(
        string? executableName,
        IReadOnlyDictionary<string, string?>? environmentVariables = null)
    {
        return ResolveExecutablePath(executableName, environmentVariables) is not null;
    }

    /// <summary>
    /// Resolves an executable name inside the supplied directories only, in order.
    /// Unlike <see cref="ResolveExecutablePaths" />, this does not consult the process directory, the working directory,
    /// <c>HAGICODE_AGENT_CLI_PATH</c>, or <c>PATH</c>. Windows extensions follow the same rules.
    /// </summary>
    /// <param name="executableName">The bare executable name.</param>
    /// <param name="directories">The directories to probe, in order. Missing directories are skipped.</param>
    /// <param name="environmentVariables">Optional environment supplying <c>PATHEXT</c> on Windows.</param>
    /// <returns>The distinct resolved absolute paths in probe order.</returns>
    internal IReadOnlyList<string> ResolveExecutablePathsInDirectories(
        string executableName,
        IEnumerable<string> directories,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        ArgumentNullException.ThrowIfNull(directories);
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return [];
        }

        var resolvedPaths = new List<string>();
        var comparer = _isWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);

        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                continue;
            }

            foreach (var candidate in EnumerateCandidates(Path.Combine(directory, executableName), environmentVariables))
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                var fullPath = Path.GetFullPath(candidate);
                if (seen.Add(fullPath))
                {
                    resolvedPaths.Add(fullPath);
                }
            }
        }

        return resolvedPaths;
    }

    /// <summary>
    /// Finds the packaged native binary that an npm shim's package layout places beside a shim directory.
    /// Only the layouts for the process architecture are tried. The nested layout is tried before the hoisted one, and the main package file last.
    /// </summary>
    /// <param name="layout">The packaged binary layout of the agent.</param>
    /// <param name="shimDirectory">The directory that holds the npm shim, or the npm global prefix.</param>
    /// <returns>The absolute path of the packaged binary, or <see langword="null" /> when none exists.</returns>
    internal string? FindPackagedBinary(CliPackagedBinaryLayout layout, string shimDirectory)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (string.IsNullOrWhiteSpace(shimDirectory))
        {
            return null;
        }

        var architecture = _processArchitecture();
        foreach (var target in layout.Targets.Where(target => target.Architecture == architecture))
        {
            var nested = CombineSegments(shimDirectory, "node_modules", layout.MainPackage, "node_modules", target.PlatformPackage, target.FileInPackage);
            if (File.Exists(nested))
            {
                return Path.GetFullPath(nested);
            }

            var hoisted = CombineSegments(shimDirectory, "node_modules", target.PlatformPackage, target.FileInPackage);
            if (File.Exists(hoisted))
            {
                return Path.GetFullPath(hoisted);
            }
        }

        if (layout.MainPackageFile is { } mainPackageFile)
        {
            var mainFile = CombineSegments(shimDirectory, "node_modules", layout.MainPackage, mainPackageFile);
            if (File.Exists(mainFile))
            {
                return Path.GetFullPath(mainFile);
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the npm global prefix directories of a descriptor on the current platform, expanded from the supplied environment.
    /// </summary>
    /// <param name="descriptor">The install descriptor.</param>
    /// <param name="environmentVariables">The environment that supplies <c>APPDATA</c> and the other template variables.</param>
    /// <returns>The npm prefix directories in strategy order. Empty when the descriptor has no npm route.</returns>
    internal IReadOnlyList<string> GetNpmPrefixDirectories(CliInstallDescriptor descriptor, IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        // Template separators follow the host file system, so the expanded path is usable on the host. The injected
        // Windows flag only changes which lookup rules apply.
        var platform = Path.DirectorySeparatorChar == '\\' ? CliPlatform.Windows : CliPlatform.Linux;
        var environment = environmentVariables ?? new Dictionary<string, string?>();
        return descriptor.InstallStrategies
            .Where(static strategy => strategy.Kind == CliInstallKind.Npm)
            .SelectMany(strategy => strategy.GetWellKnownDirectories(platform, environment))
            .Where(Directory.Exists)
            .Distinct(_isWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Determines whether a provider's Windows lookup follows a registry rule other than plain extension-first resolution.
    /// </summary>
    /// <param name="descriptor">The install descriptor.</param>
    /// <returns><see langword="true" /> when the policy or a packaged-binary layout applies.</returns>
    internal static bool HasWindowsPolicy(CliInstallDescriptor descriptor)
    {
        return descriptor.WindowsExecutablePolicy == CliWindowsExecutablePolicy.RequireNative
               || descriptor.PackagedBinaryLayout is not null;
    }

    /// <summary>
    /// Collects the paths a Windows policy accepts for one name, in precedence order. Tiers are visited in order, and a
    /// later tier only adds paths after an earlier one. The npm global prefix is consulted only when the tiers produced nothing.
    /// For a <c>RequireNative</c> agent, command scripts are never accepted and are reported in <paramref name="skippedScripts" />.
    /// For a <c>PreferNative</c> agent, a tier's command scripts are accepted only when that tier has no native path.
    /// </summary>
    private List<string> CollectWindowsPolicyPaths(
        CliInstallDescriptor descriptor,
        string executableName,
        IReadOnlyDictionary<string, string?>? environmentVariables,
        List<string> skippedScripts)
    {
        var requireNative = descriptor.WindowsExecutablePolicy == CliWindowsExecutablePolicy.RequireNative;
        var accepted = new List<string>();
        foreach (var tier in GetProbeTiers(environmentVariables))
        {
            var directories = tier.Where(static directory => !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)).ToArray();
            var tierNative = new List<string>();

            foreach (var directory in directories)
            {
                var executable = Path.Combine(directory, executableName + ExecutableExtension);
                if (File.Exists(executable))
                {
                    tierNative.Add(Path.GetFullPath(executable));
                }
            }

            var shimDirectories = directories.Where(directory => GetCommandScripts(directory, executableName, environmentVariables).Count > 0).ToArray();
            if (descriptor.PackagedBinaryLayout is { } layout)
            {
                foreach (var shimDirectory in shimDirectories)
                {
                    if (FindPackagedBinary(layout, shimDirectory) is { } packaged)
                    {
                        tierNative.Add(packaged);
                    }
                }
            }

            var scripts = shimDirectories.SelectMany(directory => GetCommandScripts(directory, executableName, environmentVariables)).ToArray();
            accepted.AddRange(tierNative);
            if (requireNative)
            {
                skippedScripts.AddRange(scripts);
            }
            else if (tierNative.Count == 0)
            {
                accepted.AddRange(scripts);
            }
        }

        if (accepted.Count > 0)
        {
            return accepted;
        }

        var npmDirectories = GetNpmPrefixDirectories(descriptor, environmentVariables);
        if (descriptor.PackagedBinaryLayout is { } npmLayout)
        {
            foreach (var npmDirectory in npmDirectories)
            {
                if (FindPackagedBinary(npmLayout, npmDirectory) is { } packaged)
                {
                    accepted.Add(packaged);
                }
            }
        }

        foreach (var npmDirectory in npmDirectories)
        {
            var scripts = GetCommandScripts(npmDirectory, executableName, environmentVariables);
            if (requireNative)
            {
                skippedScripts.AddRange(scripts);
            }
            else if (accepted.Count == 0)
            {
                accepted.AddRange(scripts);
            }
        }

        return accepted;
    }

    private IReadOnlyList<string> GetCommandScripts(
        string directory,
        string executableName,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        if (!_isWindows())
        {
            return [];
        }

        var scripts = new List<string>();
        foreach (var extension in GetWindowsExtensions(environmentVariables))
        {
            if (extension.Equals(ExecutableExtension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var script = Path.Combine(directory, executableName + extension);
            if (File.Exists(script))
            {
                scripts.Add(Path.GetFullPath(script));
            }
        }

        return scripts;
    }

    private bool ShouldResolveDirectly(string executableName)
    {
        if (!_isWindows())
        {
            return Path.IsPathRooted(executableName)
                   || executableName.Contains(Path.DirectorySeparatorChar)
                   || executableName.Contains(Path.AltDirectorySeparatorChar);
        }

        return Path.IsPathFullyQualified(executableName)
               || !string.IsNullOrWhiteSpace(Path.GetExtension(executableName));
    }

    private IEnumerable<string> EnumerateTierCandidates(
        IReadOnlyList<string> tier,
        string executableName,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var directories = tier.Where(static directory => !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)).ToArray();
        if (!_isWindows() || !string.IsNullOrWhiteSpace(Path.GetExtension(executableName)))
        {
            foreach (var directory in directories)
            {
                yield return Path.Combine(directory, executableName);
            }

            yield break;
        }

        // Extension-major inside the tier: every directory is tried for .exe before any directory is tried for .cmd.
        foreach (var extension in GetWindowsExtensions(environmentVariables))
        {
            foreach (var directory in directories)
            {
                yield return Path.Combine(directory, executableName) + extension;
            }
        }
    }

    private static string? ResolveDirectPath(string executableName)
    {
        return File.Exists(executableName) ? Path.GetFullPath(executableName) : null;
    }

    /// <summary>
    /// Groups the probe directories into tiers in precedence order: the process and working directories, then
    /// <c>HAGICODE_AGENT_CLI_PATH</c>, then <c>PATH</c>. A later tier is consulted only when an earlier one has no match.
    /// </summary>
    private IReadOnlyList<IReadOnlyList<string>> GetProbeTiers(IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var processDirectory = System.Environment.ProcessPath is { } processPath
            ? Path.GetDirectoryName(processPath)
            : null;

        var nearTier = new List<string>();
        if (!string.IsNullOrWhiteSpace(processDirectory))
        {
            nearTier.Add(processDirectory);
        }

        nearTier.Add(Directory.GetCurrentDirectory());

        return
        [
            nearTier,
            EnumerateEnvironmentDirectories(environmentVariables, AgentCliPathEnvironmentVariable).ToArray(),
            EnumerateEnvironmentDirectories(environmentVariables, PathEnvironmentVariable).ToArray(),
        ];
    }

    private static IEnumerable<string> EnumerateEnvironmentDirectories(
        IReadOnlyDictionary<string, string?>? environmentVariables,
        string environmentVariableName)
    {
        var pathValue = GetEnvironmentValue(environmentVariables, environmentVariableName);
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            yield break;
        }

        foreach (var path in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return path;
        }
    }

    private IEnumerable<string> EnumerateCandidates(string basePath, IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        if (!_isWindows())
        {
            yield return basePath;
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(Path.GetExtension(basePath)))
        {
            yield return basePath;
            yield break;
        }

        foreach (var extension in GetWindowsExtensions(environmentVariables))
        {
            yield return basePath + extension;
        }
    }

    /// <summary>
    /// Returns the Windows extensions in probe order: <c>.exe</c>, <c>.cmd</c>, <c>.bat</c>, then the remaining <c>PATHEXT</c> entries.
    /// The first three are fixed, so the order does not depend on how <c>PATHEXT</c> lists them.
    /// </summary>
    private IEnumerable<string> GetWindowsExtensions(IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var extensions = new List<string>(NativeWindowsExtensions);

        var pathExtensions = GetEnvironmentValue(environmentVariables, PathExtEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(pathExtensions))
        {
            return extensions;
        }

        foreach (var extension in pathExtensions
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(static extension => (extension.StartsWith('.') ? extension : "." + extension).ToLowerInvariant()))
        {
            if (!extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                extensions.Add(extension);
            }
        }

        return extensions;
    }

    private static string CombineSegments(string root, params string[] segments)
    {
        var path = root;
        foreach (var segment in segments)
        {
            foreach (var part in segment.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            {
                path = Path.Combine(path, part);
            }
        }

        return path;
    }

    private static string? GetEnvironmentValue(IReadOnlyDictionary<string, string?>? environmentVariables, string key)
    {
        if (environmentVariables is not null && environmentVariables.TryGetValue(key, out var value))
        {
            return value;
        }

        return System.Environment.GetEnvironmentVariable(key);
    }
}
