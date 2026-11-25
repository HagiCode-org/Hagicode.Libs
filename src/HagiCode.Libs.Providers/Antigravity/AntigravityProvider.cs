using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers;

namespace HagiCode.Libs.Providers.Antigravity;

/// <summary>
/// Implements Antigravity CLI integration (<c>agy acp --stdio</c>) over the shared ACP session layer.
/// </summary>
public class AntigravityProvider : ICliProvider<AntigravityOptions>
{
    private const string ProviderDisplayName = "Antigravity";
    private static readonly string[] DefaultExecutableCandidates = ["agy"];
    private static readonly string[] ManagedArguments = ["acp", "--stdio"];
    private const string SignInRequiredMessage =
        "Antigravity CLI requires sign-in before a session can start. Run 'agy acp --login' on this machine, then try again.";
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(45);

    private readonly CliExecutableResolver _executableResolver;
    private readonly CliProcessManager _processManager;
    private readonly IRuntimeEnvironmentResolver? _runtimeEnvironmentResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="AntigravityProvider" /> class.
    /// </summary>
    /// <param name="executableResolver">The executable resolver.</param>
    /// <param name="processManager">The process manager.</param>
    /// <param name="runtimeEnvironmentResolver">The optional runtime environment resolver.</param>
    public AntigravityProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _runtimeEnvironmentResolver = runtimeEnvironmentResolver;
    }

    /// <inheritdoc />
    public string Name => "antigravity";

    /// <inheritdoc />
    public bool IsAvailable => _executableResolver.ResolveProviderExecutable(ProviderDisplayName, DefaultExecutableCandidates).IsFound;

    /// <inheritdoc />
    public async IAsyncEnumerable<CliMessage> ExecuteAsync(
        AntigravityOptions options,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        var executablePath = ResolveExecutablePath(options, runtimeEnvironment)
            ?? throw new FileNotFoundException(
                "Unable to locate the Antigravity executable. Set AntigravityOptions.ExecutablePath or ensure 'agy' is on PATH.");

        var workingDirectory = ResolveWorkingDirectory(options.WorkingDirectory);
        var startContext = new ProcessStartContext
        {
            ExecutablePath = executablePath,
            Arguments = BuildCommandArguments(options),
            WorkingDirectory = workingDirectory,
            EnvironmentVariables = BuildEnvironmentVariables(options, runtimeEnvironment),
            Ownership = new CliProcessOwnershipRegistration { ProviderName = Name, SessionId = options.SessionId }
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
            var executablePath = _executableResolver.ResolveProviderExecutable(ProviderDisplayName, DefaultExecutableCandidates, runtimeEnvironment).ResolvedPath;
            if (executablePath is null)
            {
                return new CliProviderTestResult
                {
                    ProviderName = Name,
                    Success = false,
                    ErrorMessage = "Antigravity executable was not found. Install the Antigravity CLI ('agy') locally or set AntigravityOptions.ExecutablePath."
                };
            }

            var startContext = new ProcessStartContext
            {
                ExecutablePath = executablePath,
                Arguments = BuildCommandArguments(new AntigravityOptions()),
                WorkingDirectory = Directory.GetCurrentDirectory(),
                EnvironmentVariables = new Dictionary<string, string?>(runtimeEnvironment, StringComparer.Ordinal),
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
                ErrorMessage = $"Antigravity ACP startup timed out after {DefaultStartupTimeout.TotalSeconds:0} seconds."
            };
        }
        catch (Exception ex)
        {
            return new CliProviderTestResult
            {
                ProviderName = Name,
                Success = false,
                ErrorMessage = $"Antigravity ACP handshake failed: {ex.Message}"
            };
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    internal virtual IReadOnlyList<string> BuildCommandArguments(AntigravityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var arguments = new List<string>(ManagedArguments);
        foreach (var rawArgument in options.ExtraArguments)
        {
            var normalizedArgument = ArgumentValueNormalizer.NormalizeOptionalValue(rawArgument);
            if (normalizedArgument is null || IsManagedArgument(normalizedArgument))
            {
                continue;
            }

            arguments.Add(normalizedArgument);
        }

        return arguments;
    }

    internal virtual IReadOnlyDictionary<string, string?> BuildEnvironmentVariables(
        AntigravityOptions options,
        IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        var environment = new Dictionary<string, string?>(runtimeEnvironment, StringComparer.Ordinal);
        foreach (var entry in options.EnvironmentVariables)
        {
            environment[entry.Key] = entry.Value;
        }

        return environment;
    }

    private static bool IsManagedArgument(string argument)
    {
        return ManagedArguments.Any(managed => string.Equals(managed, argument, StringComparison.OrdinalIgnoreCase));
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
        AntigravityOptions options,
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
        await AuthenticateIfRequestedAsync(sessionClient, options, initializeResult, startupCts.Token).ConfigureAwait(false);

        var sessionHandle = await StartSessionOrExplainSignInAsync(
            sessionClient,
            workingDirectory,
            options.SessionId,
            startupCts.Token).ConfigureAwait(false);

        yield return AntigravityAcpMessageMapper.CreateSessionLifecycleMessage(sessionHandle);

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

    private async Task<IReadOnlyDictionary<string, string?>> ResolveRuntimeEnvironmentAsync(CancellationToken cancellationToken)
    {
        if (_runtimeEnvironmentResolver is null)
        {
            return new Dictionary<string, string?>();
        }

        return await _runtimeEnvironmentResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
    }

    private string? ResolveExecutablePath(AntigravityOptions options, IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        if (!string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            return _executableResolver.ResolveExecutablePath(options.ExecutablePath, runtimeEnvironment);
        }

        return _executableResolver.ResolveProviderExecutable(ProviderDisplayName, DefaultExecutableCandidates, runtimeEnvironment).ResolvedPath;
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
        const string transportMode = "via Antigravity ACP stdio";
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
                        new[] { name, version, transportMode }.Where(static value => !string.IsNullOrWhiteSpace(value)));
                }
            }

            if (initializeResult.TryGetProperty("protocolVersion", out var protocolVersion))
            {
                return $"ACP protocol {protocolVersion} {transportMode}";
            }
        }

        return $"ACP initialize succeeded {transportMode}";
    }

    private static async Task AuthenticateIfRequestedAsync(
        IAcpSessionClient sessionClient,
        AntigravityOptions options,
        JsonElement initializeResult,
        CancellationToken cancellationToken)
    {
        // Only an explicitly requested method is invoked. Antigravity advertises an interactive Google
        // sign-in, and starting that from a headless run would block, so it is left to the operator.
        var methodId = ArgumentValueNormalizer.NormalizeOptionalValue(options.AuthenticationMethod);
        if (methodId is null)
        {
            return;
        }

        var advertisedMethods = ExtractAdvertisedAuthenticationMethods(initializeResult);
        if (advertisedMethods.Count > 0 &&
            !advertisedMethods.Any(method => string.Equals(method, methodId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Antigravity authentication method '{methodId}' was requested but initialize advertised {DescribeAdvertisedMethods(advertisedMethods)}.");
        }

        try
        {
            await sessionClient.InvokeBootstrapMethodAsync(
                "authenticate",
                new { methodId },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Antigravity authentication with method '{methodId}' failed: {ex.Message}", ex);
        }
    }

    private static async Task<AcpSessionHandle> StartSessionOrExplainSignInAsync(
        IAcpSessionClient sessionClient,
        string workingDirectory,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await sessionClient.StartSessionAsync(workingDirectory, sessionId, null, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (IsAuthenticationRequired(ex))
        {
            throw new InvalidOperationException(SignInRequiredMessage, ex);
        }
    }

    private static bool IsAuthenticationRequired(Exception exception)
    {
        return exception.Message.Contains("Authentication required", StringComparison.OrdinalIgnoreCase);
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
                streamFailure = new InvalidOperationException($"Antigravity stream ended unexpectedly: {ex.Message}", ex);
            }

            if (streamFailure is not null)
            {
                yield return AntigravityAcpMessageMapper.CreateTerminalFailureMessage(sessionId, streamFailure);
                yield break;
            }

            if (isResumedSession && AntigravityAcpMessageMapper.IsReplayAssistantNotification(notification))
            {
                continue;
            }

            foreach (var message in AntigravityAcpMessageMapper.NormalizeNotification(notification))
            {
                if (string.Equals(message.Type, "assistant", StringComparison.OrdinalIgnoreCase) &&
                    AntigravityAcpMessageMapper.TryExtractMessageText(message.Content, out _))
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
            yield return AntigravityAcpMessageMapper.CreateTerminalFailureMessage(sessionId, promptFailure);
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
            if (!AntigravityAcpMessageMapper.ShouldPreferPromptCompletedNotification(promptResult) &&
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
            AntigravityAcpMessageMapper.TryExtractPromptResultText(promptResult, out var fallbackText) &&
            !AntigravityAcpMessageMapper.IsFailurePromptResult(promptResult))
        {
            yield return AntigravityAcpMessageMapper.CreateAssistantMessage(sessionId, fallbackText, promptResult);
        }

        yield return AntigravityAcpMessageMapper.CreateTerminalMessage(sessionId, promptResult);
    }

    private static IEnumerable<CliMessage> BuildResumedSessionMessages(
        string sessionId,
        JsonElement promptResult,
        IReadOnlyList<CliMessage> bufferedAssistantMessages,
        CliMessage? terminalMessage)
    {
        if (!AntigravityAcpMessageMapper.IsFailurePromptResult(promptResult) &&
            AntigravityAcpMessageMapper.TryExtractPromptResultText(promptResult, out var promptText))
        {
            yield return AntigravityAcpMessageMapper.CreateAssistantMessage(sessionId, promptText, promptResult);
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

        yield return AntigravityAcpMessageMapper.CreateTerminalMessage(sessionId, promptResult);
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
