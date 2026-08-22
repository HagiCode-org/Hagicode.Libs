using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers;

namespace HagiCode.Libs.Providers.Codebuddy;

/// <summary>
/// Implements CodeBuddy CLI integration over a minimal ACP session layer.
/// </summary>
public class CodebuddyProvider : ICliProvider<CodebuddyOptions>
{
    private static readonly string[] DefaultExecutableCandidates = ["codebuddy", "codebuddy-cli"];
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(15);

    private readonly CliExecutableResolver _executableResolver;
    private readonly CliProcessManager _processManager;
    private readonly IRuntimeEnvironmentResolver? _runtimeEnvironmentResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="CodebuddyProvider" /> class.
    /// </summary>
    /// <param name="executableResolver">The executable resolver.</param>
    /// <param name="processManager">The process manager.</param>
    /// <param name="runtimeEnvironmentResolver">The optional runtime environment resolver.</param>
    public CodebuddyProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _runtimeEnvironmentResolver = runtimeEnvironmentResolver;
    }

    /// <inheritdoc />
    public string Name => "codebuddy";

    /// <inheritdoc />
    public bool IsAvailable => _executableResolver.ResolveFirstAvailablePath(DefaultExecutableCandidates) is not null;

    /// <inheritdoc />
    public async IAsyncEnumerable<CliMessage> ExecuteAsync(
        CodebuddyOptions options,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        var executablePath = ResolveExecutablePath(options, runtimeEnvironment)
            ?? throw new FileNotFoundException(
                "Unable to locate the CodeBuddy executable. Set CodebuddyOptions.ExecutablePath or ensure 'codebuddy' is on PATH.");

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
            yield return message;
        }
    }

    /// <inheritdoc />
    public async Task<CliProviderTestResult> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
            var executablePath = _executableResolver.ResolveFirstAvailablePath(DefaultExecutableCandidates, runtimeEnvironment);
            if (executablePath is null)
            {
                return new CliProviderTestResult
                {
                    ProviderName = Name,
                    Success = false,
                    ErrorMessage = "CodeBuddy executable was not found. Install CodeBuddy or set CodebuddyOptions.ExecutablePath."
                };
            }

            var startContext = new ProcessStartContext
            {
                ExecutablePath = executablePath,
                Arguments = ["--acp"],
                WorkingDirectory = Directory.GetCurrentDirectory(),
                EnvironmentVariables = runtimeEnvironment,
                Ownership = new CliProcessOwnershipRegistration { ProviderName = Name }
            };

            await using var sessionClient = CreateSessionClient(startContext);
            using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupCts.CancelAfter(DefaultStartupTimeout);

            await sessionClient.ConnectAsync(startupCts.Token).ConfigureAwait(false);
            var initializeResult = await sessionClient.InitializeAsync(startupCts.Token).ConfigureAwait(false);

            return new CliProviderTestResult
            {
                ProviderName = Name,
                Success = true,
                Version = DescribeInitializeResult(initializeResult)
            };
        }
        catch (Exception ex)
        {
            return new CliProviderTestResult
            {
                ProviderName = Name,
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    internal virtual IReadOnlyList<string> BuildCommandArguments(CodebuddyOptions options)
    {
        var arguments = new List<string> { "--acp" };
        foreach (var argument in options.ExtraArguments)
        {
            var normalizedArgument = ArgumentValueNormalizer.NormalizeOptionalValue(argument);
            if (normalizedArgument is null ||
                string.Equals(normalizedArgument, "--acp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            arguments.Add(normalizedArgument);
        }

        return arguments;
    }

    internal virtual IReadOnlyDictionary<string, string?> BuildEnvironmentVariables(
        CodebuddyOptions options,
        IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        var environment = new Dictionary<string, string?>(runtimeEnvironment, StringComparer.Ordinal);
        foreach (var entry in options.EnvironmentVariables)
        {
            environment[entry.Key] = entry.Value;
        }

        return environment;
    }

    /// <summary>
    /// Creates the ACP session client used for a single execution.
    /// </summary>
    /// <param name="startContext">The subprocess start context.</param>
    /// <returns>The ACP session client.</returns>
    protected virtual IAcpSessionClient CreateSessionClient(ProcessStartContext startContext)
    {
        return new AcpSessionClient(CreateAcpTransport(startContext));
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
        CodebuddyOptions options,
        string prompt,
        string workingDirectory,
        ProcessStartContext startContext,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var sessionClient = CreateSessionClient(startContext);
        using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupCts.CancelAfter(options.StartupTimeout ?? DefaultStartupTimeout);

        await sessionClient.ConnectAsync(startupCts.Token).ConfigureAwait(false);
        await sessionClient.InitializeAsync(startupCts.Token).ConfigureAwait(false);
        var sessionHandle = await sessionClient.StartSessionAsync(
            workingDirectory,
            options.SessionId,
            options.Model,
            startupCts.Token).ConfigureAwait(false);
        await EnsureModeAsync(sessionClient, sessionHandle.SessionId, options.ModeId, startupCts.Token).ConfigureAwait(false);

        yield return CodebuddyAcpMessageMapper.CreateSessionLifecycleMessage(sessionHandle);

        await foreach (var message in StreamPromptAttemptAsync(
                           sessionClient,
                           sessionHandle.SessionId,
                           sessionHandle.IsResumed,
                           prompt,
                           cancellationToken).ConfigureAwait(false))
        {
            yield return message;
        }
    }

    protected virtual Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        return Task.Delay(delay, cancellationToken);
    }

    private async Task<IReadOnlyDictionary<string, string?>> ResolveRuntimeEnvironmentAsync(CancellationToken cancellationToken)
    {
        if (_runtimeEnvironmentResolver is null)
        {
            return new Dictionary<string, string?>();
        }

        return await _runtimeEnvironmentResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
    }

    private string? ResolveExecutablePath(CodebuddyOptions options, IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        if (!string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            return _executableResolver.ResolveExecutablePath(options.ExecutablePath, runtimeEnvironment);
        }

        return _executableResolver.ResolveFirstAvailablePath(DefaultExecutableCandidates, runtimeEnvironment);
    }

    private static string ResolveWorkingDirectory(string? workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            return Path.GetFullPath(workingDirectory);
        }

        return Directory.GetCurrentDirectory();
    }

    private static string DescribeInitializeResult(JsonElement initializeResult)
    {
        if (initializeResult.ValueKind == JsonValueKind.Object)
        {
            if (initializeResult.TryGetProperty("agentInfo", out var agentInfo) &&
                agentInfo.ValueKind == JsonValueKind.Object)
            {
                var name = TryGetString(agentInfo, "name");
                var version = TryGetString(agentInfo, "version");
                if (!string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(version))
                {
                    return string.Join(" ", new[] { name, version }.Where(static value => !string.IsNullOrWhiteSpace(value)));
                }
            }

            if (initializeResult.TryGetProperty("protocolVersion", out var protocolVersion))
            {
                return $"ACP protocol {protocolVersion}";
            }
        }

        return "ACP initialize succeeded";
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var propertyElement) &&
               propertyElement.ValueKind == JsonValueKind.String
            ? propertyElement.GetString()
            : null;
    }

    private static async IAsyncEnumerable<CliMessage> StreamPromptMessagesAsync(
        IAcpSessionClient sessionClient,
        string sessionId,
        Task<JsonElement> promptTask,
        bool isResumedSession,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sawAssistantText = false;
        var isHistoryReplayActive = false;
        var receivedCompletionNotification = false;
        using var receiveUpdatesCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = CancelReceiveLoopWhenPromptCompletesAsync(promptTask, receiveUpdatesCancellation);
        await using var updateEnumerator = sessionClient.ReceiveNotificationsAsync(receiveUpdatesCancellation.Token)
            .GetAsyncEnumerator(receiveUpdatesCancellation.Token);

        while (true)
        {
            AcpNotification notification = null!;
            Exception? streamFailure = null;
            try
            {
                if (!await updateEnumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    break;
                }

                notification = updateEnumerator.Current;
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
                streamFailure = new InvalidOperationException($"CodeBuddy stream ended unexpectedly: {ex.Message}", ex);
            }

            if (streamFailure is not null)
            {
                yield return CodebuddyAcpMessageMapper.CreateTerminalFailureMessage(sessionId, streamFailure);
                yield break;
            }

            if (isResumedSession &&
                CodebuddyAcpMessageMapper.TryGetReplayWindowBoundary(notification, out var isReplayStart))
            {
                isHistoryReplayActive = isReplayStart;
            }

            if (isResumedSession &&
                isHistoryReplayActive &&
                IsAgentMessageChunk(notification))
            {
                continue;
            }

            foreach (var normalizedMessage in CodebuddyAcpMessageMapper.NormalizeNotification(notification))
            {
                var message = normalizedMessage;
                if (string.Equals(message.Type, "assistant", StringComparison.OrdinalIgnoreCase) &&
                    CodebuddyAcpMessageMapper.TryExtractMessageText(message.Content, out _))
                {
                    sawAssistantText = true;
                }

                if (string.Equals(message.Type, "terminal.completed", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var terminalPromptResult = await promptTask.ConfigureAwait(false);
                        if (CodebuddyAcpMessageMapper.IsFailurePromptResult(terminalPromptResult))
                        {
                            message = CodebuddyAcpMessageMapper.CreateTerminalMessage(sessionId, terminalPromptResult);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        message = CodebuddyAcpMessageMapper.CreateTerminalFailureMessage(sessionId, ex);
                    }
                }

                if (IsTerminalMessage(message.Type))
                {
                    if (string.Equals(message.Type, "terminal.failed", StringComparison.OrdinalIgnoreCase))
                    {
                        yield return message;
                        yield break;
                    }

                    receivedCompletionNotification = true;
                    break;
                }

                yield return message;
            }

            if (receivedCompletionNotification)
            {
                break;
            }
        }

        JsonElement promptResult;
        Exception? promptFailure = null;
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
            promptFailure = ex;
            promptResult = default;
        }

        if (promptFailure is not null)
        {
            yield return CodebuddyAcpMessageMapper.CreateTerminalFailureMessage(sessionId, promptFailure);
            yield break;
        }

        foreach (var fallbackMessage in BuildFallbackMessages(sessionId, promptResult, sawAssistantText, sessionClient))
        {
            yield return fallbackMessage;
        }
    }

    private static bool IsAgentMessageChunk(AcpNotification notification)
    {
        return string.Equals(notification.Method, "session/update", StringComparison.OrdinalIgnoreCase) &&
               notification.Parameters.ValueKind == JsonValueKind.Object &&
               notification.Parameters.TryGetProperty("update", out var updateElement) &&
               updateElement.ValueKind == JsonValueKind.Object &&
               updateElement.TryGetProperty("sessionUpdate", out var updateKind) &&
               updateKind.ValueKind == JsonValueKind.String &&
               string.Equals(updateKind.GetString(), "agent_message_chunk", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task CancelReceiveLoopWhenPromptCompletesAsync(
        Task<JsonElement> promptTask,
        CancellationTokenSource receiveUpdatesCancellation)
    {
        try
        {
            var promptResult = await promptTask.ConfigureAwait(false);
            if (!CodebuddyAcpMessageMapper.ShouldPreferPromptCompletedNotification(promptResult) &&
                !receiveUpdatesCancellation.IsCancellationRequested)
            {
                TryCancelReceiveLoop(receiveUpdatesCancellation);
            }
        }
        catch
        {
            TryCancelReceiveLoop(receiveUpdatesCancellation);
        }
    }

    private static IAsyncEnumerable<CliMessage> StreamPromptAttemptAsync(
        IAcpSessionClient sessionClient,
        string sessionId,
        bool isResumedSession,
        string prompt,
        CancellationToken cancellationToken)
    {
        var promptTask = sessionClient.SendPromptAsync(sessionId, prompt, cancellationToken);
        return StreamPromptMessagesAsync(sessionClient, sessionId, promptTask, isResumedSession, cancellationToken);
    }

    private static IEnumerable<CliMessage> BuildFallbackMessages(
        string sessionId,
        JsonElement promptResult,
        bool sawAssistantText,
        IAcpSessionClient sessionClient)
    {
        if (!sawAssistantText &&
            CodebuddyAcpMessageMapper.TryExtractPromptResultText(promptResult, out var fallbackText) &&
            !CodebuddyAcpMessageMapper.IsFailurePromptResult(promptResult))
        {
            yield return CodebuddyAcpMessageMapper.CreateAssistantMessage(sessionId, fallbackText, promptResult);
        }

        if (!sawAssistantText)
        {
            var diagnostic = sessionClient.GetDiagnosticSummary();
            if (!string.IsNullOrWhiteSpace(diagnostic))
            {
                yield return CodebuddyAcpMessageMapper.CreateTerminalFailureMessage(
                    sessionId,
                    new InvalidOperationException(diagnostic));
                yield break;
            }
        }

        yield return CodebuddyAcpMessageMapper.CreateTerminalMessage(sessionId, promptResult);
    }

    private static void TryCancelReceiveLoop(CancellationTokenSource cancellationTokenSource)
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

    private static async Task EnsureModeAsync(
        IAcpSessionClient sessionClient,
        string sessionId,
        string? modeId,
        CancellationToken cancellationToken)
    {
        var normalizedModeId = ArgumentValueNormalizer.NormalizeOptionalValue(modeId);
        if (normalizedModeId is null)
        {
            return;
        }

        await sessionClient.SetModeAsync(sessionId, normalizedModeId, cancellationToken).ConfigureAwait(false);
    }
}