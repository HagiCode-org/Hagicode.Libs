using HagiCode.Libs.Git.Internal;

namespace HagiCode.Libs.Git;

public sealed partial class GitClient
{
    /// <inheritdoc />
    public Task<GitResult<GitSyncOutcome>> PushAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        return SyncAsync(
            repositoryPath,
            ["push"],
            "Git push failed.",
            GitFailureClassifier.ClassifyPush,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<GitResult<GitSyncOutcome>> PullAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        return SyncAsync(
            repositoryPath,
            ["pull", "--ff-only"],
            "Git pull failed.",
            GitFailureClassifier.ClassifyPull,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<GitRebaseOutcome> RebaseAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        if (!GitRepositoryLayout.IsRepositoryRoot(repositoryPath))
        {
            return RebaseFailure(
                currentBranch: "HEAD",
                new GitFailure(GitFailureCode.InvalidRepository, $"Git repository not found or invalid: {repositoryPath}"));
        }

        var currentBranch = GitRepositoryLayout.ReadHeadBranch(repositoryPath) ?? "HEAD";

        var status = await GetStatusAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (!status.Success)
        {
            return RebaseFailure(currentBranch, status.Failure);
        }

        if (status.Value!.HasChanges)
        {
            return RebaseFailure(
                currentBranch,
                new GitFailure(GitFailureCode.WorkingTreeDirty, "Working tree is dirty. Please commit or stage your changes before rebasing."),
                workingTreeClean: false);
        }

        // The rebase follows the configured upstream so the transport, credentials and target match `git pull --rebase`.
        var upstream = await ReadUpstreamAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (upstream is null)
        {
            return RebaseFailure(
                currentBranch,
                new GitFailure(GitFailureCode.NoUpstream, "Current branch has no configured upstream tracking branch."),
                workingTreeClean: true);
        }

        var pull = await RunAsync(repositoryPath, ["pull", "--rebase"], CommandKind.Remote, cancellationToken).ConfigureAwait(false);
        if (pull.Succeeded)
        {
            return new GitRebaseOutcome
            {
                CurrentBranch = currentBranch,
                TargetBranch = upstream,
                RemoteBranchAvailable = true,
                WorkingTreeClean = true,
                RebaseAborted = false
            };
        }

        var details = JoinDetails(pull);
        if (!GitFailureClassifier.LooksLikeRebaseConflict(details))
        {
            return RebaseFailure(
                currentBranch,
                ToFailure(pull, GitFailureCode.RebaseFailed, "Git rebase failed."),
                upstream,
                remoteBranchAvailable: true,
                workingTreeClean: true);
        }

        var abort = await RunAsync(repositoryPath, ["rebase", "--abort"], CommandKind.Write, cancellationToken).ConfigureAwait(false);
        if (abort.Succeeded)
        {
            return RebaseFailure(
                currentBranch,
                new GitFailure(
                    GitFailureCode.RebaseConflictAborted,
                    "Rebase hit conflicts and was automatically aborted. Repository state has been restored.",
                    pull.ExitCode,
                    pull.StandardOutput + abort.StandardOutput,
                    pull.StandardError + abort.StandardError),
                upstream,
                remoteBranchAvailable: true,
                workingTreeClean: true,
                rebaseAborted: true);
        }

        return RebaseFailure(
            currentBranch,
            ToFailure(abort, GitFailureCode.RebaseFailed, "Rebase hit conflicts and abort did not complete successfully."),
            upstream,
            remoteBranchAvailable: true,
            workingTreeClean: true);
    }

    /// <inheritdoc />
    public async Task<GitResult<GitTrackingInfo?>> GetTrackingInfoAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        if (!GitRepositoryLayout.IsRepositoryRoot(repositoryPath))
        {
            return GitResult<GitTrackingInfo?>.Fail(InvalidRepository(repositoryPath));
        }

        var upstream = await ReadUpstreamAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (upstream is null)
        {
            return GitResult<GitTrackingInfo?>.Ok(null);
        }

        // Left side of the symmetric difference is HEAD (ahead), right side is the upstream (behind).
        var counts = await RunAsync(
            repositoryPath,
            ["rev-list", "--left-right", "--count", "HEAD...@{u}"],
            CommandKind.Read,
            cancellationToken).ConfigureAwait(false);

        if (!counts.Succeeded)
        {
            return GitResult<GitTrackingInfo?>.Fail(ToFailure(counts, GitFailureCode.CommandFailed, "Could not count commits against the upstream."));
        }

        var parts = counts.StandardOutput.Split(['\t', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ahead)
            || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var behind))
        {
            return GitResult<GitTrackingInfo?>.Fail(new GitFailure(
                GitFailureCode.CommandFailed,
                "git rev-list returned an unexpected count format.",
                counts.ExitCode,
                counts.StandardOutput,
                counts.StandardError));
        }

        return GitResult<GitTrackingInfo?>.Ok(new GitTrackingInfo(upstream, ahead, behind));
    }

    private async Task<GitResult<GitSyncOutcome>> SyncAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        string message,
        Func<string, GitFailureCode> classify,
        CancellationToken cancellationToken)
    {
        if (!GitRepositoryLayout.IsRepositoryRoot(repositoryPath))
        {
            return GitResult<GitSyncOutcome>.Fail(InvalidRepository(repositoryPath));
        }

        var branch = GitRepositoryLayout.ReadHeadBranch(repositoryPath) ?? "HEAD";

        // Without an upstream there is nothing to push to or pull from, so no network access is attempted.
        var upstream = await ReadUpstreamAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (upstream is null)
        {
            return GitResult<GitSyncOutcome>.Fail(new GitFailure(
                GitFailureCode.NoUpstream,
                "Current branch has no upstream tracking branch."));
        }

        var outcome = await RunAsync(repositoryPath, arguments, CommandKind.Remote, cancellationToken).ConfigureAwait(false);
        if (outcome.Succeeded)
        {
            return GitResult<GitSyncOutcome>.Ok(new GitSyncOutcome(branch, upstream));
        }

        if (outcome.TimedOut || outcome.Unavailable || outcome.LaunchError is not null)
        {
            return GitResult<GitSyncOutcome>.Fail(ToFailure(outcome, GitFailureCode.CommandFailed, message));
        }

        var code = classify(JoinDetails(outcome));
        return GitResult<GitSyncOutcome>.Fail(new GitFailure(
            code,
            DescribeSyncFailure(code, message),
            outcome.ExitCode,
            outcome.StandardOutput,
            outcome.StandardError));
    }

    private static string DescribeSyncFailure(GitFailureCode code, string message) => code switch
    {
        GitFailureCode.NoUpstream => "Current branch has no upstream tracking branch.",
        GitFailureCode.AuthenticationFailed => "Remote requires credentials that are not available without a prompt.",
        GitFailureCode.NonFastForward => "Remote rejected the update because it is not a fast-forward.",
        GitFailureCode.RemoteRejected => "Remote rejected the request.",
        GitFailureCode.FastForwardRequired => "Pull requires a merge or rebase because fast-forward only pull could not be completed.",
        _ => message
    };

    /// <summary>Returns the upstream in short form (for example <c>origin/main</c>), or <see langword="null" /> when HEAD is detached or has none.</summary>
    private async Task<string?> ReadUpstreamAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var outcome = await RunAsync(
            repositoryPath,
            ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"],
            CommandKind.Read,
            cancellationToken).ConfigureAwait(false);

        var value = outcome.StandardOutput.Trim();
        return outcome.Succeeded && value.Length > 0 ? value : null;
    }

    private static string JoinDetails(GitProcessOutcome outcome)
    {
        var parts = new[] { outcome.StandardError.Trim(), outcome.StandardOutput.Trim() }
            .Where(part => part.Length > 0);
        return string.Join(Environment.NewLine, parts);
    }

    private static GitRebaseOutcome RebaseFailure(
        string currentBranch,
        GitFailure failure,
        string? targetBranch = null,
        bool remoteBranchAvailable = false,
        bool workingTreeClean = false,
        bool rebaseAborted = false)
    {
        return new GitRebaseOutcome
        {
            CurrentBranch = currentBranch,
            TargetBranch = targetBranch,
            RemoteBranchAvailable = remoteBranchAvailable,
            WorkingTreeClean = workingTreeClean,
            RebaseAborted = rebaseAborted,
            Failure = failure
        };
    }
}
