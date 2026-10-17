using HagiCode.Libs.Git.Internal;

namespace HagiCode.Libs.Git;

public sealed partial class GitClient
{
    /// <inheritdoc />
    public async Task<GitResult<GitCloneOutcome>> EnsureCloneAsync(string remoteUrl, string targetPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return GitResult<GitCloneOutcome>.Fail(new GitFailure(GitFailureCode.InvalidArgument, "The repository remote URL is missing."));
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return GitResult<GitCloneOutcome>.Fail(new GitFailure(GitFailureCode.InvalidArgument, "The repository target path is missing."));
        }

        var fullPath = Path.GetFullPath(targetPath);

        if (File.Exists(fullPath))
        {
            return GitResult<GitCloneOutcome>.Fail(new GitFailure(
                GitFailureCode.CloneConflict,
                $"The repository target path points to a file: {fullPath}"));
        }

        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return GitResult<GitCloneOutcome>.Fail(new GitFailure(
                GitFailureCode.InvalidArgument,
                $"The repository target path is invalid: {fullPath}"));
        }

        try
        {
            Directory.CreateDirectory(parent);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return GitResult<GitCloneOutcome>.Fail(new GitFailure(
                GitFailureCode.CloneFailed,
                $"Failed to prepare the repository directory '{fullPath}': {exception.Message}"));
        }

        if (Directory.Exists(fullPath))
        {
            // An empty folder that is not a repository is treated as absent, as before.
            if (!GitRepositoryLayout.IsRepositoryRoot(fullPath) && !Directory.EnumerateFileSystemEntries(fullPath).Any())
            {
                Directory.Delete(fullPath, recursive: false);
            }
        }

        if (Directory.Exists(fullPath))
        {
            if (!GitRepositoryLayout.IsRepositoryRoot(fullPath))
            {
                return GitResult<GitCloneOutcome>.Fail(new GitFailure(
                    GitFailureCode.CloneConflict,
                    $"The target directory already exists but is not a valid Git repository: {fullPath}"));
            }

            var existing = await GetRemoteUrlAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (!existing.Success)
            {
                return GitResult<GitCloneOutcome>.Fail(existing.Failure);
            }

            if (!GitRemoteUrlComparer.Matches(existing.Value, remoteUrl))
            {
                var found = string.IsNullOrWhiteSpace(existing.Value) ? "(missing origin)" : existing.Value;
                return GitResult<GitCloneOutcome>.Fail(new GitFailure(
                    GitFailureCode.CloneConflict,
                    $"The target directory already contains a different Git repository. Expected '{remoteUrl}', found '{found}'."));
            }

            return GitResult<GitCloneOutcome>.Ok(new GitCloneOutcome(fullPath, ReusedExistingRepository: true));
        }

        var clone = await RunAsync(
            parent,
            ["clone", "--quiet", "--", remoteUrl, fullPath],
            CommandKind.Remote,
            cancellationToken).ConfigureAwait(false);

        if (clone.Succeeded)
        {
            return GitResult<GitCloneOutcome>.Ok(new GitCloneOutcome(fullPath, ReusedExistingRepository: false));
        }

        // Remove only what this call left behind: a folder that is not a repository after a failed clone.
        TryDeleteIncompleteClone(fullPath);

        return GitResult<GitCloneOutcome>.Fail(ToFailure(clone, GitFailureCode.CloneFailed, $"Failed to clone the repository into '{fullPath}'."));
    }

    private static void TryDeleteIncompleteClone(string fullPath)
    {
        try
        {
            if (Directory.Exists(fullPath) && !GitRepositoryLayout.IsRepositoryRoot(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Leaving a partial folder behind is safer than masking the clone failure with a cleanup error.
        }
    }
}
