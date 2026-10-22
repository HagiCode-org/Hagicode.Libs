using System.ComponentModel;
using System.Text;

namespace HagiCode.Libs.Git.Internal;

/// <summary>One git invocation. Arguments are an argument list, never a shell string.</summary>
/// <param name="Executable">The resolved git executable.</param>
/// <param name="Arguments">Command arguments, without the executable name.</param>
/// <param name="WorkingDirectory">The directory git runs in, or <see langword="null" /> for the current directory.</param>
/// <param name="Timeout">The time limit; the process tree is terminated when it elapses.</param>
/// <param name="ReadOnly">True for inspection commands: sets <c>GIT_OPTIONAL_LOCKS=0</c> so they never take optional index locks.</param>
/// <param name="StandardInput">Text written to stdin as UTF-8 without a byte-order mark, or <see langword="null" /> for an empty stdin.</param>
internal sealed record GitProcessRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    TimeSpan Timeout,
    bool ReadOnly,
    string? StandardInput);

/// <summary>The captured result of one git invocation.</summary>
/// <param name="ExitCode">The exit code, or -1 when the process did not run to completion.</param>
/// <param name="StandardOutput">Standard output decoded as UTF-8.</param>
/// <param name="StandardError">Standard error decoded as UTF-8.</param>
/// <param name="TimedOut">True when the timeout elapsed and the process tree was terminated.</param>
/// <param name="LaunchError">Set when the process could not be started.</param>
/// <param name="Unavailable">True when no git executable was found at all.</param>
internal sealed record GitProcessOutcome(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut = false,
    string? LaunchError = null,
    bool Unavailable = false)
{
    /// <summary>Gets a value indicating whether the command ran to completion with exit code 0.</summary>
    public bool Succeeded => ExitCode == 0 && !TimedOut && LaunchError is null;

    /// <summary>Creates the outcome used when no git executable is available.</summary>
    public static GitProcessOutcome NotFound(string message) => new(-1, string.Empty, string.Empty, LaunchError: message, Unavailable: true);
}

/// <summary>
/// Runs git as a child process with the rules from design D4: argument lists, no prompts, the C locale for
/// classification, UTF-8 stdio, concurrent stdout and stderr reads, stdin support, and a timeout or cancellation that
/// terminates the whole process tree.
/// </summary>
internal sealed class GitProcessRunner
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StreamDrainTimeout = TimeSpan.FromSeconds(10);

    private readonly IReadOnlyDictionary<string, string?>? _environmentOverrides;
    private int _startedCount;

    /// <summary>Initializes a runner that applies <paramref name="environmentOverrides" /> to every child process.</summary>
    public GitProcessRunner(IReadOnlyDictionary<string, string?>? environmentOverrides)
    {
        _environmentOverrides = environmentOverrides;
    }

    /// <summary>Gets the number of git processes this runner has tried to start. Tests use it to count process cost.</summary>
    public int StartedCount => Volatile.Read(ref _startedCount);

    /// <summary>Runs one git command and returns its captured output.</summary>
    /// <exception cref="OperationCanceledException">The caller's token was cancelled. The process tree has been terminated.</exception>
    public async Task<GitProcessOutcome> RunAsync(GitProcessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = request.Executable,
            WorkingDirectory = request.WorkingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8WithoutBom,
            StandardOutputEncoding = Utf8WithoutBom,
            StandardErrorEncoding = Utf8WithoutBom
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        ApplyEnvironment(startInfo.Environment, request.ReadOnly);

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        Interlocked.Increment(ref _startedCount);
        try
        {
            if (!process.Start())
            {
                return new GitProcessOutcome(-1, string.Empty, string.Empty, LaunchError: "git did not start.");
            }
        }
        catch (Win32Exception exception)
        {
            return new GitProcessOutcome(-1, string.Empty, string.Empty, LaunchError: exception.Message);
        }

        // Both pipes are drained concurrently so a large stdout cannot block git while stderr is unread.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdinTask = WriteStandardInputAsync(process, request.StandardInput);

        using var timeoutSource = new CancellationTokenSource(request.Timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await KillProcessTreeAsync(process).ConfigureAwait(false);
            await DrainAsync(stdoutTask, stderrTask, stdinTask).ConfigureAwait(false);
            // Output is re-read below; the drain above only makes sure the pipes are finished.

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            timedOut = true;
        }

        var stdout = await ReadOutputAsync(stdoutTask).ConfigureAwait(false);
        var stderr = await ReadOutputAsync(stderrTask).ConfigureAwait(false);

        return timedOut
            ? new GitProcessOutcome(-1, stdout, stderr, TimedOut: true)
            : new GitProcessOutcome(process.ExitCode, stdout, stderr);
    }

    private void ApplyEnvironment(IDictionary<string, string?> environment, bool readOnly)
    {
        // Forced rules first, so a caller override can still change them for tests.
        environment["GIT_TERMINAL_PROMPT"] = "0";
        environment["GCM_INTERACTIVE"] = "never";
        environment["LC_ALL"] = "C";
        environment["LANG"] = "C";

        if (readOnly)
        {
            environment["GIT_OPTIONAL_LOCKS"] = "0";
        }

        if (_environmentOverrides is null)
        {
            return;
        }

        foreach (var (name, value) in _environmentOverrides)
        {
            if (value is null)
            {
                environment.Remove(name);
            }
            else
            {
                environment[name] = value;
            }
        }
    }

    private static async Task WriteStandardInputAsync(System.Diagnostics.Process process, string? standardInput)
    {
        try
        {
            if (standardInput is null)
            {
                process.StandardInput.Close();
                return;
            }

            await process.StandardInput.WriteAsync(standardInput).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // git exited before reading stdin. Its exit code and output already describe the outcome.
        }
    }

    private static async Task KillProcessTreeAsync(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (Win32Exception)
        {
            // Could not signal; the drain below is bounded, so the call still returns.
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(ExitGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Left to the OS; the caller already has the timeout outcome.
        }
    }

    private static async Task DrainAsync(Task<string> stdout, Task<string> stderr, Task stdin)
    {
        try
        {
            await Task.WhenAll(stdout, stderr, stdin).WaitAsync(StreamDrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A grandchild may still hold a pipe open; the outcome does not depend on the rest of its output.
        }
    }

    private static async Task<string> ReadOutputAsync(Task<string> task)
    {
        try
        {
            return await task.WaitAsync(StreamDrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return string.Empty;
        }
    }
}
