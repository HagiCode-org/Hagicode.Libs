using System.Collections.Concurrent;

namespace HagiCode.Libs.Providers.Pi;

/// <summary>
/// Runs <see cref="PiOptions.UserInputHandler" /> for the question dialogs of one RPC execution, one background task per
/// dialog, so the read loop keeps streaming while a user thinks.
/// </summary>
/// <remarks>
/// Every task ends by writing exactly one <c>extension_ui_response</c> for its dialog, except when the run itself is over:
/// answers become the dialog value, while no answer, the question timeout and a failing handler become a cancelled response,
/// so the extension's tool reports that the user declined and the run reaches its normal result. When the caller cancels, a
/// best-effort cancelled response is written with a short timeout and the caller's cancellation surfaces through the read
/// loop as usual.
/// </remarks>
internal sealed class PiPendingDialogs : IDisposable
{
    private static readonly TimeSpan CancelResponseTimeout = TimeSpan.FromSeconds(2);

    private readonly Func<PiUserInputRequest, CancellationToken, Task<PiUserInputResponse?>> _handler;
    private readonly TimeSpan _questionTimeout;
    private readonly PiRpcWriter _writer;
    private readonly CancellationToken _callerToken;
    private readonly CancellationTokenSource _shutdown;
    private readonly ConcurrentDictionary<string, Task> _tasks = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="PiPendingDialogs" /> class.
    /// </summary>
    /// <param name="handler">The handler that produces the answers.</param>
    /// <param name="questionTimeout">How long a question may stay unanswered, or <see cref="Timeout.InfiniteTimeSpan" />.</param>
    /// <param name="writer">The gated writer of the execution.</param>
    /// <param name="callerToken">The cancellation token of the whole execution.</param>
    public PiPendingDialogs(
        Func<PiUserInputRequest, CancellationToken, Task<PiUserInputResponse?>> handler,
        TimeSpan questionTimeout,
        PiRpcWriter writer,
        CancellationToken callerToken)
    {
        _handler = handler;
        _questionTimeout = questionTimeout;
        _writer = writer;
        _callerToken = callerToken;
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
    }

    /// <summary>
    /// Registers a dialog as open and starts its background task. The handler is invoked on a thread-pool thread, so the
    /// caller never waits for it.
    /// </summary>
    /// <returns><see langword="false" /> when a dialog with that id is already open and the request was ignored.</returns>
    public bool Start(PiUserInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_tasks.ContainsKey(request.RequestId))
        {
            return false;
        }

        _tasks[request.RequestId] = Task.Run(() => RunAsync(request));
        return true;
    }

    /// <summary>
    /// Ends every dialog that is still waiting without writing to Pi. Used when the run is over, so a handler that waits for
    /// an answer is released.
    /// </summary>
    public void CancelOutstanding()
    {
        if (!_tasks.IsEmpty)
        {
            _shutdown.Cancel();
        }
    }

    /// <summary>
    /// Waits, for at most <paramref name="timeout" />, for the tasks that are still running, so a cancelled response that was
    /// started by a cancellation reaches Pi before the next command is written.
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

    private async Task RunAsync(PiUserInputRequest request)
    {
        try
        {
            await RunCoreAsync(request).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A broken transport ends the run through the read loop; there is nobody to tell here.
        }
    }

    private async Task RunCoreAsync(PiUserInputRequest request)
    {
        string response;
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
                ? PiRpcProtocol.BuildDialogCancelled(request.RequestId)
                : PiRpcProtocol.BuildDialogValue(request.RequestId, answer.ToDialogValue());
        }
        catch (OperationCanceledException) when (_callerToken.IsCancellationRequested)
        {
            await SendCancelledAsync(request).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // The run is over; Pi is going away and there is nothing left to answer.
            return;
        }
        catch (Exception)
        {
            // The timeout passed, the handler failed, or it answered nothing: the user declined, as far as Pi can tell.
            response = PiRpcProtocol.BuildDialogCancelled(request.RequestId);
        }

        await _writer.TrySendAsync(response, _callerToken).ConfigureAwait(false);
    }

    private async Task SendCancelledAsync(PiUserInputRequest request)
    {
        using var cancelSource = new CancellationTokenSource(CancelResponseTimeout);
        try
        {
            await _writer.TrySendAsync(PiRpcProtocol.BuildDialogCancelled(request.RequestId), cancelSource.Token)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort: the process is about to be stopped anyway.
        }
    }
}
