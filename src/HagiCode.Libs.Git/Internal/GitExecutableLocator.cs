using HagiCode.Libs.Core.Discovery;

namespace HagiCode.Libs.Git.Internal;

/// <summary>The outcome of locating the git executable.</summary>
/// <param name="Path">The absolute path, or <see langword="null" /> when git was not found.</param>
/// <param name="Message">Explains the resolution. For a missing git it names the searched locations.</param>
internal sealed record GitExecutableResolution(string? Path, string Message);

/// <summary>
/// Locates <c>git</c> with the executable resolver from <c>HagiCode.Libs.Core</c>, which already knows how to probe
/// <c>PATH</c> on Windows (including <c>PATHEXT</c>), Linux and macOS. An explicit path from the options takes priority.
/// </summary>
internal sealed class GitExecutableLocator
{
    private const string GitExecutableName = "git";

    private readonly CliExecutableResolver _resolver;

    public GitExecutableLocator(CliExecutableResolver? resolver = null)
    {
        _resolver = resolver ?? new CliExecutableResolver();
    }

    /// <summary>Resolves the git executable for the given configuration.</summary>
    /// <param name="explicitPath">A configured path or command name, or <see langword="null" /> to search <c>PATH</c>.</param>
    /// <param name="environmentVariables">The environment that supplies <c>PATH</c>; <see langword="null" /> uses the process environment.</param>
    public GitExecutableResolution Resolve(string? explicitPath, IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var configured = _resolver.ResolveExecutablePath(explicitPath, environmentVariables);
            return configured is null
                ? new GitExecutableResolution(null, $"The configured git executable '{explicitPath}' was not found.")
                : new GitExecutableResolution(configured, $"Using configured git executable '{configured}'.");
        }

        var resolved = _resolver.ResolveExecutablePath(GitExecutableName, environmentVariables);
        if (resolved is not null)
        {
            return new GitExecutableResolution(resolved, $"Resolved git to '{resolved}'.");
        }

        return new GitExecutableResolution(
            null,
            $"No '{GitExecutableName}' executable was found. Searched PATH entries: {DescribeSearchedDirectories(environmentVariables)}.");
    }

    private static string DescribeSearchedDirectories(IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var path = environmentVariables is not null && environmentVariables.TryGetValue("PATH", out var overridden)
            ? overridden
            : Environment.GetEnvironmentVariable("PATH");

        var directories = (path ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return directories.Length == 0 ? "(none)" : string.Join(", ", directories);
    }
}
