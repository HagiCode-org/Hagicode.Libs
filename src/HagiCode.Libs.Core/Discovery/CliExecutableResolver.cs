using System.Collections;

namespace HagiCode.Libs.Core.Discovery;

/// <summary>
/// Resolves CLI executable paths from PATH-like environments.
/// </summary>
public class CliExecutableResolver
{
    private const string AgentCliPathEnvironmentVariable = "HAGICODE_AGENT_CLI_PATH";
    private static readonly string[] DefaultWindowsExtensions = [".exe", ".cmd", ".bat"];
    private readonly Func<bool> _isWindows;

    /// <summary>
    /// Initializes a new instance of the <see cref="CliExecutableResolver" /> class.
    /// </summary>
    public CliExecutableResolver()
        : this(static () => OperatingSystem.IsWindows())
    {
    }

    internal CliExecutableResolver(Func<bool> isWindows)
    {
        _isWindows = isWindows ?? throw new ArgumentNullException(nameof(isWindows));
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

        foreach (var candidate in EnumerateProbeCandidates(executableName, environmentVariables))
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

    private IEnumerable<string> EnumerateProbeCandidates(
        string executableName,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        foreach (var directory in GetProbeDirectories(environmentVariables))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var basePath = Path.Combine(directory, executableName);
            foreach (var candidate in EnumerateCandidates(basePath, environmentVariables))
            {
                yield return candidate;
            }
        }
    }

    private static string? ResolveDirectPath(string executableName)
    {
        return File.Exists(executableName) ? Path.GetFullPath(executableName) : null;
    }

    private IEnumerable<string> GetProbeDirectories(IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var processDirectory = System.Environment.ProcessPath is { } processPath
            ? Path.GetDirectoryName(processPath)
            : null;

        if (!string.IsNullOrWhiteSpace(processDirectory))
        {
            yield return processDirectory;
        }

        yield return Directory.GetCurrentDirectory();

        foreach (var path in EnumerateEnvironmentDirectories(environmentVariables, AgentCliPathEnvironmentVariable))
        {
            yield return path;
        }

        foreach (var path in EnumerateEnvironmentDirectories(environmentVariables, "PATH"))
        {
            yield return path;
        }
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

    private IEnumerable<string> GetWindowsExtensions(IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var pathExtensions = GetEnvironmentValue(environmentVariables, "PATHEXT");
        if (string.IsNullOrWhiteSpace(pathExtensions))
        {
            return DefaultWindowsExtensions;
        }

        return pathExtensions
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static extension => (extension.StartsWith('.') ? extension : "." + extension).ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
