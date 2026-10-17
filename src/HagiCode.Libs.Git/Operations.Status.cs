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

        // Cheapest first; the first positive answer decides, because the three checks are ORed together.
        //   1) staged: the index differs from HEAD
        //   2) unstaged: the working tree differs from the index
        //   3) untracked: a non-ignored untracked file exists (the most expensive, so last)
        var staged = await QuietDiffAsync(repositoryPath, ["diff-index", "--quiet", "HEAD"], cancellationToken).ConfigureAwait(false);
        var isDirty = staged.HasChanges == true;

        bool? unstaged = null;
        if (!isDirty)
        {
            unstaged = (await QuietDiffAsync(repositoryPath, ["diff-files", "--quiet"], cancellationToken).ConfigureAwait(false)).HasChanges;
            isDirty = unstaged == true;
        }

        bool? untracked = null;
        if (!isDirty)
        {
            untracked = await HasUntrackedFilesAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
            isDirty = untracked == true;
        }

        // Exit 128 from diff-index means HEAD does not resolve yet (an unborn branch). Only the porcelain output can
        // then say whether anything is staged, so it is read once. Other failures are not retried with a heavier command.
        if (!isDirty && staged.Fatal)
        {
            var porcelain = await ReadPorcelainAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
            if (porcelain is null)
            {
                return GitResult<GitDirtyProbe>.Ok(new GitDirtyProbe(
                    HasChanges: false,
                    Branch: branch,
                    IsConclusive: false,
                    Message: "The porcelain fallback for an unborn HEAD failed."));
            }

            return GitResult<GitDirtyProbe>.Ok(new GitDirtyProbe(
                HasChanges: porcelain.Entries.Count > 0,
                Branch: porcelain.Branch ?? branch));
        }

        if (!isDirty && (staged.HasChanges is null || unstaged is null || untracked is null))
        {
            return GitResult<GitDirtyProbe>.Ok(new GitDirtyProbe(
                HasChanges: false,
                Branch: branch,
                IsConclusive: false,
                Message: "A dirty probe command failed without a positive result."));
        }

        return GitResult<GitDirtyProbe>.Ok(new GitDirtyProbe(isDirty, branch));
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

    /// <summary>
    /// Runs a quiet diff. Exit 0 means no difference, 1 means a difference, 128 means HEAD is unresolved (reported as
    /// <see cref="QuietDiffOutcome.Fatal" />), and anything else is inconclusive.
    /// </summary>
    private async Task<QuietDiffOutcome> QuietDiffAsync(string repositoryPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var outcome = await RunAsync(repositoryPath, arguments, CommandKind.Read, cancellationToken).ConfigureAwait(false);
        if (outcome.Succeeded)
        {
            return new QuietDiffOutcome(false, Fatal: false);
        }

        if (!outcome.TimedOut && !outcome.Unavailable && outcome.LaunchError is null && outcome.ExitCode == 1)
        {
            return new QuietDiffOutcome(true, Fatal: false);
        }

        return new QuietDiffOutcome(null, Fatal: !outcome.TimedOut && outcome.ExitCode == 128);
    }

    private async Task<bool?> HasUntrackedFilesAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var outcome = await RunAsync(
            repositoryPath,
            ["ls-files", "--others", "--exclude-standard"],
            CommandKind.Read,
            cancellationToken).ConfigureAwait(false);

        return outcome.Succeeded ? !string.IsNullOrWhiteSpace(outcome.StandardOutput) : null;
    }

    private async Task<PorcelainStatus?> ReadPorcelainAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var outcome = await RunAsync(
            repositoryPath,
            ["--no-optional-locks", "status", "--porcelain=2", "-z", "--branch", "--untracked-files=all"],
            CommandKind.Read,
            cancellationToken).ConfigureAwait(false);

        return outcome.Succeeded ? GitPorcelainV2Parser.Parse(outcome.StandardOutput) : null;
    }

    private readonly record struct QuietDiffOutcome(bool? HasChanges, bool Fatal);
}
