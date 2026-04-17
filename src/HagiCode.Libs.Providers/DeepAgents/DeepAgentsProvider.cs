using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Pooling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HagiCode.Libs.Providers.DeepAgents;

/// <summary>
/// Implements DeepAgents ACP integration over the shared ACP session layer.
/// </summary>
public class DeepAgentsProvider : ICliProvider<DeepAgentsOptions>
{
    private static readonly HashSet<string> ManagedFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--name", "-n",
        "--description", "-d",
        "--model", "-m",
        "--workspace", "-w",
        "--skills", "-s",
        "--memory"
    };

    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(15);

    private readonly CliExecutableResolver _executableResolver;
    private readonly CliProcessManager _processManager;
    private readonly IRuntimeEnvironmentResolver? _runtimeEnvironmentResolver;
    private readonly CliProviderPoolCoordinator _poolCoordinator;
    private readonly CliProviderPoolConfigurationRegistry _poolConfiguration;
    private readonly DeepAgentsLaunchResolver _launchResolver;
    private readonly ILogger<DeepAgentsProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeepAgentsProvider" /> class.
    /// </summary>
    public DeepAgentsProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null,
        CliProviderPoolCoordinator? poolCoordinator = null,
        CliProviderPoolConfigurationRegistry? poolConfiguration = null,
        ILogger<DeepAgentsProvider>? logger = null)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _runtimeEnvironmentResolver = runtimeEnvironmentResolver;
        _poolCoordinator = poolCoordinator ?? new CliProviderPoolCoordinator();
        _poolConfiguration = poolConfiguration ?? new CliProviderPoolConfigurationRegistry();
        _launchResolver = new DeepAgentsLaunchResolver(_executableResolver);
        _logger = logger ?? NullLogger<DeepAgentsProvider>.Instance;
    }

    /// <inheritdoc />
    public string Name => "deepagents";

    /// <inheritdoc />
    public bool IsAvailable => _launchResolver.Resolve(new DeepAgentsOptions(), [], new Dictionary<string, string?>()) is not null;

    /// <inheritdoc />
    public async IAsyncEnumerable<CliMessage> ExecuteAsync(
        DeepAgentsOptions options,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        var workingDirectory = ResolveWorkingDirectory(options);
        var startContext = CreateStartContextWithLogging(options, runtimeEnvironment, workingDirectory)
            ?? throw new FileNotFoundException(
                "Unable to locate a DeepAgents launcher. Set DeepAgentsOptions.ExecutablePath, install 'deepagents-cli', or ensure 'deepagents' or 'uvx' is available.");

        var compatibilityFingerprint = CliPoolFingerprintBuilder.Build(
            startContext.ExecutablePath,
            workingDirectory,
            startContext.Arguments,
            startContext.EnvironmentVariables);
        var poolSettings = ResolvePoolSettings(options);
        if (!poolSettings.Enabled)
        {
            await foreach (var message in ExecuteOneShotAsync(options, prompt, workingDirectory, startContext, cancellationToken).ConfigureAwait(false))
            {
                yield return message;
            }

            yield break;
        }

        var logicalSessionKey = ResolveLogicalSessionKey(options.SessionId, compatibilityFingerprint, poolSettings);
        var request = new CliAcpPoolRequest(Name, logicalSessionKey, compatibilityFingerprint, poolSettings);

        await using var lease = await _poolCoordinator.AcquireAcpSessionAsync(
            request,
            ct => CreatePooledEntryAsync(options, workingDirectory, startContext, request, ct),
            cancellationToken).ConfigureAwait(false);

        var supportsSessionLoad = await SupportsSessionLoadAsync(lease.Entry.SessionClient, cancellationToken).ConfigureAwait(false);
        if (lease.IsWarmLease)
        {
            if (supportsSessionLoad)
            {
                var normalizedHandle = await lease.Entry.SessionClient.StartSessionAsync(
                    workingDirectory,
                    lease.Entry.SessionId,
                    model: null,
                    cancellationToken).ConfigureAwait(false);
                lease.Entry.RefreshSession(normalizedHandle, request.CompatibilityFingerprint);
            }
            else
            {
                lease.Entry.Touch();
            }

            await EnsureModeAsync(lease.Entry.SessionClient, lease.Entry.SessionId, options.ModeId, cancellationToken).ConfigureAwait(false);
        }

        var lifecycleHandle = lease.IsWarmLease
            ? new AcpSessionHandle(lease.Entry.SessionId, true, default)
            : lease.Entry.SessionHandle;
        yield return DeepAgentsAcpMessageMapper.CreateSessionLifecycleMessage(lifecycleHandle);

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
            var startContext = CreateStartContextWithLogging(new DeepAgentsOptions(), runtimeEnvironment, Directory.GetCurrentDirectory());
            if (startContext is null)
            {
                return new CliProviderTestResult
                {
                    ProviderName = Name,
                    Success = false,
                    ErrorMessage = "DeepAgents launcher was not found. Install 'deepagents-cli', use 'deepagents --acp' or 'uvx --from deepagents-cli deepagents --acp', or set DeepAgentsOptions.ExecutablePath."
                };
            }

            await using var sessionClient = CreateSessionClient(startContext);
            using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupCts.CancelAfter(DefaultStartupTimeout);

            await sessionClient.ConnectAsync(startupCts.Token).ConfigureAwait(false);
            var initializeResult = await sessionClient.InitializeAsync(startupCts.Token).ConfigureAwait(false);

            return new CliProviderTestResult
            {
                ProviderName = Name,
                Success = true,
                Version = DescribeInitializeResult(initializeResult, startContext)
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CliProviderTestResult
            {
                ProviderName = Name,
                Success = false,
                ErrorMessage = $"DeepAgents ACP startup timed out after {DefaultStartupTimeout.TotalSeconds:0} seconds."
            };
        }
        catch (Exception ex)
        {
            return new CliProviderTestResult
            {
                ProviderName = Name,
                Success = false,
                ErrorMessage = $"DeepAgents ACP handshake failed: {ex.Message}"
            };
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _poolCoordinator.DisposeAcpProviderAsync(Name).ConfigureAwait(false);
    }

    internal virtual IReadOnlyList<string> BuildCommandArguments(DeepAgentsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var arguments = new List<string>();

        AppendOption(arguments, "--name", options.AgentName);
        AppendOption(arguments, "--description", options.AgentDescription);
        AppendOption(arguments, "--model", options.Model);

        AppendJoinedPaths(arguments, "--skills", options.SkillsDirectories);
        AppendJoinedPaths(arguments, "--memory", options.MemoryFiles);

        foreach (var argument in NormalizeExtraArguments(options.ExtraArguments))
        {
            arguments.Add(argument);
        }

        return arguments;
    }

    internal virtual IReadOnlyDictionary<string, string?> BuildEnvironmentVariables(
        DeepAgentsOptions options,
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
        return new AcpSessionClient(CreateAcpTransport(startContext), Name);
    }

    /// <summary>
    /// Creates the raw ACP transport used by the session client.
    /// </summary>
    protected virtual IAcpTransport CreateAcpTransport(ProcessStartContext startContext)
    {
        return new SubprocessAcpTransport(_processManager, startContext);
    }

    private CliPoolSettings ResolvePoolSettings(DeepAgentsOptions options)
    {
        return CliPoolSettings.Merge(_poolConfiguration.GetSettings(Name), options.PoolSettings);
    }

    private static string? ResolveLogicalSessionKey(string? requestedSessionId, string compatibilityFingerprint, CliPoolSettings settings)
    {
        var normalizedSessionId = ArgumentValueNormalizer.NormalizeOptionalValue(requestedSessionId);
        if (normalizedSessionId is not null)
        {
            _ = compatibilityFingerprint;
            return normalizedSessionId;
        }

        return settings.KeepAnonymousSessions ? $"deepagents:{Guid.NewGuid():N}" : null;
    }

    private async Task<PooledAcpSessionEntry> CreatePooledEntryAsync(
        DeepAgentsOptions options,
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
            _ = initializeResult;

            if (IsSessionResumeUnsupported(options, initializeResult))
            {
                throw new InvalidOperationException(CreateUnsupportedSessionResumeMessage());
            }

            var sessionHandle = await sessionClient.StartSessionAsync(
                workingDirectory,
                options.SessionId,
                model: null,
                startupCts.Token).ConfigureAwait(false);
            await EnsureModeAsync(sessionClient, sessionHandle.SessionId, options.ModeId, startupCts.Token).ConfigureAwait(false);
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
        DeepAgentsOptions options,
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
        _ = initializeResult;

        if (IsSessionResumeUnsupported(options, initializeResult))
        {
            throw new InvalidOperationException(CreateUnsupportedSessionResumeMessage());
        }

        var sessionHandle = await sessionClient.StartSessionAsync(
            workingDirectory,
            options.SessionId,
            model: null,
            startupCts.Token).ConfigureAwait(false);
        await EnsureModeAsync(sessionClient, sessionHandle.SessionId, options.ModeId, startupCts.Token).ConfigureAwait(false);

        yield return DeepAgentsAcpMessageMapper.CreateSessionLifecycleMessage(sessionHandle);

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

    private ProcessStartContext? CreateStartContext(
        DeepAgentsOptions options,
        IReadOnlyDictionary<string, string?> runtimeEnvironment,
        string workingDirectory)
    {
        var managedArguments = BuildCommandArguments(options);
        var launcher = _launchResolver.Resolve(options, managedArguments, runtimeEnvironment);
        if (launcher is null)
        {
            return null;
        }

        return new ProcessStartContext
        {
            ExecutablePath = launcher.ExecutablePath,
            Arguments = launcher.Arguments,
            WorkingDirectory = workingDirectory,
            EnvironmentVariables = BuildEnvironmentVariables(options, runtimeEnvironment),
            Ownership = new CliProcessOwnershipRegistration { ProviderName = Name }
        };
    }

    private ProcessStartContext? CreateStartContextWithLogging(
        DeepAgentsOptions options,
        IReadOnlyDictionary<string, string?> runtimeEnvironment,
        string workingDirectory)
    {
        var startContext = CreateStartContext(options, runtimeEnvironment, workingDirectory);
        if (startContext is null)
        {
            return null;
        }

        _logger.LogInformation(
            "DeepAgents CLI launch prepared. executablePath={ExecutablePath}, argvRaw={ArgvRaw}, commandPreview={CommandPreview}, workingDirectory={WorkingDirectory}",
            startContext.ExecutablePath,
            System.Text.Json.JsonSerializer.Serialize(startContext.Arguments),
            CommandPreviewFormatter.Format(startContext.ExecutablePath, startContext.Arguments),
            startContext.WorkingDirectory ?? "(none)");

        return startContext;
    }

    private static string ResolveWorkingDirectory(DeepAgentsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var workspaceRoot = ResolveWorkspaceArgumentValue(options);
        if (workspaceRoot is not null)
        {
            return workspaceRoot;
        }

        return Directory.GetCurrentDirectory();
    }

    private static string? ResolveWorkspaceArgumentValue(DeepAgentsOptions options)
    {
        var workspaceRoot = ArgumentValueNormalizer.NormalizeOptionalValue(options.WorkspaceRoot)
                            ?? ArgumentValueNormalizer.NormalizeOptionalValue(options.WorkingDirectory);
        return workspaceRoot is null ? null : Path.GetFullPath(workspaceRoot);
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

    private static void AppendJoinedPaths(List<string> arguments, string flag, IReadOnlyList<string> paths)
    {
        var normalizedPaths = paths
            .Select(ArgumentValueNormalizer.NormalizeOptionalValue)
            .Where(static path => path is not null)
            .Cast<string>()
            .Select(Path.GetFullPath)
            .ToArray();
        if (normalizedPaths.Length == 0)
        {
            return;
        }

        arguments.Add(flag);
        arguments.Add(string.Join(",", normalizedPaths));
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

            if (string.Equals(normalizedArgument, "deepagents", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedArgument, "npx", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedArgument, "uvx", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedArgument, "--acp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(normalizedArgument, "--from", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < extraArguments.Count)
                {
                    index++;
                }

                continue;
            }

            if (normalizedArgument.StartsWith("--from=", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedArgument, "deepagents-cli", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryGetManagedFlagName(normalizedArgument, out var managedFlagName))
            {
                if (!normalizedArgument.Contains('=', StringComparison.Ordinal) && index + 1 < extraArguments.Count)
                {
                    index++;
                }

                _ = managedFlagName;
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
        if (!ManagedFlags.Contains(candidate))
        {
            return false;
        }

        managedFlagName = candidate;
        return true;
    }

    private static string DescribeInitializeResult(JsonElement initializeResult, ProcessStartContext startContext)
    {
        const string bootstrapMode = "DeepAgents ACP bootstrap";
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
                return $"ACP protocol {protocolVersion} via {bootstrapMode} ({Path.GetFileName(startContext.ExecutablePath)})";
            }
        }

        return $"ACP initialize succeeded via {bootstrapMode} ({Path.GetFileName(startContext.ExecutablePath)})";
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var propertyElement) &&
               propertyElement.ValueKind == JsonValueKind.String
            ? propertyElement.GetString()
            : null;
    }

    private static bool IsSessionResumeUnsupported(DeepAgentsOptions options, JsonElement initializeResult)
    {
        return ArgumentValueNormalizer.NormalizeOptionalValue(options.SessionId) is not null &&
               !SupportsSessionLoad(initializeResult);
    }

    private static async Task<bool> SupportsSessionLoadAsync(IAcpSessionClient sessionClient, CancellationToken cancellationToken)
    {
        var initializeResult = await sessionClient.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return SupportsSessionLoad(initializeResult);
    }

    private static bool SupportsSessionLoad(JsonElement initializeResult)
    {
        if (initializeResult.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        if (!initializeResult.TryGetProperty("agentCapabilities", out var capabilities) ||
            capabilities.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        if (!capabilities.TryGetProperty("loadSession", out var loadSessionValue) ||
            (loadSessionValue.ValueKind != JsonValueKind.True && loadSessionValue.ValueKind != JsonValueKind.False))
        {
            return true;
        }

        return loadSessionValue.GetBoolean();
    }

    private static string CreateUnsupportedSessionResumeMessage()
    {
        return "DeepAgents ACP does not advertise session/load support. Resume requests only work while the original ACP process stays warm in the shared pool; a cold restart cannot restore prior conversation state.";
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
                streamFailure = new InvalidOperationException($"DeepAgents stream ended unexpectedly: {ex.Message}", ex);
            }

            if (streamFailure is not null)
            {
                yield return DeepAgentsAcpMessageMapper.CreateTerminalFailureMessage(sessionId, streamFailure);
                yield break;
            }

            if (isResumedSession && DeepAgentsAcpMessageMapper.IsReplayAssistantNotification(notification))
            {
                continue;
            }

            foreach (var message in DeepAgentsAcpMessageMapper.NormalizeNotification(notification))
            {
                if (string.Equals(message.Type, "assistant", StringComparison.OrdinalIgnoreCase) &&
                    DeepAgentsAcpMessageMapper.TryExtractMessageText(message.Content, out _))
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
                        continue;
                    }
                }

                yield return message;
            }
        }

        if (isResumedSession && bufferedAssistantMessages is not null)
        {
            foreach (var bufferedMessage in bufferedAssistantMessages)
            {
                yield return bufferedMessage;
            }

            if (terminalMessage is not null)
            {
                yield return terminalMessage;
            }
        }

        if (!sawAssistantText || !sawTerminalMessage)
        {
            JsonElement promptResult;
            Exception? promptFailure = null;
            try
            {
                promptResult = await promptTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                promptFailure = ex;
                promptResult = default;
            }

            if (promptFailure is not null)
            {
                yield return DeepAgentsAcpMessageMapper.CreateTerminalFailureMessage(sessionId, promptFailure);
                yield break;
            }

            if (!sawAssistantText &&
                DeepAgentsAcpMessageMapper.TryExtractPromptResultText(promptResult, out var promptText) &&
                !string.IsNullOrWhiteSpace(promptText))
            {
                yield return DeepAgentsAcpMessageMapper.CreateAssistantMessage(sessionId, promptText, promptResult);
            }

            if (!sawTerminalMessage || !DeepAgentsAcpMessageMapper.ShouldPreferPromptCompletedNotification(promptResult))
            {
                yield return DeepAgentsAcpMessageMapper.CreateTerminalMessage(sessionId, promptResult);
            }
        }
    }

    private static async Task CancelReceiveLoopWhenPromptCompletesAsync(
        Task<JsonElement> promptTask,
        CancellationTokenSource receiveUpdatesCancellation)
    {
        try
        {
            await promptTask.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            receiveUpdatesCancellation.Cancel();
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
