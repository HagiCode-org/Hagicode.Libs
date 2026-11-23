namespace HagiCode.Libs.Core.Process.Platform;

/// <summary>
/// Decides whether a root process's cgroup may be expanded into containment members.
/// </summary>
/// <remarks>
/// Only a transient <c>run-*.scope</c> unit that was not created for the snapshotter itself is expanded. A service cgroup, a session scope and the root cgroup are never expanded,
/// so a host that runs inside a shared cgroup does not list its neighbours.
/// </remarks>
internal static class LinuxCgroupMembership
{
    /// <summary>
    /// Returns the unified (cgroup v2) path from the contents of <c>/proc/&lt;pid&gt;/cgroup</c>.
    /// </summary>
    /// <param name="cgroupText">The file contents.</param>
    /// <returns>The path such as <c>/user.slice/app.slice/run-u12.scope</c>, or <see langword="null" /> when no unified line exists.</returns>
    public static string? ParseUnifiedPath(string cgroupText)
    {
        foreach (var line in cgroupText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("0::", StringComparison.Ordinal))
            {
                return line["0::".Length..].Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Returns a value indicating whether a root's cgroup may be expanded.
    /// </summary>
    /// <param name="rootCgroupPath">The root's unified cgroup path.</param>
    /// <param name="ownCgroupPath">The snapshotter's own unified cgroup path, or <see langword="null" /> when unknown.</param>
    public static bool MayExpand(string? rootCgroupPath, string? ownCgroupPath)
    {
        if (string.IsNullOrWhiteSpace(rootCgroupPath))
        {
            return false;
        }

        var trimmed = rootCgroupPath.TrimEnd('/');
        if (string.Equals(trimmed, ownCgroupPath?.TrimEnd('/'), StringComparison.Ordinal))
        {
            return false;
        }

        var leaf = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        return leaf.StartsWith("run-", StringComparison.Ordinal)
               && leaf.EndsWith(".scope", StringComparison.Ordinal);
    }
}
