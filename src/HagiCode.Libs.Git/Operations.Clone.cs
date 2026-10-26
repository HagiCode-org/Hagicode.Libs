using HagiCode.Libs.Git.Internal;

namespace HagiCode.Libs.Git;

public sealed partial class GitClient
{
    /// <inheritdoc />
    public Task<GitResult<GitCloneOutcome>> EnsureCloneAsync(string remoteUrl, string targetPath, CancellationToken cancellationToken = default) =>
        EnsureCloneAsync(remoteUrl, targetPath, new GitCloneOptions(), cancellationToken);

    /// <inheritdoc />
    public async Task<GitResult<GitCloneOutcome>> EnsureCloneAsync(
        string remoteUrl,
        string targetPath,
        GitCloneOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return GitResult<GitCloneOutcome>.Fail(new GitFailure(GitFailureCode.InvalidArgument, "The repository remote URL is missing."));
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return GitResult<GitCloneOutcome>.Fail(new GitFailure(GitFailureCode.InvalidArgument, "The repository target path is missing."));
        }

        // The protocol check runs before any file-system change or process, so a refused remote leaves nothing behind.
        IReadOnlyList<string>? allowedProtocols = null;
        if (options.AllowedProtocols is not null)
        {
            var normalized = options.AllowedProtocols.Select(protocol => protocol?.Trim().ToLowerInvariant() ?? string.Empty).ToList();
            var invalid = normalized.FirstOrDefault(protocol => !IsProtocolName(protocol));
            if (invalid is not null)
            {
                return GitResult<GitCloneOutcome>.Fail(new GitFailure(
                    GitFailureCode.InvalidArgument,
                    $"The allowed protocol '{invalid}' is not a valid protocol name."));
            }

            var protocol = DeriveProtocol(remoteUrl);
            if (!normalized.Contains(protocol, StringComparer.Ordinal))
            {
                return GitResult<GitCloneOutcome>.Fail(new GitFailure(
                    GitFailureCode.UnsupportedProtocol,
                    $"The remote uses the '{protocol}' protocol, which is not in the allowed list ({string.Join(", ", normalized)}). Nothing was cloned."));
            }

            allowedProtocols = normalized;
        }

        var fullPath = Path.GetFullPath(targetPath);

        if (File.Exists(fullPath))
        {
            return GitResult<GitCloneOutcome>.Fail(new GitFailure(
                GitFailureCode.CloneConflict,
                $"The repository target path points to a file: {fullPath}"));
        }

        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return GitResult<GitCloneOutcome>.Fail(new GitFailure(
                GitFailureCode.InvalidArgument,
                $"The repository target path is invalid: {fullPath}"));
        }

        try
        {
            Directory.CreateDirectory(parent);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return GitResult<GitCloneOutcome>.Fail(new GitFailure(
                GitFailureCode.CloneFailed,
                $"Failed to prepare the repository directory '{fullPath}': {exception.Message}"));
        }

        if (Directory.Exists(fullPath))
        {
            // An empty folder that is not a repository is treated as absent, as before.
            if (!GitRepositoryLayout.IsRepositoryRoot(fullPath) && !Directory.EnumerateFileSystemEntries(fullPath).Any())
            {
                Directory.Delete(fullPath, recursive: false);
            }
        }

        if (Directory.Exists(fullPath))
        {
            if (!GitRepositoryLayout.IsRepositoryRoot(fullPath))
            {
                return GitResult<GitCloneOutcome>.Fail(new GitFailure(
                    GitFailureCode.CloneConflict,
                    $"The target directory already exists but is not a valid Git repository: {fullPath}"));
            }

            var existing = await GetRemoteUrlAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (!existing.Success)
            {
                return GitResult<GitCloneOutcome>.Fail(existing.Failure);
            }

            if (!GitRemoteUrlComparer.Matches(existing.Value, remoteUrl))
            {
                var found = string.IsNullOrWhiteSpace(existing.Value) ? "(missing origin)" : existing.Value;
                return GitResult<GitCloneOutcome>.Fail(new GitFailure(
                    GitFailureCode.CloneConflict,
                    $"The target directory already contains a different Git repository. Expected '{remoteUrl}', found '{found}'."));
            }

            return GitResult<GitCloneOutcome>.Ok(new GitCloneOutcome(fullPath, ReusedExistingRepository: true));
        }

        var clone = await RunAsync(
            parent,
            [.. ProtocolGuardArguments(allowedProtocols), "clone", "--quiet", "--", remoteUrl, fullPath],
            CommandKind.Remote,
            cancellationToken).ConfigureAwait(false);

        if (clone.Succeeded)
        {
            return GitResult<GitCloneOutcome>.Ok(new GitCloneOutcome(fullPath, ReusedExistingRepository: false));
        }

        // Remove only what this call left behind: a folder that is not a repository after a failed clone.
        TryDeleteIncompleteClone(fullPath);

        return GitResult<GitCloneOutcome>.Fail(ToFailure(clone, GitFailureCode.CloneFailed, $"Failed to clone the repository into '{fullPath}'."));
    }

    /// <summary>
    /// Builds the <c>-c</c> options that make git refuse every protocol except the allowed ones. Git's own default is kept for
    /// <c>null</c>, which means no allowlist.
    /// </summary>
    private static IReadOnlyList<string> ProtocolGuardArguments(IReadOnlyList<string>? allowedProtocols)
    {
        if (allowedProtocols is null)
        {
            return [];
        }

        var arguments = new List<string> { "-c", "protocol.allow=never" };
        foreach (var protocol in allowedProtocols)
        {
            arguments.Add("-c");
            arguments.Add($"protocol.{protocol}.allow=always");
        }

        return arguments;
    }

    /// <summary>
    /// Derives the transport git would use for <paramref name="remoteUrl" />. A <c>scheme://</c> URL gives its scheme. A remote helper
    /// (<c>ext::command</c>) gives the name before <c>::</c>, so it is its own protocol. An scp-style <c>[user@]host:path</c> remote
    /// gives <c>ssh</c>. Anything else is a local path, which git serves as <c>file</c>.
    /// </summary>
    private static string DeriveProtocol(string remoteUrl)
    {
        var schemeEnd = remoteUrl.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd > 0 && IsProtocolName(remoteUrl[..schemeEnd]))
        {
            return remoteUrl[..schemeEnd].ToLowerInvariant();
        }

        var helperEnd = remoteUrl.IndexOf("::", StringComparison.Ordinal);
        if (helperEnd > 0 && IsProtocolName(remoteUrl[..helperEnd]))
        {
            return remoteUrl[..helperEnd].ToLowerInvariant();
        }

        // Git treats a colon before any slash as scp-style, except a one-letter drive such as C:\repo.
        var colon = remoteUrl.IndexOf(':');
        var slash = remoteUrl.IndexOf('/');
        var isScpStyle = colon >= 0 && (slash < 0 || colon < slash) && !(colon == 1 && char.IsAsciiLetter(remoteUrl[0]));
        return isScpStyle ? "ssh" : "file";
    }

    private static bool IsProtocolName(string value) =>
        value.Length > 0
        && char.IsAsciiLetter(value[0])
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '+' or '-' or '.');

    private static void TryDeleteIncompleteClone(string fullPath)
    {
        try
        {
            if (Directory.Exists(fullPath) && !GitRepositoryLayout.IsRepositoryRoot(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Leaving a partial folder behind is safer than masking the clone failure with a cleanup error.
        }
    }
}
