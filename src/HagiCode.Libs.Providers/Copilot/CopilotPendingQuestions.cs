using System.Diagnostics;
using System.Threading.Channels;
using GitHub.Copilot;

namespace HagiCode.Libs.Providers.Copilot;

/// <summary>
/// Runs <see cref="CopilotOptions.UserInputHandler" /> for the questions of one Copilot run and bridges them to the SDK.
/// </summary>
/// <remarks>
/// The SDK callback is the only trigger: it starts the handler and emits one <c>question.request</c> stream message per
/// question. The <c>user_input.requested</c> events are queued first-in first-out and matched to the callback by question
/// text, which supplies the question id. <c>user_input.completed</c> closes a question that Copilot answered elsewhere and
/// is ignored for a question the handler already answered.
/// A question that is not answered is declined by throwing <see cref="CopilotUserInputDeclinedException" /> from the callback.
/// No answer, a refused answer, the question timeout and a failing handler decline the question and set
/// <see cref="HasDeclinedQuestion" />. When the caller cancels the run, the pending question is cancelled and no decline is
/// recorded. While at least one question is pending, <see cref="PendingCount" /> is positive and
/// <see cref="PausedDuration" /> accumulates, so the idle watchdog and the request deadline pause.
/// </remarks>
internal sealed class CopilotPendingQuestions : IDisposable
{
    private readonly Func<CopilotUserInputRequest, CancellationToken, Task<CopilotUserInputResponse?>> _handler;
    private readonly TimeSpan _questionTimeout;
    private readonly CancellationTokenSource _shutdown;
    private readonly object _gate = new();
    private readonly List<QueuedRequest> _queued = [];
    private readonly List<PendingQuestion> _pending = [];
    private ChannelWriter<CopilotSdkStreamEvent>? _writer;
    private Action? _recordProgress;
    private long _pauseStartedTimestamp = -1;
    private TimeSpan _pausedDuration = TimeSpan.Zero;
    private int _generatedIds;
    private int _declined;

    /// <summary>
    /// Initializes a new instance of the <see cref="CopilotPendingQuestions" /> class.
    /// </summary>
    /// <param name="handler">The handler that produces the answers.</param>
    /// <param name="questionTimeout">How long a question may stay unanswered, or <see cref="Timeout.InfiniteTimeSpan" />.</param>
    /// <param name="callerToken">The cancellation token of the whole run.</param>
    public CopilotPendingQuestions(
        Func<CopilotUserInputRequest, CancellationToken, Task<CopilotUserInputResponse?>> handler,
        TimeSpan questionTimeout,
        CancellationToken callerToken)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _questionTimeout = questionTimeout;
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
    }

    /// <summary>
    /// Gets the number of questions that are waiting for the handler.
    /// </summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>
    /// Gets the total time the run has spent with at least one question pending.
    /// </summary>
    public TimeSpan PausedDuration
    {
        get
        {
            lock (_gate)
            {
                return _pauseStartedTimestamp < 0
                    ? _pausedDuration
                    : _pausedDuration + Stopwatch.GetElapsedTime(_pauseStartedTimestamp);
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the run declined a question.
    /// </summary>
    public bool HasDeclinedQuestion => Volatile.Read(ref _declined) != 0;

    /// <summary>
    /// Attaches the stream of the run. Questions and diagnostics are written to <paramref name="writer" />, and
    /// <paramref name="recordProgress" /> is called when the last pending question closes, so the idle clock restarts.
    /// </summary>
    public void Attach(ChannelWriter<CopilotSdkStreamEvent> writer, Action recordProgress)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(recordProgress);
        lock (_gate)
        {
            _writer = writer;
            _recordProgress = recordProgress;
        }
    }

    /// <summary>
    /// Queues a <c>user_input.requested</c> event for the next callback with the same question text. A late event for a question
    /// whose callback already started without a match is dropped, so it cannot be matched to a later question.
    /// </summary>
    public void RecordRequested(UserInputRequestedData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_gate)
        {
            if (_pending.Any(pending => pending.SdkRequestId is null &&
                                        string.Equals(pending.Question, data.Question, StringComparison.Ordinal)))
            {
                return;
            }

            _queued.Add(new QueuedRequest(data.RequestId, data.ToolCallId, data.Question));
        }
    }

    /// <summary>
    /// Handles a <c>user_input.completed</c> event. For a question that is still pending, the question is cancelled and the
    /// closing event is returned so the stream can tell the caller. For a queued question, it is removed. Otherwise, including
    /// a question the handler already answered, the event is ignored.
    /// </summary>
    /// <returns>The closing stream event, or <see langword="null" /> when nothing was pending for the event.</returns>
    public CopilotSdkStreamEvent? RecordCompleted(string? requestId)
    {
        if (string.IsNullOrEmpty(requestId))
        {
            return null;
        }

        lock (_gate)
        {
            if (_pending.FirstOrDefault(pending => !pending.Answered &&
                                                    !pending.CompletedByCopilot &&
                                                    string.Equals(pending.SdkRequestId, requestId, StringComparison.Ordinal))
                is { } pending)
            {
                pending.CompletedByCopilot = true;
                pending.Source.Cancel();
                return new CopilotSdkStreamEvent(CopilotSdkStreamEventType.QuestionClosed, QuestionId: pending.QuestionId);
            }

            var queuedIndex = _queued.FindIndex(queued => string.Equals(queued.RequestId, requestId, StringComparison.Ordinal));
            if (queuedIndex >= 0)
            {
                _queued.RemoveAt(queuedIndex);
            }

            return null;
        }
    }

    /// <summary>
    /// The SDK user-input callback. It returns the answer for the question, or throws <see cref="CopilotUserInputDeclinedException" />
    /// when the question is declined.
    /// </summary>
    public async Task<UserInputResponse> HandleAsync(UserInputRequest request, UserInputInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(invocation);

        var question = request.Question ?? string.Empty;
        if (_shutdown.IsCancellationRequested)
        {
            // The run is over; a callback that arrives now is declined without starting a handler.
            throw new CopilotUserInputDeclinedException(question);
        }

        var choices = request.Choices is { Count: > 0 } offered ? offered.ToArray() : Array.Empty<string>();
        // The SDK reports AllowFreeform as nullable; an absent value offers choices only.
        var allowFreeform = request.AllowFreeform ?? false;

        PendingQuestion pending;
        CopilotUserInputRequest userRequest;
        ChannelWriter<CopilotSdkStreamEvent>? writer;
        lock (_gate)
        {
            var queuedIndex = _queued.FindIndex(queued => string.Equals(queued.Question, question, StringComparison.Ordinal));
            var queued = queuedIndex >= 0 ? _queued[queuedIndex] : null;
            if (queued is not null)
            {
                _queued.RemoveAt(queuedIndex);
            }

            var questionId = FirstNonEmpty(queued?.ToolCallId, queued?.RequestId)
                             ?? $"copilot-input-{invocation.SessionId}-{Interlocked.Increment(ref _generatedIds)}";
            pending = new PendingQuestion(
                questionId,
                queued?.RequestId,
                question,
                CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token));
            if (_questionTimeout != Timeout.InfiniteTimeSpan)
            {
                pending.Source.CancelAfter(_questionTimeout);
            }

            if (_pending.Count == 0)
            {
                _pauseStartedTimestamp = Stopwatch.GetTimestamp();
            }

            _pending.Add(pending);
            userRequest = new CopilotUserInputRequest(questionId, question, choices, allowFreeform);
            writer = _writer;
        }

        writer?.TryWrite(new CopilotSdkStreamEvent(
            CopilotSdkStreamEventType.QuestionRequested,
            SessionId: invocation.SessionId,
            QuestionId: userRequest.RequestId,
            Question: question,
            Choices: choices,
            AllowFreeform: allowFreeform));

        UserInputResponse? answer = null;
        string? declineReason = null;
        try
        {
            var handlerTask = _handler(userRequest, pending.Source.Token);
            _ = handlerTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            // WaitAsync keeps the timeout and the cancellation effective for a handler that ignores its token.
            var response = await handlerTask.WaitAsync(pending.Source.Token).ConfigureAwait(false);
            answer = response?.ToSdkResponse(userRequest);
            if (answer is null)
            {
                declineReason = "the answer is empty, has more than one value, or is not an offered choice";
            }
            else
            {
                lock (_gate)
                {
                    pending.Answered = true;
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // The run is being stopped: nothing is recorded as declined.
        }
        catch (OperationCanceledException) when (pending.CompletedByCopilot)
        {
            // Copilot completed the question elsewhere, so the answer it already has is used.
        }
        catch (OperationCanceledException)
        {
            declineReason = $"it was not answered within {_questionTimeout}";
        }
        catch (Exception exception)
        {
            declineReason = $"the handler failed: {exception.Message}";
        }
        finally
        {
            RemovePending(pending);
        }

        if (answer is not null)
        {
            return answer;
        }

        if (declineReason is not null && !_shutdown.IsCancellationRequested && !pending.CompletedByCopilot)
        {
            Volatile.Write(ref _declined, 1);
            writer?.TryWrite(new CopilotSdkStreamEvent(
                CopilotSdkStreamEventType.Diagnostic,
                SessionId: invocation.SessionId,
                Content: $"Copilot question '{userRequest.RequestId}' was declined: {declineReason}."));
        }

        throw new CopilotUserInputDeclinedException(userRequest.RequestId);
    }

    /// <summary>
    /// Ends every pending question and stops accepting answers. Used when the run is over, so the callbacks return and the
    /// runtime can be disposed.
    /// </summary>
    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private void RemovePending(PendingQuestion pending)
    {
        Action? recordProgress = null;
        lock (_gate)
        {
            if (!_pending.Remove(pending))
            {
                return;
            }

            if (_pending.Count == 0)
            {
                _pausedDuration += Stopwatch.GetElapsedTime(_pauseStartedTimestamp);
                _pauseStartedTimestamp = -1;
                recordProgress = _recordProgress;
            }
        }

        // The source is not disposed here: it can be cancelled from inside this call (for example by a completed event), and
        // its registration on the run token ends with the run.
        recordProgress?.Invoke();
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }

    private sealed record QueuedRequest(string? RequestId, string? ToolCallId, string Question);

    private sealed class PendingQuestion(
        string questionId,
        string? sdkRequestId,
        string question,
        CancellationTokenSource source)
    {
        public string QuestionId { get; } = questionId;

        public string? SdkRequestId { get; } = sdkRequestId;

        public string Question { get; } = question;

        public CancellationTokenSource Source { get; } = source;

        public bool Answered { get; set; }

        public bool CompletedByCopilot { get; set; }
    }
}
