namespace HagiCode.Libs.Git;

/// <summary>
/// Provider-agnostic git operations for one host process. Every method runs the <c>git</c> CLI (or reads repository
/// metadata directly for the documented fast paths), never throws for an expected git failure, and returns typed results with
/// <see cref="GitFailureCode" /> values. Exceptions are reserved for cancellation (<see cref="OperationCanceledException" />)
/// and for caller misuse that cannot be expressed as a result.
/// </summary>
public interface IGitClient
{
    /// <summary>Returns true when <paramref name="path" /> is the root of a repository (a <c>.git</c> directory, or a <c>.git</c> file for worktrees and submodules).</summary>
    Task<bool> IsRepositoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Walks up from <paramref name="path" /> to the nearest folder that contains a <c>.git</c> directory.</summary>
    Task<GitResult<string>> FindRepositoryRootAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Returns the URL of the <c>origin</c> remote, or a successful <see langword="null" /> value when there is none.</summary>
    Task<GitResult<string?>> GetRemoteUrlAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the effective <c>user.name</c> and <c>user.email</c>, local configuration first and then global, with the scope of each value.
    /// Read-only: the client never writes configuration.
    /// </summary>
    Task<GitResult<GitIdentity>> GetIdentityAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the value of one configuration key in one scope (<see cref="GitConfigScope.Local" /> or <see cref="GitConfigScope.Global" />),
    /// or a successful <see langword="null" /> value when the key is not set in that scope. Read-only.
    /// </summary>
    Task<GitResult<string?>> GetConfigValueAsync(string repositoryPath, string key, GitConfigScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the upstream of the current branch and its ahead and behind counts, or a successful <see langword="null" /> value
    /// when HEAD is detached or the branch has no upstream.
    /// </summary>
    Task<GitResult<GitTrackingInfo?>> GetTrackingInfoAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>Returns the current branch, or <c>HEAD</c> when detached. An unborn branch returns its name.</summary>
    Task<GitResult<string>> GetCurrentBranchAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>Returns whether a local branch, or a remote-tracking branch with that short name, exists.</summary>
    Task<GitResult<bool>> BranchExistsAsync(string repositoryPath, string branchName, CancellationToken cancellationToken = default);

    /// <summary>Returns whether <paramref name="branchName" /> is a valid branch name (<c>git check-ref-format refs/heads/...</c>).</summary>
    Task<GitResult<bool>> IsBranchNameValidAsync(string repositoryPath, string branchName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cheap change probe: a boolean, the branch and the upstream tracking, with no file list. One <c>git status</c> process
    /// decides all of them, so ignore rules are honored. A timeout, a launch error or a non-zero exit is reported as
    /// inconclusive (<see cref="GitDirtyProbe.IsConclusive" /> is false) and never as a clean tree.
    /// </summary>
    Task<GitResult<GitDirtyProbe>> ProbeDirtyAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>Returns the full status (staged, unstaged, untracked, renamed, conflicted) as <c>git status --porcelain=2</c> reports it.</summary>
    Task<GitResult<GitStatus>> GetStatusAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>Stages one path, including its deletion. Paths outside the repository are rejected.</summary>
    Task<GitResult> StageAsync(string repositoryPath, string path, CancellationToken cancellationToken = default);

    /// <summary>Removes one path from the index, leaving the working tree unchanged. Works before the first commit.</summary>
    Task<GitResult> UnstageAsync(string repositoryPath, string path, CancellationToken cancellationToken = default);

    /// <summary>Stages every changed, untracked or conflicted path. Returns how many paths were targeted.</summary>
    Task<GitResult<int>> StageAllAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>Unstages every staged path. Returns how many paths were targeted.</summary>
    Task<GitResult<int>> UnstageAllAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a commit from the staged changes, recording <paramref name="message" /> exactly as given. Fails with
    /// <see cref="GitFailureCode.NothingStaged" /> or <see cref="GitFailureCode.IdentityNotConfigured" />. Git hooks do not run.
    /// </summary>
    Task<GitResult<GitCommitOutcome>> CommitAsync(string repositoryPath, string message, CancellationToken cancellationToken = default);

    /// <summary>Pushes the current branch to its upstream. Never prompts for credentials.</summary>
    Task<GitResult<GitSyncOutcome>> PushAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>Pulls the current branch from its upstream, fast-forward only. Never prompts for credentials.</summary>
    Task<GitResult<GitSyncOutcome>> PullAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebases the current branch onto its upstream (<c>pull --rebase</c>). Refuses a dirty tree, and aborts automatically when
    /// the rebase stops on conflicts. The outcome reports the state of the repository in every case.
    /// </summary>
    Task<GitRebaseOutcome> RebaseAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures a repository for <paramref name="remoteUrl" /> exists at <paramref name="targetPath" />. Clones when absent, reuses a
    /// repository whose origin matches, and fails without deleting anything when the target conflicts.
    /// </summary>
    Task<GitResult<GitCloneOutcome>> EnsureCloneAsync(string remoteUrl, string targetPath, CancellationToken cancellationToken = default);

    /// <summary>Finds repositories under <paramref name="rootPath" /> within the limits in <paramref name="options" />. Does not descend into a repository.</summary>
    Task<GitResult<IReadOnlyList<string>>> ScanForRepositoriesAsync(
        string rootPath,
        GitScanOptions? options = null,
        CancellationToken cancellationToken = default);
}
