using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using HagiCode.Libs.Core.Discovery;

namespace HagiCode.Libs.Providers.OpenCode;

public sealed class OpenCodeProcessLauncher
{
    private static readonly Regex ListeningRegex = new(
        @"opencode server listening.* on\s+(https?://[^\s]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly CliExecutableResolver _executableResolver;

    public OpenCodeProcessLauncher(CliExecutableResolver executableResolver)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
    }

    public async Task<OpenCodeProcessHandle> StartAsync(
        OpenCodeStandaloneServerOptions options,
        IReadOnlyDictionary<string, string?> runtimeEnvironment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runtimeEnvironment);

        var executablePath = ResolveExecutablePath(options, runtimeEnvironment)
            ?? throw new FileNotFoundException("OpenCode executable was not found. Install OpenCode or set OpenCodeOptions.ExecutablePath.");
        var port = GetFreeTcpPort();
        var startupTimeout = options.StartupTimeout ?? TimeSpan.FromSeconds(15);
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory)
                ? Directory.GetCurrentDirectory()
                : options.WorkingDirectory.Trim()
        };

        startInfo.ArgumentList.Add("serve");
        startInfo.ArgumentList.Add("--hostname=127.0.0.1");
        startInfo.ArgumentList.Add($"--port={port}");
        foreach (var argument in options.ExtraArguments)
        {
            var normalized = argument?.Trim();
            if (string.IsNullOrWhiteSpace(normalized) || string.Equals(normalized, "serve", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            startInfo.ArgumentList.Add(normalized);
        }

        foreach (var (key, value) in runtimeEnvironment)
        {
            if (value is null)
            {
                startInfo.Environment.Remove(key);
                continue;
            }

            startInfo.Environment[key] = value;
        }

        foreach (var (key, value) in options.EnvironmentVariables)
        {
            if (value is null)
            {
                startInfo.Environment.Remove(key);
                continue;
            }

            startInfo.Environment[key] = value;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var output = new StringBuilder();
        var readySource = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start OpenCode executable '{executablePath}'.");
        }

        var stdoutPump = PumpAsync(process.StandardOutput, output, line =>
        {
            if (TryParseListeningUrl(line, out var baseUri) && baseUri is not null)
            {
                readySource.TrySetResult(baseUri);
            }
        });
        var stderrPump = PumpAsync(process.StandardError, output, _ => { });
        var exitedTask = process.WaitForExitAsync(cancellationToken);
        var timeoutTask = Task.Delay(startupTimeout, cancellationToken);

        var completed = await Task.WhenAny(readySource.Task, exitedTask, timeoutTask).ConfigureAwait(false);
        if (completed == readySource.Task)
        {
            return new OpenCodeProcessHandle(process, readySource.Task.Result, output, stdoutPump, stderrPump);
        }

        TryKill(process);
        await Task.WhenAll(SuppressAsync(stdoutPump), SuppressAsync(stderrPump)).ConfigureAwait(false);

        if (completed == timeoutTask)
        {
            throw new TimeoutException($"Timed out waiting for OpenCode runtime startup after {startupTimeout}. Output: {output}");
        }

        throw new InvalidOperationException($"OpenCode runtime exited before it reported a listening URL. Output: {output}");
    }

    public string? ResolveExecutablePath(OpenCodeStandaloneServerOptions options, IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runtimeEnvironment);

        if (!string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            return _executableResolver.ResolveExecutablePath(options.ExecutablePath.Trim(), runtimeEnvironment) ?? options.ExecutablePath.Trim();
        }

        return _executableResolver.ResolveFirstAvailablePath(["opencode"], runtimeEnvironment);
    }

    public static bool TryParseListeningUrl(string line, out Uri? baseUri)
    {
        var match = ListeningRegex.Match(line);
        if (match.Success && Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var parsed))
        {
            baseUri = parsed;
            return true;
        }

        baseUri = null;
        return false;
    }

    public static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder output, Action<string> onLine)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            lock (output)
            {
                output.AppendLine(line);
            }

            onLine(line);
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
}

public sealed class OpenCodeProcessHandle : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _output;
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;
    private int _disposed;

    internal OpenCodeProcessHandle(
        Process process,
        Uri baseUri,
        StringBuilder output,
        Task stdoutPump,
        Task stderrPump)
    {
        _process = process;
        BaseUri = baseUri;
        _output = output;
        _stdoutPump = stdoutPump;
        _stderrPump = stderrPump;
    }

    public Uri BaseUri { get; }

    public int? ProcessId => _process.Id;

    public string CapturedOutput
    {
        get
        {
            lock (_output)
            {
                return _output.ToString();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }

        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            await _stdoutPump.ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            await _stderrPump.ConfigureAwait(false);
        }
        catch
        {
        }

        _process.Dispose();
    }
}
