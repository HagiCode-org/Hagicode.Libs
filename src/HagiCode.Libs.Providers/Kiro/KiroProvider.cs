using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.Kiro;

/// <summary>
/// Implements Kiro CLI integration over a minimal ACP session layer.
/// </summary>
public class KiroProvider : ICliProvider<KiroOptions>
{
    private static readonly string[] DefaultExecutableCandidates = ["kiro", "kiro-cli"];
    private const string DefaultBootstrapMethod = "authenticate";
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(15);

    private readonly CliExecutableResolver _executableResolver;
    private readonly CliProcessManager _processManager;
    private readonly IRuntimeEnvironmentResolver? _runtimeEnvironmentResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="KiroProvider" /> class.
    /// </summary>
    /// <param name="executableResolver">The executable resolver.</param>
    /// <param name="processManager">The process manager.</param>
    /// <param name="runtimeEnvironmentResolver">The optional runtime environment resolver.</param>
    public KiroProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _runtimeEnvironmentResolver = runtimeEnvironmentResolver;
    }

    /// <inheritdoc />
    public string Name => "kiro";

    /// <inheritdoc />
    public bool IsAvailable => _executableResolver.ResolveFirstAvailablePath(DefaultExecutableCandidates) is not null;

    /// <inheritdoc />
    public async IAsyncEnumerable<CliMessage> ExecuteAsync(
        KiroOptions options,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        var executablePath = ResolveExecutablePath(options, runtimeEnvironment)
            ?? throw new FileNotFoundException(
                "Unable to locate the Kiro executable. Set KiroOptions.ExecutablePath or ensure 'kiro' or 'kiro-cli' is on PATH.");

        var workingDirectory = ResolveWorkingDirectory(options.WorkingDirectory);
        var startContext = new ProcessStartContext
        {
            ExecutablePath = executablePath,
            Arguments = BuildCommandArguments(options),
            WorkingDirectory = workingDirectory,
            EnvironmentVariables = BuildEnvironmentVariables(options, runtimeEnvironment)
        };

        await using var sessionClient = CreateSessionClient(startContext);
        using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupCts.CancelAfter(options.StartupTimeout ?? DefaultStartupTimeout);

        await sessionClient.ConnectAsync(startupCts.Token).ConfigureAwait(false);
        var initializeResult = await sessionClient.InitializeAsync(startupCts.Token).ConfigureAwait(false);
        await EnsureBootstrapAsync(sessionClient, options, initializeResult, startupCts.Token).ConfigureAwait(false);

        var sessionHandle = await sessionClient.StartSessionAsync(
            workingDirectory,
            options.SessionId,
            options.Model,
            startupCts.Token).ConfigureAwait(false);

        yield return KiroAcpMessageMapper.CreateSessionLifecycleMessage(sessionHandle);

        var promptTask = sessionClient.SendPromptAsync(sessionHandle.SessionId, prompt, cancellationToken);
        await foreach (var message in StreamPromptMessagesAsync(
                           sessionClient,
                           sessionHandle.SessionId,
                           sessionHandle.IsResumed,
                           promptTask,
                           cancellationToken).ConfigureAwait(false))
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
                    ErrorMessage = "Kiro executable was not found. Install Kiro or ensure 'kiro'/'kiro-cli' is on PATH."
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
            if (TryCreatePingAuthenticationFailure(initializeResult, out var authenticationFailure))
            {
                return new CliProviderTestResult
                {
                    ProviderName = Name,
                    Success = false,
                    ErrorMessage = authenticationFailure
                };
            }

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
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal virtual IReadOnlyList<string> BuildCommandArguments(KiroOptions options)
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
        KiroOptions options,
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

    private async Task<IReadOnlyDictionary<string, string?>> ResolveRuntimeEnvironmentAsync(CancellationToken cancellationToken)
    {
        if (_runtimeEnvironmentResolver is null)
        {
            return new Dictionary<string, string?>();
        }

        return await _runtimeEnvironmentResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureBootstrapAsync(
        IAcpSessionClient sessionClient,
        KiroOptions options,
        JsonElement initializeResult,
        CancellationToken cancellationToken)
    {
        var bootstrapRequest = ResolveBootstrapRequest(options, initializeResult);
        if (bootstrapRequest is null)
        {
            return;
        }

        JsonElement bootstrapResult;
        try
        {
            bootstrapResult = await sessionClient.InvokeBootstrapMethodAsync(
                bootstrapRequest.Method,
                bootstrapRequest.Parameters,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Kiro bootstrap failed during authentication: {ex.Message}", ex);
        }

        if (TryGetBootstrapFailureMessage(bootstrapResult, out var failureMessage))
        {
            throw new InvalidOperationException($"Kiro bootstrap failed during authentication: {failureMessage}");
        }
    }

    private static BootstrapRequest? ResolveBootstrapRequest(KiroOptions options, JsonElement initializeResult)
    {
        var explicitBootstrapMethod = ArgumentValueNormalizer.NormalizeOptionalValue(options.BootstrapMethod);
        var advertisedAuthMethods = GetAdvertisedAuthMethods(initializeResult);
        var actionableAuthMethods = GetActionableAuthMethods(advertisedAuthMethods);
        var alreadyAuthenticated = IsAlreadyAuthenticated(initializeResult);
        var hasExplicitAuthentication = HasExplicitAuthenticationOptions(options);

        if (explicitBootstrapMethod is null && alreadyAuthenticated && !hasExplicitAuthentication)
        {
            return null;
        }

        if (explicitBootstrapMethod is not null)
        {
            var selectedExplicitMethodId = ResolveAuthenticationMethodId(options, actionableAuthMethods);
            var explicitParameters = BuildBootstrapParameters(options, selectedExplicitMethodId);
            return new BootstrapRequest(explicitBootstrapMethod, explicitParameters);
        }

        if (hasExplicitAuthentication)
        {
            var selectedExplicitMethodId = ResolveAuthenticationMethodId(options, actionableAuthMethods);
            var explicitParameters = BuildBootstrapParameters(options, selectedExplicitMethodId);
            return new BootstrapRequest(DefaultBootstrapMethod, explicitParameters);
        }

        if (!ShouldAutoBootstrapFromInitialize(initializeResult, actionableAuthMethods))
        {
            return null;
        }

        var selectedMethodId = ResolveAuthenticationMethodId(options, actionableAuthMethods);
        var parameters = BuildBootstrapParameters(options, selectedMethodId);
        return new BootstrapRequest(explicitBootstrapMethod ?? DefaultBootstrapMethod, parameters);
    }

    private static bool HasExplicitAuthenticationOptions(KiroOptions options)
    {
        return ArgumentValueNormalizer.NormalizeOptionalValue(options.AuthenticationMethod) is not null ||
               ArgumentValueNormalizer.NormalizeOptionalValue(options.AuthenticationToken) is not null ||
               options.AuthenticationInfo.Count > 0 ||
               ArgumentValueNormalizer.NormalizeOptionalValue(options.BootstrapMethod) is not null ||
               options.BootstrapParameters.Count > 0;
    }

    private static bool HasAuthenticationChallenge(JsonElement initializeResult)
    {
        return TryGetBoolean(initializeResult, "authRequired") == true ||
               TryGetBoolean(initializeResult, "authenticationRequired") == true ||
               TryGetBoolean(initializeResult, "requiresAuthentication") == true;
    }

    private static bool IsAlreadyAuthenticated(JsonElement initializeResult)
    {
        return TryGetBoolean(initializeResult, "isAuthenticated") == true ||
               TryGetBoolean(initializeResult, "authenticated") == true;
    }

    private static string? ResolveAuthenticationMethodId(KiroOptions options, IReadOnlyList<string> advertisedAuthMethods)
    {
        var preferredMethod = ArgumentValueNormalizer.NormalizeOptionalValue(options.AuthenticationMethod);
        if (preferredMethod is null)
        {
            return advertisedAuthMethods.FirstOrDefault();
        }

        if (advertisedAuthMethods.Count == 0)
        {
            return preferredMethod;
        }

        return advertisedAuthMethods.FirstOrDefault(method => string.Equals(method, preferredMethod, StringComparison.OrdinalIgnoreCase)) ??
               preferredMethod;
    }

    private static object? BuildBootstrapParameters(KiroOptions options, string? methodId)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in options.BootstrapParameters)
        {
            parameters[entry.Key] = entry.Value;
        }

        var authenticationInfo = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in options.AuthenticationInfo)
        {
            authenticationInfo[entry.Key] = entry.Value;
        }

        var token = ArgumentValueNormalizer.NormalizeOptionalValue(options.AuthenticationToken);
        if (token is not null)
        {
            authenticationInfo["token"] = token;
        }

        if (methodId is not null && !parameters.ContainsKey("methodId"))
        {
            parameters["methodId"] = methodId;
        }

        if (authenticationInfo.Count > 0 && !parameters.ContainsKey("methodInfo"))
        {
            parameters["methodInfo"] = authenticationInfo;
        }

        return parameters.Count == 0 ? null : parameters;
    }

    private static IReadOnlyList<string> GetAdvertisedAuthMethods(JsonElement initializeResult)
    {
        if (initializeResult.ValueKind != JsonValueKind.Object ||
            !initializeResult.TryGetProperty("authMethods", out var authMethodsElement) ||
            authMethodsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var methods = new List<string>();
        foreach (var authMethod in authMethodsElement.EnumerateArray())
        {
            switch (authMethod.ValueKind)
            {
                case JsonValueKind.String when !string.IsNullOrWhiteSpace(authMethod.GetString()):
                    methods.Add(authMethod.GetString()!);
                    break;
                case JsonValueKind.Object:
                {
                    var methodId = TryGetString(authMethod, "id") ??
                                   TryGetString(authMethod, "methodId") ??
                                   TryGetString(authMethod, "name") ??
                                   TryGetString(authMethod, "type");
                    if (!string.IsNullOrWhiteSpace(methodId))
                    {
                        methods.Add(methodId!);
                    }

                    break;
                }
            }
        }

        return methods;
    }

    private static string DescribeAdvertisedMethods(IReadOnlyList<string> advertisedMethods)
    {
        return advertisedMethods.Count == 0
            ? "no advertised methods"
            : string.Join(", ", advertisedMethods);
    }

    private static IReadOnlyList<string> GetActionableAuthMethods(IReadOnlyList<string> advertisedAuthMethods)
    {
        return advertisedAuthMethods
            .Where(methodId => !IsInformationalLocalLoginMethod(methodId))
            .ToArray();
    }

    private static bool ShouldAutoBootstrapFromInitialize(
        JsonElement initializeResult,
        IReadOnlyList<string> actionableAuthMethods)
    {
        if (actionableAuthMethods.Count == 0)
        {
            return false;
        }

        return HasAuthenticationChallenge(initializeResult);
    }

    private static bool TryCreatePingAuthenticationFailure(JsonElement initializeResult, out string? message)
    {
        message = null;

        if (IsAlreadyAuthenticated(initializeResult))
        {
            return false;
        }

        var actionableAuthMethods = GetActionableAuthMethods(GetAdvertisedAuthMethods(initializeResult));
        if (!HasAuthenticationChallenge(initializeResult) && actionableAuthMethods.Count == 0)
        {
            return false;
        }

        if (actionableAuthMethods.Count == 0)
        {
            return false;
        }

        message = $"Kiro initialize succeeded but requires authentication ({DescribeAdvertisedMethods(actionableAuthMethods)}). Use ExecuteAsync with KiroOptions authentication settings.";
        return true;
    }

    private static bool IsInformationalLocalLoginMethod(string methodId)
    {
        return string.Equals(methodId, "kiro-login", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetBootstrapFailureMessage(JsonElement bootstrapResult, out string? message)
    {
        message = null;

        if (bootstrapResult.ValueKind == JsonValueKind.False)
        {
            message = "bootstrap RPC returned false.";
            return true;
        }

        if (bootstrapResult.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var propertyName in new[] { "accepted", "authenticated", "success", "ok" })
        {
            if (TryGetBoolean(bootstrapResult, propertyName) == false)
            {
                message = TryGetString(bootstrapResult, "message") ??
                          TryGetString(bootstrapResult, "error") ??
                          TryGetString(bootstrapResult, "reason") ??
                          $"{propertyName}=false";
                return true;
            }
        }

        return false;
    }

    private string? ResolveExecutablePath(KiroOptions options, IReadOnlyDictionary<string, string?> runtimeEnvironment)
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
        const string bootstrapMode = "Kiro ACP bootstrap";
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

    private static bool? TryGetBoolean(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var propertyElement) &&
               (propertyElement.ValueKind == JsonValueKind.True || propertyElement.ValueKind == JsonValueKind.False)
            ? propertyElement.GetBoolean()
            : null;
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
                streamFailure = new InvalidOperationException($"Kiro stream ended unexpectedly: {ex.Message}", ex);
            }

            if (streamFailure is not null)
            {
                yield return KiroAcpMessageMapper.CreateTerminalFailureMessage(sessionId, streamFailure);
                yield break;
            }

            if (isResumedSession && KiroAcpMessageMapper.IsReplayAssistantNotification(notification))
            {
                continue;
            }

            foreach (var message in KiroAcpMessageMapper.NormalizeNotification(notification))
            {
                if (string.Equals(message.Type, "assistant", StringComparison.OrdinalIgnoreCase) &&
                    KiroAcpMessageMapper.TryExtractMessageText(message.Content, out _))
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
            yield return KiroAcpMessageMapper.CreateTerminalFailureMessage(sessionId, promptFailure);
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
            if (!KiroAcpMessageMapper.ShouldPreferPromptCompletedNotification(promptResult) &&
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

    private static IEnumerable<CliMessage> BuildFallbackMessages(
        string sessionId,
        JsonElement promptResult,
        bool sawAssistantText)
    {
        if (!sawAssistantText &&
            KiroAcpMessageMapper.TryExtractPromptResultText(promptResult, out var fallbackText) &&
            !KiroAcpMessageMapper.IsFailurePromptResult(promptResult))
        {
            yield return KiroAcpMessageMapper.CreateAssistantMessage(sessionId, fallbackText, promptResult);
        }

        yield return KiroAcpMessageMapper.CreateTerminalMessage(sessionId, promptResult);
    }

    private static IEnumerable<CliMessage> BuildResumedSessionMessages(
        string sessionId,
        JsonElement promptResult,
        IReadOnlyList<CliMessage> bufferedAssistantMessages,
        CliMessage? terminalMessage)
    {
        if (!KiroAcpMessageMapper.IsFailurePromptResult(promptResult) &&
            KiroAcpMessageMapper.TryExtractPromptResultText(promptResult, out var promptText) &&
            !string.IsNullOrWhiteSpace(promptText))
        {
            yield return KiroAcpMessageMapper.CreateAssistantMessage(sessionId, promptText, promptResult);
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

        yield return KiroAcpMessageMapper.CreateTerminalMessage(sessionId, promptResult);
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

    private sealed record BootstrapRequest(string Method, object? Parameters);
}
