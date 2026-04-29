using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using GitHub.Copilot.SDK;

namespace HagiCode.Libs.Providers.Copilot;

internal sealed class GitHubCopilotSdkGateway : ICopilotSdkGateway
{
    private static readonly SemaphoreSlim EnvironmentMutationLock = new(1, 1);
    private static readonly TimeSpan IdleCancellationGracePeriod = TimeSpan.FromSeconds(10);

    public async Task<ICopilotSdkRuntime> CreateRuntimeAsync(
        CopilotSdkRequest request,
        CancellationToken cancellationToken = default)
    {
        var clientOptions = BuildClientOptions(request);
        using var startupCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupCancellationTokenSource.CancelAfter(request.StartupTimeout);

        var environmentScope = await CopilotSdkEnvironmentScope.EnterAsync(request.EnvironmentVariables, cancellationToken);
        CopilotClient? client = null;
        try
        {
            client = new CopilotClient(clientOptions);
            CopilotSession session;
            CopilotSdkStreamEvent lifecycleEvent;
            if (!string.IsNullOrWhiteSpace(request.SessionId))
            {
                try
                {
                    session = await client.ResumeSessionAsync(
                        request.SessionId,
                        BuildResumeSessionConfig(request),
                        startupCancellationTokenSource.Token);
                    lifecycleEvent = new CopilotSdkStreamEvent(
                        CopilotSdkStreamEventType.SessionResumed,
                        SessionId: session.SessionId,
                        RequestedSessionId: request.SessionId);
                }
                catch (InvalidOperationException)
                {
                    session = await client.CreateSessionAsync(
                        BuildSessionConfig(request),
                        startupCancellationTokenSource.Token);
                    lifecycleEvent = new CopilotSdkStreamEvent(
                        CopilotSdkStreamEventType.SessionStarted,
                        SessionId: session.SessionId,
                        RequestedSessionId: request.SessionId);
                }
            }
            else
            {
                session = await client.CreateSessionAsync(
                    BuildSessionConfig(request),
                    startupCancellationTokenSource.Token);
                lifecycleEvent = new CopilotSdkStreamEvent(
                    CopilotSdkStreamEventType.SessionStarted,
                    SessionId: session.SessionId);
            }

            return new PooledCopilotSdkRuntime(client, session, environmentScope, lifecycleEvent);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && startupCancellationTokenSource.IsCancellationRequested)
        {
            if (client is not null)
            {
                await client.DisposeAsync();
            }

            await environmentScope.DisposeAsync();
            throw new InvalidOperationException(
                NormalizeFailureMessage(rawMessage: null, startupTimedOut: true, startupTimeout: request.StartupTimeout),
                ex);
        }
        catch
        {
            if (client is not null)
            {
                await client.DisposeAsync();
            }

            await environmentScope.DisposeAsync();
            throw;
        }
    }

    public async IAsyncEnumerable<CopilotSdkStreamEvent> SendPromptAsync(
        CopilotSdkRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var runtime = await CreateRuntimeAsync(request, cancellationToken).ConfigureAwait(false);
        await foreach (var eventData in runtime.SendPromptAsync(request, cancellationToken).ConfigureAwait(false))
        {
            yield return eventData;
        }
    }

    internal static string NormalizeFailureMessage(
        string? rawMessage,
        bool startupTimedOut = false,
        TimeSpan? startupTimeout = null,
        bool idleTimedOut = false,
        TimeSpan? idleTimeout = null)
    {
        if (startupTimedOut)
        {
            var timeout = startupTimeout ?? TimeSpan.Zero;
            return $"[startup_timeout] Copilot CLI startup timed out after {timeout.TotalSeconds:0} seconds.";
        }

        if (idleTimedOut)
        {
            var timeout = idleTimeout ?? TimeSpan.Zero;
            return $"[idle_timeout] Copilot CLI stream timed out after {timeout.TotalSeconds:0} seconds without any session events.";
        }

        if (!string.IsNullOrWhiteSpace(rawMessage) && CopilotCliCompatibility.TryExtractRejectedOption(rawMessage) is not null)
        {
            return CopilotCliCompatibility.DescribeUnknownOption(rawMessage);
        }

        return rawMessage ?? "GitHub Copilot stream failed.";
    }

    internal static SessionEventDispatchResult DispatchSessionEvent(SessionEvent evt, bool sawDelta)
    {
        var events = new List<CopilotSdkStreamEvent>();
        string? sessionId = evt switch
        {
            SessionStartEvent sessionStartEvent => sessionStartEvent.Data?.SessionId,
            _ => null
        };

        switch (evt)
        {
            case SessionStartEvent sessionStartEvent:
                events.Add(new CopilotSdkStreamEvent(
                    CopilotSdkStreamEventType.SessionStarted,
                    SessionId: FirstNonEmpty(sessionStartEvent.Data?.SessionId, sessionId)));
                break;

            case SessionResumeEvent:
                events.Add(new CopilotSdkStreamEvent(
                    CopilotSdkStreamEventType.SessionResumed,
                    SessionId: sessionId));
                break;

            case AssistantMessageDeltaEvent deltaEvent when !string.IsNullOrEmpty(deltaEvent.Data.DeltaContent):
                sawDelta = true;
                events.Add(new CopilotSdkStreamEvent(
                    CopilotSdkStreamEventType.TextDelta,
                    SessionId: sessionId,
                    Content: deltaEvent.Data.DeltaContent));
                break;

            case AssistantMessageDeltaEvent:
                break;

            case AssistantMessageEvent messageEvent when !string.IsNullOrEmpty(messageEvent.Data.Content):
                events.Add(new CopilotSdkStreamEvent(
                    CopilotSdkStreamEventType.AssistantSnapshot,
                    SessionId: sessionId,
                    Content: messageEvent.Data.Content));
                break;

            case AssistantMessageEvent:
                break;

            case AssistantReasoningEvent reasoningEvent when !string.IsNullOrEmpty(reasoningEvent.Data.Content):
                events.Add(new CopilotSdkStreamEvent(
                    CopilotSdkStreamEventType.ReasoningDelta,
                    SessionId: sessionId,
                    Content: reasoningEvent.Data.Content,
                    ReasoningId: reasoningEvent.Data.ReasoningId));
                break;

            case AssistantReasoningEvent:
                break;

            case AssistantReasoningDeltaEvent reasoningDeltaEvent when !string.IsNullOrEmpty(reasoningDeltaEvent.Data.DeltaContent):
                events.Add(new CopilotSdkStreamEvent(
                    CopilotSdkStreamEventType.ReasoningDelta,
                    SessionId: sessionId,
                    Content: reasoningDeltaEvent.Data.DeltaContent,
                    ReasoningId: reasoningDeltaEvent.Data.ReasoningId));
                break;

            case AssistantReasoningDeltaEvent:
                break;

            case AssistantStreamingDeltaEvent streamingDeltaEvent:
                events.Add(new CopilotSdkStreamEvent(
                    CopilotSdkStreamEventType.StreamingDelta,
                    SessionId: sessionId,
                    TotalResponseSizeBytes: streamingDeltaEvent.Data.TotalResponseSizeBytes));
                break;

            case ToolExecutionStartEvent toolStartEvent:
                events.Add(new CopilotSdkStreamEvent(
                    CopilotSdkStreamEventType.ToolExecutionStart,
                    SessionId: sessionId,
                    ToolName: FirstNonEmpty(toolStartEvent.Data.ToolName, toolStartEvent.Data.McpToolName),
                    ToolCallId: toolStartEvent.Data.ToolCallId));
                break;

            case ToolExecutionCompleteEvent toolCompleteEvent:
                events.Add(new CopilotSdkStreamEvent(
                    CopilotSdkStreamEventType.ToolExecutionEnd,
                    SessionId: sessionId,
                    Content: ExtractToolExecutionContent(toolCompleteEvent),
                    ToolCallId: toolCompleteEvent.Data.ToolCallId));
                break;

            case SessionErrorEvent errorEvent:
                events.Add(new CopilotSdkStreamEvent(
                    CopilotSdkStreamEventType.Error,
                    SessionId: sessionId,
                    ErrorMessage: NormalizeFailureMessage(errorEvent.Data.Message)));
                break;

            default:
                events.Add(new CopilotSdkStreamEvent(
                    CopilotSdkStreamEventType.RawEvent,
                    SessionId: sessionId,
                    Content: SerializeForDiagnostics(evt),
                    RawEventType: evt.GetType().Name));
                break;
        }

        return new SessionEventDispatchResult(sawDelta, events);
    }

    internal static CopilotClientOptions BuildClientOptions(CopilotSdkRequest request)
    {
        return new CopilotClientOptions
        {
            AutoStart = true,
            UseStdio = true,
            AutoRestart = true,
            Cwd = request.WorkingDirectory,
            CliPath = request.CliPath,
            CliUrl = request.CliUrl,
            UseLoggedInUser = request.UseLoggedInUser,
            GitHubToken = request.GitHubToken,
            CliArgs = [.. request.CliArgs]
        };
    }

    private static SessionConfig BuildSessionConfig(CopilotSdkRequest request)
    {
        return new SessionConfig
        {
            SessionId = request.SessionId,
            Model = request.Model,
            WorkingDirectory = request.WorkingDirectory,
            Streaming = true,
            OnPermissionRequest = PermissionHandler.ApproveAll
        };
    }

    private static ResumeSessionConfig BuildResumeSessionConfig(CopilotSdkRequest request)
    {
        return new ResumeSessionConfig
        {
            Model = request.Model,
            WorkingDirectory = request.WorkingDirectory,
            Streaming = true,
            OnPermissionRequest = PermissionHandler.ApproveAll
        };
    }

    internal static async IAsyncEnumerable<CopilotSdkStreamEvent> StreamProducedEventsAsync(
        Func<ChannelWriter<CopilotSdkStreamEvent>, CancellationToken, Task> producer,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<CopilotSdkStreamEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var producerTask = Task.Run(async () =>
        {
            try
            {
                await producer(channel.Writer, cancellationToken);
                channel.Writer.TryComplete();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        await foreach (var eventData in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return eventData;
        }

        await producerTask;
    }

    private static async Task CopyEventsAsync(
        IAsyncEnumerable<CopilotSdkStreamEvent> source,
        ChannelWriter<CopilotSdkStreamEvent> writer,
        CancellationToken cancellationToken)
    {
        await foreach (var eventData in source.WithCancellation(cancellationToken))
        {
            writer.TryWrite(eventData);
        }
    }

    private static string? ExtractToolExecutionContent(ToolExecutionCompleteEvent toolCompleteEvent)
    {
        if (!toolCompleteEvent.Data.Success)
        {
            return FirstNonEmpty(
                toolCompleteEvent.Data.Error?.Message,
                toolCompleteEvent.Data.Error?.Code,
                toolCompleteEvent.Data.Result?.DetailedContent,
                toolCompleteEvent.Data.Result?.Content,
                SerializeForDiagnostics(toolCompleteEvent.Data.Result?.Contents));
        }

        return FirstNonEmpty(
            toolCompleteEvent.Data.Result?.DetailedContent,
            toolCompleteEvent.Data.Result?.Content,
            SerializeForDiagnostics(toolCompleteEvent.Data.Result?.Contents));
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }

    private static string? SerializeForDiagnostics(object? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch
        {
            return value.ToString();
        }
    }

    internal sealed record SessionEventDispatchResult(bool SawDelta, IReadOnlyList<CopilotSdkStreamEvent> Events);

    private sealed class PooledCopilotSdkRuntime(
        CopilotClient client,
        CopilotSession session,
        CopilotSdkEnvironmentScope environmentScope,
        CopilotSdkStreamEvent lifecycleEvent) : ICopilotSdkRuntime
    {
        private bool _lifecycleEventSent;
        private Task? _shutdownTask;

        public string SessionId => session.SessionId;

        public async IAsyncEnumerable<CopilotSdkStreamEvent> SendPromptAsync(
            CopilotSdkRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var eventData in StreamProducedEventsAsync(async (writer, streamCancellationToken) =>
            {
                var terminalEventWritten = 0;
                var sawDelta = false;
                var lastEventUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
                using var requestCancellationTokenSource =
                    CancellationTokenSource.CreateLinkedTokenSource(streamCancellationToken);
                using var idleWatchdogCancellationTokenSource =
                    CancellationTokenSource.CreateLinkedTokenSource(streamCancellationToken);
                var forcedShutdownSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                void RecordProgress()
                {
                    Interlocked.Exchange(ref lastEventUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
                }

                bool TryWriteTerminal(CopilotSdkStreamEvent terminalEvent)
                {
                    if (Interlocked.Exchange(ref terminalEventWritten, 1) != 0)
                    {
                        return false;
                    }

                    RecordProgress();
                    writer.TryWrite(terminalEvent);
                    return true;
                }

                if (!_lifecycleEventSent)
                {
                    _lifecycleEventSent = true;
                    writer.TryWrite(lifecycleEvent);
                    RecordProgress();
                }

                using var subscription = session.On(evt =>
                {
                    var dispatchResult = DispatchSessionEvent(evt, sawDelta);
                    sawDelta = dispatchResult.SawDelta;

                    foreach (var mappedEvent in dispatchResult.Events)
                    {
                        var normalizedEvent = mappedEvent.SessionId is null || mappedEvent.RequestedSessionId is null
                            ? mappedEvent with
                            {
                                SessionId = mappedEvent.SessionId ?? SessionId,
                                RequestedSessionId = mappedEvent.RequestedSessionId ?? request.SessionId
                            }
                            : mappedEvent;

                        if (normalizedEvent.Type == CopilotSdkStreamEventType.Error)
                        {
                            Interlocked.Exchange(ref terminalEventWritten, 1);
                        }

                        RecordProgress();
                        writer.TryWrite(normalizedEvent);
                    }
                });

                var sendTask = session.SendAndWaitAsync(
                    new MessageOptions { Prompt = request.Prompt },
                    request.Timeout,
                    requestCancellationTokenSource.Token);

                var idleWatchdogTask = StartIdleWatchdogAsync(
                    request,
                    () => new DateTimeOffset(Interlocked.Read(ref lastEventUtcTicks), TimeSpan.Zero),
                    requestCancellationTokenSource,
                    sendTask,
                    forcedShutdownSignal,
                    TryWriteTerminal,
                    idleWatchdogCancellationTokenSource.Token);

                try
                {
                    var completedTask = await Task.WhenAny(sendTask, forcedShutdownSignal.Task);
                    if (ReferenceEquals(completedTask, forcedShutdownSignal.Task))
                    {
                        return;
                    }

                    await sendTask;
                }
                catch (OperationCanceledException) when (streamCancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException) when (requestCancellationTokenSource.IsCancellationRequested)
                {
                    TryWriteTerminal(new CopilotSdkStreamEvent(
                        CopilotSdkStreamEventType.Error,
                        SessionId: SessionId,
                        ErrorMessage: NormalizeFailureMessage(
                            rawMessage: null,
                            idleTimedOut: true,
                            idleTimeout: request.IdleTimeout)));
                }
                catch (Exception ex)
                {
                    TryWriteTerminal(new CopilotSdkStreamEvent(
                        CopilotSdkStreamEventType.Error,
                        SessionId: SessionId,
                        ErrorMessage: NormalizeFailureMessage(ex.Message)));
                }
                finally
                {
                    requestCancellationTokenSource.Cancel();
                    idleWatchdogCancellationTokenSource.Cancel();

                    try
                    {
                        await idleWatchdogTask;
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    if (Interlocked.CompareExchange(ref terminalEventWritten, 0, 0) == 0)
                    {
                        // Tool-call turns must always terminate so upstream Orleans and UI layers
                        // do not remain stuck in a running state after the SDK returns.
                        TryWriteTerminal(new CopilotSdkStreamEvent(
                            CopilotSdkStreamEventType.Completed,
                            SessionId: SessionId));
                    }
                }
            }, cancellationToken))
            {
                yield return eventData;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await ShutdownTransportAsync();
        }

        private Task ShutdownTransportAsync()
        {
            var existing = Volatile.Read(ref _shutdownTask);
            if (existing != null)
            {
                return existing;
            }

            var created = ShutdownTransportCoreAsync();
            var prior = Interlocked.CompareExchange(ref _shutdownTask, created, null);
            return prior ?? created;
        }

        private async Task ShutdownTransportCoreAsync()
        {
            try
            {
                await session.DisposeAsync();
            }
            catch
            {
            }

            try
            {
                await client.DisposeAsync();
            }
            catch
            {
            }

            try
            {
                await environmentScope.DisposeAsync();
            }
            catch
            {
            }
        }

        private async Task StartIdleWatchdogAsync(
            CopilotSdkRequest request,
            Func<DateTimeOffset> getLastEventUtc,
            CancellationTokenSource requestCancellationTokenSource,
            Task sendTask,
            TaskCompletionSource<bool> forcedShutdownSignal,
            Func<CopilotSdkStreamEvent, bool> tryWriteTerminal,
            CancellationToken cancellationToken)
        {
            using var watchdogTimer = new PeriodicTimer(TimeSpan.FromSeconds(5));

            try
            {
                while (await watchdogTimer.WaitForNextTickAsync(cancellationToken))
                {
                    if (sendTask.IsCompleted ||
                        requestCancellationTokenSource.IsCancellationRequested ||
                        cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    var idleFor = DateTimeOffset.UtcNow - getLastEventUtc();
                    if (idleFor < request.IdleTimeout)
                    {
                        continue;
                    }

                    tryWriteTerminal(new CopilotSdkStreamEvent(
                        CopilotSdkStreamEventType.Error,
                        SessionId: SessionId,
                        ErrorMessage: NormalizeFailureMessage(
                            rawMessage: null,
                            idleTimedOut: true,
                            idleTimeout: request.IdleTimeout)));

                    requestCancellationTokenSource.Cancel();

                    var graceCompletedTask = await Task.WhenAny(
                        sendTask,
                        Task.Delay(IdleCancellationGracePeriod, cancellationToken));

                    if (!ReferenceEquals(graceCompletedTask, sendTask))
                    {
                        await ShutdownTransportAsync();
                        forcedShutdownSignal.TrySetResult(true);
                    }

                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private sealed class CopilotSdkEnvironmentScope : IAsyncDisposable
    {
        private readonly Dictionary<string, string?> _originalValues = new(StringComparer.Ordinal);
        private bool _disposed;

        private CopilotSdkEnvironmentScope()
        {
        }

        public static async Task<CopilotSdkEnvironmentScope> EnterAsync(
            IReadOnlyDictionary<string, string?> environmentVariables,
            CancellationToken cancellationToken)
        {
            var scope = new CopilotSdkEnvironmentScope();
            await EnvironmentMutationLock.WaitAsync(cancellationToken);
            try
            {
                foreach (var entry in environmentVariables)
                {
                    scope._originalValues[entry.Key] = Environment.GetEnvironmentVariable(entry.Key);
                    Environment.SetEnvironmentVariable(entry.Key, entry.Value);
                }

                return scope;
            }
            catch
            {
                await scope.DisposeAsync();
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            foreach (var entry in _originalValues)
            {
                Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            }

            _disposed = true;
            EnvironmentMutationLock.Release();
            return ValueTask.CompletedTask;
        }
    }
}
