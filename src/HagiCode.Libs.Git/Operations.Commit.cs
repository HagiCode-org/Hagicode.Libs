namespace HagiCode.Libs.Git;

public sealed partial class GitClient
{
    /// <inheritdoc />
    public async Task<GitResult<GitCommitOutcome>> CommitAsync(string repositoryPath, string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return GitResult<GitCommitOutcome>.Fail(new GitFailure(GitFailureCode.InvalidArgument, "The commit message is null or empty."));
        }

        if (!Internal.GitRepositoryLayout.IsRepositoryRoot(repositoryPath))
        {
            return GitResult<GitCommitOutcome>.Fail(InvalidRepository(repositoryPath));
        }

        // Nothing staged is checked before identity, matching the order the backend has always reported errors in.
        var diff = await RunAsync(repositoryPath, ["diff", "--cached", "--quiet"], CommandKind.Read, cancellationToken).ConfigureAwait(false);
        if (diff.Succeeded)
        {
            return GitResult<GitCommitOutcome>.Fail(new GitFailure(GitFailureCode.NothingStaged, "No staged changes to commit."));
        }

        if (diff.ExitCode != 1 || diff.TimedOut || diff.Unavailable || diff.LaunchError is not null)
        {
            return GitResult<GitCommitOutcome>.Fail(ToFailure(diff, GitFailureCode.CommandFailed, "Could not read the staged changes."));
        }

        // Identity is read-only and must be complete, because git would otherwise guess an email address from the host name.
        var identity = await ReadIdentityAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (!identity.Success)
        {
            return GitResult<GitCommitOutcome>.Fail(identity.Failure);
        }

        if (!identity.Value!.IsComplete)
        {
            return GitResult<GitCommitOutcome>.Fail(new GitFailure(
                GitFailureCode.IdentityNotConfigured,
                "Git identity is not configured. Set user.name and user.email before committing."));
        }

        // --cleanup=verbatim keeps the message byte-for-byte (no comment stripping), and --no-verify keeps hooks out of the path,
        // as the LibGit2Sharp commit never ran them.
        var commit = await RunAsync(
            repositoryPath,
            ["commit", "--quiet", "--no-verify", "--cleanup=verbatim", "-F", "-"],
            CommandKind.Write,
            cancellationToken,
            standardInput: message).ConfigureAwait(false);

        if (!commit.Succeeded)
        {
            return GitResult<GitCommitOutcome>.Fail(ToFailure(commit, GitFailureCode.CommandFailed, "git commit failed."));
        }

        var head = await RunAsync(repositoryPath, ["rev-parse", "HEAD"], CommandKind.Read, cancellationToken).ConfigureAwait(false);
        if (!head.Succeeded)
        {
            return GitResult<GitCommitOutcome>.Fail(ToFailure(head, GitFailureCode.CommandFailed, "The new commit could not be read."));
        }

        return GitResult<GitCommitOutcome>.Ok(new GitCommitOutcome(head.StandardOutput.Trim(), message));
    }
}
