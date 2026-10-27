namespace HagiCode.Libs.Skills.Discovery;

/// <summary>Resolves a path to its real location by following every symbolic link and junction on the way.</summary>
internal static class SkillPathResolver
{
    private const int MaxLinkHops = 40;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Makes a path absolute without resolving links, so that links are resolved by <see cref="TryResolveRealPath" />.</summary>
    public static string MakeAbsolute(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(Directory.GetCurrentDirectory(), path);

    /// <summary>Returns false when a link loop or too many links prevent a real path.</summary>
    public static bool TryResolveRealPath(string absolutePath, out string realPath) =>
        TryResolve(absolutePath, 0, out realPath);

    /// <summary>True when <paramref name="path" /> is the same as, or lies below, <paramref name="root" />. Both must be real paths.</summary>
    public static bool IsWithin(string path, string root)
    {
        if (string.Equals(path, root, PathComparison))
        {
            return true;
        }

        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    /// <summary>True when the entry itself is a symbolic link or junction (its link target is not null).</summary>
    public static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

    private static bool TryResolve(string absolutePath, int hops, out string realPath)
    {
        realPath = string.Empty;
        var root = Path.GetPathRoot(absolutePath) ?? string.Empty;
        var current = root;
        var segments = absolutePath[root.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                // The current path is already resolved, so its parent is the real parent.
                current = Path.GetDirectoryName(current) ?? current;
                continue;
            }

            var next = Path.Combine(current, segment);
            var target = new FileInfo(next).LinkTarget;
            if (target is null)
            {
                current = next;
                continue;
            }

            if (hops >= MaxLinkHops)
            {
                return false;
            }

            var targetPath = Path.IsPathRooted(target) ? target : Path.Combine(current, target);
            if (!TryResolve(targetPath, hops + 1, out var resolvedTarget))
            {
                return false;
            }

            current = resolvedTarget;
        }

        realPath = current;
        return true;
    }
}
