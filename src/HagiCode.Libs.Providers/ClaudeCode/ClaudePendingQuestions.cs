using System.Collections.Concurrent;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.ClaudeCode;

/// <summary>
/// Runs <see cref="ClaudeCodeOptions.UserInputHandler" /> for the interactive questions of one execution, one background
/// task per question, so the receive loop keeps streaming while a user thinks.
/// </summary>
/// <remarks>
/// Every task ends by writing exactly one <c>control_response</c> for its request, except when the run itself is over:
/// answers become <c>allow</c> with the answers, while no answer, the question timeout and a failing handler become
/// <c>deny</c> with a message, so the CLI reports the call as denied and the turn reaches its normal result. When the
/// caller cancels, a best-effort <c>deny</c> is written with a short timeout and the caller's cancellation surfaces
/// through the receive loop as usual.
/// </remarks>
internal sealed class ClaudePendingQuestions : IDisposable
{
    private static readonly TimeSpan CancelDenyTimeout = TimeSpan.FromSeconds(2);

    private readonly Func<ClaudeUserInputRequest, CancellationToken, Task<ClaudeUserInputResponse?>> _handler;
    private readonly TimeSpan _questionTimeout;
    private readonly ClaudeStdinWriter _writer;
    private readonly ClaudeStreamCompletionTracker _tracker;
    private readonly CancellationToken _callerToken;
    private readonly CancellationTokenSource _shutdown;
    private readonly ConcurrentDictionary<string, Task> _tasks = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="ClaudePendingQuestions" /> class.
    /// </summary>
    /// <param name="handler">The handler that produces the answers.</param>
    /// <param name="questionTimeout">How long a question may stay unanswered, or <see cref="Timeout.InfiniteTimeSpan" />.</param>
    /// <param name="writer">The gated writer of the execution.</param>
    /// <param name="tracker">The tracker whose wait bounds a pending question suspends.</param>
    /// <param name="callerToken">The cancellation token of the whole execution.</param>
    public ClaudePendingQuestions(
        Func<ClaudeUserInputRequest, CancellationToken, Task<ClaudeUserInputResponse?>> handler,
        TimeSpan questionTimeout,
        ClaudeStdinWriter writer,
        ClaudeStreamCompletionTracker tracker,
        CancellationToken callerToken)
    {
        _handler = handler;
        _questionTimeout = questionTimeout;
        _writer = writer;
        _tracker = tracker;
        _callerToken = callerToken;
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
    }

    /// <summary>
    /// Registers a question as pending and starts its background task. The handler is invoked on a thread-pool thread, so
    /// the caller never waits for it.
    /// </summary>
    public void Start(ClaudeUserInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_tasks.ContainsKey(request.RequestId))
        {
            return;
        }

        _tracker.RecordQuestionPending();
        var task = Task.Run(() => RunAsync(request));
        _tasks[request.RequestId] = task;
    }

    /// <summary>
    /// Ends every question that is still waiting without writing to the CLI. Used when the run is over, so a handler
    /// that waits for an answer is released.
    /// </summary>
    public void CancelOutstanding()
    {
        if (!_tasks.IsEmpty)
        {
            _shutdown.Cancel();
        }
    }

    /// <summary>
    /// Waits, for at most <paramref name="timeout" />, for the tasks that are still running, so a deny that was started
    /// by a cancellation reaches the CLI before the transport is disposed.
    /// </summary>
    public async Task WaitForOutstandingAsync(TimeSpan timeout)
    {
        var pending = _tasks.Values.Where(static task => !task.IsCompleted).ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(pending).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Every task handles its own failures; a timeout only means a handler outlived the grace period.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _shutdown.Dispose();
    }

    private async Task RunAsync(ClaudeUserInputRequest request)
    {
        // The question counts as resolved the moment its decision is made, before the response is written: the CLI echoes
        // that write at once, and the receive loop must see the old wait bounds again by the time the echo wakes it.
        var resolved = 0;
        void Resolve()
        {
            if (Interlocked.Exchange(ref resolved, 1) == 0)
            {
                _tracker.RecordQuestionResolved();
            }
        }

        try
        {
            await RunCoreAsync(request, Resolve).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A broken transport ends the run through the receive loop; there is nobody to tell here.
        }
        finally
        {
            Resolve();
        }
    }

    private async Task RunCoreAsync(ClaudeUserInputRequest request, Action resolve)
    {
        CliMessage response;
        using var handlerSource = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        if (_questionTimeout != Timeout.InfiniteTimeSpan)
        {
            handlerSource.CancelAfter(_questionTimeout);
        }

        try
        {
            var handlerTask = _handler(request, handlerSource.Token);
            _ = handlerTask.ContinueWith(
                static task => { _ = task.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            // WaitAsync keeps the timeout and the cancellation effective for a handler that ignores its token.
            var answer = await handlerTask.WaitAsync(handlerSource.Token).ConfigureAwait(false);
            response = answer is null
                ? ClaudeControlProtocol.BuildDenyResponse(request.RequestId, ClaudeControlProtocol.NoAnswerMessage)
                : ClaudeControlProtocol.BuildAllowResponse(request.RequestId, request.Input, answer.Answers);
        }
        catch (OperationCanceledException) when (_callerToken.IsCancellationRequested)
        {
            resolve();
            await SendCancelDenyAsync(request).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // The run is over; the CLI is going away and there is nothing left to answer.
            return;
        }
        catch (OperationCanceledException) when (handlerSource.IsCancellationRequested)
        {
            response = ClaudeControlProtocol.BuildDenyResponse(request.RequestId, ClaudeControlProtocol.QuestionTimedOutMessage);
        }
        catch (Exception exception)
        {
            response = ClaudeControlProtocol.BuildDenyResponse(
                request.RequestId,
                string.IsNullOrWhiteSpace(exception.Message)
                    ? ClaudeControlProtocol.NoAnswerMessage
                    : $"The question handler failed: {exception.Message}");
        }

        resolve();
        await _writer.SendAsync(response, _callerToken).ConfigureAwait(false);
    }

    private async Task SendCancelDenyAsync(ClaudeUserInputRequest request)
    {
        using var denySource = new CancellationTokenSource(CancelDenyTimeout);
        try
        {
            await _writer.SendAsync(
                    ClaudeControlProtocol.BuildDenyResponse(request.RequestId, ClaudeControlProtocol.QuestionCancelledMessage),
                    denySource.Token)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort: the transport is about to be disposed anyway.
        }
    }
}
