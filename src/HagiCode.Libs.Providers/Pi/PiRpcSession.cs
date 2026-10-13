using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.Pi;

/// <summary>
/// Serializes the writes the provider makes to Pi's standard input.
/// </summary>
/// <remarks>
/// The read loop (commands, answers to foreign dialogs) and the background task of every open question write concurrently, and
/// a write is a plain write plus flush on one stream, so two unsynchronized writers could interleave inside one JSON line. One
/// gate per execution keeps every line whole.
/// </remarks>
internal sealed class PiRpcWriter(IPiRpcProcess process)
{
    private static readonly TimeSpan CloseWaitTimeout = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _closed;

    /// <summary>
    /// Writes one line, waiting for any write that is already in progress.
    /// </summary>
    /// <returns>
    /// <see langword="false" /> when Pi no longer reads its input (closed, or the process is gone), so the caller can let the
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
    /// Closes Pi's standard input after the write in progress, which asks Pi for an orderly shutdown. Later writes do nothing.
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
internal sealed class PiRpcOutcome
{
    /// <summary>
    /// The standard error of a Pi that refused to start because a user extension registers <c>ask_user_question</c> as well.
    /// When set, the run produced no message and the provider falls back to the JSON print path.
    /// </summary>
    public string? QuestionToolConflict { get; set; }
}

/// <summary>
/// The settings of one RPC run.
/// </summary>
/// <param name="RequestedSessionId">The session id the caller asked for, for the started or resumed comparison.</param>
/// <param name="WorkingDirectory">The working directory of the process, reported on the session message.</param>
/// <param name="UserInputHandler">Answers the question dialogs.</param>
/// <param name="QuestionTimeout">How long a question may stay unanswered.</param>
/// <param name="ExitTimeout">How long Pi gets to exit after its input is closed before the process is stopped.</param>
internal sealed record PiRpcSessionSettings(
    string? RequestedSessionId,
    string? WorkingDirectory,
    Func<PiUserInputRequest, CancellationToken, Task<PiUserInputResponse?>> UserInputHandler,
    TimeSpan QuestionTimeout,
    TimeSpan ExitTimeout);

/// <summary>
/// Drives one <c>pi --mode rpc</c> process: the <c>get_state</c> handshake, the prompt, the question dialogs and the end of
/// the run, and turns Pi's output into the shared message contract of the JSON print path.
/// </summary>
/// <remarks>
/// <para>
/// Everything that is not RPC transport goes through the unchanged <see cref="PiJsonEventMapper.StreamingState" />: the RPC
/// stream carries the same session events as <c>--mode json</c>, only the session header is missing, and it is synthesized
/// from the <c>get_state</c> reply so the session lifecycle messages are identical on both paths.
/// </para>
/// <para>
/// The run ends on <c>agent_settled</c>, not on process exit. Standard input is then closed, which makes Pi exit.
/// </para>
/// </remarks>
internal sealed class PiRpcSession
{
    private static readonly TimeSpan DialogDrainTimeout = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan AbortWriteTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DrainStopTimeout = TimeSpan.FromSeconds(1);

    private readonly IPiRpcProcess _process;
    private readonly PiJsonEventMapper.StreamingState _eventState;
    private readonly PiRpcSessionSettings _settings;
    private readonly PiRpcOutcome _outcome;
    private readonly PiRpcWriter _writer;

    private PiPendingDialogs? _dialogs;
    private string _prompt = string.Empty;
    private string? _sessionId;
    private string? _rejection;
    private string? _rejectionSource;
    private bool _settled;
    private bool _promptHandled;
    private int _recordsRead;

    /// <summary>
    /// Initializes a new instance of the <see cref="PiRpcSession" /> class.
    /// </summary>
    /// <param name="process">The started Pi process.</param>
    /// <param name="eventState">The streaming state of the event mapper that normalizes Pi's events.</param>
    /// <param name="settings">The settings of the run.</param>
    /// <param name="outcome">Receives what the run found out besides its messages.</param>
    public PiRpcSession(
        IPiRpcProcess process,
        PiJsonEventMapper.StreamingState eventState,
        PiRpcSessionSettings settings,
        PiRpcOutcome outcome)
    {
        _process = process;
        _eventState = eventState;
        _settings = settings;
        _outcome = outcome;
        _writer = new PiRpcWriter(process);
    }

    /// <summary>
    /// Runs the prompt and streams the normalized messages, ending with the terminal message. A cancellation closes the open
    /// dialogs, aborts the run, ends the process and surfaces as an <see cref="OperationCanceledException" />.
    /// </summary>
    public async IAsyncEnumerable<CliMessage> RunAsync(
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _prompt = prompt;
        using var dialogs = new PiPendingDialogs(_settings.UserInputHandler, _settings.QuestionTimeout, _writer, cancellationToken);
        _dialogs = dialogs;
        var ended = false;

        // try/finally is allowed around a yield, unlike catch, so failures are captured into pendingException by small
        // try/catch blocks that contain no yield. Whichever way the run ends, the dialogs that still wait are released.
        try
        {
            ExceptionDispatchInfo? pendingException = null;
            try
            {
                // A false result means Pi is already gone; the read loop below reports how it ended.
                await _writer.TrySendAsync(PiRpcProtocol.BuildGetState(), cancellationToken).ConfigureAwait(false);
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
            dialogs.CancelOutstanding();
            await dialogs.WaitForOutstandingAsync(DialogDrainTimeout).ConfigureAwait(false);

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

        _recordsRead++;

        JsonElement record;
        try
        {
            using var document = JsonDocument.Parse(line);
            record = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Not protocol data: the mapper records it as an invalid output line, as on the JSON path.
            return LineResult.From(_eventState.ProcessOutputLine(line));
        }

        switch (PiRpcProtocol.ReadString(record, "type"))
        {
            case PiRpcProtocol.ResponseType:
                return await HandleResponseAsync(record, cancellationToken).ConfigureAwait(false);

            case PiRpcProtocol.ExtensionUiRequestType:
                return await HandleDialogAsync(record, cancellationToken).ConfigureAwait(false);

            case PiRpcProtocol.AgentSettledType:
                _settled = true;
                return LineResult.End;

            default:
                return LineResult.From(_eventState.ProcessOutputLine(line));
        }
    }

    private async Task<LineResult> HandleResponseAsync(JsonElement response, CancellationToken cancellationToken)
    {
        switch (PiRpcProtocol.ReadString(response, "id"))
        {
            case PiRpcProtocol.StateRequestId:
                return await HandleStateResponseAsync(response, cancellationToken).ConfigureAwait(false);

            case PiRpcProtocol.PromptRequestId:
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
            Reject($"Pi rejected get_state: {ReadError(response)}", "get_state");
            return LineResult.End;
        }

        var sessionId = response.TryGetProperty("data", out var data) ? PiRpcProtocol.ReadString(data, "sessionId") : null;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            Reject("Pi did not report a session id in its get_state reply.", "get_state");
            return LineResult.End;
        }

        _sessionId = sessionId;
        var messages = _eventState.ProcessOutputLine(BuildSessionRecord(sessionId));

        // The prompt follows the state reply, so the session message always precedes the run's own messages. A false result
        // means Pi is already gone; the read loop reports how it ended.
        await _writer.TrySendAsync(PiRpcProtocol.BuildPrompt(_prompt), cancellationToken).ConfigureAwait(false);
        return LineResult.From(messages);
    }

    private LineResult HandlePromptResponse(JsonElement response)
    {
        if (!IsSuccess(response))
        {
            // A rejected prompt starts no run, so no agent_settled follows.
            Reject(ReadError(response), "prompt");
            return LineResult.End;
        }

        // "handled": an extension command or input handler consumed the prompt, no run started and none will settle.
        if (response.TryGetProperty("data", out var data) && PiRpcProtocol.ReadString(data, "disposition") == "handled")
        {
            _promptHandled = true;
            return LineResult.End;
        }

        return LineResult.None;
    }

    private async Task<LineResult> HandleDialogAsync(JsonElement request, CancellationToken cancellationToken)
    {
        var method = PiRpcProtocol.ReadString(request, "method");
        var dialogId = PiRpcProtocol.ReadString(request, "id");

        // notify, setStatus, setWidget, setTitle and set_editor_text expect no response and carry nothing the caller needs.
        if (dialogId is not { Length: > 0 } || !PiRpcProtocol.ExpectsDialogResponse(method))
        {
            return LineResult.None;
        }

        var message = new CliMessage(PiUserInputRequest.MessageType, request);
        if (method == "input" && PiUserInputRequest.TryFromCliMessage(message, out var question))
        {
            // The handler starts before the caller sees the question, exactly as for Claude; its answer is written from its
            // own task, so the read loop never waits for a person.
            return _dialogs!.Start(question) ? LineResult.From([message]) : LineResult.None;
        }

        // A dialog of a user extension (select, confirm, editor, an input without the bundled marker or with another wire
        // version): there is nobody to ask, so it is dismissed. The extension sees what it sees when a user presses escape.
        await _writer.TrySendAsync(PiRpcProtocol.BuildDialogCancelled(dialogId), cancellationToken).ConfigureAwait(false);
        return LineResult.None;
    }

    /// <summary>
    /// Ends a run whose read loop is over: no more dialogs, standard input closed, a bounded wait for the exit, then the
    /// terminal message.
    /// </summary>
    private async Task<IReadOnlyList<CliMessage>> EndRunAsync(CancellationToken cancellationToken)
    {
        _dialogs!.CancelOutstanding();
        await _writer.CloseAsync().ConfigureAwait(false);

        // A Pi that settled has its result; one that does not exit in time is stopped and still counts as having completed.
        var runIsOver = _settled || _promptHandled || _rejection is not null;
        var exitCode = await AwaitExitAsync(cancellationToken).ConfigureAwait(false) ?? (runIsOver ? 0 : 1);

        var standardError = await ReadStandardErrorAsync().ConfigureAwait(false);

        if (_rejection is not null)
        {
            return [CreateRejectionMessage(_rejection, _rejectionSource!, standardError)];
        }

        if (_settled || _promptHandled)
        {
            return _eventState.Complete(exitCode, standardError);
        }

        // The stream ended before agent_settled.
        if (_recordsRead == 0 && exitCode != 0 && PiRpcProtocol.IsQuestionToolConflict(standardError))
        {
            _outcome.QuestionToolConflict = standardError.Trim();
            return [];
        }

        // A failure the events themselves reported (a failed turn names its source event) or a non-zero exit keeps the mapper's
        // diagnostic. Exit code 0 without a settled run is otherwise not a completion, whatever the events said.
        var completion = _eventState.Complete(exitCode, standardError);
        if (exitCode != 0 || completion.Any(static message => message.Type == "terminal.failed" && message.Content.TryGetProperty("source_event_type", out _)))
        {
            return completion;
        }

        return [CreateUnsettledMessage(standardError, exitCode)];
    }

    /// <summary>
    /// The caller cancelled (or stopped enumerating): the open dialogs are dismissed, the run is aborted, standard input is
    /// closed and the process is stopped when it does not exit within the bound.
    /// </summary>
    private async Task CancelRunAsync()
    {
        // Each dialog task writes its cancelled response when the caller's token is cancelled, so the extension's tool call
        // ends at once; the signal the extension passes to its dialog does the same when Pi aborts.
        await _dialogs!.WaitForOutstandingAsync(DialogDrainTimeout).ConfigureAwait(false);

        using (var abortSource = new CancellationTokenSource(AbortWriteTimeout))
        {
            try
            {
                await _writer.TrySendAsync(PiRpcProtocol.BuildAbort(), abortSource.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort: the process is stopped below when it does not exit.
            }
        }

        // Pi handles the abort before it notices the closed input, then exits; it never exits on its own otherwise.
        await _writer.CloseAsync().ConfigureAwait(false);
        await StopProcessAsync(closeInputFirst: false).ConfigureAwait(false);
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
    /// Waits, bounded, for the process to exit and stops it when it does not. Output keeps being read meanwhile, so Pi never
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
    /// It carries no timestamp, so the mapper compares the reported id with the requested one.
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
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "terminal.failed",
            ["text"] = text,
            ["error"] = text,
            ["message"] = text,
            ["stop_reason"] = "rejected",
            ["source_event_type"] = $"rpc_{source}"
        };

        AddSessionId(payload);
        if (!string.IsNullOrWhiteSpace(standardError))
        {
            payload["stderr"] = standardError.Trim();
        }

        return new CliMessage("terminal.failed", JsonSerializer.SerializeToElement(payload));
    }

    private CliMessage CreateUnsettledMessage(string standardError, int exitCode)
    {
        var text = string.IsNullOrWhiteSpace(standardError)
            ? "Pi ended before the run settled."
            : $"Pi ended before the run settled.{Environment.NewLine}{standardError.Trim()}";
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "terminal.failed",
            ["text"] = text,
            ["error"] = text,
            ["message"] = text,
            ["stop_reason"] = "unsettled",
            ["exit_code"] = exitCode
        };

        AddSessionId(payload);
        if (!string.IsNullOrWhiteSpace(standardError))
        {
            payload["stderr"] = standardError.Trim();
        }

        return new CliMessage("terminal.failed", JsonSerializer.SerializeToElement(payload));
    }

    private void AddSessionId(Dictionary<string, object?> payload)
    {
        if (!string.IsNullOrWhiteSpace(_sessionId))
        {
            payload["session_id"] = _sessionId;
            payload["sessionId"] = _sessionId;
        }
    }

    private static bool IsSuccess(JsonElement response)
    {
        return response.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;
    }

    private static string ReadError(JsonElement response)
    {
        return PiRpcProtocol.ReadString(response, "error") is { Length: > 0 } error ? error : "no error text";
    }

    private readonly record struct LineResult(IReadOnlyList<CliMessage> Messages, bool EndsRun)
    {
        public static LineResult None => new([], false);

        public static LineResult End => new([], true);

        public static LineResult From(IReadOnlyList<CliMessage> messages) => new(messages, false);
    }
}
