namespace HagiCode.Libs.Core.Discovery;

/// <summary>
/// Applies path-selection rules for GitHub Copilot CLI discovery.
/// </summary>
public static class CopilotExecutablePathPolicy
{
    private const string VsCodeCopilotCliSegment = "/user/globalstorage/github.copilot-chat/copilotcli/";

    /// <summary>
    /// Selects the preferred Copilot executable path from resolved candidates.
    /// </summary>
    /// <param name="resolvedPaths">The resolved executable paths.</param>
    /// <returns>The preferred path, prioritizing real CLI binaries over VS Code shims.</returns>
    public static string? SelectPreferredPath(IEnumerable<string> resolvedPaths)
    {
        ArgumentNullException.ThrowIfNull(resolvedPaths);

        string? vscodeShimFallback = null;
        foreach (var resolvedPath in resolvedPaths)
        {
            if (string.IsNullOrWhiteSpace(resolvedPath))
            {
                continue;
            }

            if (!IsVsCodeShimPath(resolvedPath))
            {
                return resolvedPath;
            }

            vscodeShimFallback ??= resolvedPath;
        }

        return vscodeShimFallback;
    }

    /// <summary>
    /// Determines whether the resolved path points at the VS Code Copilot Chat shim.
    /// </summary>
    /// <param name="resolvedPath">The resolved executable path.</param>
    /// <returns><see langword="true" /> when the path points at a VS Code shim; otherwise <see langword="false" />.</returns>
    public static bool IsVsCodeShimPath(string? resolvedPath)
    {
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            return false;
        }

        var normalizedPath = resolvedPath.Replace('\\', '/');
        return normalizedPath.Contains(VsCodeCopilotCliSegment, StringComparison.OrdinalIgnoreCase);
    }
}
