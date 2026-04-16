using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Pooling;

namespace HagiCode.Libs.Providers.QoderCli;

/// <summary>
/// Implements QoderCLI CLI integration over a minimal ACP session layer.
/// </summary>
public class QoderCliProvider : ICliProvider<QoderCliOptions>
{
    private static readonly string[] DefaultExecutableCandidates = ["qodercli"];
    private static readonly string[] PermissionBypassFlags = ["--yolo", "--dangerously-skip-permissions"];
    private const string UnattendedSessionMode = "yolo";
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(15);

    private readonly CliExecutableResolver _executableResolver;
    private readonly CliProcessManager _processManager;
    private readonly IRuntimeEnvironmentResolver? _runtimeEnvironmentResolver;
    private readonly CliProviderPoolCoordinator _poolCoordinator;
    private readonly CliProviderPoolConfigurationRegistry _poolConfiguration;

    private sealed record BufferedAssistantChunk(
        CliMessage Message,
        string? RequestId,
        string? TurnId,
        bool MessageEnd);

    /// <summary>
    /// Initializes a new instance of the <see cref="QoderCliProvider" /> class.
    /// </summary>
    /// <param name="executableResolver">The executable resolver.</param>
    /// <param name="processManager">The process manager.</param>
    /// <param name="runtimeEnvironmentResolver">The optional runtime environment resolver.</param>
    public QoderCliProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null,
        CliProviderPoolCoordinator? poolCoordinator = null,
        CliProviderPoolConfigurationRegistry? poolConfiguration = null)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _runtimeEnvironmentResolver = runtimeEnvironmentResolver;
        _poolCoordinator = poolCoordinator ?? new CliProviderPoolCoordinator();
        _poolConfiguration = poolConfiguration ?? new CliProviderPoolConfigurationRegistry();
    }

    /// <inheritdoc />
    public string Name => "qodercli";

    /// <inheritdoc />
    public bool IsAvailable => _executableResolver.ResolveFirstAvailablePath(DefaultExecutableCandidates) is not null;

    /// <inheritdoc />
    public async IAsyncEnumerable<CliMessage> ExecuteAsync(
        QoderCliOptions options,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        var executablePath = ResolveExecutablePath(options, runtimeEnvironment)
            ?? throw new FileNotFoundException(
                "Unable to locate the QoderCLI executable. Set QoderCliOptions.ExecutablePath or ensure 'qodercli' is on PATH.");

        var workingDirectory = ResolveWorkingDirectory(options.WorkingDirectory);
        var startContext = new ProcessStartContext
        {
            ExecutablePath = executablePath,
            Arguments = BuildCommandArguments(options),
            WorkingDirectory = workingDirectory,
            EnvironmentVariables = BuildEnvironmentVariables(options, runtimeEnvironment)
        };

        var poolSettings = ResolvePoolSettings(options);
        if (!poolSettings.Enabled)
        {
            await foreach (var message in ExecuteOneShotAsync(options, prompt, workingDirectory, startContext, cancellationToken).ConfigureAwait(false))
            {
                yield return message;
            }

            yield break;
        }

        var logicalSessionKey = ResolveLogicalSessionKey(options.SessionId, poolSettings);
        var request = new CliAcpPoolRequest(
            Name,
            logicalSessionKey,
            CliPoolFingerprintBuilder.Build(executablePath, workingDirectory, startContext.Arguments, startContext.EnvironmentVariables, UnattendedSessionMode),
            poolSettings);

        await using var lease = await _poolCoordinator.AcquireAcpSessionAsync(
            request,
            ct => CreatePooledEntryAsync(options, workingDirectory, startContext, request, ct),
            cancellationToken).ConfigureAwait(false);

        if (lease.IsWarmLease)
        {
            var normalizedHandle = await lease.Entry.SessionClient.StartSessionAsync(
                workingDirectory,
                lease.Entry.SessionId,
                options.Model,
                cancellationToken).ConfigureAwait(false);
            lease.Entry.RefreshSession(normalizedHandle, request.CompatibilityFingerprint);
            await lease.Entry.SessionClient.SetModeAsync(lease.Entry.SessionId, UnattendedSessionMode, cancellationToken).ConfigureAwait(false);
        }

        var lifecycleHandle = lease.IsWarmLease
            ? new AcpSessionHandle(lease.Entry.SessionId, true, default)
            : lease.Entry.SessionHandle;
        yield return QoderCliAcpMessageMapper.CreateSessionLifecycleMessage(lifecycleHandle);

        var shouldEvictAnonymous = options.SessionId is null && !poolSettings.KeepAnonymousSessions;
        var faulted = false;
        await lease.Entry.ExecutionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await foreach (var message in ProviderErrorAutoRetryCoordinator.ExecuteAsync(
                               prompt,
                               options.ProviderErrorAutoRetry,
                               retryPrompt => StreamPromptAttemptAsync(
                                   lease.Entry.SessionClient,
                                   lease.Entry.SessionId,
                                   lease.IsWarmLease || lifecycleHandle.IsResumed,
                                   retryPrompt,
                                   cancellationToken),
                               () => !string.IsNullOrWhiteSpace(lease.Entry.SessionId),
                               DelayAsync,
                               retryableTerminalType: "terminal.failed",
                               cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(message.Type, "terminal.failed", StringComparison.OrdinalIgnoreCase))
                {
                    faulted = true;
                }

                yield return message;
            }
        }
        finally
        {
            lease.Entry.ExecutionLock.Release();
            lease.IsFaulted = faulted || shouldEvictAnonymous;
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
                    ErrorMessage = "QoderCLI executable was not found. Install QoderCLI or set QoderCliOptions.ExecutablePath."
                };
            }

            var startContext = new ProcessStartContext
            {
                ExecutablePath = executablePath,
                Arguments = ["--acp"],
                WorkingDirectory = Directory.GetCurrentDirectory(),
                EnvironmentVariables = runtimeEnvironment
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
        await _poolCoordinator.DisposeAcpProviderAsync(Name).ConfigureAwait(false);
    }

    internal virtual IReadOnlyList<string> BuildCommandArguments(QoderCliOptions options)
    {
        var arguments = new List<string> { "--acp" };
        foreach (var argument in options.ExtraArguments)
        {
            var normalizedArgument = ArgumentValueNormalizer.NormalizeOptionalValue(argument);
            if (normalizedArgument is null ||
                string.Equals(normalizedArgument, "--acp", StringComparison.OrdinalIgnoreCase) ||
                PermissionBypassFlags.Any(flag => string.Equals(flag, normalizedArgument, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            arguments.Add(normalizedArgument);
        }

        return arguments;
    }

    internal virtual IReadOnlyDictionary<string, string?> BuildEnvironmentVariables(
        QoderCliOptions options,
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

    private CliPoolSettings ResolvePoolSettings(QoderCliOptions options)
    {
        return CliPoolSettings.Merge(_poolConfiguration.GetSettings(Name), options.PoolSettings);
    }

    private static string? ResolveLogicalSessionKey(string? requestedSessionId, CliPoolSettings settings)
    {
        var normalizedSessionId = ArgumentValueNormalizer.NormalizeOptionalValue(requestedSessionId);
        if (normalizedSessionId is not null)
        {
            return normalizedSessionId;
        }

        return settings.KeepAnonymousSessions ? $"qodercli:{Guid.NewGuid():N}" : null;
    }

    private async Task<PooledAcpSessionEntry> CreatePooledEntryAsync(
        QoderCliOptions options,
        string workingDirectory,
        ProcessStartContext startContext,
        CliAcpPoolRequest request,
        CancellationToken cancellationToken)
    {
        var sessionClient = CreateSessionClient(startContext);
        try
        {
            using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupCts.CancelAfter(options.StartupTimeout ?? DefaultStartupTimeout);

            await sessionClient.ConnectAsync(startupCts.Token).ConfigureAwait(false);
            await sessionClient.InitializeAsync(startupCts.Token).ConfigureAwait(false);
            var sessionHandle = await sessionClient.StartSessionAsync(
                workingDirectory,
                options.SessionId,
                options.Model,
                startupCts.Token).ConfigureAwait(false);
            await sessionClient.SetModeAsync(sessionHandle.SessionId, UnattendedSessionMode, startupCts.Token).ConfigureAwait(false);
            return new PooledAcpSessionEntry(
                Name,
                sessionHandle.SessionId,
                sessionClient,
                request.CompatibilityFingerprint,
                sessionHandle,
                request.Settings);
        }
        catch
        {
            await sessionClient.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async IAsyncEnumerable<CliMessage> ExecuteOneShotAsync(
        QoderCliOptions options,
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
        await sessionClient.SetModeAsync(sessionHandle.SessionId, UnattendedSessionMode, startupCts.Token).ConfigureAwait(false);

        yield return QoderCliAcpMessageMapper.CreateSessionLifecycleMessage(sessionHandle);

        await foreach (var message in ProviderErrorAutoRetryCoordinator.ExecuteAsync(
                           prompt,
                           options.ProviderErrorAutoRetry,
                           retryPrompt => StreamPromptAttemptAsync(
                               sessionClient,
                               sessionHandle.SessionId,
                               sessionHandle.IsResumed,
                               retryPrompt,
                               cancellationToken),
                           () => !string.IsNullOrWhiteSpace(sessionHandle.SessionId),
                           DelayAsync,
                           retryableTerminalType: "terminal.failed",
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

    private string? ResolveExecutablePath(QoderCliOptions options, IReadOnlyDictionary<string, string?> runtimeEnvironment)
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
        bool isResumedSession,
        Task<JsonElement> promptTask,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sawTerminalMessage = false;
        var sawAssistantText = false;
        var bufferedAssistantMessages = isResumedSession ? new List<BufferedAssistantChunk>() : null;
        CliMessage? terminalMessage = null;
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
                streamFailure = new InvalidOperationException($"QoderCLI stream ended unexpectedly: {ex.Message}", ex);
            }

            if (streamFailure is not null)
            {
                yield return QoderCliAcpMessageMapper.CreateTerminalFailureMessage(sessionId, streamFailure);
                yield break;
            }

            if (isResumedSession && QoderCliAcpMessageMapper.IsReplayAssistantNotification(notification))
            {
                continue;
            }

            var notificationRequestId = TryGetNotificationMetaString(notification, "ai-coding/request-id");
            var notificationTurnId = TryGetNotificationMetaString(notification, "ai-coding/turn-id");
            var notificationMessageEnd = TryGetNotificationMetaBoolean(notification, "ai-coding/message-end") == true;

            foreach (var message in QoderCliAcpMessageMapper.NormalizeNotification(notification))
            {
                if (string.Equals(message.Type, "assistant", StringComparison.OrdinalIgnoreCase) &&
                    QoderCliAcpMessageMapper.TryExtractMessageText(message.Content, out _))
                {
                    sawAssistantText = true;
                    if (isResumedSession)
                    {
                        bufferedAssistantMessages!.Add(new BufferedAssistantChunk(
                            message,
                            notificationRequestId,
                            notificationTurnId,
                            notificationMessageEnd));
                        continue;
                    }
                }

                if (IsTerminalMessage(message.Type))
                {
                    sawTerminalMessage = true;
                    if (isResumedSession)
                    {
                        terminalMessage = message;
                        break;
                    }

                    yield return message;
                    yield break;
                }

                yield return message;
            }

            if (isResumedSession && sawTerminalMessage)
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
            yield return QoderCliAcpMessageMapper.CreateTerminalFailureMessage(sessionId, promptFailure);
            yield break;
        }

        if (isResumedSession)
        {
            foreach (var resumedMessage in BuildResumedSessionMessages(
                         sessionId,
                         promptResult,
                         bufferedAssistantMessages ?? [],
                         terminalMessage))
            {
                yield return resumedMessage;
            }

            yield break;
        }

        if (sawTerminalMessage)
        {
            yield break;
        }

        foreach (var fallbackMessage in BuildFallbackMessages(sessionId, promptResult, sawAssistantText))
        {
            yield return fallbackMessage;
        }
    }

    private static async Task CancelReceiveLoopWhenPromptCompletesAsync(
        Task<JsonElement> promptTask,
        CancellationTokenSource receiveUpdatesCancellation)
    {
        try
        {
            var promptResult = await promptTask.ConfigureAwait(false);
            if (!QoderCliAcpMessageMapper.ShouldPreferPromptCompletedNotification(promptResult) &&
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
        return StreamPromptMessagesAsync(sessionClient, sessionId, isResumedSession, promptTask, cancellationToken);
    }

    private static IEnumerable<CliMessage> BuildFallbackMessages(
        string sessionId,
        JsonElement promptResult,
        bool sawAssistantText)
    {
        if (!sawAssistantText &&
            QoderCliAcpMessageMapper.TryExtractPromptResultText(promptResult, out var fallbackText) &&
            !QoderCliAcpMessageMapper.IsFailurePromptResult(promptResult))
        {
            yield return QoderCliAcpMessageMapper.CreateAssistantMessage(sessionId, fallbackText, promptResult);
        }

        yield return QoderCliAcpMessageMapper.CreateTerminalMessage(sessionId, promptResult);
    }

    private static IEnumerable<CliMessage> BuildResumedSessionMessages(
        string sessionId,
        JsonElement promptResult,
        IReadOnlyList<BufferedAssistantChunk> bufferedAssistantMessages,
        CliMessage? terminalMessage)
    {
        foreach (var assistantMessage in ResolveResumedAssistantMessages(sessionId, promptResult, bufferedAssistantMessages))
        {
            yield return assistantMessage;
        }

        if (terminalMessage is not null)
        {
            yield return terminalMessage;
            yield break;
        }

        yield return QoderCliAcpMessageMapper.CreateTerminalMessage(sessionId, promptResult);
    }

    private static IReadOnlyList<CliMessage> ResolveResumedAssistantMessages(
        string sessionId,
        JsonElement promptResult,
        IReadOnlyList<BufferedAssistantChunk> bufferedAssistantMessages)
    {
        var selectedBufferedMessages = SelectCurrentTurnAssistantMessages(bufferedAssistantMessages);
        var bufferedText = ConcatenateAssistantText(selectedBufferedMessages);
        string? promptText = null;
        var hasPromptText = !QoderCliAcpMessageMapper.IsFailurePromptResult(promptResult) &&
                            QoderCliAcpMessageMapper.TryExtractPromptResultText(promptResult, out promptText) &&
                            !string.IsNullOrWhiteSpace(promptText);

        if (selectedBufferedMessages.Count > 0)
        {
            if (hasPromptText)
            {
                if (string.Equals(bufferedText, promptText, StringComparison.Ordinal) ||
                    promptText!.Contains(bufferedText, StringComparison.Ordinal))
                {
                    return selectedBufferedMessages;
                }

                if (bufferedText.Contains(promptText!, StringComparison.Ordinal))
                {
                    return
                    [
                        QoderCliAcpMessageMapper.CreateAssistantMessage(sessionId, promptText, promptResult)
                    ];
                }
            }

            return selectedBufferedMessages;
        }

        if (hasPromptText)
        {
            return
            [
                QoderCliAcpMessageMapper.CreateAssistantMessage(sessionId, promptText, promptResult)
            ];
        }

        return [];
    }

    private static IReadOnlyList<CliMessage> SelectCurrentTurnAssistantMessages(
        IReadOnlyList<BufferedAssistantChunk> bufferedAssistantMessages)
    {
        if (bufferedAssistantMessages.Count == 0)
        {
            return [];
        }

        IReadOnlyList<BufferedAssistantChunk> selectedBufferedMessages = bufferedAssistantMessages;
        var lastTurnKey = bufferedAssistantMessages
            .Select(static chunk => CreateTurnKey(chunk.RequestId, chunk.TurnId))
            .LastOrDefault(static key => key is not null);
        if (!string.IsNullOrWhiteSpace(lastTurnKey))
        {
            var matchedMessages = bufferedAssistantMessages
                .Where(chunk => string.Equals(CreateTurnKey(chunk.RequestId, chunk.TurnId), lastTurnKey, StringComparison.Ordinal))
                .ToList();
            if (matchedMessages.Count > 0)
            {
                selectedBufferedMessages = matchedMessages;
            }
        }

        var replayTrimmedMessages = TrimReplayChunksBeforeLatestCompletedBoundary(selectedBufferedMessages);
        return replayTrimmedMessages.Select(static chunk => chunk.Message).ToList();
    }

    private static string ConcatenateAssistantText(IReadOnlyList<CliMessage> messages)
    {
        if (messages.Count == 0)
        {
            return string.Empty;
        }

        return string.Concat(messages
            .Select(static message => QoderCliAcpMessageMapper.TryExtractMessageText(message.Content, out var text) ? text : null)
            .Where(static text => !string.IsNullOrEmpty(text)));
    }

    private static string? CreateTurnKey(string? requestId, string? turnId)
    {
        if (string.IsNullOrWhiteSpace(requestId) && string.IsNullOrWhiteSpace(turnId))
        {
            return null;
        }

        return $"{requestId ?? string.Empty}::{turnId ?? string.Empty}";
    }

    private static string? TryGetNotificationMetaString(AcpNotification notification, string propertyName)
    {
        if (!string.Equals(notification.Method, "session/update", StringComparison.OrdinalIgnoreCase) ||
            notification.Parameters.ValueKind != JsonValueKind.Object ||
            !notification.Parameters.TryGetProperty("_meta", out var metaElement) ||
            metaElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return metaElement.TryGetProperty(propertyName, out var propertyElement) &&
               propertyElement.ValueKind == JsonValueKind.String
            ? propertyElement.GetString()
            : null;
    }

    private static bool? TryGetNotificationMetaBoolean(AcpNotification notification, string propertyName)
    {
        if (!string.Equals(notification.Method, "session/update", StringComparison.OrdinalIgnoreCase) ||
            notification.Parameters.ValueKind != JsonValueKind.Object ||
            !notification.Parameters.TryGetProperty("_meta", out var metaElement) ||
            metaElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return metaElement.TryGetProperty(propertyName, out var propertyElement) &&
               (propertyElement.ValueKind == JsonValueKind.True || propertyElement.ValueKind == JsonValueKind.False)
            ? propertyElement.GetBoolean()
            : null;
    }

    private static IReadOnlyList<BufferedAssistantChunk> TrimReplayChunksBeforeLatestCompletedBoundary(
        IReadOnlyList<BufferedAssistantChunk> bufferedAssistantMessages)
    {
        if (bufferedAssistantMessages.Count <= 1)
        {
            return bufferedAssistantMessages;
        }

        var lastCompletedBoundaryIndex = -1;
        for (var index = 0; index < bufferedAssistantMessages.Count - 1; index++)
        {
            if (bufferedAssistantMessages[index].MessageEnd)
            {
                lastCompletedBoundaryIndex = index;
            }
        }

        return lastCompletedBoundaryIndex >= 0
            ? bufferedAssistantMessages.Skip(lastCompletedBoundaryIndex + 1).ToList()
            : bufferedAssistantMessages;
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
}
