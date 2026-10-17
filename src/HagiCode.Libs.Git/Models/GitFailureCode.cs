namespace HagiCode.Libs.Git;

/// <summary>
/// Stable, machine-readable reasons an expected git operation failed. Values are part of the public contract:
/// consumers map them to their own codes, so do not renumber or rename without a migration.
/// </summary>
public enum GitFailureCode
{
    /// <summary>The path is empty, missing, or not a git repository.</summary>
    InvalidRepository,

    /// <summary>No <c>git</c> executable could be found. The failure message names the directories that were searched.</summary>
    GitUnavailable,

    /// <summary>The caller supplied an argument the operation cannot use (for example an empty branch or clone target).</summary>
    InvalidArgument,

    /// <summary>A path resolves outside the repository root and was rejected by the path guard.</summary>
    PathOutsideRepository,

    /// <summary>The current branch has no upstream tracking branch, or HEAD is detached.</summary>
    NoUpstream,

    /// <summary>The remote asked for credentials that are not available. The process was never prompted.</summary>
    AuthenticationFailed,

    /// <summary>The remote rejected a push because the update is not a fast-forward.</summary>
    NonFastForward,

    /// <summary>The remote rejected the request for another reason (hook, protected branch, permission, transport).</summary>
    RemoteRejected,

    /// <summary>A fast-forward-only pull could not complete; a merge or rebase is required.</summary>
    FastForwardRequired,

    /// <summary>The working tree has uncommitted changes and the operation refuses to run.</summary>
    WorkingTreeDirty,

    /// <summary>A commit was requested but the index has no staged changes.</summary>
    NothingStaged,

    /// <summary>Neither the repository nor the global configuration provides a user name and email.</summary>
    IdentityNotConfigured,

    /// <summary>A rebase stopped on conflicts and was aborted; the repository was restored.</summary>
    RebaseConflictAborted,

    /// <summary>A rebase failed and the repository may need attention (abort did not complete, or the failure was not a conflict).</summary>
    RebaseFailed,

    /// <summary>The clone target conflicts with existing content (a file, a non-empty non-repository directory, or a different origin). Nothing was deleted.</summary>
    CloneConflict,

    /// <summary>The clone command failed.</summary>
    CloneFailed,

    /// <summary>A git command exited non-zero and no more specific classification applied.</summary>
    CommandFailed,

    /// <summary>The git command did not finish within its timeout. The process tree was terminated.</summary>
    Timeout
}
