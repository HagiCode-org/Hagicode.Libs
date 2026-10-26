namespace HagiCode.Libs.Git;

/// <summary>A successful or failed commit.</summary>
/// <param name="CommitSha">The full 40-character (or 64-character) object name of the new commit.</param>
/// <param name="Message">The exact message that was recorded.</param>
public sealed record GitCommitOutcome(string CommitSha, string Message);

/// <summary>A successful push or pull.</summary>
/// <param name="Branch">The local branch that was synchronized.</param>
/// <param name="Upstream">The upstream branch in short form (for example <c>origin/main</c>).</param>
public sealed record GitSyncOutcome(string Branch, string Upstream);

/// <summary>
/// The result of a rebase. Unlike other operations, rebase reports repository state on failure too (branch names, whether
/// the working tree is clean, whether an automatic abort ran), so the outcome always carries these fields.
/// </summary>
public sealed record GitRebaseOutcome
{
    /// <summary>Gets a value indicating whether the rebase completed.</summary>
    public bool Success => Failure is null;

    /// <summary>Gets the current branch name, or <c>HEAD</c> when detached.</summary>
    public required string CurrentBranch { get; init; }

    /// <summary>Gets the upstream branch in short form, or <see langword="null" /> when none is configured.</summary>
    public string? TargetBranch { get; init; }

    /// <summary>Gets a value indicating whether an upstream branch was available.</summary>
    public bool RemoteBranchAvailable { get; init; }

    /// <summary>Gets a value indicating whether the working tree was clean when the operation finished.</summary>
    public bool WorkingTreeClean { get; init; }

    /// <summary>Gets a value indicating whether an automatic <c>rebase --abort</c> completed.</summary>
    public bool RebaseAborted { get; init; }

    /// <summary>Gets the failure, or <see langword="null" /> when the rebase succeeded.</summary>
    public GitFailure? Failure { get; init; }
}

/// <summary>The result of ensuring a repository exists at a target path.</summary>
/// <param name="RepositoryPath">The full path of the repository.</param>
/// <param name="ReusedExistingRepository">True when a matching repository was already present and nothing was cloned.</param>
public sealed record GitCloneOutcome(string RepositoryPath, bool ReusedExistingRepository);

/// <summary>Options for <see cref="IGitClient.EnsureCloneAsync(string, string, GitCloneOptions, CancellationToken)" />.</summary>
/// <param name="AllowedProtocols">
/// The transport protocols the remote may use, such as <c>https</c> or <c>ssh</c>. <see langword="null" /> keeps every protocol git supports.
/// An empty list refuses every remote.
/// </param>
public sealed record GitCloneOptions(IReadOnlyCollection<string>? AllowedProtocols = null);

/// <summary>Options for <see cref="IGitClient.RemoveRepositoryAsync" />.</summary>
/// <param name="Force">Skip the dirty and unpushed-commit checks. The structural checks (standalone root, no links) always apply.</param>
public sealed record GitRemoveOptions(bool Force = false);

/// <summary>The result of removing a repository checkout.</summary>
/// <param name="RepositoryPath">The full path of the removed checkout.</param>
public sealed record GitRemoveOutcome(string RepositoryPath);

/// <summary>Options for <see cref="IGitClient.ScanForRepositoriesAsync" />.</summary>
/// <param name="MaxDepth">How many folder levels below the root are visited. The root itself is checked at depth 0.</param>
/// <param name="ExcludeHidden">Skip folders whose name starts with a dot.</param>
/// <param name="ExcludePatterns">Folder names (exact, case-sensitive) to skip.</param>
/// <param name="MaxFoldersPerLevel">At most this many folders are visited per directory; the rest of that directory is skipped.</param>
public sealed record GitScanOptions(
    int MaxDepth = 3,
    bool ExcludeHidden = true,
    IReadOnlyCollection<string>? ExcludePatterns = null,
    int MaxFoldersPerLevel = 1000);

/// <summary>The upstream of the current branch and how far the branch has diverged from it.</summary>
/// <param name="Upstream">The upstream in short form (for example <c>origin/main</c>).</param>
/// <param name="Ahead">Commits on the local branch that are not on the upstream.</param>
/// <param name="Behind">Commits on the upstream that are not on the local branch.</param>
public sealed record GitTrackingInfo(string Upstream, int Ahead, int Behind);
