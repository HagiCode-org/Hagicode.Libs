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
            var session = await client.CreateSessionAsync(new SessionConfig
            {
                Model = request.Model,
                WorkingDirectory = request.WorkingDirectory,
                Streaming = true,
                OnPermissionRequest = PermissionHandler.ApproveAll
            }, startupCancellationTokenSource.Token);

            return new PooledCopilotSdkRuntime(client, session, environmentScope);
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
        var channel = Channel.CreateUnbounded<CopilotSdkStreamEvent>();
        var events = new List<CopilotSdkStreamEvent>();

        switch (evt)
        {
            case AssistantMessageDeltaEvent deltaEvent when !string.IsNullOrWhiteSpace(deltaEvent.Data.DeltaContent):
                sawDelta = true;
                events.Add(new CopilotSdkStreamEvent(
                    CopilotSdkStreamEventType.TextDelta,
                    Content: deltaEvent.Data.DeltaContent));
                break;

            case AssistantMessageDeltaEvent:
                break;

            case AssistantMessageEvent messageEvent when !sawDelta && !string.IsNullOrWhiteSpace(messageEvent.Data.Content):
                events.Add(new CopilotSdkStreamEvent(
                    CopilotSdkStreamEventType.TextDelta,
                    Content: messageEvent.Data.Content));
                break;

            case AssistantMessageEvent:
                break;

            case AssistantReasoningEvent reasoningEvent when !string.IsNullOrWhiteSpace(reasoningEvent.Data.Content):
                events.Add(new CopilotSdkStreamEvent(
                    CopilotSdkStreamEventType.ReasoningDelta,
                    Content: reasoningEvent.Data.Content));
                break;

            case ToolExecutionStartEvent toolStartEvent:
                events.Add(new CopilotSdkStreamEvent(
                    CopilotSdkStreamEventType.ToolExecutionStart,
                    ToolName: FirstNonEmpty(toolStartEvent.Data.ToolName, toolStartEvent.Data.McpToolName),
                    ToolCallId: toolStartEvent.Data.ToolCallId));
                break;

            case ToolExecutionCompleteEvent toolCompleteEvent:
                events.Add(new CopilotSdkStreamEvent(
                    CopilotSdkStreamEventType.ToolExecutionEnd,
                    Content: ExtractToolExecutionContent(toolCompleteEvent),
                    ToolCallId: toolCompleteEvent.Data.ToolCallId));
                break;

            case SessionErrorEvent errorEvent:
                events.Add(new CopilotSdkStreamEvent(
                    CopilotSdkStreamEventType.Error,
                    ErrorMessage: NormalizeFailureMessage(errorEvent.Data.Message)));
                break;

            default:
                events.Add(new CopilotSdkStreamEvent(
                    CopilotSdkStreamEventType.RawEvent,
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
        CopilotSdkEnvironmentScope environmentScope) : ICopilotSdkRuntime
    {
        public async IAsyncEnumerable<CopilotSdkStreamEvent> SendPromptAsync(
            CopilotSdkRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var eventData in StreamProducedEventsAsync(async (writer, streamCancellationToken) =>
            {
                var terminalEventWritten = false;
                var sawDelta = false;
                using var subscription = session.On(evt =>
                {
                    var dispatchResult = DispatchSessionEvent(evt, sawDelta);
                    sawDelta = dispatchResult.SawDelta;

                    foreach (var mappedEvent in dispatchResult.Events)
                    {
                        if (mappedEvent.Type == CopilotSdkStreamEventType.Error)
                        {
                            terminalEventWritten = true;
                        }

                        writer.TryWrite(mappedEvent);
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
                            ErrorMessage: NormalizeFailureMessage(ex.Message)));
                    }
                }
                finally
                {
                    if (!terminalEventWritten)
                    {
                        writer.TryWrite(new CopilotSdkStreamEvent(CopilotSdkStreamEventType.Completed));
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
