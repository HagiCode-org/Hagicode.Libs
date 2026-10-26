using HagiCode.Libs.Git.Internal;

namespace HagiCode.Libs.Git;

public sealed partial class GitClient
{
    /// <inheritdoc />
    public async Task<GitResult<GitRemoveOutcome>> RemoveRepositoryAsync(
        string repositoryPath,
        GitRemoveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            return GitResult<GitRemoveOutcome>.Fail(new GitFailure(GitFailureCode.InvalidRepository, "The repository path is missing."));
        }

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        var refusal = RefuseUnlessStandaloneRoot(fullPath);
        if (refusal is not null)
        {
            return GitResult<GitRemoveOutcome>.Fail(refusal);
        }

        if (options?.Force != true)
        {
            var dirty = await RefuseUnlessSafeToRemoveAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (dirty is not null)
            {
                return GitResult<GitRemoveOutcome>.Fail(dirty);
            }
        }

        try
        {
            await Task.Run(() => DeleteTree(new DirectoryInfo(fullPath), cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return GitResult<GitRemoveOutcome>.Fail(new GitFailure(
                GitFailureCode.RemoveFailed,
                $"Failed to remove the repository at '{fullPath}'; part of it may already be deleted. {exception.Message}"));
        }

        return GitResult<GitRemoveOutcome>.Ok(new GitRemoveOutcome(fullPath));
    }

    /// <summary>
    /// Accepts only the root of a standalone repository: a real directory, not a link, whose <c>.git</c> is a real directory.
    /// Worktrees and submodules (<c>.git</c> is a file), subdirectories, links and filesystem roots are refused.
    /// </summary>
    private static GitFailure? RefuseUnlessStandaloneRoot(string fullPath)
    {
        if (string.Equals(fullPath, Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase))
        {
            return new GitFailure(GitFailureCode.InvalidRepository, $"Refusing to remove a filesystem root: {fullPath}");
        }

        var directory = new DirectoryInfo(fullPath);
        if (!directory.Exists)
        {
            return new GitFailure(GitFailureCode.InvalidRepository, $"Repository directory not found: {fullPath}");
        }

        if (IsLink(directory))
        {
            return new GitFailure(GitFailureCode.InvalidRepository, $"Refusing to remove a link; remove its target instead: {fullPath}");
        }

        var dotGit = new DirectoryInfo(Path.Combine(fullPath, ".git"));
        if (!dotGit.Exists || IsLink(dotGit) || !GitRepositoryLayout.IsRepositoryRoot(fullPath))
        {
            return new GitFailure(
                GitFailureCode.InvalidRepository,
                $"Refusing to remove a path that is not the root of a standalone repository (its .git must be a directory): {fullPath}");
        }

        return null;
    }

    /// <summary>
    /// Returns the reason the checkout must not be removed without force, or <see langword="null" /> when it is clean and every commit
    /// is on its upstream. An inconclusive status is refused, because it cannot prove the tree is clean.
    /// </summary>
    private async Task<GitFailure?> RefuseUnlessSafeToRemoveAsync(string fullPath, CancellationToken cancellationToken)
    {
        var probe = await ProbeDirtyAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (!probe.Success)
        {
            return probe.Failure;
        }

        var state = probe.Value;
        if (!state.IsConclusive)
        {
            return new GitFailure(
                GitFailureCode.CommandFailed,
                $"Refusing to remove '{fullPath}' because its state could not be determined. {state.Message}");
        }

        if (state.HasChanges)
        {
            return new GitFailure(
                GitFailureCode.WorkingTreeDirty,
                $"Refusing to remove '{fullPath}' with uncommitted changes. Use force to remove it anyway.");
        }

        // No tracking data means no upstream or a detached HEAD: the commits cannot be proven to be pushed.
        if (state.Tracking is null)
        {
            return new GitFailure(
                GitFailureCode.UnpushedCommits,
                $"Refusing to remove '{fullPath}' because its HEAD has no upstream branch or is detached. Use force to remove it anyway.");
        }

        if (state.Tracking.Ahead > 0)
        {
            return new GitFailure(
                GitFailureCode.UnpushedCommits,
                $"Refusing to remove '{fullPath}' because {state.Tracking.Ahead} commit(s) are not on {state.Tracking.Upstream}. Use force to remove it anyway.");
        }

        return null;
    }

    /// <summary>
    /// Deletes a tree depth first. A link is removed as a link and never entered, so its target survives. Cancellation is checked before
    /// each entry, so a cancelled call leaves the remaining entries in place.
    /// </summary>
    private static void DeleteTree(DirectoryInfo directory, CancellationToken cancellationToken)
    {
        FileSystemInfo[] entries;
        try
        {
            entries = directory.GetFileSystemInfos();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Could not list '{directory.FullName}': {exception.Message}", exception);
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry is DirectoryInfo child && !IsLink(child))
            {
                DeleteTree(child, cancellationToken);
            }
            else
            {
                DeleteEntry(entry);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        DeleteEntry(directory);
    }

    /// <summary>
    /// Deletes one non-directory entry or an empty directory. Read-only is cleared first, because git object files are read-only on Windows.
    /// Links are never given attributes, since that would change their target.
    /// </summary>
    private static void DeleteEntry(FileSystemInfo entry)
    {
        try
        {
            if (!IsLink(entry) && (entry.Attributes & FileAttributes.ReadOnly) != 0)
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }

            entry.Delete();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Could not delete '{entry.FullName}': {exception.Message}", exception);
        }
    }

    /// <summary>True for a symbolic link or a Windows reparse point (junction), which must be removed without being entered.</summary>
    private static bool IsLink(FileSystemInfo entry) =>
        entry.LinkTarget is not null || (entry.Attributes & FileAttributes.ReparsePoint) != 0;
}
