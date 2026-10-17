using HagiCode.Libs.Git.Internal;

namespace HagiCode.Libs.Git;

public sealed partial class GitClient
{
    private const int MaxAncestorLevels = 15;

    /// <inheritdoc />
    public Task<bool> IsRepositoryAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GitRepositoryLayout.IsRepositoryRoot(path));
    }

    /// <inheritdoc />
    public Task<GitResult<string>> FindRepositoryRootAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(GitResult<string>.Fail(new GitFailure(GitFailureCode.InvalidArgument, "The path is null or empty.")));
        }

        return Task.Run(() =>
        {
            // Only a .git directory marks a root here, as the backend has always done. A worktree or submodule .git file
            // is still reported as a repository by IsRepositoryAsync.
            var fullPath = Path.GetFullPath(path);
            var current = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);

            for (var level = 0; level < MaxAncestorLevels && !string.IsNullOrEmpty(current); level++)
            {
                if (Directory.Exists(Path.Combine(current, ".git")))
                {
                    return GitResult<string>.Ok(current);
                }

                current = Path.GetDirectoryName(current);
            }

            return GitResult<string>.Fail(new GitFailure(GitFailureCode.InvalidRepository, $"No .git directory found for path: {path}"));
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<GitResult<string?>> GetRemoteUrlAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        if (!GitRepositoryLayout.IsRepositoryRoot(repositoryPath))
        {
            return GitResult<string?>.Fail(InvalidRepository(repositoryPath));
        }

        var outcome = await RunAsync(
            repositoryPath,
            ["config", "--local", "--get", "remote.origin.url"],
            CommandKind.Read,
            cancellationToken).ConfigureAwait(false);

        if (outcome.Succeeded)
        {
            return GitResult<string?>.Ok(outcome.StandardOutput.TrimEnd('\r', '\n'));
        }

        // Exit code 1 from git config means the key is not set.
        if (!outcome.TimedOut && !outcome.Unavailable && outcome.LaunchError is null && outcome.ExitCode == 1)
        {
            return GitResult<string?>.Ok(null);
        }

        return GitResult<string?>.Fail(ToFailure(outcome, GitFailureCode.CommandFailed, "Could not read the origin remote URL."));
    }

    /// <inheritdoc />
    public async Task<GitResult<string?>> GetConfigValueAsync(
        string repositoryPath,
        string key,
        GitConfigScope scope,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return GitResult<string?>.Fail(new GitFailure(GitFailureCode.InvalidArgument, "The configuration key is null or empty."));
        }

        var flag = scope switch
        {
            GitConfigScope.Local => "--local",
            GitConfigScope.Global => "--global",
            _ => null
        };

        if (flag is null)
        {
            return GitResult<string?>.Fail(new GitFailure(GitFailureCode.InvalidArgument, "The configuration scope must be local or global."));
        }

        if (!GitRepositoryLayout.IsRepositoryRoot(repositoryPath))
        {
            return GitResult<string?>.Fail(InvalidRepository(repositoryPath));
        }

        var outcome = await RunAsync(repositoryPath, ["config", flag, "--get", key], CommandKind.Read, cancellationToken).ConfigureAwait(false);
        if (outcome.Succeeded)
        {
            return GitResult<string?>.Ok(outcome.StandardOutput.TrimEnd('\r', '\n'));
        }

        // Exit code 1 from git config means the key is not set in this scope.
        if (!outcome.TimedOut && !outcome.Unavailable && outcome.LaunchError is null && outcome.ExitCode == 1)
        {
            return GitResult<string?>.Ok(null);
        }

        return GitResult<string?>.Fail(ToFailure(outcome, GitFailureCode.CommandFailed, $"Could not read configuration key '{key}'."));
    }

    /// <inheritdoc />
    public async Task<GitResult<GitIdentity>> GetIdentityAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        if (!GitRepositoryLayout.IsRepositoryRoot(repositoryPath))
        {
            return GitResult<GitIdentity>.Fail(InvalidRepository(repositoryPath));
        }

        return await ReadIdentityAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads <c>user.name</c> and <c>user.email</c> from every scope in one process. <c>--show-scope</c> is used because
    /// <c>--global --get</c> exits with 128 when the global file is missing, which would be an error here rather than "not set".
    /// </summary>
    private async Task<GitResult<GitIdentity>> ReadIdentityAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var outcome = await RunAsync(
            repositoryPath,
            ["config", "--show-scope", "--list", "-z"],
            CommandKind.Read,
            cancellationToken).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            return GitResult<GitIdentity>.Fail(ToFailure(outcome, GitFailureCode.CommandFailed, "Could not read the git identity."));
        }

        var values = ParseScopedConfig(outcome.StandardOutput);
        var (name, nameScope) = Resolve(values, "user.name");
        var (email, emailScope) = Resolve(values, "user.email");
        return GitResult<GitIdentity>.Ok(new GitIdentity(name, nameScope, email, emailScope));
    }

    /// <inheritdoc />
    public Task<GitResult<string>> GetCurrentBranchAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!GitRepositoryLayout.IsRepositoryRoot(repositoryPath))
        {
            return Task.FromResult(GitResult<string>.Fail(InvalidRepository(repositoryPath)));
        }

        var branch = GitRepositoryLayout.ReadHeadBranch(repositoryPath);
        return Task.FromResult(branch is null
            ? GitResult<string>.Fail(new GitFailure(GitFailureCode.CommandFailed, "HEAD could not be read."))
            : GitResult<string>.Ok(branch));
    }

    /// <inheritdoc />
    public async Task<GitResult<bool>> BranchExistsAsync(string repositoryPath, string branchName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(branchName))
        {
            return GitResult<bool>.Ok(false);
        }

        if (!GitRepositoryLayout.IsRepositoryRoot(repositoryPath))
        {
            return GitResult<bool>.Fail(InvalidRepository(repositoryPath));
        }

        var outcome = await RunAsync(
            repositoryPath,
            ["for-each-ref", "--format=%(refname)", "refs/heads", "refs/remotes"],
            CommandKind.Read,
            cancellationToken).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            return GitResult<bool>.Fail(ToFailure(outcome, GitFailureCode.CommandFailed, "Could not list branches."));
        }

        var exists = outcome.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(refName => MatchesBranchName(refName, branchName));

        return GitResult<bool>.Ok(exists);
    }

    /// <summary>
    /// A local branch matches by its full name. A remote-tracking branch matches by its friendly name (<c>origin/x</c>) or by the
    /// part after the remote name (<c>x</c>), which is what the backend has always accepted.
    /// </summary>
    private static bool MatchesBranchName(string refName, string branchName)
    {
        const string heads = "refs/heads/";
        const string remotes = "refs/remotes/";

        if (refName.StartsWith(heads, StringComparison.Ordinal))
        {
            return string.Equals(refName[heads.Length..], branchName, StringComparison.Ordinal);
        }

        if (!refName.StartsWith(remotes, StringComparison.Ordinal))
        {
            return false;
        }

        var friendly = refName[remotes.Length..];
        if (string.Equals(friendly, branchName, StringComparison.Ordinal))
        {
            return true;
        }

        var separator = friendly.IndexOf('/');
        if (separator < 0 || separator == friendly.Length - 1)
        {
            return false;
        }

        return string.Equals(friendly[(separator + 1)..], branchName, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public async Task<GitResult<bool>> IsBranchNameValidAsync(string repositoryPath, string branchName, CancellationToken cancellationToken = default)
    {
        if (!GitRepositoryLayout.IsRepositoryRoot(repositoryPath))
        {
            return GitResult<bool>.Fail(InvalidRepository(repositoryPath));
        }

        var trimmed = branchName?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return GitResult<bool>.Ok(false);
        }

        var outcome = await RunAsync(
            repositoryPath,
            ["check-ref-format", $"refs/heads/{trimmed}"],
            CommandKind.Read,
            cancellationToken).ConfigureAwait(false);

        if (outcome.Succeeded)
        {
            return GitResult<bool>.Ok(true);
        }

        if (!outcome.TimedOut && !outcome.Unavailable && outcome.LaunchError is null && outcome.ExitCode == 1)
        {
            return GitResult<bool>.Ok(false);
        }

        return GitResult<bool>.Fail(ToFailure(outcome, GitFailureCode.CommandFailed, "Could not validate the branch name."));
    }

    /// <summary>
    /// Parses <c>git config --show-scope --list -z</c>: each record is <c>scope NUL key LF value NUL</c>. Only local and
    /// global scopes are kept, and the last value for a key wins, which matches git's own precedence within a scope.
    /// </summary>
    private static Dictionary<(GitConfigScope Scope, string Key), string> ParseScopedConfig(string output)
    {
        var values = new Dictionary<(GitConfigScope, string), string>();
        var parts = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index + 1 < parts.Length; index += 2)
        {
            var scope = parts[index] switch
            {
                "local" => GitConfigScope.Local,
                "global" => GitConfigScope.Global,
                _ => GitConfigScope.None
            };

            if (scope == GitConfigScope.None)
            {
                continue;
            }

            var entry = parts[index + 1];
            var separator = entry.IndexOf('\n');
            var key = separator < 0 ? entry : entry[..separator];
            var value = separator < 0 ? string.Empty : entry[(separator + 1)..];
            values[(scope, key)] = value;
        }

        return values;
    }

    private static (string? Value, GitConfigScope Scope) Resolve(
        IReadOnlyDictionary<(GitConfigScope Scope, string Key), string> values,
        string key)
    {
        foreach (var scope in new[] { GitConfigScope.Local, GitConfigScope.Global })
        {
            if (values.TryGetValue((scope, key), out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return (value, scope);
            }
        }

        return (null, GitConfigScope.None);
    }
}
