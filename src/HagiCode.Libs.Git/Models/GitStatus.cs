namespace HagiCode.Libs.Git;

/// <summary>The kind of change recorded for one side (index or working tree) of a status entry.</summary>
public enum GitChangeKind
{
    /// <summary>No change on this side.</summary>
    None,

    /// <summary>A new file was added.</summary>
    Added,

    /// <summary>The content changed.</summary>
    Modified,

    /// <summary>The file was deleted.</summary>
    Deleted,

    /// <summary>The file was renamed (index side only).</summary>
    Renamed,

    /// <summary>The file was copied (index side only).</summary>
    Copied,

    /// <summary>The file type changed (for example regular file to symlink).</summary>
    TypeChanged,

    /// <summary>The path has an unresolved merge conflict.</summary>
    Unmerged,

    /// <summary>The path is not tracked and is not ignored.</summary>
    Untracked
}

/// <summary>One path reported by <c>git status</c>. Ignored paths are never reported.</summary>
/// <param name="Path">The path relative to the repository root, using forward slashes as git reports it.</param>
/// <param name="OriginalPath">The former path for renamed or copied entries; otherwise <see langword="null" />.</param>
/// <param name="IndexChange">The change between HEAD and the index.</param>
/// <param name="WorkingTreeChange">The change between the index and the working tree.</param>
public sealed record GitStatusEntry(
    string Path,
    string? OriginalPath,
    GitChangeKind IndexChange,
    GitChangeKind WorkingTreeChange)
{
    /// <summary>Gets a value indicating whether the entry has a staged change.</summary>
    public bool IsStaged => IndexChange is not GitChangeKind.None and not GitChangeKind.Untracked;

    /// <summary>Gets a value indicating whether the entry has an unstaged change or is untracked.</summary>
    public bool IsUnstaged => WorkingTreeChange is not GitChangeKind.None;
}

/// <summary>
/// The full working-tree status, equivalent to <c>git status --porcelain=2 --branch --untracked-files=all</c>.
/// </summary>
/// <param name="Branch">The current branch name, or <c>HEAD</c> when detached.</param>
/// <param name="Entries">Every changed, conflicted or untracked path.</param>
public sealed record GitStatus(string Branch, IReadOnlyList<GitStatusEntry> Entries)
{
    /// <summary>Gets a value indicating whether any entry exists.</summary>
    public bool HasChanges => Entries.Count > 0;
}

/// <summary>
/// The cheap dirty probe: a boolean and the branch, with no file list. Git is authoritative for the boolean.
/// </summary>
/// <param name="HasChanges">Whether the working tree differs from HEAD, has unstaged changes, or has untracked files.</param>
/// <param name="Branch">The current branch name, or <c>HEAD</c> when detached; <see langword="null" /> when unknown.</param>
/// <param name="IsConclusive">False when the probe could not reach a positive or negative answer. <see cref="HasChanges" /> is then not a claim that the tree is clean.</param>
/// <param name="Message">Explains an inconclusive probe.</param>
/// <param name="Tracking">The upstream and ahead/behind counts from the same status read, or <see langword="null" /> when the branch has no usable upstream.</param>
public sealed record GitDirtyProbe(
    bool HasChanges,
    string? Branch,
    bool IsConclusive = true,
    string? Message = null,
    GitTrackingInfo? Tracking = null);
