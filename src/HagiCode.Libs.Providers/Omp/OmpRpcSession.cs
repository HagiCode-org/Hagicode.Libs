using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.Omp;

/// <summary>
/// Serializes the writes the provider makes to OMP's standard input.
/// </summary>
/// <remarks>
/// The read loop (commands, answers to foreign dialogs) and the background task of every open question write concurrently, and
/// a write is a plain write plus flush on one stream, so two unsynchronized writers could interleave inside one JSON line. One
/// gate per execution keeps every line whole.
/// </remarks>
internal sealed class OmpRpcWriter(IOmpRpcProcess process)
{
    private static readonly TimeSpan CloseWaitTimeout = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _closed;

    /// <summary>
    /// Writes one line, waiting for any write that is already in progress.
    /// </summary>
    /// <returns>
    /// <see langword="false" /> when OMP no longer reads its input (closed, or the process is gone), so the caller can let the
    /// read loop report how the run ended. A cancelled token throws.
    /// </returns>
    public async Task<bool> TrySendAsync(string line, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_closed)
            {
                return false;
            }

            await process.WriteLineAsync(line, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Closes OMP's standard input after the write in progress, which makes OMP shut down. Later writes do nothing.
    /// </summary>
    public async Task CloseAsync()
    {
        var gateHeld = await _gate.WaitAsync(CloseWaitTimeout).ConfigureAwait(false);
        try
        {
            _closed = true;
            process.CloseInput();
        }
        finally
        {
            if (gateHeld)
            {
                _gate.Release();
            }
        }
    }
}

/// <summary>
/// What an RPC run found out besides its messages.
/// </summary>
internal sealed class OmpRpcOutcome
{
    /// <summary>
    /// The error text OMP answered <c>set_ask_dialog</c> with, as a build without the command does. When set, the run sent no
    /// prompt and produced no message, and the provider runs the request in JSON print mode instead.
    /// </summary>
    public string? AskDialogRejection { get; set; }
}

/// <summary>
/// The settings of one RPC run.
/// </summary>
/// <param name="RequestedSessionId">The session id the caller asked for, for the started or resumed comparison.</param>
/// <param name="WorkingDirectory">The working directory of the process, reported on the session message.</param>
/// <param name="UserInputHandler">Answers the <c>ask</c> requests.</param>
/// <param name="QuestionTimeout">How long a question may stay unanswered.</param>
/// <param name="ExitTimeout">
/// How long OMP gets to settle after an abort, and to exit after its input is closed, before the process is stopped.
/// </param>
internal sealed record OmpRpcSessionSettings(
    string? RequestedSessionId,
    string? WorkingDirectory,
    Func<OmpUserInputRequest, CancellationToken, Task<OmpUserInputResponse?>> UserInputHandler,
    TimeSpan QuestionTimeout,
    TimeSpan ExitTimeout);

/// <summary>
/// Runs <see cref="OmpOptions.UserInputHandler" /> for the <c>ask</c> requests of one RPC execution, one background task per
/// request, so the read loop keeps streaming while a user thinks.
/// </summary>
/// <remarks>
/// A task writes at most one <c>extension_ui_response</c> for its request: the converted answers, or a cancelled response
/// when the handler answers nothing, leaves a question unanswered, fails or times out (OMP then ends the run as aborted). It
/// writes nothing when OMP closed the request with its own <c>cancel</c> frame, when the caller cancelled (the session sends
/// <c>abort</c> instead, which makes OMP close the request) or when the run is over.
/// </remarks>
internal sealed class OmpPendingQuestions : IDisposable
{
    private readonly Func<OmpUserInputRequest, CancellationToken, Task<OmpUserInputResponse?>> _handler;
    private readonly TimeSpan _questionTimeout;
    private readonly OmpRpcWriter _writer;
    private readonly CancellationTokenSource _shutdown;
    private readonly ConcurrentDictionary<string, OpenQuestion> _questions = new(StringComparer.Ordinal);
    private int _hostCancelled;

    /// <summary>
    /// Initializes a new instance of the <see cref="OmpPendingQuestions" /> class.
    /// </summary>
    /// <param name="handler">The handler that produces the answers.</param>
    /// <param name="questionTimeout">How long a question may stay unanswered, or <see cref="Timeout.InfiniteTimeSpan" />.</param>
    /// <param name="writer">The gated writer of the execution.</param>
    /// <param name="callerToken">The cancellation token of the whole execution.</param>
    public OmpPendingQuestions(
        Func<OmpUserInputRequest, CancellationToken, Task<OmpUserInputResponse?>> handler,
        TimeSpan questionTimeout,
        OmpRpcWriter writer,
        CancellationToken callerToken)
    {
        _handler = handler;
        _questionTimeout = questionTimeout;
        _writer = writer;
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
    }

    /// <summary>
    /// Whether the provider answered an <c>ask</c> request as cancelled, which is why OMP ends the run as aborted.
    /// </summary>
    public bool HostCancelled => Volatile.Read(ref _hostCancelled) == 1;

    /// <summary>
    /// Registers a request as open and starts its background task. The handler is invoked on a thread-pool thread, so the
    /// caller never waits for it.
    /// </summary>
    /// <returns><see langword="false" /> when a request with that id is already open and this one was ignored.</returns>
    public bool Start(OmpUserInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var question = new OpenQuestion(CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token));
        if (!_questions.TryAdd(request.RequestId, question))
        {
            question.Source.Dispose();
            return false;
        }

        question.Task = Task.Run(() => RunAsync(request, question.Source.Token));
        return true;
    }

    /// <summary>
    /// Closes the request OMP cancelled with its own <c>cancel</c> frame: the handler's token is cancelled and nothing is
    /// written for it. An unknown id is ignored.
    /// </summary>
    public void Cancel(string? requestId)
    {
        if (requestId is not null && _questions.TryGetValue(requestId, out var question))
        {
            TryCancel(question.Source);
        }
    }

    /// <summary>
    /// Records a cancelled answer the session wrote itself, for an <c>ask</c> request it could not read.
    /// </summary>
    public void MarkHostCancelled() => Interlocked.Exchange(ref _hostCancelled, 1);

    /// <summary>
    /// Ends every request that is still waiting without writing to OMP. Used when the run is over, so a handler that waits for
    /// an answer is released.
    /// </summary>
    public void CancelOutstanding()
    {
        if (!_questions.IsEmpty)
        {
            TryCancel(_shutdown);
        }
    }

    /// <summary>
    /// Waits, for at most <paramref name="timeout" />, for the tasks that are still running.
    /// </summary>
    public async Task WaitForOutstandingAsync(TimeSpan timeout)
    {
        var pending = _questions.Values
            .Select(static question => question.Task)
            .Where(static task => task is { IsCompleted: false })
            .Cast<Task>()
            .ToArray();
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
        foreach (var question in _questions.Values)
        {
            question.Source.Dispose();
        }

        _shutdown.Dispose();
    }

    private static void TryCancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RunAsync(OmpUserInputRequest request, CancellationToken questionToken)
    {
        try
        {
            await RunCoreAsync(request, questionToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A broken transport ends the run through the read loop; there is nobody to tell here.
        }
    }

    private async Task RunCoreAsync(OmpUserInputRequest request, CancellationToken questionToken)
    {
        using var handlerSource = CancellationTokenSource.CreateLinkedTokenSource(questionToken);
        if (_questionTimeout != Timeout.InfiniteTimeSpan)
        {
            handlerSource.CancelAfter(_questionTimeout);
        }

        string? response;
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
            var answers = answer?.ToAskAnswers(request);
            response = answers is null ? null : OmpRpcProtocol.BuildAskAnswers(request.RequestId, answers);
        }
        catch (Exception) when (questionToken.IsCancellationRequested)
        {
            // OMP closed the request, the caller cancelled (the session aborts the run) or the run is over.
            return;
        }
        catch (Exception)
        {
            // The timeout passed or the handler failed: the user declined, as far as OMP can tell.
            response = null;
        }

        if (response is null)
        {
            // Set before the write, so the read loop sees it by the time OMP reports the aborted run.
            MarkHostCancelled();
            response = OmpRpcProtocol.BuildDialogCancelled(request.RequestId);
        }

        await _writer.TrySendAsync(response, questionToken).ConfigureAwait(false);
    }

    private sealed class OpenQuestion(CancellationTokenSource source)
    {
        public CancellationTokenSource Source { get; } = source;

        public Task? Task { get; set; }
    }
}

/// <summary>
/// Drives one <c>omp --mode rpc-ui</c> process: the <c>set_ask_dialog</c> and <c>get_state</c> handshake, the prompt, the
/// <c>ask</c> requests and the end of the run, and turns OMP's output into the shared message contract of the JSON print path.
/// </summary>
/// <remarks>
/// <para>
/// Everything that is not RPC transport goes through the unchanged <see cref="OmpJsonEventMapper.StreamingState" />: the RPC
/// stream carries the same session events as <c>--mode json</c>, only the session header is missing, and it is synthesized
/// from the <c>get_state</c> reply so the session lifecycle messages are identical on both paths. <c>message_update</c> events
/// lose the stale message snapshot RPC adds to them, so text and thinking reach the mapper as the JSON path sends them.
/// </para>
/// <para>
/// The run ends on <c>session_settled</c>, which follows the <c>prompt_result</c> of the final yield, not on process exit.
/// Standard input is then closed, which makes OMP exit. <c>prompt_result.status</c> decides the terminal message.
/// </para>
/// </remarks>
internal sealed class OmpRpcSession
{
    private static readonly TimeSpan DialogDrainTimeout = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan AbortWriteTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DrainStopTimeout = TimeSpan.FromSeconds(1);

    private readonly IOmpRpcProcess _process;
    private readonly OmpJsonEventMapper.StreamingState _eventState;
    private readonly OmpRpcSessionSettings _settings;
    private readonly OmpRpcOutcome _outcome;
    private readonly OmpRpcWriter _writer;

    private OmpPendingQuestions? _questions;
    private string _prompt = string.Empty;
    private string? _sessionId;
    private string? _rejection;
    private string? _rejectionSource;
    private bool _promptSent;
    private bool _settled;
    private bool _endedWithoutAgent;
    private string? _promptStatus;
    private JsonElement? _promptError;

    /// <summary>
    /// Initializes a new instance of the <see cref="OmpRpcSession" /> class.
    /// </summary>
    /// <param name="process">The started OMP process.</param>
    /// <param name="eventState">The streaming state of the event mapper that normalizes OMP's events.</param>
    /// <param name="settings">The settings of the run.</param>
    /// <param name="outcome">Receives what the run found out besides its messages.</param>
    public OmpRpcSession(
        IOmpRpcProcess process,
        OmpJsonEventMapper.StreamingState eventState,
        OmpRpcSessionSettings settings,
        OmpRpcOutcome outcome)
    {
        _process = process;
        _eventState = eventState;
        _settings = settings;
        _outcome = outcome;
        _writer = new OmpRpcWriter(process);
    }

    /// <summary>
    /// Runs the prompt and streams the normalized messages, ending with the terminal message. A cancellation aborts the run,
    /// ends the process and surfaces as an <see cref="OperationCanceledException" />.
    /// </summary>
    public async IAsyncEnumerable<CliMessage> RunAsync(
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _prompt = prompt;
        using var questions = new OmpPendingQuestions(_settings.UserInputHandler, _settings.QuestionTimeout, _writer, cancellationToken);
        _questions = questions;
        var ended = false;

        // try/finally is allowed around a yield, unlike catch, so failures are captured into pendingException by small
        // try/catch blocks that contain no yield. Whichever way the run ends, the questions that still wait are released.
        try
        {
            ExceptionDispatchInfo? pendingException = null;
            try
            {
                // A false result means OMP is already gone; the read loop below reports how it ended.
                await _writer.TrySendAsync(OmpRpcProtocol.BuildSetAskDialog(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                pendingException = ExceptionDispatchInfo.Capture(ex);
            }

            while (pendingException is null)
            {
                string? line;
                try
                {
                    line = await _process.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    pendingException = ExceptionDispatchInfo.Capture(ex);
                    break;
                }

                if (line is null)
                {
                    break;
                }

                LineResult result;
                try
                {
                    result = await HandleLineAsync(line, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    pendingException = ExceptionDispatchInfo.Capture(ex);
                    break;
                }

                foreach (var message in result.Messages)
                {
                    yield return message;
                }

                if (result.EndsRun)
                {
                    break;
                }
            }

            if (pendingException is not null)
            {
                if (pendingException.SourceException is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    await CancelRunAsync().ConfigureAwait(false);
                }
                else
                {
                    await StopProcessAsync().ConfigureAwait(false);
                }

                ended = true;
                pendingException.Throw();
            }

            var terminalMessages = await EndRunAsync(cancellationToken).ConfigureAwait(false);
            ended = true;
            foreach (var message in terminalMessages)
            {
                yield return message;
            }
        }
        finally
        {
            questions.CancelOutstanding();
            await questions.WaitForOutstandingAsync(DialogDrainTimeout).ConfigureAwait(false);

            // Reached without an orderly end when the consumer stopped enumerating, or EndRunAsync was interrupted: the
            // process must not outlive the call.
            if (!ended)
            {
                await CancelRunAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<LineResult> HandleLineAsync(string line, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return LineResult.None;
        }

        JsonElement frame;
        try
        {
            using var document = JsonDocument.Parse(line);
            frame = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Not protocol data: the mapper records it as an invalid output line, as on the JSON path.
            return LineResult.From(_eventState.ProcessOutputLine(line));
        }

        switch (OmpRpcProtocol.ReadString(frame, "type"))
        {
            case OmpRpcProtocol.ReadyType:
            case OmpRpcProtocol.AvailableCommandsUpdateType:
                return LineResult.None;

            case OmpRpcProtocol.ResponseType:
                return await HandleResponseAsync(frame, cancellationToken).ConfigureAwait(false);

            case OmpRpcProtocol.ExtensionUiRequestType:
                return await HandleDialogAsync(frame, cancellationToken).ConfigureAwait(false);

            case OmpRpcProtocol.PromptResultType:
                return HandlePromptResult(frame);

            case OmpRpcProtocol.SessionSettledType:
                _settled = true;
                return LineResult.End;

            case OmpRpcProtocol.MessageUpdateType:
                return LineResult.From(_eventState.ProcessOutputLine(WithoutStaleSnapshot(frame, line)));

            default:
                return LineResult.From(_eventState.ProcessOutputLine(line));
        }
    }

    /// <summary>
    /// Removes the message snapshot <c>rpc-ui</c> attaches to a <c>message_update</c> and <c>--mode json</c> does not
    /// (<c>message</c>, and <c>assistantMessageEvent.partial</c> of text and thinking events). OMP serializes the snapshot after
    /// later deltas already landed in it, so the mapper would emit text ahead of its delta and then the delta again; text and
    /// thinking reach the mapper as deltas, exactly as on the JSON path. A tool-call event keeps its <c>partial</c>: it names
    /// the call's real id and tool from the first event on, where the delta alone has neither.
    /// </summary>
    private static string WithoutStaleSnapshot(JsonElement frame, string line)
    {
        var eventType = frame.TryGetProperty("assistantMessageEvent", out var assistantEvent)
            ? OmpRpcProtocol.ReadString(assistantEvent, "type")
            : null;
        var keepsPartial = eventType?.StartsWith("toolcall_", StringComparison.Ordinal) == true;
        var hasSnapshot = frame.TryGetProperty("message", out _)
                          || (!keepsPartial && assistantEvent.ValueKind == JsonValueKind.Object && assistantEvent.TryGetProperty("partial", out _));
        if (!hasSnapshot || JsonNode.Parse(line) is not JsonObject update)
        {
            return line;
        }

        update.Remove("message");
        update.Remove("messageId");
        if (!keepsPartial && update["assistantMessageEvent"] is JsonObject eventObject)
        {
            eventObject.Remove("partial");
        }

        return update.ToJsonString();
    }

    private async Task<LineResult> HandleResponseAsync(JsonElement response, CancellationToken cancellationToken)
    {
        switch (OmpRpcProtocol.ReadString(response, "id"))
        {
            case OmpRpcProtocol.SetAskDialogRequestId:
                if (!IsSuccess(response))
                {
                    // No prompt was sent: the provider runs the request in JSON print mode.
                    _outcome.AskDialogRejection = ReadError(response);
                    return LineResult.End;
                }

                await _writer.TrySendAsync(OmpRpcProtocol.BuildGetState(), cancellationToken).ConfigureAwait(false);
                return LineResult.None;

            case OmpRpcProtocol.StateRequestId:
                return await HandleStateResponseAsync(response, cancellationToken).ConfigureAwait(false);

            case OmpRpcProtocol.PromptRequestId:
                return HandlePromptResponse(response);

            default:
                // Only the answers to commands the provider wrote carry its ids; the answer to abort needs no action.
                return LineResult.None;
        }
    }

    private async Task<LineResult> HandleStateResponseAsync(JsonElement response, CancellationToken cancellationToken)
    {
        if (!IsSuccess(response))
        {
            Reject($"OMP rejected get_state: {ReadError(response)}", "get_state");
            return LineResult.End;
        }

        var sessionId = response.TryGetProperty("data", out var data) ? OmpRpcProtocol.ReadString(data, "sessionId") : null;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            Reject("OMP did not report a session id in its get_state reply.", "get_state");
            return LineResult.End;
        }

        _sessionId = sessionId;
        var messages = _eventState.ProcessOutputLine(BuildSessionRecord(sessionId));

        // The prompt follows the state reply, so the session message always precedes the run's own messages. A false result
        // means OMP is already gone; the read loop reports how it ended.
        _promptSent = await _writer.TrySendAsync(OmpRpcProtocol.BuildPrompt(_prompt), cancellationToken).ConfigureAwait(false);
        return LineResult.From(messages);
    }

    private LineResult HandlePromptResponse(JsonElement response)
    {
        if (!IsSuccess(response))
        {
            // A rejected prompt starts no run, so no session_settled follows.
            Reject(ReadError(response), "prompt");
            return LineResult.End;
        }

        // A builtin slash command finished locally: no prompt_result and no session_settled follow.
        if (response.TryGetProperty("data", out var data) && OmpRpcProtocol.ReadBoolean(data, "agentInvoked") == false)
        {
            _endedWithoutAgent = true;
            return LineResult.End;
        }

        return LineResult.None;
    }

    private LineResult HandlePromptResult(JsonElement result)
    {
        if (OmpRpcProtocol.ReadString(result, "id") is { } id && id != OmpRpcProtocol.PromptRequestId)
        {
            return LineResult.None;
        }

        _promptStatus = OmpRpcProtocol.ReadString(result, "status");
        _promptError = result.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object ? error : null;

        // A prompt that never reached the agent gets no session_settled; one that did is over only once the session settles,
        // since background work can wake the agent again after this yield.
        if (OmpRpcProtocol.ReadBoolean(result, "agentInvoked") == false)
        {
            _endedWithoutAgent = true;
            return LineResult.End;
        }

        return LineResult.None;
    }

    private async Task<LineResult> HandleDialogAsync(JsonElement request, CancellationToken cancellationToken)
    {
        var method = OmpRpcProtocol.ReadString(request, "method");
        if (method == OmpRpcProtocol.CancelMethod)
        {
            _questions!.Cancel(OmpRpcProtocol.ReadString(request, "targetId"));
            return LineResult.None;
        }

        var dialogId = OmpRpcProtocol.ReadString(request, "id");
        if (dialogId is not { Length: > 0 })
        {
            return LineResult.None;
        }

        if (method == OmpUserInputRequest.AskMethod)
        {
            if (OmpUserInputRequest.TryParse(request, out var question))
            {
                // The handler starts before the caller sees the question, exactly as for Claude and Pi; its answer is written
                // from its own task, so the read loop never waits for a person.
                return _questions!.Start(question)
                    ? LineResult.From([new CliMessage(OmpUserInputRequest.MessageType, request)])
                    : LineResult.None;
            }

            // An ask request this provider cannot read: nobody can answer it, so it is declined and OMP ends the run.
            _questions!.MarkHostCancelled();
            await _writer.TrySendAsync(OmpRpcProtocol.BuildDialogCancelled(dialogId), cancellationToken).ConfigureAwait(false);
            return LineResult.None;
        }

        // notify, setStatus, setWidget, setTitle, set_editor_text and open_url expect no response.
        if (!OmpRpcProtocol.ExpectsDialogResponse(method))
        {
            return LineResult.None;
        }

        // A dialog of an extension (select, confirm, input, editor): there is nobody to ask, so it is dismissed. The extension
        // sees what it sees when a user presses escape.
        await _writer.TrySendAsync(OmpRpcProtocol.BuildDialogCancelled(dialogId), cancellationToken).ConfigureAwait(false);
        return LineResult.None;
    }

    /// <summary>
    /// Ends a run whose read loop is over: no more questions, standard input closed, a bounded wait for the exit, then the
    /// terminal message.
    /// </summary>
    private async Task<IReadOnlyList<CliMessage>> EndRunAsync(CancellationToken cancellationToken)
    {
        _questions!.CancelOutstanding();
        await _writer.CloseAsync().ConfigureAwait(false);

        // An OMP whose run is over has its result; one that does not exit in time is stopped and still counts as having ended.
        var runIsOver = _settled || _endedWithoutAgent || _rejection is not null || _outcome.AskDialogRejection is not null;
        var exitCode = await AwaitExitAsync(cancellationToken).ConfigureAwait(false) ?? (runIsOver ? 0 : 1);

        var standardError = await ReadStandardErrorAsync().ConfigureAwait(false);

        if (_outcome.AskDialogRejection is not null)
        {
            return [];
        }

        if (_rejection is not null)
        {
            return [CreateRejectionMessage(_rejection, _rejectionSource!, standardError)];
        }

        if (_settled || _endedWithoutAgent)
        {
            return CompleteFromPromptResult(exitCode, standardError);
        }

        // The stream ended before session_settled. A failure the events themselves reported (a failed turn names its source
        // event) or a non-zero exit keeps the mapper's diagnostic. Exit code 0 without a settled run is otherwise not a
        // completion, whatever the events said; output that was not JSON stays part of the diagnostic.
        var completion = _eventState.Complete(exitCode, standardError);
        if (exitCode != 0 || completion.Any(static message => message.Type == "terminal.failed" && message.Content.TryGetProperty("source_event_type", out _)))
        {
            return completion;
        }

        var invalidOutputLines = completion
            .Where(static message => message.Type == "terminal.failed")
            .Select(static message => message.Content.TryGetProperty("invalid_output_lines", out var lines) ? lines : (JsonElement?)null)
            .FirstOrDefault(static lines => lines is not null);
        return [CreateUnsettledMessage(standardError, exitCode, invalidOutputLines)];
    }

    private IReadOnlyList<CliMessage> CompleteFromPromptResult(int exitCode, string standardError)
    {
        if (_promptStatus == OmpRpcProtocol.ErrorStatus)
        {
            return [CreatePromptErrorMessage(standardError, exitCode)];
        }

        if (exitCode != 0)
        {
            return _eventState.Complete(exitCode, standardError);
        }

        if (_promptStatus == OmpRpcProtocol.AbortedStatus)
        {
            // A declined, expired or unanswered question: the provider sent the cancelled answer, so OMP ended the run on the
            // host's own decision. Any other abort was not asked for.
            return _questions!.HostCancelled
                ? [CreateAbortedCompletionMessage()]
                : [CreateAbortedFailureMessage(standardError)];
        }

        return _eventState.Complete(exitCode, standardError);
    }

    /// <summary>
    /// The caller cancelled (or stopped enumerating): the run is aborted, OMP closes an open question with its own
    /// <c>cancel</c> frame and settles, standard input is closed and the process is stopped when it does not exit within the
    /// bound. No answer is written for an open question.
    /// </summary>
    private async Task CancelRunAsync()
    {
        if (_promptSent && !_settled)
        {
            using (var abortSource = new CancellationTokenSource(AbortWriteTimeout))
            {
                try
                {
                    await _writer.TrySendAsync(OmpRpcProtocol.BuildAbort(), abortSource.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Best effort: the process is stopped below when it does not exit.
                }
            }

            await DrainUntilSettledAsync().ConfigureAwait(false);
        }

        await _writer.CloseAsync().ConfigureAwait(false);
        await StopProcessAsync(closeInputFirst: false).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads, bounded, what OMP writes after an abort until the session settles, closing the questions OMP cancels.
    /// </summary>
    private async Task DrainUntilSettledAsync()
    {
        using var drainSource = new CancellationTokenSource(_settings.ExitTimeout);
        try
        {
            while (await _process.ReadLineAsync(drainSource.Token).ConfigureAwait(false) is { } line)
            {
                JsonElement frame;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    frame = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    continue;
                }

                switch (OmpRpcProtocol.ReadString(frame, "type"))
                {
                    case OmpRpcProtocol.SessionSettledType:
                        _settled = true;
                        return;

                    case OmpRpcProtocol.PromptResultType when OmpRpcProtocol.ReadBoolean(frame, "agentInvoked") == false:
                        return;

                    case OmpRpcProtocol.ExtensionUiRequestType when OmpRpcProtocol.ReadString(frame, "method") == OmpRpcProtocol.CancelMethod:
                        _questions!.Cancel(OmpRpcProtocol.ReadString(frame, "targetId"));
                        break;
                }
            }
        }
        catch (Exception)
        {
            // The bound passed or the stream ended: closing standard input ends the rest.
        }
    }

    private async Task StopProcessAsync(bool closeInputFirst = true)
    {
        if (closeInputFirst)
        {
            await _writer.CloseAsync().ConfigureAwait(false);
        }

        try
        {
            await AwaitExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await _process.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits, bounded, for the process to exit and stops it when it does not. Output keeps being read meanwhile, so OMP never
    /// blocks on a full pipe.
    /// </summary>
    /// <returns>The exit code, or <see langword="null" /> when the process had to be stopped.</returns>
    private async Task<int?> AwaitExitAsync(CancellationToken cancellationToken)
    {
        using var drainSource = new CancellationTokenSource();
        var drain = DrainOutputAsync(drainSource.Token);
        try
        {
            return await _process.WaitForExitAsync(cancellationToken).WaitAsync(_settings.ExitTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await _process.StopAsync().ConfigureAwait(false);
            return null;
        }
        finally
        {
            await drainSource.CancelAsync().ConfigureAwait(false);
            try
            {
                await drain.WaitAsync(DrainStopTimeout).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The drain only keeps the pipe empty; nothing it saw matters any more.
            }
        }
    }

    private async Task DrainOutputAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _process.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
            }
        }
        catch (Exception)
        {
            // The stream ends, is cancelled or disposed together with the process.
        }
    }

    private async Task<string> ReadStandardErrorAsync()
    {
        try
        {
            return await _process.StandardError.WaitAsync(_settings.ExitTimeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private void Reject(string text, string source)
    {
        _rejection = text;
        _rejectionSource = source;
    }

    /// <summary>
    /// The <c>session</c> record the JSON print mode starts with and RPC mode lacks, built from the <c>get_state</c> reply.
    /// </summary>
    private string BuildSessionRecord(string sessionId)
    {
        var record = new Dictionary<string, object?>
        {
            ["type"] = "session",
            ["id"] = sessionId
        };

        if (!string.IsNullOrWhiteSpace(_settings.WorkingDirectory))
        {
            record["cwd"] = Path.GetFullPath(_settings.WorkingDirectory);
        }

        return JsonSerializer.Serialize(record);
    }

    private CliMessage CreateRejectionMessage(string text, string source, string standardError)
    {
        var payload = CreateFailurePayload(text, "rejected", standardError);
        payload["source_event_type"] = $"rpc_{source}";
        return new CliMessage("terminal.failed", JsonSerializer.SerializeToElement(payload));
    }

    private CliMessage CreateUnsettledMessage(string standardError, int exitCode, JsonElement? invalidOutputLines)
    {
        var text = new StringBuilder("OMP ended before the run settled.");
        if (!string.IsNullOrWhiteSpace(standardError))
        {
            text.AppendLine().Append(standardError.Trim());
        }

        var lines = invalidOutputLines?.EnumerateArray().Select(static line => line.GetString() ?? string.Empty).ToArray() ?? [];
        if (lines.Length > 0)
        {
            text.AppendLine().Append("OMP returned non-JSON output:").AppendLine().Append(string.Join(Environment.NewLine, lines));
        }

        var payload = CreateFailurePayload(text.ToString(), "unsettled", standardError);
        payload["exit_code"] = exitCode;
        if (lines.Length > 0)
        {
            payload["invalid_output_lines"] = lines;
        }

        return new CliMessage("terminal.failed", JsonSerializer.SerializeToElement(payload));
    }

    private CliMessage CreatePromptErrorMessage(string standardError, int exitCode)
    {
        var error = _promptError;
        var text = error is { } details && OmpRpcProtocol.ReadString(details, "message") is { Length: > 0 } message
            ? message
            : "OMP reported a failed prompt.";
        var payload = CreateFailurePayload(text, OmpRpcProtocol.ErrorStatus, standardError);
        payload["source_event_type"] = OmpRpcProtocol.PromptResultType;
        if (error is { } errorElement)
        {
            AddIfNotEmpty(payload, "provider", OmpRpcProtocol.ReadString(errorElement, "provider"));
            AddIfNotEmpty(payload, "model", OmpRpcProtocol.ReadString(errorElement, "model"));
            if (errorElement.TryGetProperty("httpStatus", out var httpStatus) && httpStatus.TryGetInt32(out var status))
            {
                payload["http_status"] = status;
            }

            if (OmpRpcProtocol.ReadBoolean(errorElement, "retryable") is { } retryable)
            {
                payload["retryable"] = retryable;
            }
        }

        if (exitCode != 0)
        {
            payload["exit_code"] = exitCode;
        }

        return new CliMessage("terminal.failed", JsonSerializer.SerializeToElement(payload));
    }

    private CliMessage CreateAbortedCompletionMessage()
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "terminal.completed",
            ["stop_reason"] = OmpRpcProtocol.AbortedStatus,
            ["source_event_type"] = OmpRpcProtocol.PromptResultType
        };

        AddSessionId(payload);
        return new CliMessage("terminal.completed", JsonSerializer.SerializeToElement(payload));
    }

    private CliMessage CreateAbortedFailureMessage(string standardError)
    {
        var payload = CreateFailurePayload("OMP aborted the run.", OmpRpcProtocol.AbortedStatus, standardError);
        payload["source_event_type"] = OmpRpcProtocol.PromptResultType;
        return new CliMessage("terminal.failed", JsonSerializer.SerializeToElement(payload));
    }

    private Dictionary<string, object?> CreateFailurePayload(string text, string stopReason, string standardError)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "terminal.failed",
            ["text"] = text,
            ["error"] = text,
            ["message"] = text,
            ["stop_reason"] = stopReason
        };

        AddSessionId(payload);
        AddIfNotEmpty(payload, "stderr", standardError.Trim());
        return payload;
    }

    private void AddSessionId(Dictionary<string, object?> payload)
    {
        if (!string.IsNullOrWhiteSpace(_sessionId))
        {
            payload["session_id"] = _sessionId;
            payload["sessionId"] = _sessionId;
        }
    }

    private static void AddIfNotEmpty(Dictionary<string, object?> payload, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            payload[key] = value;
        }
    }

    private static bool IsSuccess(JsonElement response)
    {
        return response.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;
    }

    private static string ReadError(JsonElement response)
    {
        return OmpRpcProtocol.ReadString(response, "error") is { Length: > 0 } error ? error : "no error text";
    }

    private readonly record struct LineResult(IReadOnlyList<CliMessage> Messages, bool EndsRun)
    {
        public static LineResult None => new([], false);

        public static LineResult End => new([], true);

        public static LineResult From(IReadOnlyList<CliMessage> messages) => new(messages, false);
    }
}
