using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using HagiCode.Libs.Core.Discovery;

namespace HagiCode.Libs.Providers.OpenCode;

public sealed class OpenCodeProcessLauncher
{
    // OpenCode 2.0 prints "server listening on <url>"; 1.x printed "opencode server listening ... on <url>". Both are accepted.
    private static readonly Regex ListeningRegex = new(
        @"(?:opencode\s+)?server listening.* on\s+(https?://[^\s]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // OpenCode 2.0 prints "server password <value>" right after the listening line.
    private static readonly Regex PasswordRegex = new(
        @"^\s*server password\s+(\S+)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // The password line follows the listening line within the same startup burst; wait briefly for it.
    private static readonly TimeSpan PasswordLineGrace = TimeSpan.FromSeconds(1);

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
        return await StartAsync(executablePath, options, runtimeEnvironment, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<OpenCodeProcessHandle> StartAsync(
        string executablePath,
        OpenCodeStandaloneServerOptions options,
        IReadOnlyDictionary<string, string?> runtimeEnvironment,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runtimeEnvironment);

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
        var passwordSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

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

            if (TryParsePassword(line, out var password))
            {
                passwordSource.TrySetResult(password);
            }
        });
        var stderrPump = PumpAsync(process.StandardError, output, _ => { });
        var exitedTask = process.WaitForExitAsync(cancellationToken);
        var timeoutTask = Task.Delay(startupTimeout, cancellationToken);

        var completed = await Task.WhenAny(readySource.Task, exitedTask, timeoutTask).ConfigureAwait(false);
        if (completed == readySource.Task)
        {
            var passwordLine = await Task.WhenAny(passwordSource.Task, Task.Delay(PasswordLineGrace, cancellationToken)).ConfigureAwait(false);
            var serverPassword = passwordLine == passwordSource.Task ? passwordSource.Task.Result : null;
            return new OpenCodeProcessHandle(process, readySource.Task.Result, serverPassword, output, stdoutPump, stderrPump);
        }

        TryKill(process);
        await Task.WhenAll(SuppressAsync(stdoutPump), SuppressAsync(stderrPump)).ConfigureAwait(false);

        var passwordCaptured = passwordSource.Task.IsCompletedSuccessfully ? passwordSource.Task.Result : null;
        var redactedOutput = OpenCodeSecretRedactor.Redact(CaptureOutput(output), passwordCaptured);

        if (completed == timeoutTask)
        {
            throw new TimeoutException($"Timed out waiting for OpenCode runtime startup after {startupTimeout}. Output: {redactedOutput}");
        }

        throw new InvalidOperationException($"OpenCode runtime exited before it reported a listening URL. Output: {redactedOutput}");
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

    public static bool TryParsePassword(string line, out string password)
    {
        var match = PasswordRegex.Match(line);
        if (match.Success)
        {
            password = match.Groups[1].Value;
            return true;
        }

        password = string.Empty;
        return false;
    }

    public static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string CaptureOutput(StringBuilder output)
    {
        lock (output)
        {
            return output.ToString();
        }
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
        string? serverPassword,
        StringBuilder output,
        Task stdoutPump,
        Task stderrPump)
    {
        _process = process;
        BaseUri = baseUri;
        ServerPassword = serverPassword;
        _output = output;
        _stdoutPump = stdoutPump;
        _stderrPump = stderrPump;
    }

    public Uri BaseUri { get; }

    public int? ProcessId => _process.Id;

    /// <summary>
    /// Gets the server password printed by the owned runtime, or <c>null</c> when none was printed.
    /// Internal so that the password stays inside the lifecycle code.
    /// </summary>
    internal string? ServerPassword { get; }

    public string CapturedOutput
    {
        get
        {
            lock (_output)
            {
                return OpenCodeSecretRedactor.Redact(_output.ToString(), ServerPassword) ?? string.Empty;
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
