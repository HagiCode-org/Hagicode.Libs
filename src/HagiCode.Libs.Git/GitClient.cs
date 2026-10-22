using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Git.Internal;

namespace HagiCode.Libs.Git;

/// <summary>
/// The <c>git</c>-CLI implementation of <see cref="IGitClient" />. Operations are split across partial files by concern:
/// repository inspection, status, staging, commit, remote sync, clone and scanning.
/// </summary>
public sealed partial class GitClient : IGitClient
{
    private enum CommandKind
    {
        /// <summary>Inspection: no optional locks, the local timeout.</summary>
        Read,

        /// <summary>Local mutation: the local timeout.</summary>
        Write,

        /// <summary>Network-capable: the remote timeout.</summary>
        Remote
    }

    private readonly GitClientOptions _options;
    private readonly GitProcessRunner _runner;
    private readonly Lazy<GitExecutableResolution> _executable;

    /// <summary>Initializes a client that uses the given options.</summary>
    public GitClient(GitClientOptions options)
        : this(options, resolver: null)
    {
    }

    internal GitClient(GitClientOptions options, CliExecutableResolver? resolver, GitProcessRunner? runner = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _runner = runner ?? new GitProcessRunner(options.EnvironmentVariables);

        var locator = new GitExecutableLocator(resolver);
        _executable = new Lazy<GitExecutableResolution>(() => locator.Resolve(options.GitExecutablePath, options.EnvironmentVariables));
    }

    /// <summary>
    /// Runs git with <c>core.quotepath=false</c> so non-ASCII paths come back unescaped.
    /// </summary>
    private async Task<GitProcessOutcome> RunAsync(
        string? workingDirectory,
        IReadOnlyList<string> arguments,
        CommandKind kind,
        CancellationToken cancellationToken,
        string? standardInput = null)
    {
        var resolution = _executable.Value;
        if (resolution.Path is null)
        {
            return GitProcessOutcome.NotFound(resolution.Message);
        }

        var fullArguments = new List<string>(arguments.Count + 2) { "-c", "core.quotepath=false" };
        fullArguments.AddRange(arguments);

        var timeout = kind == CommandKind.Remote ? _options.RemoteCommandTimeout : _options.CommandTimeout;
        var request = new GitProcessRequest(
            resolution.Path,
            fullArguments,
            workingDirectory,
            timeout,
            ReadOnly: kind == CommandKind.Read,
            StandardInput: standardInput);

        return await _runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Converts a failed command into a typed failure, keeping the captured output.</summary>
    private static GitFailure ToFailure(GitProcessOutcome outcome, GitFailureCode code, string message)
    {
        if (outcome.Unavailable)
        {
            return new GitFailure(GitFailureCode.GitUnavailable, outcome.LaunchError ?? "git was not found.");
        }

        if (outcome.TimedOut)
        {
            return new GitFailure(
                GitFailureCode.Timeout,
                $"{message} The git process did not finish in time and was terminated.",
                StandardOutput: outcome.StandardOutput,
                StandardError: outcome.StandardError);
        }

        if (outcome.LaunchError is not null)
        {
            return new GitFailure(GitFailureCode.CommandFailed, $"{message} {outcome.LaunchError}");
        }

        return new GitFailure(code, message, outcome.ExitCode, outcome.StandardOutput, outcome.StandardError);
    }

    private static GitFailure InvalidRepository(string path) =>
        new(GitFailureCode.InvalidRepository, $"Git repository not found or invalid: {path}");
}
