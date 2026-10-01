using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers;

namespace HagiCode.Libs.Providers.Junie;

/// <summary>
/// Implements Junie CLI integration over the shared ACP session layer.
/// </summary>
public class JunieProvider : ICliProvider<JunieOptions>
{
    private static readonly string[] DefaultExecutableCandidates = ["junie", "junie-cli"];
    private const string ManagedBootstrapArgument = "--acp=true";
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(45);

    private readonly CliExecutableResolver _executableResolver;
    private readonly CliProcessManager _processManager;
    private readonly IRuntimeEnvironmentResolver? _runtimeEnvironmentResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="JunieProvider" /> class.
    /// </summary>
    /// <param name="executableResolver">The executable resolver.</param>
    /// <param name="processManager">The process manager.</param>
    /// <param name="runtimeEnvironmentResolver">The optional runtime environment resolver.</param>
    public JunieProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _runtimeEnvironmentResolver = runtimeEnvironmentResolver;
    }

    /// <inheritdoc />
    public string Name => "junie";

    /// <inheritdoc />
    public bool IsAvailable => _executableResolver.ResolveFirstAvailablePath(DefaultExecutableCandidates) is not null;

    /// <inheritdoc />
    public async IAsyncEnumerable<CliMessage> ExecuteAsync(
        JunieOptions options,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        var executablePath = ResolveExecutablePath(options, runtimeEnvironment)
            ?? throw new FileNotFoundException(
                "Unable to locate the Junie executable. Set JunieOptions.ExecutablePath or ensure 'junie' or 'junie-cli' is on PATH.");

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
                    ErrorMessage = "Junie executable was not found. Install Junie locally or set JunieOptions.ExecutablePath."
                };
            }

            var startContext = new ProcessStartContext
            {
                ExecutablePath = executablePath,
                Arguments = BuildCommandArguments(new JunieOptions()),
                WorkingDirectory = Directory.GetCurrentDirectory(),
                EnvironmentVariables = SanitizeProcessEnvironment(runtimeEnvironment),
                Ownership = new CliProcessOwnershipRegistration { ProviderName = Name }
            };

            await using var sessionClient = CreateSessionClient(startContext);
            using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupCts.CancelAfter(DefaultStartupTimeout);

            await sessionClient.ConnectAsync(startupCts.Token).ConfigureAwait(false);
            var initializeResult = await sessionClient.InitializeAsync(startupCts.Token).ConfigureAwait(false);
            if (TryCreatePingFailure(initializeResult, out var failureMessage))
            {
                return new CliProviderTestResult
                {
                    ProviderName = Name,
                    Success = false,
                    ErrorMessage = failureMessage
                };
            }

            await AuthenticateIfRequiredAsync(sessionClient, new JunieOptions(), initializeResult, startupCts.Token).ConfigureAwait(false);

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
                ErrorMessage = $"Junie ACP startup timed out after {DefaultStartupTimeout.TotalSeconds:0} seconds."
            };
        }
        catch (Exception ex)
        {
            return new CliProviderTestResult
            {
                ProviderName = Name,
                Success = false,
                ErrorMessage = $"Junie ACP handshake failed: {ex.Message}"
            };
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    internal virtual IReadOnlyList<string> BuildCommandArguments(JunieOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var arguments = new List<string> { ManagedBootstrapArgument };
        AppendFlag(arguments, "--model", options.Model);
        AppendFlag(arguments, "--effort", options.Effort);
        AppendFlag(arguments, "--provider", options.Provider);
        AppendFlag(arguments, "--config-location", options.ConfigLocation);
        if (options.Brave ?? true)
        {
            arguments.Add("--brave");
        }

        for (var index = 0; index < options.ExtraArguments.Count; index++)
        {
            var normalizedArgument = ArgumentValueNormalizer.NormalizeOptionalValue(options.ExtraArguments[index]);
            if (normalizedArgument is null ||
                string.Equals(normalizedArgument, "acp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedArgument, "--acp", StringComparison.OrdinalIgnoreCase) ||
                normalizedArgument.StartsWith("--acp=", StringComparison.OrdinalIgnoreCase) ||
                IsFlagOrAssignment(normalizedArgument, "--brave"))
            {
                if (string.Equals(normalizedArgument, "--brave", StringComparison.OrdinalIgnoreCase) &&
                    index + 1 < options.ExtraArguments.Count &&
                    bool.TryParse(options.ExtraArguments[index + 1], out _))
                {
                    index++;
                }

                continue;
            }

            if ((options.Model is not null && IsFlagOrAssignment(normalizedArgument, "--model")) ||
                (options.Provider is not null && IsFlagOrAssignment(normalizedArgument, "--provider")))
            {
                if (string.Equals(normalizedArgument, "--model", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(normalizedArgument, "--provider", StringComparison.OrdinalIgnoreCase))
                {
                    index++;
                }

                continue;
            }

            arguments.Add(normalizedArgument);
        }

        return arguments;
    }

    internal virtual IReadOnlyDictionary<string, string?> BuildEnvironmentVariables(
        JunieOptions options,
        IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        var environment = SanitizeProcessEnvironment(runtimeEnvironment);
        foreach (var entry in options.EnvironmentVariables)
        {
            environment[entry.Key] = entry.Value;
        }

        var authToken = ArgumentValueNormalizer.NormalizeOptionalValue(options.AuthToken);
        if (authToken is not null)
        {
            environment["JUNIE_API_KEY"] = authToken;
        }

        var junieHome = ArgumentValueNormalizer.NormalizeOptionalValue(options.JunieHome);
        if (junieHome is not null)
        {
            environment["JUNIE_HOME"] = junieHome;
        }

        var effort = ArgumentValueNormalizer.NormalizeOptionalValue(options.Effort);
        if (effort is not null)
        {
            environment["JUNIE_EFFORT"] = effort;
        }

        environment["_JPACKAGE_LAUNCHER"] = null;
        return environment;
    }

    private static Dictionary<string, string?> SanitizeProcessEnvironment(IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        var environment = new Dictionary<string, string?>(runtimeEnvironment, StringComparer.Ordinal);
        // Null removes even an inherited value when CliProcessManager builds the subprocess environment.
        environment["_JPACKAGE_LAUNCHER"] = null;
        return environment;
    }

    private static void AppendFlag(List<string> arguments, string flag, string? value)
    {
        var normalized = ArgumentValueNormalizer.NormalizeOptionalValue(value);
        if (normalized is null)
        {
            return;
        }

        arguments.Add(flag);
        arguments.Add(normalized);
    }

    private static bool IsFlagOrAssignment(string argument, string flag)
    {
        return string.Equals(argument, flag, StringComparison.OrdinalIgnoreCase)
               || argument.StartsWith($"{flag}=", StringComparison.OrdinalIgnoreCase);
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
        JunieOptions options,
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

        yield return JunieAcpMessageMapper.CreateSessionLifecycleMessage(sessionHandle);

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

    private string? ResolveExecutablePath(JunieOptions options, IReadOnlyDictionary<string, string?> runtimeEnvironment)
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
        const string bootstrapMode = "Junie ACP bootstrap";
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

    private static bool TryCreatePingFailure(JsonElement initializeResult, out string? message)
    {
        message = null;

        var advertisedMethods = ExtractAdvertisedAuthenticationMethods(initializeResult);
        if (advertisedMethods.Count > 0 || IsAuthenticated(initializeResult))
        {
            return false;
        }

        var authenticationRequired = TryGetBoolean(initializeResult, "authRequired") == true ||
                                     TryGetBoolean(initializeResult, "authenticationRequired") == true ||
                                     TryGetBoolean(initializeResult, "requiresAuthentication") == true;
        if (!authenticationRequired)
        {
            return false;
        }

        message = "Junie initialize succeeded but did not advertise a usable authentication method. Use ExecuteAsync with JunieOptions authentication settings.";
        return true;
    }

    private static async Task AuthenticateIfRequiredAsync(
        IAcpSessionClient sessionClient,
        JunieOptions options,
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
            throw new InvalidOperationException($"Junie bootstrap failed during authentication: {ex.Message}", ex);
        }

        EnsureAuthenticationSucceeded(methodId, authenticationResult);
    }

    private static bool RequiresAuthentication(
        JunieOptions options,
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

    private static string ResolveAuthenticationMethod(JunieOptions options, IReadOnlyList<string> advertisedMethods)
    {
        var preferredMethod = ArgumentValueNormalizer.NormalizeOptionalValue(options.AuthenticationMethod);
        if (preferredMethod is not null)
        {
            if (advertisedMethods.Count > 0 &&
                !advertisedMethods.Any(method => string.Equals(method, preferredMethod, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Junie authentication method '{preferredMethod}' was requested but initialize advertised {DescribeAdvertisedMethods(advertisedMethods)}.");
            }

            return preferredMethod;
        }

        if (advertisedMethods.Count > 0)
        {
            return advertisedMethods[0];
        }

        throw new InvalidOperationException(
            "Junie authentication was requested but initialize did not advertise any authentication methods and no explicit JunieOptions.AuthenticationMethod was supplied.");
    }

    private static object BuildAuthenticationParameters(JunieOptions options, string methodId)
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
                $"Junie bootstrap failed during authentication: method '{methodId}' returned an unsupported payload kind ({authenticationResult.ValueKind}).");
        }

        if (authenticationResult.TryGetProperty("accepted", out var acceptedElement) &&
            acceptedElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            !acceptedElement.GetBoolean())
        {
            throw new InvalidOperationException(
                $"Junie bootstrap failed during authentication: method '{methodId}' was rejected.");
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

    private static bool? TryGetBoolean(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var propertyElement) &&
               propertyElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? propertyElement.GetBoolean()
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
                streamFailure = new InvalidOperationException($"Junie stream ended unexpectedly: {ex.Message}", ex);
            }

            if (streamFailure is not null)
            {
                yield return JunieAcpMessageMapper.CreateTerminalFailureMessage(sessionId, streamFailure);
                yield break;
            }

            if (isResumedSession && JunieAcpMessageMapper.IsReplayAssistantNotification(notification))
            {
                continue;
            }

            foreach (var message in JunieAcpMessageMapper.NormalizeNotification(notification))
            {
                if (string.Equals(message.Type, "assistant", StringComparison.OrdinalIgnoreCase) &&
                    JunieAcpMessageMapper.TryExtractMessageText(message.Content, out _))
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
            yield return JunieAcpMessageMapper.CreateTerminalFailureMessage(sessionId, promptFailure);
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
            if (!JunieAcpMessageMapper.ShouldPreferPromptCompletedNotification(promptResult) &&
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
            JunieAcpMessageMapper.TryExtractPromptResultText(promptResult, out var fallbackText) &&
            !JunieAcpMessageMapper.IsFailurePromptResult(promptResult))
        {
            yield return JunieAcpMessageMapper.CreateAssistantMessage(sessionId, fallbackText, promptResult);
        }

        yield return JunieAcpMessageMapper.CreateTerminalMessage(sessionId, promptResult);
    }

    private static IEnumerable<CliMessage> BuildResumedSessionMessages(
        string sessionId,
        JsonElement promptResult,
        IReadOnlyList<CliMessage> bufferedAssistantMessages,
        CliMessage? terminalMessage)
    {
        if (!JunieAcpMessageMapper.IsFailurePromptResult(promptResult) &&
            JunieAcpMessageMapper.TryExtractPromptResultText(promptResult, out var promptText))
        {
            yield return JunieAcpMessageMapper.CreateAssistantMessage(sessionId, promptText, promptResult);
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

        yield return JunieAcpMessageMapper.CreateTerminalMessage(sessionId, promptResult);
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
