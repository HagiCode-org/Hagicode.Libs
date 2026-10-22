using HagiCode.Libs.Git.Internal;

namespace HagiCode.Libs.Git;

public sealed partial class GitClient
{
    /// <inheritdoc />
    public async Task<GitResult<GitDirtyProbe>> ProbeDirtyAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        if (!GitRepositoryLayout.IsRepositoryRoot(repositoryPath))
        {
            return GitResult<GitDirtyProbe>.Fail(InvalidRepository(repositoryPath));
        }

        var branch = GitRepositoryLayout.ReadHeadBranch(repositoryPath);

        // One status process answers the boolean, the upstream and the ahead/behind counts. Ignored paths are not listed,
        // and an untracked directory is listed once, so the output stays small in a repository with a large untracked tree.
        var outcome = await RunAsync(
            repositoryPath,
            ["--no-optional-locks", "status", "--porcelain=v2", "-z", "--branch", "--untracked-files=normal"],
            CommandKind.Read,
            cancellationToken).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            return GitResult<GitDirtyProbe>.Ok(new GitDirtyProbe(
                HasChanges: false,
                Branch: branch,
                IsConclusive: false,
                Message: "git status did not complete, so the working tree state is unknown."));
        }

        var parsed = GitPorcelainV2Parser.Parse(outcome.StandardOutput);
        return GitResult<GitDirtyProbe>.Ok(new GitDirtyProbe(
            HasChanges: parsed.Entries.Count > 0,
            Branch: branch,
            Tracking: ToTracking(parsed)));
    }

    /// <inheritdoc />
    public async Task<GitResult<GitStatus>> GetStatusAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        if (!GitRepositoryLayout.IsRepositoryRoot(repositoryPath))
        {
            return GitResult<GitStatus>.Fail(InvalidRepository(repositoryPath));
        }

        var outcome = await RunAsync(
            repositoryPath,
            ["--no-optional-locks", "status", "--porcelain=2", "-z", "--branch", "--untracked-files=all"],
            CommandKind.Read,
            cancellationToken).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            return GitResult<GitStatus>.Fail(ToFailure(outcome, GitFailureCode.CommandFailed, "git status failed."));
        }

        var parsed = GitPorcelainV2Parser.Parse(outcome.StandardOutput);
        var branch = GitRepositoryLayout.ReadHeadBranch(repositoryPath) ?? parsed.Branch ?? "HEAD";
        return GitResult<GitStatus>.Ok(new GitStatus(branch, parsed.Entries));
    }

    /// <summary>Tracking is reported only when git printed the upstream and both counts, the same cases <see cref="GetTrackingInfoAsync" /> answers with a value.</summary>
    private static GitTrackingInfo? ToTracking(PorcelainStatus status) =>
        status.Upstream is not null && status.Ahead is { } ahead && status.Behind is { } behind
            ? new GitTrackingInfo(status.Upstream, ahead, behind)
            : null;
}
