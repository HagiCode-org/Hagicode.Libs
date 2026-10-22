using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.Dsh;

/// <summary>
/// Implements DeepSeek Harness (dsh) integration over the shared ACP session layer.
/// </summary>
public class DshProvider : ICliProvider<DshOptions>
{
    private static readonly string[] DefaultExecutableCandidates = ["dsh", "dsh-cli"];
    private const string ManagedProfileArguments = "acp";
    private const string IdentityMarker = "DeepSeek Harness";
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    private readonly CliExecutableResolver _executableResolver;
    private readonly CliProcessManager _processManager;
    private readonly IRuntimeEnvironmentResolver? _runtimeEnvironmentResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="DshProvider" /> class.
    /// </summary>
    /// <param name="executableResolver">The executable resolver.</param>
    /// <param name="processManager">The process manager.</param>
    /// <param name="runtimeEnvironmentResolver">The optional runtime environment resolver.</param>
    public DshProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _runtimeEnvironmentResolver = runtimeEnvironmentResolver;
    }

    /// <inheritdoc />
    public string Name => "dsh";

    /// <inheritdoc />
    public bool IsAvailable => _executableResolver.ResolveProviderExecutable("Dsh", DefaultExecutableCandidates).IsFound;

    /// <inheritdoc />
    public async IAsyncEnumerable<CliMessage> ExecuteAsync(
        DshOptions options,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        var executablePath = ResolveExecutablePath(options, runtimeEnvironment)
            ?? throw new FileNotFoundException(
                "Unable to locate the dsh executable. Set DshOptions.ExecutablePath or ensure 'dsh' or 'dsh-cli' is on PATH.");

        var secrets = options.EnvironmentVariables.Values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .ToArray();

        string? identityError;
        try
        {
            identityError = await VerifyIdentityAsync(executablePath, runtimeEnvironment, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            identityError = $"dsh identity check failed: {ex.Message}";
        }

        if (identityError is not null)
        {
            yield return RedactTerminalFailure(DshAcpMessageMapper.CreateTerminalFailureMessage(string.Empty, identityError), secrets);
            yield break;
        }

        var workingDirectory = ResolveWorkingDirectory(options.WorkingDirectory);
        var startContext = new ProcessStartContext
        {
            ExecutablePath = executablePath,
            Arguments = BuildCommandArguments(options),
            WorkingDirectory = workingDirectory,
            EnvironmentVariables = BuildEnvironmentVariables(options, runtimeEnvironment),
            Ownership = new CliProcessOwnershipRegistration { ProviderName = Name }
        };

        await foreach (var message in ExecuteOneShotAsync(options, prompt, workingDirectory, startContext, cancellationToken).ConfigureAwait(false))
        {
            yield return RedactTerminalFailure(message, secrets);
        }
    }

    /// <inheritdoc />
    public async Task<CliProviderTestResult> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
            var executablePath = _executableResolver.ResolveProviderExecutable("Dsh", DefaultExecutableCandidates, runtimeEnvironment).ResolvedPath;
            if (executablePath is null)
            {
                return CreatePingFailure("dsh executable was not found. Install DeepSeek Harness locally or set DshOptions.ExecutablePath.");
            }

            var identityError = await VerifyIdentityAsync(executablePath, runtimeEnvironment, cancellationToken).ConfigureAwait(false);
            if (identityError is not null)
            {
                return CreatePingFailure(identityError);
            }

            var version = await RunProbeAsync(executablePath, "--version", runtimeEnvironment, cancellationToken).ConfigureAwait(false);
            var versionText = FirstNonEmptyLine(version.StandardOutput);
            if (version.TimedOut || version.ExitCode != 0 || versionText is null)
            {
                return CreatePingFailure("dsh --version did not report a version.");
            }

            var startContext = new ProcessStartContext
            {
                ExecutablePath = executablePath,
                Arguments = BuildCommandArguments(new DshOptions()),
                WorkingDirectory = Directory.GetCurrentDirectory(),
                EnvironmentVariables = runtimeEnvironment,
                Ownership = new CliProcessOwnershipRegistration { ProviderName = Name }
            };

            var handshakeError = await HandshakeAsync(startContext, cancellationToken).ConfigureAwait(false);
            if (handshakeError is not null)
            {
                return CreatePingFailure(handshakeError);
            }

            return new CliProviderTestResult
            {
                ProviderName = Name,
                Success = true,
                Version = versionText
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CreatePingFailure($"dsh ping failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    internal IReadOnlyList<string> BuildCommandArguments(DshOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var arguments = new List<string> { "--profile", ManagedProfileArguments };
        foreach (var extraArgument in options.ExtraArguments)
        {
            var normalizedArgument = ArgumentValueNormalizer.NormalizeOptionalValue(extraArgument);
            if (normalizedArgument is null)
            {
                continue;
            }

            if (string.Equals(normalizedArgument, "--profile", StringComparison.OrdinalIgnoreCase) ||
                normalizedArgument.StartsWith("--profile=", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"DshOptions.ExtraArguments cannot contain '{normalizedArgument}' because the provider always boots the managed '--profile {ManagedProfileArguments}' ACP profile.",
                    nameof(options));
            }

            arguments.Add(normalizedArgument);
        }

        return arguments;
    }

    /// <summary>
    /// Creates the ACP session client used for a single execution.
    /// </summary>
    /// <param name="startContext">The subprocess start context.</param>
    /// <param name="provider">The dsh provider id paired with the model during session start.</param>
    /// <returns>The ACP session client.</returns>
    protected virtual IAcpSessionClient CreateSessionClient(ProcessStartContext startContext, string? provider)
    {
        return new DshAcpSessionClient(new AcpSessionClient(CreateAcpTransport(startContext)), provider);
    }

    /// <summary>
    /// Creates the raw ACP transport used by the session client.
    /// </summary>
    /// <param name="startContext">The subprocess start context.</param>
    /// <returns>The ACP transport.</returns>
    protected virtual IAcpTransport CreateAcpTransport(ProcessStartContext startContext)
    {
        return new SubprocessAcpTransport(_processManager, startContext);
    }

    private async IAsyncEnumerable<CliMessage> ExecuteOneShotAsync(
        DshOptions options,
        string prompt,
        string workingDirectory,
        ProcessStartContext startContext,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var sessionClient = CreateSessionClient(startContext, options.Provider);
        using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var startupTimeout = options.StartupTimeout ?? DefaultStartupTimeout;
        startupCts.CancelAfter(startupTimeout);

        AcpSessionHandle? sessionHandle = null;
        string? startupError = null;
        try
        {
            await sessionClient.ConnectAsync(startupCts.Token).ConfigureAwait(false);
            await sessionClient.InitializeAsync(startupCts.Token).ConfigureAwait(false);
            sessionHandle = await sessionClient.StartSessionAsync(
                workingDirectory,
                options.SessionId,
                options.Model,
                startupCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            startupError = $"dsh ACP startup timed out after {startupTimeout.TotalSeconds:0} seconds.";
        }
        catch (Exception ex)
        {
            startupError = $"dsh ACP startup failed: {ex.Message}";
        }

        if (sessionHandle is null)
        {
            yield return DshAcpMessageMapper.CreateTerminalFailureMessage(
                string.Empty,
                DescribeFailure(startupError ?? "dsh ACP startup failed.", sessionClient));
            yield break;
        }

        yield return DshAcpMessageMapper.CreateSessionLifecycleMessage(sessionHandle);

        await foreach (var message in StreamPromptAsync(sessionClient, sessionHandle.SessionId, prompt, cancellationToken).ConfigureAwait(false))
        {
            yield return message;
        }
    }

    private static async IAsyncEnumerable<CliMessage> StreamPromptAsync(
        IAcpSessionClient sessionClient,
        string sessionId,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var promptTask = sessionClient.SendPromptAsync(sessionId, prompt, cancellationToken);
        using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = CancelReceiveLoopWhenPromptFinishesAsync(promptTask, receiveCts);
        await using var notifications = sessionClient.ReceiveNotificationsAsync(receiveCts.Token).GetAsyncEnumerator(receiveCts.Token);

        var sawAssistantText = false;
        while (true)
        {
            AcpNotification notification = null!;
            string? streamError = null;
            try
            {
                if (!await notifications.MoveNextAsync().ConfigureAwait(false))
                {
                    break;
                }

                notification = notifications.Current;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && promptTask.IsCompleted)
            {
                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                streamError = $"dsh stream ended unexpectedly: {ex.Message}";
            }

            if (streamError is not null)
            {
                yield return DshAcpMessageMapper.CreateTerminalFailureMessage(sessionId, DescribeFailure(streamError, sessionClient));
                yield break;
            }

            foreach (var message in DshAcpMessageMapper.NormalizeNotification(notification))
            {
                if (string.Equals(message.Type, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    sawAssistantText = true;
                }

                yield return message;
                if (IsTerminalMessage(message.Type))
                {
                    yield break;
                }
            }
        }

        JsonElement promptResult = default;
        string? promptError = null;
        try
        {
            promptResult = await promptTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            promptError = ex.Message;
        }

        if (promptError is not null)
        {
            yield return DshAcpMessageMapper.CreateTerminalFailureMessage(
                sessionId,
                DescribeFailure($"dsh prompt failed: {promptError}", sessionClient));
            yield break;
        }

        if (!sawAssistantText && DshAcpMessageMapper.TryGetResultText(promptResult, out var resultText))
        {
            yield return DshAcpMessageMapper.CreateAssistantMessage(sessionId, resultText!, promptResult);
        }

        yield return DshAcpMessageMapper.CreateTerminalMessage(sessionId, promptResult);
    }

    private async Task<string?> HandshakeAsync(ProcessStartContext startContext, CancellationToken cancellationToken)
    {
        await using var sessionClient = CreateSessionClient(startContext, provider: null);
        using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupCts.CancelAfter(DefaultStartupTimeout);

        try
        {
            await sessionClient.ConnectAsync(startupCts.Token).ConfigureAwait(false);
            await sessionClient.InitializeAsync(startupCts.Token).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return DescribeFailure($"dsh ACP startup timed out after {DefaultStartupTimeout.TotalSeconds:0} seconds.", sessionClient);
        }
        catch (Exception ex)
        {
            return DescribeFailure($"dsh ACP handshake failed: {ex.Message}", sessionClient);
        }
    }

    private Task<ProcessResult> RunProbeAsync(
        string executablePath,
        string argument,
        IReadOnlyDictionary<string, string?> runtimeEnvironment,
        CancellationToken cancellationToken)
    {
        return _processManager.ExecuteAsync(
            new ProcessStartContext
            {
                ExecutablePath = executablePath,
                Arguments = [argument],
                EnvironmentVariables = runtimeEnvironment,
                Timeout = ProbeTimeout
            },
            cancellationToken);
    }

    private async Task<string?> VerifyIdentityAsync(
        string executablePath,
        IReadOnlyDictionary<string, string?> runtimeEnvironment,
        CancellationToken cancellationToken)
    {
        var help = await RunProbeAsync(executablePath, "--help", runtimeEnvironment, cancellationToken).ConfigureAwait(false);
        if (help.TimedOut)
        {
            return $"dsh --help timed out after {ProbeTimeout.TotalSeconds:0} seconds.";
        }

        if (!help.StandardOutput.Contains(IdentityMarker, StringComparison.OrdinalIgnoreCase))
        {
            return
                $"'{executablePath}' is not DeepSeek Harness because dsh --help did not identify it. Set DshOptions.ExecutablePath to a DeepSeek Harness binary.";
        }

        return null;
    }

    private CliProviderTestResult CreatePingFailure(string message)
    {
        return new CliProviderTestResult
        {
            ProviderName = Name,
            Success = false,
            ErrorMessage = message
        };
    }

    private async Task<IReadOnlyDictionary<string, string?>> ResolveRuntimeEnvironmentAsync(CancellationToken cancellationToken)
    {
        if (_runtimeEnvironmentResolver is null)
        {
            return new Dictionary<string, string?>();
        }

        return await _runtimeEnvironmentResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
    }

    private string? ResolveExecutablePath(DshOptions options, IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        if (!string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            return _executableResolver.ResolveExecutablePath(options.ExecutablePath, runtimeEnvironment);
        }

        return _executableResolver.ResolveProviderExecutable("Dsh", DefaultExecutableCandidates, runtimeEnvironment).ResolvedPath;
    }

    private static IReadOnlyDictionary<string, string?> BuildEnvironmentVariables(
        DshOptions options,
        IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        var environment = new Dictionary<string, string?>(runtimeEnvironment, StringComparer.Ordinal);
        foreach (var entry in options.EnvironmentVariables)
        {
            environment[entry.Key] = entry.Value;
        }

        return environment;
    }

    private static string ResolveWorkingDirectory(string? workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            return Path.GetFullPath(workingDirectory);
        }

        return Directory.GetCurrentDirectory();
    }

    private static string? FirstNonEmptyLine(string text)
    {
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
    }

    private static CliMessage RedactTerminalFailure(CliMessage message, IReadOnlyCollection<string> secrets)
    {
        if (secrets.Count == 0 ||
            !string.Equals(message.Type, "terminal.failed", StringComparison.Ordinal) ||
            message.Content.ValueKind != JsonValueKind.Object ||
            !message.Content.TryGetProperty("message", out var messageElement) ||
            messageElement.ValueKind != JsonValueKind.String)
        {
            return message;
        }

        var text = messageElement.GetString() ?? string.Empty;
        var redacted = secrets.Aggregate(text, static (current, secret) => current.Replace(secret, "[redacted]", StringComparison.Ordinal));
        if (redacted == text)
        {
            return message;
        }

        var sessionId = message.Content.TryGetProperty("session_id", out var sessionIdElement) &&
                        sessionIdElement.ValueKind == JsonValueKind.String
            ? sessionIdElement.GetString() ?? string.Empty
            : string.Empty;

        return DshAcpMessageMapper.CreateTerminalFailureMessage(sessionId, redacted);
    }

    private static string DescribeFailure(string message, IAcpSessionClient sessionClient)
    {
        var diagnostics = sessionClient.GetDiagnosticSummary();
        return string.IsNullOrWhiteSpace(diagnostics)
            ? message
            : $"{message}{Environment.NewLine}{diagnostics}";
    }

    private static async Task CancelReceiveLoopWhenPromptFinishesAsync(
        Task<JsonElement> promptTask,
        CancellationTokenSource receiveCts)
    {
        try
        {
            var promptResult = await promptTask.ConfigureAwait(false);
            if (!DshAcpMessageMapper.IsSuccessfulStopReason(promptResult))
            {
                TryCancel(receiveCts);
            }
        }
        catch
        {
            TryCancel(receiveCts);
        }
    }

    private static void TryCancel(CancellationTokenSource cancellationTokenSource)
    {
        try
        {
            cancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static bool IsTerminalMessage(string messageType)
    {
        return string.Equals(messageType, "terminal.completed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(messageType, "terminal.failed", StringComparison.OrdinalIgnoreCase);
    }
}
