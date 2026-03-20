using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.Hermes;

/// <summary>
/// Implements Hermes CLI integration over a managed ACP session.
/// </summary>
public class HermesProvider : ICliProvider<HermesOptions>
{
    private static readonly string[] DefaultExecutableCandidates = ["hermes", "hermes-cli"];
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(15);

    private readonly CliExecutableResolver _executableResolver;
    private readonly CliProcessManager _processManager;
    private readonly IRuntimeEnvironmentResolver? _runtimeEnvironmentResolver;
    private readonly ConcurrentDictionary<string, CachedHermesSession> _sessions = new(StringComparer.Ordinal);
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
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _runtimeEnvironmentResolver = runtimeEnvironmentResolver;
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
        var sessionResolution = await GetOrCreateSessionAsync(
            options,
            runtimeEnvironment,
            workingDirectory,
            cancellationToken).ConfigureAwait(false);

        await sessionResolution.Session.ExecutionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            yield return sessionResolution.LifecycleMessage;

            var promptTask = sessionResolution.Session.SessionClient.SendPromptAsync(
                sessionResolution.Session.SessionId,
                prompt,
                cancellationToken);
            await foreach (var message in StreamPromptMessagesAsync(
                               sessionResolution.Session.SessionClient,
                               sessionResolution.Session.SessionId,
                               promptTask,
                               cancellationToken).ConfigureAwait(false))
            {
                yield return message;
            }

            sessionResolution.Session.Touch();
        }
        finally
        {
            sessionResolution.Session.ExecutionLock.Release();
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var sessions = _sessions.Values.Distinct().ToArray();
        _sessions.Clear();
        foreach (var session in sessions)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal virtual IReadOnlyList<string> BuildCommandArguments(HermesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var arguments = options.Arguments
            .Where(static argument => !string.IsNullOrWhiteSpace(argument))
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

    private async Task<SessionResolution> GetOrCreateSessionAsync(
        HermesOptions options,
        IReadOnlyDictionary<string, string?> runtimeEnvironment,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.SessionId) &&
            _sessions.TryGetValue(options.SessionId, out var existingSession))
        {
            existingSession.Touch();
            return new SessionResolution(
                existingSession,
                HermesAcpMessageMapper.CreateSessionReusedMessage(existingSession.SessionId, options.SessionId));
        }

        var executablePath = ResolveExecutablePath(options, runtimeEnvironment)
            ?? throw new FileNotFoundException(
                "Unable to locate the Hermes executable. Set HermesOptions.ExecutablePath or ensure 'hermes' is on PATH.");
        var startContext = new ProcessStartContext
        {
            ExecutablePath = executablePath,
            Arguments = BuildCommandArguments(options),
            WorkingDirectory = workingDirectory,
            EnvironmentVariables = BuildEnvironmentVariables(options, runtimeEnvironment)
        };

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

            var session = new CachedHermesSession(sessionHandle.SessionId, sessionClient);
            RegisterSession(session, options.SessionId);
            return new SessionResolution(session, HermesAcpMessageMapper.CreateSessionLifecycleMessage(sessionHandle));
        }
        catch
        {
            await sessionClient.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void RegisterSession(CachedHermesSession session, string? alias)
    {
        if (!_sessions.TryAdd(session.SessionId, session))
        {
            throw new InvalidOperationException($"Hermes session '{session.SessionId}' has already been registered in memory.");
        }

        if (!string.IsNullOrWhiteSpace(alias) &&
            !string.Equals(alias, session.SessionId, StringComparison.Ordinal))
        {
            _sessions.TryAdd(alias, session);
        }
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

    private sealed class CachedHermesSession(string sessionId, IAcpSessionClient sessionClient) : IAsyncDisposable
    {
        public string SessionId { get; } = sessionId;

        public IAcpSessionClient SessionClient { get; } = sessionClient;

        public SemaphoreSlim ExecutionLock { get; } = new(1, 1);

        public DateTimeOffset LastUsedAt { get; private set; } = DateTimeOffset.UtcNow;

        public void Touch()
        {
            LastUsedAt = DateTimeOffset.UtcNow;
        }

        public async ValueTask DisposeAsync()
        {
            ExecutionLock.Dispose();
            await SessionClient.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed record SessionResolution(CachedHermesSession Session, CliMessage LifecycleMessage);
}
