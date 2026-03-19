using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.Exploration.Git;

/// <summary>
/// Scans directories for Git repositories and loads their state.
/// </summary>
public sealed class GitRepositoryScanner
{
    private static readonly string[] DefaultExcludedDirectories = [".git", "node_modules", "dist", "bin", "obj"];
    private readonly CliProcessManager _processManager;
    private readonly CliExecutableResolver _executableResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="GitRepositoryScanner" /> class.
    /// </summary>
    /// <param name="processManager">The process manager used for Git commands.</param>
    /// <param name="executableResolver">The executable resolver.</param>
    public GitRepositoryScanner(CliProcessManager processManager, CliExecutableResolver executableResolver)
    {
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
    }

    /// <summary>
    /// Recursively scans for Git repositories.
    /// </summary>
    /// <param name="rootPath">The root directory to scan.</param>
    /// <param name="maxDepth">The maximum recursion depth.</param>
    /// <param name="excludedDirectories">Optional directory names to skip.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>The discovered repositories.</returns>
    public async Task<IReadOnlyList<GitRepositoryInfo>> ScanAsync(
        string rootPath,
        int maxDepth = 5,
        IEnumerable<string>? excludedDirectories = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException(rootPath);
        }

        var repositories = new List<GitRepositoryInfo>();
        var excluded = new HashSet<string>(DefaultExcludedDirectories, StringComparer.OrdinalIgnoreCase);
        if (excludedDirectories is not null)
        {
            foreach (var entry in excludedDirectories)
            {
                excluded.Add(entry);
            }
        }

        await ScanDirectoryAsync(rootPath, 0, maxDepth, excluded, repositories, cancellationToken);
        return repositories.OrderBy(static repository => repository.RepositoryPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Loads Git metadata for a repository.
    /// </summary>
    /// <param name="repositoryPath">The repository path.</param>
    /// <param name="cancellationToken">Cancels the metadata load.</param>
    /// <returns>The repository metadata.</returns>
    public async Task<GitRepositoryInfo> GetRepositoryInfoAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var gitExecutable = _executableResolver.ResolveExecutablePath("git");
        if (gitExecutable is null)
        {
            return new GitRepositoryInfo { RepositoryPath = Path.GetFullPath(repositoryPath) };
        }

        var branch = await ExecuteGitAsync(gitExecutable, repositoryPath, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken);
        if (string.IsNullOrWhiteSpace(branch) || string.Equals(branch, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            branch = await ExecuteGitAsync(gitExecutable, repositoryPath, ["symbolic-ref", "--short", "HEAD"], cancellationToken, allowFailure: true);
        }
        var remoteUrl = await ExecuteGitAsync(gitExecutable, repositoryPath, ["remote", "get-url", "origin"], cancellationToken);
        var status = await ExecuteGitAsync(gitExecutable, repositoryPath, ["status", "--porcelain"], cancellationToken, allowFailure: true);

        return new GitRepositoryInfo
        {
            RepositoryPath = Path.GetFullPath(repositoryPath),
            Branch = string.IsNullOrWhiteSpace(branch) ? null : branch,
            RemoteUrl = string.IsNullOrWhiteSpace(remoteUrl) ? null : remoteUrl,
            HasUncommittedChanges = !string.IsNullOrWhiteSpace(status)
        };
    }

    private async Task ScanDirectoryAsync(
        string currentPath,
        int depth,
        int maxDepth,
        HashSet<string> excludedDirectories,
        List<GitRepositoryInfo> repositories,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > maxDepth)
        {
            return;
        }

        if (IsGitRepository(currentPath))
        {
            repositories.Add(await GetRepositoryInfoAsync(currentPath, cancellationToken));
        }

        foreach (var directory in Directory.EnumerateDirectories(currentPath))
        {
            var directoryName = Path.GetFileName(directory);
            if (excludedDirectories.Contains(directoryName))
            {
                continue;
            }

            await ScanDirectoryAsync(directory, depth + 1, maxDepth, excludedDirectories, repositories, cancellationToken);
        }
    }

    private async Task<string?> ExecuteGitAsync(
        string gitExecutable,
        string repositoryPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool allowFailure = false)
    {
        var result = await _processManager.ExecuteAsync(
            new ProcessStartContext
            {
                ExecutablePath = gitExecutable,
                WorkingDirectory = repositoryPath,
                Arguments = arguments,
                Timeout = TimeSpan.FromSeconds(10)
            },
            cancellationToken);

        if (result.ExitCode != 0 && !allowFailure)
        {
            return null;
        }

        return result.StandardOutput.Trim();
    }

    private static bool IsGitRepository(string currentPath)
    {
        return Directory.Exists(Path.Combine(currentPath, ".git"))
            || File.Exists(Path.Combine(currentPath, ".git"));
    }
}
