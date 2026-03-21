using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using GitHub.Copilot.SDK;

namespace HagiCode.Libs.Providers.Copilot;

internal sealed class GitHubCopilotSdkGateway : ICopilotSdkGateway
{
    private static readonly SemaphoreSlim EnvironmentMutationLock = new(1, 1);

    public async IAsyncEnumerable<CopilotSdkStreamEvent> SendPromptAsync(
        CopilotSdkRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<CopilotSdkStreamEvent>();

        _ = Task.Run(async () =>
        {
            try
            {
                await CopyEventsAsync(SendPromptViaSdkAsync(request, cancellationToken), channel.Writer, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                channel.Writer.TryWrite(new CopilotSdkStreamEvent(
                    CopilotSdkStreamEventType.Error,
                    ErrorMessage: NormalizeFailureMessage(ex.Message)));
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        await foreach (var eventData in channel.Reader.ReadAllAsync(cancellationToken))
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

    private async IAsyncEnumerable<CopilotSdkStreamEvent> SendPromptViaSdkAsync(
        CopilotSdkRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var eventData in StreamProducedEventsAsync(async (writer, streamCancellationToken) =>
        {
            var clientOptions = BuildClientOptions(request);
            using var startupCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(streamCancellationToken);
            startupCancellationTokenSource.CancelAfter(request.StartupTimeout);
            CopilotSession? session = null;
            var terminalEventWritten = false;

            try
            {
                await using var environmentScope = await CopilotSdkEnvironmentScope.EnterAsync(request.EnvironmentVariables, streamCancellationToken);
                await using var client = new CopilotClient(clientOptions);

                session = await client.CreateSessionAsync(new SessionConfig
                {
                    Model = request.Model,
                    WorkingDirectory = request.WorkingDirectory,
                    Streaming = true,
                    OnPermissionRequest = PermissionHandler.ApproveAll
                }, startupCancellationTokenSource.Token);

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
                    var normalizedMessage = NormalizeFailureMessage(
                        ex.Message,
                        startupTimedOut: ex is OperationCanceledException
                                         && !streamCancellationToken.IsCancellationRequested
                                         && startupCancellationTokenSource.IsCancellationRequested,
                        startupTimeout: request.StartupTimeout);

                    if (!terminalEventWritten)
                    {
                        terminalEventWritten = true;
                        writer.TryWrite(new CopilotSdkStreamEvent(
                            CopilotSdkStreamEventType.Error,
                            ErrorMessage: normalizedMessage));
                    }
                }
            }
            catch (Exception ex)
            {
                var normalizedMessage = NormalizeFailureMessage(
                    ex.Message,
                    startupTimedOut: ex is OperationCanceledException
                                     && !streamCancellationToken.IsCancellationRequested
                                     && startupCancellationTokenSource.IsCancellationRequested,
                    startupTimeout: request.StartupTimeout);

                if (!terminalEventWritten)
                {
                    terminalEventWritten = true;
                    writer.TryWrite(new CopilotSdkStreamEvent(
                        CopilotSdkStreamEventType.Error,
                        ErrorMessage: normalizedMessage));
                }
            }
            finally
            {
                if (session is not null)
                {
                    await session.DisposeAsync();
                }

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
