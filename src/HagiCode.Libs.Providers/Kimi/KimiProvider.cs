using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Pooling;

namespace HagiCode.Libs.Providers.Kimi;

/// <summary>
/// Implements Kimi CLI integration over the shared ACP session layer.
/// </summary>
public class KimiProvider : ICliProvider<KimiOptions>
{
    private static readonly string[] DefaultExecutableCandidates = ["kimi", "kimi-cli"];
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(15);

    private readonly CliExecutableResolver _executableResolver;
    private readonly CliProcessManager _processManager;
    private readonly IRuntimeEnvironmentResolver? _runtimeEnvironmentResolver;
    private readonly CliProviderPoolCoordinator _poolCoordinator;
    private readonly CliProviderPoolConfigurationRegistry _poolConfiguration;

    /// <summary>
    /// Initializes a new instance of the <see cref="KimiProvider" /> class.
    /// </summary>
    /// <param name="executableResolver">The executable resolver.</param>
    /// <param name="processManager">The process manager.</param>
    /// <param name="runtimeEnvironmentResolver">The optional runtime environment resolver.</param>
    public KimiProvider(
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
    public string Name => "kimi";

    /// <inheritdoc />
    public bool IsAvailable => _executableResolver.ResolveFirstAvailablePath(DefaultExecutableCandidates) is not null;

    /// <inheritdoc />
    public async IAsyncEnumerable<CliMessage> ExecuteAsync(
        KimiOptions options,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        var executablePath = ResolveExecutablePath(options, runtimeEnvironment)
            ?? throw new FileNotFoundException(
                "Unable to locate the Kimi executable. Set KimiOptions.ExecutablePath or ensure 'kimi' is on PATH.");

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
            CliPoolFingerprintBuilder.Build(
                executablePath,
                workingDirectory,
                startContext.Arguments,
                startContext.EnvironmentVariables,
                options.AuthenticationMethod,
                options.AuthenticationToken,
                options.AuthenticationInfo),
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
        }

        var lifecycleHandle = lease.IsWarmLease
            ? new AcpSessionHandle(lease.Entry.SessionId, true, default)
            : lease.Entry.SessionHandle;
        yield return KimiAcpMessageMapper.CreateSessionLifecycleMessage(lifecycleHandle);

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
                    ErrorMessage = "Kimi executable was not found. Install Kimi locally or set KimiOptions.ExecutablePath."
                };
            }

            var startContext = new ProcessStartContext
            {
                ExecutablePath = executablePath,
                Arguments = ["acp"],
                WorkingDirectory = Directory.GetCurrentDirectory(),
                EnvironmentVariables = runtimeEnvironment
            };

            await using var sessionClient = CreateSessionClient(startContext);
            using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupCts.CancelAfter(DefaultStartupTimeout);

            await sessionClient.ConnectAsync(startupCts.Token).ConfigureAwait(false);
            var initializeResult = await sessionClient.InitializeAsync(startupCts.Token).ConfigureAwait(false);
            await AuthenticateIfRequiredAsync(sessionClient, new KimiOptions(), initializeResult, startupCts.Token).ConfigureAwait(false);

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

    internal virtual IReadOnlyList<string> BuildCommandArguments(KimiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var arguments = new List<string> { "acp" };
        foreach (var argument in options.ExtraArguments)
        {
            var normalizedArgument = ArgumentValueNormalizer.NormalizeOptionalValue(argument);
            if (normalizedArgument is null ||
                string.Equals(normalizedArgument, "acp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedArgument, "--acp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            arguments.Add(normalizedArgument);
        }

        return arguments;
    }

    internal virtual IReadOnlyDictionary<string, string?> BuildEnvironmentVariables(
        KimiOptions options,
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

    private CliPoolSettings ResolvePoolSettings(KimiOptions options)
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

        return settings.KeepAnonymousSessions ? $"kimi:{Guid.NewGuid():N}" : null;
    }

    private async Task<PooledAcpSessionEntry> CreatePooledEntryAsync(
        KimiOptions options,
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
            var initializeResult = await sessionClient.InitializeAsync(startupCts.Token).ConfigureAwait(false);
            await AuthenticateIfRequiredAsync(sessionClient, options, initializeResult, startupCts.Token).ConfigureAwait(false);
            var sessionHandle = await sessionClient.StartSessionAsync(
                workingDirectory,
                options.SessionId,
                options.Model,
                startupCts.Token).ConfigureAwait(false);
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
        KimiOptions options,
        string prompt,
        string workingDirectory,
        ProcessStartContext startContext,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var sessionClient = CreateSessionClient(startContext);
        using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupCts.CancelAfter(options.StartupTimeout ?? DefaultStartupTimeout);

        await sessionClient.ConnectAsync(startupCts.Token).ConfigureAwait(false);
        var initializeResult = await sessionClient.InitializeAsync(startupCts.Token).ConfigureAwait(false);
        await AuthenticateIfRequiredAsync(sessionClient, options, initializeResult, startupCts.Token).ConfigureAwait(false);

        var sessionHandle = await sessionClient.StartSessionAsync(
            workingDirectory,
            options.SessionId,
            options.Model,
            startupCts.Token).ConfigureAwait(false);

        yield return KimiAcpMessageMapper.CreateSessionLifecycleMessage(sessionHandle);

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

    private string? ResolveExecutablePath(KimiOptions options, IReadOnlyDictionary<string, string?> runtimeEnvironment)
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

    private static async Task AuthenticateIfRequiredAsync(
        IAcpSessionClient sessionClient,
        KimiOptions options,
        JsonElement initializeResult,
        CancellationToken cancellationToken)
    {
        if (!RequiresAuthentication(options, initializeResult, out var advertisedMethods))
        {
            return;
        }

        var methodId = ResolveAuthenticationMethod(options, advertisedMethods);
        var parameters = BuildAuthenticationParameters(options, methodId);

        JsonElement authenticationResult;
        try
        {
            authenticationResult = await sessionClient.InvokeBootstrapMethodAsync(
                "authenticate",
                parameters,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Kimi bootstrap failed during authentication: {ex.Message}", ex);
        }

        EnsureAuthenticationSucceeded(methodId, authenticationResult);
    }

    private static bool RequiresAuthentication(
        KimiOptions options,
        JsonElement initializeResult,
        out IReadOnlyList<string> advertisedMethods)
    {
        advertisedMethods = ExtractAdvertisedAuthenticationMethods(initializeResult);
        if (advertisedMethods.Count > 0 && !IsAuthenticated(initializeResult))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(options.AuthenticationMethod) ||
               !string.IsNullOrWhiteSpace(options.AuthenticationToken) ||
               options.AuthenticationInfo.Count > 0;
    }

    private static IReadOnlyList<string> ExtractAdvertisedAuthenticationMethods(JsonElement initializeResult)
    {
        if (initializeResult.ValueKind != JsonValueKind.Object ||
            !initializeResult.TryGetProperty("authMethods", out var authMethodsElement) ||
            authMethodsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return authMethodsElement
            .EnumerateArray()
            .Select(static element => TryGetString(element, "id"))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
    }

    private static bool IsAuthenticated(JsonElement initializeResult)
    {
        return initializeResult.ValueKind == JsonValueKind.Object &&
               initializeResult.TryGetProperty("isAuthenticated", out var authenticatedElement) &&
               authenticatedElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
               authenticatedElement.GetBoolean();
    }

    private static string ResolveAuthenticationMethod(KimiOptions options, IReadOnlyList<string> advertisedMethods)
    {
        var preferredMethod = ArgumentValueNormalizer.NormalizeOptionalValue(options.AuthenticationMethod);
        if (preferredMethod is not null)
        {
            if (advertisedMethods.Count > 0 &&
                !advertisedMethods.Any(method => string.Equals(method, preferredMethod, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Kimi authentication method '{preferredMethod}' was requested but initialize advertised {DescribeAdvertisedMethods(advertisedMethods)}.");
            }

            return preferredMethod;
        }

        if (advertisedMethods.Count > 0)
        {
            return advertisedMethods[0];
        }

        throw new InvalidOperationException(
            "Kimi authentication was requested but initialize did not advertise any authentication methods and no explicit KimiOptions.AuthenticationMethod was supplied.");
    }

    private static object BuildAuthenticationParameters(KimiOptions options, string methodId)
    {
        var methodInfo = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in options.AuthenticationInfo)
        {
            methodInfo[entry.Key] = entry.Value;
        }

        var normalizedToken = ArgumentValueNormalizer.NormalizeOptionalValue(options.AuthenticationToken);
        if (normalizedToken is not null && !methodInfo.ContainsKey("token"))
        {
            methodInfo["token"] = normalizedToken;
        }

        return methodInfo.Count == 0
            ? new { methodId }
            : new
            {
                methodId,
                methodInfo
            };
    }

    private static void EnsureAuthenticationSucceeded(string methodId, JsonElement authenticationResult)
    {
        if (authenticationResult.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Kimi bootstrap failed during authentication: method '{methodId}' returned an unsupported payload kind ({authenticationResult.ValueKind}).");
        }

        if (authenticationResult.TryGetProperty("accepted", out var acceptedElement) &&
            acceptedElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            !acceptedElement.GetBoolean())
        {
            throw new InvalidOperationException(
                $"Kimi bootstrap failed during authentication: method '{methodId}' was rejected.");
        }
    }

    private static string DescribeAdvertisedMethods(IReadOnlyList<string> advertisedMethods)
    {
        return advertisedMethods.Count == 0
            ? "no advertised methods"
            : string.Join(", ", advertisedMethods);
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
        var bufferedAssistantMessages = isResumedSession ? new List<CliMessage>() : null;
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
                streamFailure = new InvalidOperationException($"Kimi stream ended unexpectedly: {ex.Message}", ex);
            }

            if (streamFailure is not null)
            {
                yield return KimiAcpMessageMapper.CreateTerminalFailureMessage(sessionId, streamFailure);
                yield break;
            }

            if (isResumedSession && KimiAcpMessageMapper.IsReplayAssistantNotification(notification))
            {
                continue;
            }

            foreach (var message in KimiAcpMessageMapper.NormalizeNotification(notification))
            {
                if (string.Equals(message.Type, "assistant", StringComparison.OrdinalIgnoreCase) &&
                    KimiAcpMessageMapper.TryExtractMessageText(message.Content, out _))
                {
                    sawAssistantText = true;
                    if (isResumedSession)
                    {
                        bufferedAssistantMessages!.Add(message);
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
            yield return KimiAcpMessageMapper.CreateTerminalFailureMessage(sessionId, promptFailure);
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
            if (!KimiAcpMessageMapper.ShouldPreferPromptCompletedNotification(promptResult) &&
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
            KimiAcpMessageMapper.TryExtractPromptResultText(promptResult, out var fallbackText) &&
            !KimiAcpMessageMapper.IsFailurePromptResult(promptResult))
        {
            yield return KimiAcpMessageMapper.CreateAssistantMessage(sessionId, fallbackText, promptResult);
        }

        yield return KimiAcpMessageMapper.CreateTerminalMessage(sessionId, promptResult);
    }

    private static IEnumerable<CliMessage> BuildResumedSessionMessages(
        string sessionId,
        JsonElement promptResult,
        IReadOnlyList<CliMessage> bufferedAssistantMessages,
        CliMessage? terminalMessage)
    {
        if (!KimiAcpMessageMapper.IsFailurePromptResult(promptResult) &&
            KimiAcpMessageMapper.TryExtractPromptResultText(promptResult, out var promptText))
        {
            yield return KimiAcpMessageMapper.CreateAssistantMessage(sessionId, promptText, promptResult);
        }
        else
        {
            foreach (var assistantMessage in bufferedAssistantMessages)
            {
                yield return assistantMessage;
            }
        }

        if (terminalMessage is not null)
        {
            yield return terminalMessage;
            yield break;
        }

        yield return KimiAcpMessageMapper.CreateTerminalMessage(sessionId, promptResult);
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
