namespace HagiCode.Libs.Core.Discovery;

/// <summary>
/// Applies the Kiro path rule: only executables whose file name starts with <c>kiro-cli</c> are accepted.
/// </summary>
public static class KiroExecutablePathPolicy
{
    private const string ExecutableNamePrefix = "kiro-cli";

    /// <summary>
    /// Determines whether a resolved path is an accepted Kiro executable.
    /// </summary>
    /// <param name="resolvedPath">The resolved executable path.</param>
    /// <returns><see langword="true" /> when the file name starts with <c>kiro-cli</c>; otherwise <see langword="false" />.</returns>
    public static bool IsAcceptedPath(string? resolvedPath)
    {
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            return false;
        }

        // Split on both separators so a Windows-style path is judged the same way on any host.
        var separatorIndex = resolvedPath.LastIndexOfAny(['/', '\\']);
        var fileName = separatorIndex >= 0 ? resolvedPath[(separatorIndex + 1)..] : resolvedPath;
        return fileName.StartsWith(ExecutableNamePrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Keeps only the accepted Kiro executables, preserving order.
    /// </summary>
    /// <param name="resolvedPaths">The resolved executable paths.</param>
    /// <returns>The accepted paths in their original order. Unrelated executables are dropped.</returns>
    public static IReadOnlyList<string> SelectAcceptedPaths(IEnumerable<string> resolvedPaths)
    {
        ArgumentNullException.ThrowIfNull(resolvedPaths);
        return resolvedPaths.Where(static path => IsAcceptedPath(path)).ToArray();
    }
}
