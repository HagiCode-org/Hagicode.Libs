using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HagiCode.Libs.Providers.Codex.AppServer;

/// <summary>
/// Owns one <c>codex app-server</c> child process. Its standard streams carry the JSON-RPC connection.
/// </summary>
internal sealed class CodexAppServerProcess : IAsyncDisposable
{
    private const string AppServerArgument = "app-server";
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly TimeSpan GracefulExitTimeout = TimeSpan.FromSeconds(2);

    private readonly Process _process;
    private readonly ILogger _logger;
    private int _disposed;

    private CodexAppServerProcess(Process process, ILogger logger)
    {
        _process = process;
        _logger = logger;
    }

    /// <summary>The app-server's standard output, one JSON message per line.</summary>
    public TextReader Output => _process.StandardOutput;

    /// <summary>The app-server's standard input.</summary>
    public TextWriter Input => _process.StandardInput;

    /// <summary>
    /// Starts <c>codex app-server</c> from the resolved executable with exactly the given environment.
    /// </summary>
    public static CodexAppServerProcess Start(
        string executablePath,
        IReadOnlyDictionary<string, string> environment,
        string? workingDirectory,
        ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(environment);

        var effectiveLogger = logger ?? NullLogger.Instance;
        var startInfo = new ProcessStartInfo(executablePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(AppServerArgument);

        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        startInfo.Environment.Clear();
        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }

        var process = new Process { StartInfo = startInfo };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                effectiveLogger.LogDebug("codex app-server: {Line}", args.Data);
            }
        };

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Failed to start Codex app-server at '{executablePath}'.");
        }

        process.BeginErrorReadLine();
        return new CodexAppServerProcess(process, effectiveLogger);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            // Closing stdin lets app-server exit on its own; the process tree is killed only if it lingers.
            _process.StandardInput.Close();
            using var exitCts = new CancellationTokenSource(GracefulExitTimeout);
            await _process.WaitForExitAsync(exitCts.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or InvalidOperationException)
        {
            // The process is stopped below.
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(exception, "Could not stop the codex app-server process.");
        }
        finally
        {
            _process.Dispose();
        }
    }
}
