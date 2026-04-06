using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using GitHub.Copilot.SDK;

namespace HagiCode.Libs.Providers.Copilot;

internal sealed class GitHubCopilotSdkGateway : ICopilotSdkGateway
{
    private static readonly SemaphoreSlim EnvironmentMutationLock = new(1, 1);

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
        TimeSpan? startupTimeout = null)
    {
        if (startupTimedOut)
        {
            var timeout = startupTimeout ?? TimeSpan.Zero;
            return $"[startup_timeout] Copilot CLI startup timed out after {timeout.TotalSeconds:0} seconds.";
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
                    Content: reasoningEvent.Data.Content));
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

        public string SessionId => session.SessionId;

        public async IAsyncEnumerable<CopilotSdkStreamEvent> SendPromptAsync(
            CopilotSdkRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var eventData in StreamProducedEventsAsync(async (writer, streamCancellationToken) =>
            {
                var terminalEventWritten = false;
                var sawDelta = false;
                if (!_lifecycleEventSent)
                {
                    _lifecycleEventSent = true;
                    writer.TryWrite(lifecycleEvent);
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
                            terminalEventWritten = true;
                        }

                        writer.TryWrite(normalizedEvent);
                    }
                });

                try
                {
                    await session.SendAndWaitAsync(
                        new MessageOptions { Prompt = request.Prompt },
                        request.Timeout,
                        streamCancellationToken);
                }
                catch (Exception ex)
                {
                    if (!terminalEventWritten)
                    {
                        terminalEventWritten = true;
                        writer.TryWrite(new CopilotSdkStreamEvent(
                            CopilotSdkStreamEventType.Error,
                            SessionId: SessionId,
                            ErrorMessage: NormalizeFailureMessage(ex.Message)));
                    }
                }
                finally
                {
                    if (!terminalEventWritten)
                    {
                        writer.TryWrite(new CopilotSdkStreamEvent(
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
            await session.DisposeAsync();
            await client.DisposeAsync();
            await environmentScope.DisposeAsync();
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
