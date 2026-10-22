using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HagiCode.Libs.Providers.Reasonix;

/// <summary>
/// Implements Reasonix CLI integration over the shared ACP session layer. Targets the Reasonix 2.x ACP contract.
/// </summary>
public class ReasonixProvider : ICliProvider<ReasonixOptions>
{
    private static readonly string[] DefaultExecutableCandidates = ["reasonix"];
    private static readonly HashSet<string> FilteredBootstrapFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "-model",
        "-m",
        "--model",
        "-dir",
        "--dir",
        "-effort",
        "--effort",
        "-budget",
        "--budget",
        "-transcript",
        "--transcript",
        "-mcp",
        "--mcp",
        "-mcp-prefix",
        "--mcp-prefix",
        "-yolo",
        "--yolo",
        "--dangerously-skip-permissions",
        "--no-proxy"
    };
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(15);

    private readonly CliExecutableResolver _executableResolver;
    private readonly CliProcessManager _processManager;
    private readonly IRuntimeEnvironmentResolver? _runtimeEnvironmentResolver;
    private readonly ILogger<ReasonixProvider> _logger;

    private sealed record BufferedAssistantChunk(
        CliMessage Message,
        string? RequestId,
        string? TurnId,
        bool MessageEnd,
        int Generation);

    private sealed record OpenedSession(AcpSessionHandle Handle, bool ReplayFilterEnabled);

    private sealed record SessionStartResult(OpenedSession? Session, Exception? Failure);

    /// <summary>
    /// Initializes a new instance of the <see cref="ReasonixProvider" /> class.
    /// </summary>
    public ReasonixProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null,
        ILogger<ReasonixProvider>? logger = null)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _runtimeEnvironmentResolver = runtimeEnvironmentResolver;
        _logger = logger ?? NullLogger<ReasonixProvider>.Instance;
    }

    /// <inheritdoc />
    public string Name => "reasonix";

    /// <inheritdoc />
    public bool IsAvailable => _executableResolver.ResolveProviderExecutable("Reasonix", DefaultExecutableCandidates).IsFound;

    /// <inheritdoc />
    public async IAsyncEnumerable<CliMessage> ExecuteAsync(
        ReasonixOptions options,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        if (options.UserInputHandler is not null)
        {
            ValidateQuestionTimeout(options);
        }

        var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        var executablePath = ResolveExecutablePath(options, runtimeEnvironment)
            ?? throw new FileNotFoundException(
                "Unable to locate the Reasonix executable. Set ReasonixOptions.ExecutablePath or ensure 'reasonix' is on PATH.");

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
            var executablePath = _executableResolver.ResolveProviderExecutable("Reasonix", DefaultExecutableCandidates, runtimeEnvironment).ResolvedPath;
            if (executablePath is null)
            {
                return new CliProviderTestResult
                {
                    ProviderName = Name,
                    Success = false,
                    ErrorMessage = "Reasonix executable was not found. Install Reasonix locally or set ReasonixOptions.ExecutablePath."
                };
            }

            var startContext = new ProcessStartContext
            {
                ExecutablePath = executablePath,
                Arguments = BuildCommandArguments(new ReasonixOptions()),
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CliProviderTestResult
            {
                ProviderName = Name,
                Success = false,
                ErrorMessage = $"Reasonix ACP startup timed out after {DefaultStartupTimeout.TotalSeconds:0} seconds."
            };
        }
        catch (Exception ex)
        {
            return new CliProviderTestResult
            {
                ProviderName = Name,
                Success = false,
                ErrorMessage = $"Reasonix ACP handshake failed: {ex.Message}"
            };
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    internal virtual IReadOnlyList<string> BuildCommandArguments(ReasonixOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var arguments = new List<string>
        {
            "acp"
        };

        // Reasonix 1.x reduced ACP bootstrap to a transport-scoped provider selector.
        AppendOption(arguments, "-model", options.Model);

        foreach (var argument in NormalizeExtraArguments(options.ExtraArguments))
        {
            arguments.Add(argument);
        }

        return arguments;
    }

    internal virtual IReadOnlyDictionary<string, string?> BuildEnvironmentVariables(
        ReasonixOptions options,
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
    protected virtual IAcpSessionClient CreateSessionClient(ProcessStartContext startContext)
    {
        return new AcpSessionClient(CreateAcpTransport(startContext));
    }

    /// <summary>
    /// Creates the ACP session client for an execution whose permission requests may be decided by
    /// <paramref name="permissionRequestHandler" />. Without a handler it returns <see cref="CreateSessionClient(ProcessStartContext)" />.
    /// </summary>
    /// <param name="startContext">The process start context.</param>
    /// <param name="permissionRequestHandler">The permission hook, or <see langword="null" /> to keep the default answers.</param>
    protected virtual IAcpSessionClient CreateSessionClient(
        ProcessStartContext startContext,
        AcpPermissionRequestHandler? permissionRequestHandler)
    {
        return permissionRequestHandler is null
            ? CreateSessionClient(startContext)
            : new AcpSessionClient(CreateAcpTransport(startContext), agentNameHint: null, permissionRequestHandler);
    }

    /// <summary>
    /// Creates the raw ACP transport used by the session client.
    /// </summary>
    protected virtual IAcpTransport CreateAcpTransport(ProcessStartContext startContext)
    {
        return new SubprocessAcpTransport(_processManager, startContext);
    }

    private async IAsyncEnumerable<CliMessage> ExecuteOneShotAsync(
        ReasonixOptions options,
        string prompt,
        string workingDirectory,
        ProcessStartContext startContext,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The question hook exists only when a handler is set; without one, every permission request keeps its default answer.
        var pendingQuestions = options.UserInputHandler is { } userInputHandler
            ? new ReasonixPendingQuestions(userInputHandler, options.QuestionTimeout, cancellationToken, _logger)
            : null;
        await using var sessionClient = CreateSessionClient(
            startContext,
            pendingQuestions is null ? null : new AcpPermissionRequestHandler(pendingQuestions.HandleAsync));
        using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupCts.CancelAfter(options.StartupTimeout ?? DefaultStartupTimeout);

        var startResult = await TryOpenSessionAsync(sessionClient, options, workingDirectory, startupCts.Token).ConfigureAwait(false);
        if (startResult.Session is not { } openedSession)
        {
            var startFailure = startResult.Failure ?? new InvalidOperationException("Reasonix ACP session did not open.");
            _logger.LogWarning(
                startFailure,
                "Reasonix ACP session start failed for model {Model} and session {SessionId}.",
                options.Model,
                options.SessionId);
            yield return ReasonixAcpMessageMapper.CreateSessionStartFailureMessage(options.SessionId, options.Model, startFailure);
            yield break;
        }

        var sessionHandle = openedSession.Handle;
        if (options.EnableYolo)
        {
            await EnableYoloAsync(sessionClient, sessionHandle, startupCts.Token).ConfigureAwait(false);
        }

        yield return ReasonixAcpMessageMapper.CreateSessionLifecycleMessage(sessionHandle);

        await foreach (var message in StreamPromptAttemptAsync(
                           sessionClient,
                           sessionHandle.SessionId,
                           openedSession.ReplayFilterEnabled,
                           prompt,
                           pendingQuestions,
                           _logger,
                           cancellationToken).ConfigureAwait(false))
        {
            yield return message;
        }
    }

    private async Task<IReadOnlyDictionary<string, string?>> ResolveRuntimeEnvironmentAsync(CancellationToken cancellationToken)
    {
        if (_runtimeEnvironmentResolver is null)
        {
            return new Dictionary<string, string?>();
        }

        return await _runtimeEnvironmentResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
    }

    private string? ResolveExecutablePath(ReasonixOptions options, IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        if (!string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            return _executableResolver.ResolveExecutablePath(options.ExecutablePath, runtimeEnvironment);
        }

        return _executableResolver.ResolveProviderExecutable("Reasonix", DefaultExecutableCandidates, runtimeEnvironment).ResolvedPath;
    }

    private static string ResolveWorkingDirectory(string? workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            return Path.GetFullPath(workingDirectory);
        }

        return Directory.GetCurrentDirectory();
    }

    private static void ValidateQuestionTimeout(ReasonixOptions options)
    {
        if (options.QuestionTimeout != Timeout.InfiniteTimeSpan && options.QuestionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.QuestionTimeout,
                "QuestionTimeout must be positive or Timeout.InfiniteTimeSpan.");
        }
    }

    private static void AppendOption(List<string> arguments, string flag, string? value)
    {
        var normalizedValue = ArgumentValueNormalizer.NormalizeOptionalValue(value);
        if (normalizedValue is null)
        {
            return;
        }

        arguments.Add(flag);
        arguments.Add(normalizedValue);
    }

    private static IReadOnlyList<string> NormalizeExtraArguments(IReadOnlyList<string> extraArguments)
    {
        var normalizedArguments = new List<string>();

        for (var index = 0; index < extraArguments.Count; index++)
        {
            var normalizedArgument = ArgumentValueNormalizer.NormalizeOptionalValue(extraArguments[index]);
            if (normalizedArgument is null)
            {
                continue;
            }

            if (string.Equals(normalizedArgument, "reasonix", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedArgument, "acp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryGetManagedFlagName(normalizedArgument, out _))
            {
                if (!normalizedArgument.Contains('=', StringComparison.Ordinal) && index + 1 < extraArguments.Count)
                {
                    index++;
                }

                continue;
            }

            normalizedArguments.Add(normalizedArgument);
        }

        return normalizedArguments;
    }

    private static bool TryGetManagedFlagName(string argument, out string? managedFlagName)
    {
        managedFlagName = null;
        var splitIndex = argument.IndexOf('=');
        var candidate = splitIndex >= 0 ? argument[..splitIndex] : argument;
        if (!FilteredBootstrapFlags.Contains(candidate))
        {
            return false;
        }

        managedFlagName = candidate;
        return true;
    }

    private static string DescribeInitializeResult(JsonElement initializeResult)
    {
        const string bootstrapMode = "Reasonix ACP bootstrap";
        if (initializeResult.ValueKind == JsonValueKind.Object)
        {
            if (initializeResult.TryGetProperty("agentInfo", out var agentInfo) &&
                agentInfo.ValueKind == JsonValueKind.Object)
            {
                var name = TryGetString(agentInfo, "name");
                var version = TryGetString(agentInfo, "version");
                if (!string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(version))
                {
                    return string.Join(
                        " ",
                        new[] { name, version, $"via {bootstrapMode}" }.Where(static value => !string.IsNullOrWhiteSpace(value)));
                }
            }

            if (initializeResult.TryGetProperty("protocolVersion", out var protocolVersion))
            {
                return $"ACP protocol {protocolVersion} via {bootstrapMode}";
            }
        }

        return $"ACP initialize succeeded via {bootstrapMode}";
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var propertyElement) &&
               propertyElement.ValueKind == JsonValueKind.String
            ? propertyElement.GetString()
            : null;
    }

    private static async Task<SessionStartResult> TryOpenSessionAsync(
        IAcpSessionClient sessionClient,
        ReasonixOptions options,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            await sessionClient.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var session = await OpenSessionAsync(sessionClient, options, workingDirectory, cancellationToken).ConfigureAwait(false);
            return new SessionStartResult(session, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SessionStartResult(null, ex);
        }
    }

    private static async Task<OpenedSession> OpenSessionAsync(
        IAcpSessionClient sessionClient,
        ReasonixOptions options,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var initializeResult = await sessionClient.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var sessionId = ArgumentValueNormalizer.NormalizeOptionalValue(options.SessionId);
        if (sessionId is not null && IsResumeAdvertised(initializeResult))
        {
            // session/resume restores the session without replaying its history, so no replay filter is needed.
            var resumeResult = await sessionClient.InvokeBootstrapMethodAsync(
                "session/resume",
                new
                {
                    sessionId,
                    cwd = workingDirectory,
                    mcpServers = Array.Empty<object>()
                },
                cancellationToken).ConfigureAwait(false);
            return new OpenedSession(
                new AcpSessionHandle(sessionId, IsResumed: true, resumeResult.Clone()),
                ReplayFilterEnabled: false);
        }

        // session/load (when a session id is set and resume is not advertised) replays history before its response.
        var handle = await sessionClient.StartSessionAsync(
            workingDirectory,
            sessionId,
            model: null,
            cancellationToken).ConfigureAwait(false);
        return new OpenedSession(handle, ReplayFilterEnabled: handle.IsResumed);
    }

    private static bool IsResumeAdvertised(JsonElement initializeResult)
    {
        return initializeResult.ValueKind == JsonValueKind.Object &&
               initializeResult.TryGetProperty("agentCapabilities", out var agentCapabilities) &&
               agentCapabilities.ValueKind == JsonValueKind.Object &&
               agentCapabilities.TryGetProperty("sessionCapabilities", out var sessionCapabilities) &&
               sessionCapabilities.ValueKind == JsonValueKind.Object &&
               sessionCapabilities.TryGetProperty("resume", out var resume) &&
               resume.ValueKind is JsonValueKind.Object or JsonValueKind.True;
    }

    private async Task EnableYoloAsync(
        IAcpSessionClient sessionClient,
        AcpSessionHandle sessionHandle,
        CancellationToken cancellationToken)
    {
        if (!HasToolApprovalOption(sessionHandle.RawResponse, "yolo"))
        {
            _logger.LogInformation(
                "Reasonix session {SessionId} does not advertise tool_approval=yolo; keeping the default approval mode.",
                sessionHandle.SessionId);
            return;
        }

        try
        {
            await sessionClient.InvokeBootstrapMethodAsync(
                "session/set_config_option",
                new
                {
                    sessionId = sessionHandle.SessionId,
                    configId = "tool_approval",
                    value = "yolo"
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Reasonix rejected tool_approval=yolo for session {SessionId}; keeping the default approval mode.",
                sessionHandle.SessionId);
        }
    }

    private static bool HasToolApprovalOption(JsonElement sessionResult, string value)
    {
        if (sessionResult.ValueKind != JsonValueKind.Object ||
            !sessionResult.TryGetProperty("configOptions", out var configOptions) ||
            configOptions.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var option in configOptions.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object ||
                !string.Equals(TryGetString(option, "id"), "tool_approval", StringComparison.Ordinal) ||
                !option.TryGetProperty("options", out var options) ||
                options.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var candidate in options.EnumerateArray())
            {
                if (string.Equals(TryGetString(candidate, "value"), value, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static async IAsyncEnumerable<CliMessage> StreamPromptMessagesAsync(
        IAcpSessionClient sessionClient,
        string sessionId,
        bool isResumedSession,
        Task<JsonElement> promptTask,
        ReasonixPendingQuestions? pendingQuestions,
        ILogger logger,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sawTerminalMessage = false;
        var sawAssistantText = false;
        var bufferedAssistantMessages = isResumedSession ? new List<BufferedAssistantChunk>() : null;
        var replayShapeFilter = isResumedSession ? new ReasonixReplayShapeFilter() : null;
        CliMessage? terminalMessage = null;
        var assistantGeneration = 0;
        var lastBufferedMessageWasAssistant = false;
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
                streamFailure = new InvalidOperationException($"Reasonix stream ended unexpectedly: {ex.Message}", ex);
            }

            if (streamFailure is not null)
            {
                yield return ReasonixAcpMessageMapper.CreateTerminalFailureMessage(sessionId, streamFailure);
                yield break;
            }

            if (isResumedSession && ReasonixAcpMessageMapper.IsReplayAssistantNotification(notification))
            {
                continue;
            }

            if (replayShapeFilter is not null && replayShapeFilter.ShouldDrop(notification))
            {
                continue;
            }

            var notificationRequestId = TryGetNotificationMetaString(notification, "ai-coding/request-id");
            var notificationTurnId = TryGetNotificationMetaString(notification, "ai-coding/turn-id");
            var notificationMessageEnd = TryGetNotificationMetaBoolean(notification, "ai-coding/message-end") == true;

            if (pendingQuestions is null &&
                ReasonixAcpMessageMapper.IsQuestionRequestNotification(notification, out var questionId, out var questionText))
            {
                logger.LogWarning(
                    "Reasonix asked question {QuestionId} ({QuestionText}) but no user-input handler is set; it is answered with its first option.",
                    questionId,
                    questionText);
            }

            foreach (var message in ReasonixAcpMessageMapper.NormalizeNotification(notification, routeQuestions: pendingQuestions is not null))
            {
                if (string.Equals(message.Type, "assistant", StringComparison.OrdinalIgnoreCase) &&
                    ReasonixAcpMessageMapper.TryExtractMessageText(message.Content, out _))
                {
                    sawAssistantText = true;
                    if (isResumedSession)
                    {
                        bufferedAssistantMessages!.Add(new BufferedAssistantChunk(
                            message,
                            notificationRequestId,
                            notificationTurnId,
                            notificationMessageEnd,
                            assistantGeneration));
                        lastBufferedMessageWasAssistant = true;
                        continue;
                    }
                }

                if (isResumedSession &&
                    lastBufferedMessageWasAssistant &&
                    !IsTerminalMessage(message.Type))
                {
                    assistantGeneration++;
                    lastBufferedMessageWasAssistant = false;
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
            yield return ReasonixAcpMessageMapper.CreateTerminalFailureMessage(sessionId, promptFailure);
            yield break;
        }

        var questionDeclined = pendingQuestions?.HasDeclinedQuestion == true;
        if (isResumedSession)
        {
            foreach (var resumedMessage in BuildResumedSessionMessages(
                         sessionId,
                         promptResult,
                         bufferedAssistantMessages ?? [],
                         terminalMessage,
                         questionDeclined))
            {
                yield return resumedMessage;
            }

            yield break;
        }

        if (sawTerminalMessage)
        {
            yield break;
        }

        foreach (var fallbackMessage in BuildFallbackMessages(sessionId, promptResult, sawAssistantText, questionDeclined))
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
            if (!ReasonixAcpMessageMapper.ShouldPreferPromptCompletedNotification(promptResult) &&
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
        ReasonixPendingQuestions? pendingQuestions,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var promptTask = sessionClient.SendPromptAsync(sessionId, prompt, cancellationToken);
        return StreamPromptMessagesAsync(
            sessionClient,
            sessionId,
            isResumedSession,
            promptTask,
            pendingQuestions,
            logger,
            cancellationToken);
    }

    private static IEnumerable<CliMessage> BuildFallbackMessages(
        string sessionId,
        JsonElement promptResult,
        bool sawAssistantText,
        bool questionDeclined)
    {
        if (!sawAssistantText &&
            ReasonixAcpMessageMapper.TryExtractPromptResultText(promptResult, out var fallbackText) &&
            !ReasonixAcpMessageMapper.IsFailurePromptResult(promptResult))
        {
            yield return ReasonixAcpMessageMapper.CreateAssistantMessage(sessionId, fallbackText, promptResult);
        }

        yield return ReasonixAcpMessageMapper.CreateTerminalMessage(sessionId, promptResult, questionDeclined);
    }

    private static IEnumerable<CliMessage> BuildResumedSessionMessages(
        string sessionId,
        JsonElement promptResult,
        IReadOnlyList<BufferedAssistantChunk> bufferedAssistantMessages,
        CliMessage? terminalMessage,
        bool questionDeclined)
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

        yield return ReasonixAcpMessageMapper.CreateTerminalMessage(sessionId, promptResult, questionDeclined);
    }

    private static IReadOnlyList<CliMessage> ResolveResumedAssistantMessages(
        string sessionId,
        JsonElement promptResult,
        IReadOnlyList<BufferedAssistantChunk> bufferedAssistantMessages)
    {
        var selectedBufferedMessages = SelectCurrentTurnAssistantMessages(bufferedAssistantMessages);
        var bufferedText = ConcatenateAssistantText(selectedBufferedMessages);
        string? promptText = null;
        var hasPromptText = !ReasonixAcpMessageMapper.IsFailurePromptResult(promptResult) &&
                            ReasonixAcpMessageMapper.TryExtractPromptResultText(promptResult, out promptText) &&
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
                        ReasonixAcpMessageMapper.CreateAssistantMessage(sessionId, promptText, promptResult)
                    ];
                }
            }

            return selectedBufferedMessages;
        }

        if (hasPromptText)
        {
            return
            [
                ReasonixAcpMessageMapper.CreateAssistantMessage(sessionId, promptText, promptResult)
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
        else
        {
            var latestGeneration = bufferedAssistantMessages.Max(static chunk => chunk.Generation);
            var latestGenerationMessages = bufferedAssistantMessages
                .Where(chunk => chunk.Generation == latestGeneration)
                .ToList();
            if (latestGenerationMessages.Count > 0)
            {
                selectedBufferedMessages = latestGenerationMessages;
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
            .Select(static message => ReasonixAcpMessageMapper.TryExtractMessageText(message.Content, out var text) ? text : null)
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

    private static bool IsTerminalMessage(string messageType)
    {
        return string.Equals(messageType, "terminal.completed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(messageType, "terminal.failed", StringComparison.OrdinalIgnoreCase);
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
}
