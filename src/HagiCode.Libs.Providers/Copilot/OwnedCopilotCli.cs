using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.Providers.Copilot;

/// <summary>
/// A headless Copilot CLI that the gateway starts for one session run, so the process can be attributed to that session in the
/// shared process ledger. The SDK would otherwise spawn the CLI itself and keep its process hidden. The SDK connects to the
/// listener over loopback with a per-process connection token. Disposing this stops the CLI and removes its ledger entry.
/// </summary>
internal sealed class OwnedCopilotCli : IAsyncDisposable
{
    /// <summary>The provider name the ledger records for Copilot processes.</summary>
    internal const string ProviderName = "copilot";

    /// <summary>The loopback host the headless CLI listens on.</summary>
    internal const string LoopbackHost = "127.0.0.1";

    /// <summary>The environment variable the headless CLI reads its connection token from.</summary>
    internal const string ConnectionTokenVariableName = "COPILOT_CONNECTION_TOKEN";

    private const string SessionIdVariableName = "HAGI_SESSION_ID";
    private const string NoAutoUpdateFlag = "--no-auto-update";
    private const int MaxDiagnosticCharacters = 4096;

    // The CLI prints "CLI server listening on port <n>" once it accepts connections; with --port 0 that line carries the real port.
    private static readonly Regex ListeningRegex = new(
        @"CLI server listening on port (\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Process _process;
    private readonly IAsyncDisposable? _ledgerEntry;
    private readonly Task _outputPump;
    private int _disposed;

    private OwnedCopilotCli(Process process, int port, string connectionToken, IAsyncDisposable? ledgerEntry, Task outputPump)
    {
        _process = process;
        Port = port;
        ConnectionToken = connectionToken;
        _ledgerEntry = ledgerEntry;
        _outputPump = outputPump;
    }

    /// <summary>Gets the loopback port the CLI listens on.</summary>
    internal int Port { get; }

    /// <summary>Gets the connection token the CLI and the SDK share.</summary>
    internal string ConnectionToken { get; }

    /// <summary>
    /// The HagiCode session ID carried by a run's environment, or null when the run has none (or a blank one).
    /// </summary>
    internal static string? OwnerSessionIdOf(IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        foreach (var (key, value) in runtimeEnvironment)
        {
            if (string.Equals(key, SessionIdVariableName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Starts <c>copilot --headless --port 0</c> and waits until it reports its port. When an owner session is given and a process
    /// ledger is available, the process is registered under that session once it is listening. The owner id is never sent to the CLI.
    /// </summary>
    internal static async Task<OwnedCopilotCli> StartAsync(
        CliProcessManager? processManager,
        string executablePath,
        string? workingDirectory,
        IReadOnlyList<string> cliArgs,
        string? ownerSessionId,
        TimeSpan startupTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(cliArgs);

        var connectionToken = Guid.NewGuid().ToString("N");
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        startInfo.ArgumentList.Add("--headless");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add("0");
        if (!cliArgs.Contains(NoAutoUpdateFlag, StringComparer.Ordinal))
        {
            startInfo.ArgumentList.Add(NoAutoUpdateFlag);
        }

        foreach (var arg in cliArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // The child inherits the caller's environment, which already carries the environment lease's overrides.
        startInfo.Environment[ConnectionTokenVariableName] = connectionToken;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var output = new StringBuilder();
        var readySource = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start Copilot CLI '{executablePath}'.");
        }

        var stdoutPump = PumpAsync(process.StandardOutput, output, line =>
        {
            var match = ListeningRegex.Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var port))
            {
                readySource.TrySetResult(port);
            }
        });
        var stderrPump = PumpAsync(process.StandardError, output, _ => { });
        var exitedTask = process.WaitForExitAsync(cancellationToken);
        var timeoutTask = Task.Delay(startupTimeout, cancellationToken);

        var completed = await Task.WhenAny(readySource.Task, exitedTask, timeoutTask).ConfigureAwait(false);
        if (completed != readySource.Task)
        {
            TryKill(process);
            await Task.WhenAll(SuppressAsync(stdoutPump), SuppressAsync(stderrPump)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var diagnostics = CaptureOutput(output);
            process.Dispose();
            if (completed == timeoutTask)
            {
                throw new TimeoutException($"Timed out after {startupTimeout} waiting for the Copilot CLI to listen. Output: {diagnostics}");
            }

            throw new InvalidOperationException($"The Copilot CLI exited before it listened. Output: {diagnostics}");
        }

        var ledgerEntry = await RegisterAsync(processManager, ownerSessionId, process, executablePath, workingDirectory, cancellationToken)
            .ConfigureAwait(false);
        return new OwnedCopilotCli(
            process,
            readySource.Task.Result,
            connectionToken,
            ledgerEntry,
            Task.WhenAll(stdoutPump, stderrPump));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (_ledgerEntry is not null)
        {
            try
            {
                await _ledgerEntry.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        TryKill(_process);
        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        await SuppressAsync(_outputPump).ConfigureAwait(false);
        _process.Dispose();
    }

    // Attribution is best effort: the run needs the CLI, not its ledger entry, so a failed registration is not fatal.
    private static async Task<IAsyncDisposable?> RegisterAsync(
        CliProcessManager? processManager,
        string? ownerSessionId,
        Process process,
        string executablePath,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        var sessionId = string.IsNullOrWhiteSpace(ownerSessionId) ? null : ownerSessionId.Trim();
        if (processManager is null || sessionId is null)
        {
            return null;
        }

        try
        {
            return await processManager.RegisterExternalProcessAsync(
                    process,
                    new CliExternalProcessRequest
                    {
                        ProviderName = ProviderName,
                        SessionId = sessionId,
                        ExecutablePath = executablePath,
                        WorkingDirectory = workingDirectory,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder output, Action<string> onLine)
    {
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            onLine(line);
            lock (output)
            {
                if (output.Length < MaxDiagnosticCharacters)
                {
                    output.AppendLine(line);
                }
            }
        }
    }

    private static string CaptureOutput(StringBuilder output)
    {
        lock (output)
        {
            return output.ToString().Trim();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static async Task SuppressAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
