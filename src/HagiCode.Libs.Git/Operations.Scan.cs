namespace HagiCode.Libs.Git;

public sealed partial class GitClient
{
    /// <inheritdoc />
    public Task<GitResult<IReadOnlyList<string>>> ScanForRepositoriesAsync(
        string rootPath,
        GitScanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return Task.FromResult(GitResult<IReadOnlyList<string>>.Fail(
                new GitFailure(GitFailureCode.InvalidArgument, "The root path is null or empty.")));
        }

        var effective = options ?? new GitScanOptions();
        return Task.Run(() =>
        {
            if (!Directory.Exists(rootPath))
            {
                return GitResult<IReadOnlyList<string>>.Fail(
                    new GitFailure(GitFailureCode.InvalidArgument, $"The root path does not exist: {rootPath}"));
            }

            var repositories = new List<string>();
            var excluded = (effective.ExcludePatterns ?? []).ToArray();
            ScanDirectory(rootPath, depth: 0, effective, excluded, repositories, cancellationToken);
            return GitResult<IReadOnlyList<string>>.Ok(repositories);
        }, cancellationToken);
    }

    /// <summary>
    /// Depth-first traversal with the backend's limits. A folder that holds a <c>.git</c> directory is recorded and not entered.
    /// Folder limits end the current level, and unreadable folders are skipped.
    /// </summary>
    private static void ScanDirectory(
        string currentPath,
        int depth,
        GitScanOptions options,
        string[] excluded,
        List<string> repositories,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (Directory.Exists(Path.Combine(currentPath, ".git")))
            {
                repositories.Add(currentPath);
                return;
            }

            if (depth >= options.MaxDepth)
            {
                return;
            }

            var scanned = 0;
            foreach (var subdirectory in Directory.GetDirectories(currentPath))
            {
                if (scanned >= options.MaxFoldersPerLevel)
                {
                    return;
                }

                var name = Path.GetFileName(subdirectory);
                if (options.ExcludeHidden && name.StartsWith('.'))
                {
                    continue;
                }

                if (excluded.Any(pattern => string.Equals(name, pattern, StringComparison.Ordinal)))
                {
                    continue;
                }

                scanned++;
                ScanDirectory(subdirectory, depth + 1, options, excluded, repositories, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            // An unreadable folder is skipped, as the backend scanner has always done.
        }
    }
}
