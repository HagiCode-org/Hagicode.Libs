namespace HagiCode.Libs.Git.Internal;

/// <summary>
/// Reads repository metadata directly from the file system: the <c>.git</c> entry, the <c>gitdir:</c> pointer used by
/// worktrees and submodules, and the <c>HEAD</c> reference. These are the no-process fast paths from design D2.
/// </summary>
internal static class GitRepositoryLayout
{
    private const string GitDirPrefix = "gitdir:";
    private const string RefPrefix = "ref:";
    private const string HeadsPrefix = "refs/heads/";

    /// <summary>
    /// Returns true when <paramref name="path" /> is the root of a repository. The <c>.git</c> entry may be a directory,
    /// or a file that points at a worktree or submodule git directory. The git directory must contain <c>HEAD</c>.
    /// </summary>
    public static bool IsRepositoryRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var gitDirectory = ResolveGitDirectory(path);
        return gitDirectory is not null && File.Exists(Path.Combine(gitDirectory, "HEAD"));
    }

    /// <summary>
    /// Resolves the git directory for a repository root, following a <c>gitdir:</c> pointer when <c>.git</c> is a file.
    /// Returns <see langword="null" /> when no git directory exists at that path.
    /// </summary>
    public static string? ResolveGitDirectory(string repositoryPath)
    {
        var root = Path.GetFullPath(repositoryPath);
        var dotGit = Path.Combine(root, ".git");

        if (Directory.Exists(dotGit))
        {
            return dotGit;
        }

        if (!File.Exists(dotGit))
        {
            return null;
        }

        string pointer;
        try
        {
            pointer = File.ReadAllText(dotGit).Trim();
        }
        catch (IOException)
        {
            return null;
        }

        if (!pointer.StartsWith(GitDirPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var target = pointer[GitDirPrefix.Length..].Trim();
        var resolved = Path.IsPathRooted(target)
            ? target
            : Path.GetFullPath(Path.Combine(root, target));

        return Directory.Exists(resolved) ? resolved : null;
    }

    /// <summary>
    /// Returns the branch named by <c>HEAD</c>, or <c>HEAD</c> when HEAD is detached. Returns <see langword="null" /> when
    /// HEAD cannot be read. An unborn branch returns its name, because the branch reference exists before its first commit.
    /// </summary>
    public static string? ReadHeadBranch(string repositoryPath)
    {
        var gitDirectory = ResolveGitDirectory(repositoryPath);
        if (gitDirectory is null)
        {
            return null;
        }

        var headPath = Path.Combine(gitDirectory, "HEAD");
        if (!File.Exists(headPath))
        {
            return null;
        }

        string content;
        try
        {
            content = File.ReadAllText(headPath).Trim();
        }
        catch (IOException)
        {
            return null;
        }

        if (!content.StartsWith(RefPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return "HEAD";
        }

        var reference = content[RefPrefix.Length..].Trim();
        return reference.StartsWith(HeadsPrefix, StringComparison.OrdinalIgnoreCase)
            ? reference[HeadsPrefix.Length..]
            : reference;
    }
}
