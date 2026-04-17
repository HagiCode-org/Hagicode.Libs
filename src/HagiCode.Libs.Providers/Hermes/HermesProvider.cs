using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Pooling;

namespace HagiCode.Libs.Providers.Hermes;

/// <summary>
/// Implements Hermes CLI integration over a managed ACP session.
/// </summary>
public class HermesProvider : ICliProvider<HermesOptions>
{
    internal const string DefaultModeId = "bypassPermissions";
    private static readonly string[] DefaultExecutableCandidates = ["hermes", "hermes-cli"];
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(15);

    private readonly CliExecutableResolver _executableResolver;
    private readonly CliProcessManager _processManager;
    private readonly IRuntimeEnvironmentResolver? _runtimeEnvironmentResolver;
    private readonly CliProviderPoolCoordinator _poolCoordinator;
    private readonly CliProviderPoolConfigurationRegistry _poolConfiguration;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="HermesProvider" /> class.
    /// </summary>
    /// <param name="executableResolver">The executable resolver.</param>
    /// <param name="processManager">The process manager.</param>
    /// <param name="runtimeEnvironmentResolver">The optional runtime environment resolver.</param>
    public HermesProvider(
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
    public string Name => "hermes";

    /// <inheritdoc />
    public bool IsAvailable => _executableResolver.ResolveFirstAvailablePath(DefaultExecutableCandidates) is not null;

    /// <inheritdoc />
    public async IAsyncEnumerable<CliMessage> ExecuteAsync(
        HermesOptions options,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        var workingDirectory = ResolveWorkingDirectory(options.WorkingDirectory);
        var executablePath = ResolveExecutablePath(options, runtimeEnvironment)
            ?? throw new FileNotFoundException(
                "Unable to locate the Hermes executable. Set HermesOptions.ExecutablePath or ensure 'hermes' is on PATH.");
        var startContext = new ProcessStartContext
        {
            ExecutablePath = executablePath,
            Arguments = BuildCommandArguments(options),
            WorkingDirectory = workingDirectory,
            EnvironmentVariables = BuildEnvironmentVariables(options, runtimeEnvironment),
            Ownership = new CliProcessOwnershipRegistration { ProviderName = Name }
        };
        var resolvedModeId = ResolveModeId(options.ModeId);

        var poolSettings = ResolvePoolSettings(options);
        if (!poolSettings.Enabled)
        {
            await foreach (var message in ExecuteOneShotAsync(options, prompt, workingDirectory, startContext, resolvedModeId, cancellationToken).ConfigureAwait(false))
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
                resolvedModeId),
            poolSettings);

        await using var lease = await _poolCoordinator.AcquireAcpSessionAsync(
            request,
            ct => CreatePooledEntryAsync(options, workingDirectory, startContext, resolvedModeId, request, ct),
            cancellationToken).ConfigureAwait(false);

        if (lease.IsWarmLease)
        {
            var normalizedHandle = await lease.Entry.SessionClient.StartSessionAsync(
                workingDirectory,
                lease.Entry.SessionId,
                options.Model,
                cancellationToken).ConfigureAwait(false);
            lease.Entry.RefreshSession(normalizedHandle, request.CompatibilityFingerprint);
            await EnsureModeAsync(lease.Entry.SessionClient, lease.Entry.SessionId, resolvedModeId, cancellationToken).ConfigureAwait(false);
        }

        var lifecycleMessage = lease.IsWarmLease
            ? HermesAcpMessageMapper.CreateSessionReusedMessage(lease.Entry.SessionId, options.SessionId)
            : HermesAcpMessageMapper.CreateSessionLifecycleMessage(lease.Entry.SessionHandle);

        var shouldEvictAnonymous = options.SessionId is null && !poolSettings.KeepAnonymousSessions;
        var faulted = false;
        await lease.Entry.ExecutionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            yield return lifecycleMessage;

            await foreach (var message in ProviderErrorAutoRetryCoordinator.ExecuteAsync(
                               prompt,
                               options.ProviderErrorAutoRetry,
                               retryPrompt => StreamPromptAttemptAsync(
                                   lease.Entry.SessionClient,
                                   lease.Entry.SessionId,
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
                    ErrorMessage = "Hermes executable was not found. Install Hermes or set HermesOptions.ExecutablePath."
                };
            }

            var startContext = new ProcessStartContext
            {
                ExecutablePath = executablePath,
                Arguments = ["acp"],
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _poolCoordinator.DisposeAcpProviderAsync(Name).ConfigureAwait(false);
    }

    internal virtual IReadOnlyList<string> BuildCommandArguments(HermesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var arguments = options.Arguments
            .Select(ArgumentValueNormalizer.NormalizeOptionalValue)
            .Where(static argument => argument is not null)
            .Cast<string>()
            .ToArray();

        return arguments.Length == 0 ? ["acp"] : arguments;
    }

    internal virtual IReadOnlyDictionary<string, string?> BuildEnvironmentVariables(
        HermesOptions options,
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
        return new AcpSessionClient(CreateAcpTransport(startContext), Name);
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

    private CliPoolSettings ResolvePoolSettings(HermesOptions options)
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

        return settings.KeepAnonymousSessions ? $"hermes:{Guid.NewGuid():N}" : null;
    }

    private async Task<PooledAcpSessionEntry> CreatePooledEntryAsync(
        HermesOptions options,
        string workingDirectory,
        ProcessStartContext startContext,
        string resolvedModeId,
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
                sessionId: null,
                options.Model,
                startupCts.Token).ConfigureAwait(false);
            await EnsureModeAsync(sessionClient, sessionHandle.SessionId, resolvedModeId, startupCts.Token).ConfigureAwait(false);

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
        HermesOptions options,
        string prompt,
        string workingDirectory,
        ProcessStartContext startContext,
        string resolvedModeId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var sessionClient = CreateSessionClient(startContext);
        using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupCts.CancelAfter(options.StartupTimeout ?? DefaultStartupTimeout);

        await sessionClient.ConnectAsync(startupCts.Token).ConfigureAwait(false);
        await sessionClient.InitializeAsync(startupCts.Token).ConfigureAwait(false);
        var sessionHandle = await sessionClient.StartSessionAsync(
            workingDirectory,
            sessionId: null,
            options.Model,
            startupCts.Token).ConfigureAwait(false);
        await EnsureModeAsync(sessionClient, sessionHandle.SessionId, resolvedModeId, startupCts.Token).ConfigureAwait(false);

        yield return HermesAcpMessageMapper.CreateSessionLifecycleMessage(sessionHandle);

        await foreach (var message in ProviderErrorAutoRetryCoordinator.ExecuteAsync(
                           prompt,
                           options.ProviderErrorAutoRetry,
                           retryPrompt => StreamPromptAttemptAsync(
                               sessionClient,
                               sessionHandle.SessionId,
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

    private string? ResolveExecutablePath(HermesOptions options, IReadOnlyDictionary<string, string?> runtimeEnvironment)
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
        const string bootstrapMode = "managed ACP bootstrap";
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

    private static async IAsyncEnumerable<CliMessage> StreamPromptMessagesAsync(
        IAcpSessionClient sessionClient,
        string sessionId,
        Task<JsonElement> promptTask,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sawTerminalMessage = false;
        var sawAssistantText = false;
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
                streamFailure = new InvalidOperationException($"Hermes stream ended unexpectedly: {ex.Message}", ex);
            }

            if (streamFailure is not null)
            {
                yield return HermesAcpMessageMapper.CreateTerminalFailureMessage(sessionId, streamFailure);
                yield break;
            }

            foreach (var message in HermesAcpMessageMapper.NormalizeNotification(notification))
            {
                if (string.Equals(message.Type, "assistant", StringComparison.OrdinalIgnoreCase) &&
                    HermesAcpMessageMapper.TryExtractMessageText(message.Content, out _))
                {
                    sawAssistantText = true;
                }

                yield return message;
                if (IsTerminalMessage(message.Type))
                {
                    sawTerminalMessage = true;
                    yield break;
                }
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
            yield return HermesAcpMessageMapper.CreateTerminalFailureMessage(sessionId, promptFailure);
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
            if (!HermesAcpMessageMapper.ShouldPreferPromptCompletedNotification(promptResult) &&
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
        string prompt,
        CancellationToken cancellationToken)
    {
        var promptTask = sessionClient.SendPromptAsync(sessionId, prompt, cancellationToken);
        return StreamPromptMessagesAsync(sessionClient, sessionId, promptTask, cancellationToken);
    }

    private static string ResolveModeId(string? modeId)
    {
        return ArgumentValueNormalizer.NormalizeOptionalValue(modeId) ?? DefaultModeId;
    }

    private static IEnumerable<CliMessage> BuildFallbackMessages(
        string sessionId,
        JsonElement promptResult,
        bool sawAssistantText)
    {
        if (!sawAssistantText &&
            HermesAcpMessageMapper.TryExtractPromptResultText(promptResult, out var fallbackText) &&
            !HermesAcpMessageMapper.IsFailurePromptResult(promptResult))
        {
            yield return HermesAcpMessageMapper.CreateAssistantMessage(sessionId, fallbackText, promptResult);
        }

        yield return HermesAcpMessageMapper.CreateTerminalMessage(sessionId, promptResult);
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
