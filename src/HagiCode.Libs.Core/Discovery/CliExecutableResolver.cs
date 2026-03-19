using System.Collections;

namespace HagiCode.Libs.Core.Discovery;

/// <summary>
/// Resolves CLI executable paths from PATH-like environments.
/// </summary>
public class CliExecutableResolver
{
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
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return null;
        }

        if (Path.IsPathRooted(executableName) || executableName.Contains(Path.DirectorySeparatorChar) || executableName.Contains(Path.AltDirectorySeparatorChar))
        {
            return ResolveDirectPath(executableName);
        }

        foreach (var directory in GetProbeDirectories(environmentVariables))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var basePath = Path.Combine(directory, executableName);
            foreach (var candidate in EnumerateCandidates(basePath, environmentVariables))
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
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

        var pathValue = GetEnvironmentValue(environmentVariables, "PATH");
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
