using HagiCode.Libs.Git.Internal;

namespace HagiCode.Libs.Git;

public sealed partial class GitClient
{
    /// <summary>Upper bound on the characters of paths passed in one command line, well below the Windows limit.</summary>
    private const int MaxPathArgumentCharacters = 16000;

    /// <inheritdoc />
    public async Task<GitResult> StageAsync(string repositoryPath, string path, CancellationToken cancellationToken = default)
    {
        if (!GitRepositoryLayout.IsRepositoryRoot(repositoryPath))
        {
            return GitResult.Fail(InvalidRepository(repositoryPath));
        }

        var relative = ResolveRelativePath(repositoryPath, path, out var failure);
        if (relative is null)
        {
            return GitResult.Fail(failure!);
        }

        var outcome = await RunAsync(
            repositoryPath,
            ["--literal-pathspecs", "add", "-A", "--", relative],
            CommandKind.Write,
            cancellationToken).ConfigureAwait(false);

        return outcome.Succeeded
            ? GitResult.Ok()
            : GitResult.Fail(ToFailure(outcome, GitFailureCode.CommandFailed, $"Failed to stage '{relative}'."));
    }

    /// <inheritdoc />
    public async Task<GitResult> UnstageAsync(string repositoryPath, string path, CancellationToken cancellationToken = default)
    {
        if (!GitRepositoryLayout.IsRepositoryRoot(repositoryPath))
        {
            return GitResult.Fail(InvalidRepository(repositoryPath));
        }

        var relative = ResolveRelativePath(repositoryPath, path, out var failure);
        if (relative is null)
        {
            return GitResult.Fail(failure!);
        }

        var outcome = await RunAsync(
            repositoryPath,
            ["--literal-pathspecs", "reset", "-q", "--", relative],
            CommandKind.Write,
            cancellationToken).ConfigureAwait(false);

        return outcome.Succeeded
            ? GitResult.Ok()
            : GitResult.Fail(ToFailure(outcome, GitFailureCode.CommandFailed, $"Failed to unstage '{relative}'."));
    }

    /// <inheritdoc />
    public async Task<GitResult<int>> StageAllAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (!status.Success)
        {
            return GitResult<int>.Fail(status.Failure);
        }

        var targets = GitPorcelainV2Parser.StageAllTargets(status.Value!.Entries);
        if (targets.Count == 0)
        {
            return GitResult<int>.Ok(0);
        }

        var failure = await RunInBatchesAsync(
            repositoryPath,
            ["add", "-A", "--"],
            targets,
            "Failed to stage all changes.",
            cancellationToken).ConfigureAwait(false);

        return failure is null ? GitResult<int>.Ok(targets.Count) : GitResult<int>.Fail(failure);
    }

    /// <inheritdoc />
    public async Task<GitResult<int>> UnstageAllAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (!status.Success)
        {
            return GitResult<int>.Fail(status.Failure);
        }

        var targets = GitPorcelainV2Parser.UnstageAllTargets(status.Value!.Entries);
        if (targets.Count == 0)
        {
            return GitResult<int>.Ok(0);
        }

        var failure = await RunInBatchesAsync(
            repositoryPath,
            ["reset", "-q", "--"],
            targets,
            "Failed to unstage all changes.",
            cancellationToken).ConfigureAwait(false);

        return failure is null ? GitResult<int>.Ok(targets.Count) : GitResult<int>.Fail(failure);
    }

    /// <summary>
    /// Runs one command per batch of paths. Batches are cut by total length, so a long list never exceeds the command line limit.
    /// Pathspecs are literal, so a file name containing <c>*</c> or <c>[</c> is not treated as a glob.
    /// </summary>
    private async Task<GitFailure?> RunInBatchesAsync(
        string repositoryPath,
        IReadOnlyList<string> command,
        IReadOnlyList<string> paths,
        string message,
        CancellationToken cancellationToken)
    {
        var batch = new List<string>();
        var length = 0;

        foreach (var path in paths)
        {
            if (batch.Count > 0 && length + path.Length > MaxPathArgumentCharacters)
            {
                var failure = await RunBatchAsync(repositoryPath, command, batch, message, cancellationToken).ConfigureAwait(false);
                if (failure is not null)
                {
                    return failure;
                }

                batch.Clear();
                length = 0;
            }

            batch.Add(path);
            length += path.Length + 1;
        }

        return batch.Count == 0
            ? null
            : await RunBatchAsync(repositoryPath, command, batch, message, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitFailure?> RunBatchAsync(
        string repositoryPath,
        IReadOnlyList<string> command,
        IReadOnlyList<string> batch,
        string message,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>(command.Count + batch.Count + 1) { "--literal-pathspecs" };
        arguments.AddRange(command);
        arguments.AddRange(batch);

        var outcome = await RunAsync(repositoryPath, arguments, CommandKind.Write, cancellationToken).ConfigureAwait(false);
        return outcome.Succeeded ? null : ToFailure(outcome, GitFailureCode.CommandFailed, message);
    }

    /// <summary>
    /// Converts a caller path to a repository-relative, forward-slash path. Absolute paths are accepted when they lie inside the
    /// repository. Anything that resolves outside it, or to the repository root itself, is rejected.
    /// </summary>
    private static string? ResolveRelativePath(string repositoryPath, string? path, out GitFailure? failure)
    {
        failure = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            failure = new GitFailure(GitFailureCode.InvalidArgument, "The path is null or empty.");
            return null;
        }

        var root = Path.GetFullPath(repositoryPath);
        var candidate = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(root, path));
        var relative = Path.GetRelativePath(root, candidate);

        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith("../", StringComparison.Ordinal))
        {
            failure = new GitFailure(GitFailureCode.PathOutsideRepository, $"The path is outside the repository: {path}");
            return null;
        }

        if (relative == ".")
        {
            failure = new GitFailure(GitFailureCode.InvalidArgument, "The repository root cannot be used as a single path. Use StageAll or UnstageAll.");
            return null;
        }

        return relative.Replace('\\', '/');
    }
}
