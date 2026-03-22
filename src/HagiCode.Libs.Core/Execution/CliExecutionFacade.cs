using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.Core.Execution;

/// <summary>
/// Implements the shared buffered and streaming CLI execution facade.
/// </summary>
public sealed class CliExecutionFacade : ICliExecutionFacade
{
    private readonly CliProcessManager _processManager;
    private readonly IRuntimeEnvironmentResolver? _runtimeEnvironmentResolver;
    private readonly ICliExecutionPolicy _policy;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="CliExecutionFacade" /> class.
    /// </summary>
    public CliExecutionFacade(
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null,
        ICliExecutionPolicy? policy = null)
        : this(processManager, runtimeEnvironmentResolver, policy, TimeProvider.System)
    {
    }

    internal CliExecutionFacade(
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver,
        ICliExecutionPolicy? policy,
        TimeProvider timeProvider)
    {
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _runtimeEnvironmentResolver = runtimeEnvironmentResolver;
        _policy = policy ?? new AllowAllCliExecutionPolicy();
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public async Task<CliExecutionResult> ExecuteAsync(
        CliExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        var context = await BuildContextAsync(request, cancellationToken);
        var policyDecision = await _policy.EvaluateAsync(context, cancellationToken);
        if (!policyDecision.IsAllowed)
        {
            return CreateRejectedResult(context, policyDecision.Diagnostics);
        }

        try
        {
            var processResult = await _processManager.ExecuteAsync(context.ToProcessStartContext(), cancellationToken);
            return MapProcessResult(context, processResult);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelledAtUtc = _timeProvider.GetUtcNow();
            return new CliExecutionResult
            {
                Status = CliExecutionStatus.Cancelled,
                CommandPreview = context.CommandPreview,
                Diagnostics = [new CliExecutionDiagnostic("execution_cancelled", "The execution was cancelled by the caller.")],
                StartedAtUtc = cancelledAtUtc,
                CompletedAtUtc = cancelledAtUtc,
                Mode = CliExecutionMode.Buffered
            };
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<CliExecutionEvent> ExecuteStreamingAsync(
        CliExecutionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var context = await BuildContextAsync(request, cancellationToken);
        var policyDecision = await _policy.EvaluateAsync(context, cancellationToken);
        if (!policyDecision.IsAllowed)
        {
            yield return new CliExecutionEvent
            {
                Kind = CliExecutionEventKind.Completed,
                Result = CreateRejectedResult(context, policyDecision.Diagnostics),
                TimestampUtc = _timeProvider.GetUtcNow()
            };

            yield break;
        }

        var startedAtUtc = _timeProvider.GetUtcNow();
        await using var handle = await _processManager.StartAsync(context.ToProcessStartContext(), cancellationToken);

        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var capturedOutput = new ConcurrentQueue<CliExecutionOutputChunk>();
        var channel = Channel.CreateUnbounded<CliExecutionEvent>();

        var stdoutTask = PumpAsync(
            handle.StandardOutput,
            CliExecutionEventKind.StandardOutput,
            CliExecutionOutputChannel.StandardOutput,
            standardOutput,
            capturedOutput,
            channel.Writer,
            CancellationToken.None);
        var stderrTask = PumpAsync(
            handle.StandardError,
            CliExecutionEventKind.StandardError,
            CliExecutionOutputChannel.StandardError,
            standardError,
            capturedOutput,
            channel.Writer,
            CancellationToken.None);

        var timedOut = false;
        int exitCode;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (context.Timeout is { } timeout)
        {
            linkedCts.CancelAfter(timeout);
        }

        try
        {
            await handle.Process.WaitForExitAsync(linkedCts.Token);
            exitCode = handle.Process.ExitCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && context.Timeout is not null)
        {
            timedOut = true;
            await _processManager.StopForExecutionAsync(handle, CancellationToken.None);
            exitCode = handle.Process.HasExited ? handle.Process.ExitCode : -1;
        }
        catch (OperationCanceledException)
        {
            await _processManager.StopForExecutionAsync(handle, CancellationToken.None);
            throw;
        }

        await Task.WhenAll(stdoutTask, stderrTask);
        channel.Writer.TryComplete();

        await foreach (var executionEvent in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return executionEvent;
        }

        var completedAtUtc = _timeProvider.GetUtcNow();
        var diagnostics = BuildDiagnostics(exitCode, timedOut, streaming: true);
        var result = new CliExecutionResult
        {
            Status = timedOut
                ? CliExecutionStatus.TimedOut
                : CliExecutionStatus.StreamingCompleted,
            ExitCode = exitCode,
            CommandPreview = context.CommandPreview,
            StandardOutput = standardOutput.ToString(),
            StandardError = AppendTimeoutMessage(standardError.ToString(), timedOut),
            Diagnostics = diagnostics,
            CapturedOutput = capturedOutput.ToArray(),
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = completedAtUtc,
            Mode = CliExecutionMode.Streaming,
            TimedOut = timedOut
        };

        yield return new CliExecutionEvent
        {
            Kind = CliExecutionEventKind.Completed,
            Result = result,
            TimestampUtc = completedAtUtc
        };
    }

    private async Task<CliExecutionContext> BuildContextAsync(
        CliExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyDictionary<string, string?>? runtimeEnvironment = null;
        if (request.Options.ResolveRuntimeEnvironment && _runtimeEnvironmentResolver is not null)
        {
            runtimeEnvironment = await _runtimeEnvironmentResolver.ResolveAsync(cancellationToken);
        }

        return CliExecutionContext.Create(request, runtimeEnvironment);
    }

    private CliExecutionResult MapProcessResult(CliExecutionContext context, ProcessResult processResult)
    {
        var diagnostics = BuildDiagnostics(processResult.ExitCode, processResult.TimedOut, streaming: false);
        return new CliExecutionResult
        {
            Status = processResult.TimedOut
                ? CliExecutionStatus.TimedOut
                : processResult.ExitCode == 0
                    ? CliExecutionStatus.Success
                    : CliExecutionStatus.Failed,
            ExitCode = processResult.ExitCode,
            CommandPreview = string.IsNullOrWhiteSpace(processResult.CommandPreview)
                ? context.CommandPreview
                : processResult.CommandPreview,
            StandardOutput = processResult.StandardOutput,
            StandardError = processResult.StandardError,
            Diagnostics = diagnostics,
            CapturedOutput = processResult.CapturedOutput
                .Select(static chunk => new CliExecutionOutputChunk(
                    chunk.Channel == ProcessOutputChannel.StandardError
                        ? CliExecutionOutputChannel.StandardError
                        : CliExecutionOutputChannel.StandardOutput,
                    chunk.Text,
                    chunk.TimestampUtc))
                .ToArray(),
            StartedAtUtc = processResult.StartedAtUtc ?? _timeProvider.GetUtcNow(),
            CompletedAtUtc = processResult.CompletedAtUtc ?? _timeProvider.GetUtcNow(),
            Mode = context.Mode,
            TimedOut = processResult.TimedOut
        };
    }

    private CliExecutionResult CreateRejectedResult(
        CliExecutionContext context,
        IReadOnlyList<CliExecutionDiagnostic> diagnostics)
    {
        var timestampUtc = _timeProvider.GetUtcNow();
        return new CliExecutionResult
        {
            Status = CliExecutionStatus.Rejected,
            CommandPreview = context.CommandPreview,
            Diagnostics = diagnostics,
            StartedAtUtc = timestampUtc,
            CompletedAtUtc = timestampUtc,
            Mode = context.Mode
        };
    }

    private static IReadOnlyList<CliExecutionDiagnostic> BuildDiagnostics(int exitCode, bool timedOut, bool streaming)
    {
        if (timedOut)
        {
            return
            [
                new CliExecutionDiagnostic(
                    "execution_timeout",
                    "The process exceeded its timeout and was terminated.")
            ];
        }

        if (exitCode == 0)
        {
            return
            [
                new CliExecutionDiagnostic(
                    streaming ? "streaming_completed" : "execution_completed",
                    streaming
                        ? "The streaming execution completed and produced a terminal envelope."
                        : "The command completed successfully.")
            ];
        }

        return
        [
            new CliExecutionDiagnostic(
                "execution_failed",
                $"The command exited with code {exitCode}.")
        ];
    }

    private static string AppendTimeoutMessage(string standardError, bool timedOut)
    {
        if (!timedOut)
        {
            return standardError;
        }

        if (string.IsNullOrWhiteSpace(standardError))
        {
            return "The process timed out and was terminated.";
        }

        return standardError + System.Environment.NewLine + "The process timed out and was terminated.";
    }

    private async Task PumpAsync(
        StreamReader reader,
        CliExecutionEventKind eventKind,
        CliExecutionOutputChannel outputChannel,
        StringBuilder buffer,
        ConcurrentQueue<CliExecutionOutputChunk> capturedOutput,
        ChannelWriter<CliExecutionEvent> writer,
        CancellationToken cancellationToken)
    {
        var charBuffer = new char[1024];
        while (true)
        {
            var read = await reader.ReadAsync(charBuffer.AsMemory(), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            var text = new string(charBuffer, 0, read);
            buffer.Append(text);

            var timestampUtc = _timeProvider.GetUtcNow();
            capturedOutput.Enqueue(new CliExecutionOutputChunk(outputChannel, text, timestampUtc));
            await writer.WriteAsync(
                new CliExecutionEvent
                {
                    Kind = eventKind,
                    Text = text,
                    TimestampUtc = timestampUtc
                },
                cancellationToken);
        }
    }
}
