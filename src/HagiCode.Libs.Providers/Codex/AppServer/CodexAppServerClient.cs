using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using HagiCode.Libs.Providers.Codex;
using ManagedCode.CodexSharpSDK.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HagiCode.Libs.Providers.Codex.AppServer;

/// <summary>
/// A stdio JSON-RPC client for <c>codex app-server</c>. It runs one turn at a time, forwards the turn's notifications as the
/// existing <see cref="ThreadEvent"/> model, and answers <c>item/tool/requestUserInput</c> server requests through the
/// user-input handler.
/// </summary>
/// <remarks>
/// Every server request is answered exactly once: with the user's answers, or with a JSON-RPC error when the question is
/// declined, times out, or the run is stopped. On codex-cli 0.163.0 the turn waits for that reply even when the request is
/// reported as non-blocking (gate G5), so an open question is never left unanswered. Stopping a run sends
/// <c>turn/interrupt</c>, which ends the turn as <c>interrupted</c> (gate G4).
/// </remarks>
internal sealed class CodexAppServerClient : IAsyncDisposable
{
    /// <summary>The Codex feature that makes <c>item/tool/requestUserInput</c> available in default collaboration mode.</summary>
    internal const string UserInputFeatureName = "default_mode_request_user_input";

    private const string ClientName = "hagicode-libs";
    private const string ClientVersion = "1.0.0";
    private const string InitializeMethod = "initialize";
    private const string InitializedMethod = "initialized";
    private const string ThreadStartMethod = "thread/start";
    private const string ThreadResumeMethod = "thread/resume";
    private const string TurnStartMethod = "turn/start";
    private const string TurnSteerMethod = "turn/steer";
    private const string TurnInterruptMethod = "turn/interrupt";
    private const string FeatureListMethod = "experimentalFeature/list";
    private const string UserInputRequestMethod = "item/tool/requestUserInput";
    private const string CommandApprovalMethod = "item/commandExecution/requestApproval";
    private const string FileChangeApprovalMethod = "item/fileChange/requestApproval";
    private const string ServerRequestResolvedMethod = "serverRequest/resolved";

    private const int DeclinedErrorCode = -32000;
    private const int MethodNotFoundErrorCode = -32601;
    private const int InvalidParamsErrorCode = -32602;
    private const string DeclinedMessage = "The user did not answer the question.";
    private const string UnsupportedRequestMessageFormat = "HagiCode does not handle the server request '{0}'.";

    internal static readonly TimeSpan InterruptTimeout = TimeSpan.FromSeconds(5);

    private readonly TextReader _reader;
    private readonly TextWriter _writer;
    private readonly ILogger _logger;
    private readonly CodexUserInputHandler? _userInputHandler;
    private readonly TimeSpan _questionTimeout;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    private readonly ConcurrentDictionary<long, PendingSteer> _pendingSteers = new();
    private readonly CodexAppServerRequestMap _serverRequests = new();
    private readonly ConcurrentDictionary<Task, byte> _questionTasks = new();
    private readonly CodexAgentWorkTracker _agentWork = new();
    private readonly object _agentWorkLock = new();
    private long _nextRequestId;
    private Task? _readLoop;
    private ActiveTurn? _activeTurn;
    private int _connectionClosed;
    private int _disposed;

    /// <summary>
    /// Initializes a client over an open app-server connection.
    /// </summary>
    /// <param name="reader">The app-server's standard output, one JSON message per line.</param>
    /// <param name="writer">The app-server's standard input.</param>
    /// <param name="userInputHandler">Answers the questions, or <see langword="null"/> when questions are not supported.</param>
    /// <param name="questionTimeout">How long a question may stay unanswered, or <see cref="Timeout.InfiniteTimeSpan"/>.</param>
    /// <param name="logger">The logger for diagnostics.</param>
    public CodexAppServerClient(
        TextReader reader,
        TextWriter writer,
        CodexUserInputHandler? userInputHandler,
        TimeSpan questionTimeout,
        ILogger? logger = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _userInputHandler = userInputHandler;
        _questionTimeout = questionTimeout;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Starts reading the connection. Call once, before any request.
    /// </summary>
    public void Start()
    {
        ThrowIfDisposed();
        if (_readLoop is not null)
        {
            return;
        }

        _readLoop = Task.Run(() => ReadLoopAsync(_shutdown.Token));
    }

    /// <summary>
    /// Runs the <c>initialize</c> handshake and then sends the <c>initialized</c> notification.
    /// </summary>
    /// <returns>The server's <c>userAgent</c>, when it was sent.</returns>
    public async Task<string?> InitializeAsync(CancellationToken cancellationToken)
    {
        var parameters = new JsonObject
        {
            ["clientInfo"] = new JsonObject
            {
                ["name"] = ClientName,
                ["version"] = ClientVersion
            },
            ["capabilities"] = new JsonObject
            {
                ["experimentalApi"] = true
            }
        };

        var result = await RequestAsync(InitializeMethod, parameters, cancellationToken).ConfigureAwait(false);
        await NotifyAsync(InitializedMethod, null, cancellationToken).ConfigureAwait(false);
        return ReadString(result, "userAgent");
    }

    /// <summary>
    /// Whether the server lists an experimental feature by name, across all pages of the list.
    /// </summary>
    public async Task<bool> HasExperimentalFeatureAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        string? cursor = null;
        do
        {
            var parameters = new JsonObject();
            if (cursor is not null)
            {
                parameters["cursor"] = cursor;
            }

            var page = await RequestAsync(FeatureListMethod, parameters, cancellationToken).ConfigureAwait(false);
            if (page.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var feature in data.EnumerateArray())
                {
                    if (string.Equals(ReadString(feature, "name"), name, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            cursor = ReadString(page, "nextCursor");
        }
        while (cursor is not null);

        return false;
    }

    /// <summary>
    /// Starts a new thread and returns its id.
    /// </summary>
    public async Task<string> StartThreadAsync(JsonObject threadParameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(threadParameters);
        var result = await RequestAsync(ThreadStartMethod, threadParameters, cancellationToken).ConfigureAwait(false);
        return ReadThreadId(result);
    }

    /// <summary>
    /// Resumes a stored thread and returns its id.
    /// </summary>
    public async Task<string> ResumeThreadAsync(string threadId, JsonObject threadParameters, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentNullException.ThrowIfNull(threadParameters);

        var parameters = new JsonObject(threadParameters.Select(static pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone())))
        {
            ["threadId"] = threadId
        };
        var result = await RequestAsync(ThreadResumeMethod, parameters, cancellationToken).ConfigureAwait(false);
        return ReadThreadId(result);
    }

    /// <summary>
    /// Sends one turn and yields its events until the turn completes, fails, or is stopped.
    /// </summary>
    /// <remarks>
    /// When <paramref name="cancellationToken"/> is cancelled, the open questions are declined, <c>turn/interrupt</c> is sent,
    /// and the enumeration throws <see cref="OperationCanceledException"/>, the same as the exec path does.
    /// </remarks>
    public async IAsyncEnumerable<ThreadEvent> RunTurnAsync(
        string threadId,
        JsonArray input,
        JsonObject turnParameters,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(turnParameters);

        var turn = BeginTurn(threadId, cancellationToken);
        try
        {
            // A subagent change observed between turns is delivered first, before this turn's events.
            FlushAgentWork(turn);

            var startParameters = new JsonObject(turnParameters.Select(static pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone())))
            {
                ["threadId"] = threadId,
                ["input"] = input
            };
            var startResponse = await RequestAsync(TurnStartMethod, startParameters, cancellationToken).ConfigureAwait(false);
            var turnId = ReadTurnId(startResponse);
            if (string.IsNullOrWhiteSpace(turnId))
            {
                throw new InvalidOperationException("codex app-server did not return a turn id for turn/start.");
            }

            lock (turn.LifecycleGate)
            {
                turn.TurnId = turnId;
            }

            while (true)
            {
                bool hasMore;
                try
                {
                    hasMore = await turn.Events.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    ResolvePendingSteers(turn);
                    await StopTurnAsync(threadId, turn).ConfigureAwait(false);
                    throw;
                }

                if (!hasMore)
                {
                    yield break;
                }

                while (turn.Events.Reader.TryRead(out var threadEvent))
                {
                    yield return threadEvent;
                    if (threadEvent is TurnCompletedEvent or TurnFailedEvent)
                    {
                        yield break;
                    }
                }
            }
        }
        finally
        {
            EndTurn(turn);
        }
    }

    /// <summary>
    /// Sends a native steer request to the currently active turn on this connection. The request is never translated into a
    /// turn/start call. Its acknowledgement is correlated to the original steer identity and expected turn, with unresolved
    /// delivery bounded by <paramref name="deliveryTimeout"/>.
    /// </summary>
    /// <param name="steerId">The caller-provided identity for this steer.</param>
    /// <param name="threadId">The thread whose active turn should receive the steer.</param>
    /// <param name="text">The follow-up message to deliver.</param>
    /// <param name="deliveryTimeout">A positive finite deadline for the acknowledgement.</param>
    /// <param name="steering">The execution channel that receives the correlated outcome.</param>
    /// <param name="cancellationToken">Cancels the request write if delivery has not completed.</param>
    /// <returns><see cref="CodexSteerResult.Sent"/> when the request was written; confirmation is reported later as an outcome.</returns>
    public async Task<CodexSteerResult> TrySteerAsync(
        string steerId,
        string threadId,
        string text,
        TimeSpan deliveryTimeout,
        CodexRunSteering steering,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(steering);
        if (deliveryTimeout == Timeout.InfiniteTimeSpan || deliveryTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(deliveryTimeout), "The steering delivery timeout must be positive and finite.");
        }

        if (_disposed == 1 || _connectionClosed == 1)
        {
            return CodexSteerResult.NotSteerable;
        }

        var turn = Volatile.Read(ref _activeTurn);
        if (turn is null)
        {
            return CodexSteerResult.NotSteerable;
        }

        string expectedTurnId;
        var requestId = Interlocked.Increment(ref _nextRequestId);
        PendingSteer pending;
        lock (turn.LifecycleGate)
        {
            if (turn.IsClosing || !string.Equals(turn.ThreadId, threadId, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(turn.TurnId)
                || !turn.SeenSteerIds.Add(steerId))
            {
                return CodexSteerResult.NotSteerable;
            }

            expectedTurnId = turn.TurnId;
            pending = new PendingSteer(requestId, steerId, threadId, expectedTurnId, turn, steering);
            if (!_pendingSteers.TryAdd(requestId, pending))
            {
                turn.SeenSteerIds.Remove(steerId);
                return CodexSteerResult.NotSteerable;
            }
        }

        var input = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = text,
                ["text_elements"] = new JsonArray()
            }
        };
        var parameters = new JsonObject
        {
            ["threadId"] = threadId,
            ["expectedTurnId"] = expectedTurnId,
            ["clientUserMessageId"] = steerId,
            ["input"] = input
        };

        Task<JsonElement> responseTask;
        try
        {
            responseTask = await SendRequestFrameAsync(requestId, TurnSteerMethod, parameters, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidOperationException)
        {
            RemoveUnsentSteer(pending);
            return CodexSteerResult.NotSteerable;
        }

        _ = ObserveSteerOutcomeAsync(pending, responseTask, deliveryTimeout);
        return CodexSteerResult.Sent;
    }

    /// <summary>
    /// Delivers the user's answer to an open question, or declines it when <paramref name="response"/> is <see langword="null"/>.
    /// Only the first delivery for a request id takes effect.
    /// </summary>
    /// <returns><see langword="true"/> when this call answered the request.</returns>
    public async Task<bool> RespondToUserQuestionAsync(
        string requestId,
        CodexUserQuestionResponse? response,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);

        if (!_serverRequests.TryBeginResponse(requestId, out var rawId))
        {
            return false;
        }

        var message = response is null
            ? BuildErrorReply(rawId, DeclinedErrorCode, DeclinedMessage)
            : BuildAnswerReply(rawId, response);
        await WriteAsync(message, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        FailPendingRequests(new OperationCanceledException("The codex app-server connection was closed."));
        ResolvePendingSteers();
        _activeTurn?.Events.Writer.TryComplete(new IOException("The codex app-server connection was closed."));

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The read loop ends when the process is disposed; a stuck read is not worth waiting for.
            }
        }

        _shutdown.Dispose();
        _writeGate.Dispose();
    }

    private ActiveTurn BeginTurn(string threadId, CancellationToken cancellationToken)
    {
        var turn = new ActiveTurn(threadId, cancellationToken);
        if (Interlocked.CompareExchange(ref _activeTurn, turn, null) is not null)
        {
            turn.Dispose();
            throw new InvalidOperationException("Only one turn can run on a codex app-server connection at a time.");
        }

        return turn;
    }

    private void EndTurn(ActiveTurn turn)
    {
        Interlocked.CompareExchange(ref _activeTurn, null, turn);
        turn.Events.Writer.TryComplete();
        turn.Dispose();
    }

    private async Task StopTurnAsync(string threadId, ActiveTurn turn)
    {
        // Open questions are declined first, so no answer can arrive after the stop.
        foreach (var openRequest in _serverRequests.OpenKeys())
        {
            try
            {
                await RespondToUserQuestionAsync(openRequest, null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                _logger.LogDebug(exception, "Could not decline user question {RequestId} while stopping.", openRequest);
            }
        }

        if (turn.TurnId is null)
        {
            return;
        }

        try
        {
            using var interruptCts = new CancellationTokenSource(InterruptTimeout);
            await RequestAsync(
                TurnInterruptMethod,
                new JsonObject { ["threadId"] = threadId, ["turnId"] = turn.TurnId },
                interruptCts.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not ObjectDisposedException)
        {
            _logger.LogWarning(exception, "codex app-server did not confirm turn/interrupt for turn {TurnId}.", turn.TurnId);
        }
    }

    private async Task<JsonElement> RequestAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = completion;

        using var registration = cancellationToken.Register(() =>
        {
            if (_pendingRequests.TryRemove(id, out var pending))
            {
                pending.TrySetCanceled(cancellationToken);
            }
        });

        var message = new JsonObject
        {
            ["id"] = id,
            ["method"] = method
        };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        try
        {
            await WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _pendingRequests.TryRemove(id, out _);
            throw;
        }

        return await completion.Task.ConfigureAwait(false);
    }

    private async Task<Task<JsonElement>> SendRequestFrameAsync(
        long id,
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingRequests.TryAdd(id, completion))
        {
            throw new InvalidOperationException($"A codex app-server request with id {id} is already pending.");
        }

        var message = new JsonObject
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters
        };

        try
        {
            await WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (_pendingRequests.TryRemove(id, out var pending))
            {
                pending.TrySetCanceled(cancellationToken);
            }

            throw;
        }

        return completion.Task;
    }

    private async Task ObserveSteerOutcomeAsync(
        PendingSteer pending,
        Task<JsonElement> responseTask,
        TimeSpan deliveryTimeout)
    {
        try
        {
            var response = await responseTask.WaitAsync(deliveryTimeout).ConfigureAwait(false);
            var acknowledgedTurnId = ReadString(response, "turnId");
            ResolveSteer(
                pending,
                string.Equals(acknowledgedTurnId, pending.ExpectedTurnId, StringComparison.Ordinal)
                    ? CodexSteerOutcome.Consumed
                    : CodexSteerOutcome.NotConsumed);
        }
        catch (TimeoutException)
        {
            ResolveSteer(pending, CodexSteerOutcome.NotConsumed);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Codex steer {SteerId} was not confirmed by the active turn.", pending.SteerId);
            ResolveSteer(pending, CodexSteerOutcome.NotConsumed);
        }
    }

    private void ResolvePendingSteers(ActiveTurn? turn = null)
    {
        foreach (var pending in _pendingSteers.Values)
        {
            if (turn is null || ReferenceEquals(pending.Turn, turn))
            {
                ResolveSteer(pending, CodexSteerOutcome.NotConsumed);
            }
        }
    }

    private void ResolveSteer(PendingSteer pending, CodexSteerOutcome outcome)
    {
        if (!pending.TryResolve())
        {
            return;
        }

        _pendingSteers.TryRemove(pending.RequestId, out _);
        CancelPendingRequest(pending.RequestId);
        try
        {
            pending.Steering.Report(pending.SteerId, outcome);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "A Codex steering outcome observer failed for steer {SteerId}.", pending.SteerId);
        }
    }

    private void RemoveUnsentSteer(PendingSteer pending)
    {
        _pendingSteers.TryRemove(pending.RequestId, out _);
        CancelPendingRequest(pending.RequestId);
        lock (pending.Turn.LifecycleGate)
        {
            pending.Turn.SeenSteerIds.Remove(pending.SteerId);
        }
    }

    private void CancelPendingRequest(long requestId)
    {
        if (_pendingRequests.TryRemove(requestId, out var completion))
        {
            completion.TrySetCanceled();
        }
    }

    private async Task NotifyAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        var message = new JsonObject { ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        await WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var line = message.ToJsonString();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                HandleLine(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disposal is in progress.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "codex app-server read loop failed.");
        }
        finally
        {
            Volatile.Write(ref _connectionClosed, 1);
            FailPendingRequests(new IOException("The codex app-server process exited before it answered."));
            ResolvePendingSteers();
            _activeTurn?.Events.Writer.TryComplete(new IOException("The codex app-server process exited during a turn."));
        }
    }

    private void HandleLine(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement.Clone();
        var hasId = root.TryGetProperty("id", out var idElement);
        var hasMethod = root.TryGetProperty("method", out var methodElement);

        if (hasMethod && hasId)
        {
            var parameters = root.TryGetProperty("params", out var paramsElement) ? paramsElement : default;
            HandleServerRequest(methodElement.GetString() ?? string.Empty, idElement, parameters);
            return;
        }

        if (hasMethod)
        {
            var parameters = root.TryGetProperty("params", out var paramsElement) ? paramsElement : default;
            HandleNotification(methodElement.GetString() ?? string.Empty, parameters);
            return;
        }

        if (hasId)
        {
            HandleResponse(idElement, root);
        }
    }

    private void HandleResponse(JsonElement idElement, JsonElement root)
    {
        if (idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetInt64(out var id))
        {
            return;
        }

        if (!_pendingRequests.TryRemove(id, out var completion))
        {
            return;
        }

        _pendingSteers.TryGetValue(id, out var pendingSteer);

        if (root.TryGetProperty("error", out var error))
        {
            completion.TrySetException(new InvalidOperationException(
                ReadString(error, "message") ?? "codex app-server returned an error."));
            if (pendingSteer is not null)
            {
                ResolveSteer(pendingSteer, CodexSteerOutcome.NotConsumed);
            }

            return;
        }

        var result = root.TryGetProperty("result", out var resultElement) ? resultElement : default;
        completion.TrySetResult(result);
        if (pendingSteer is not null)
        {
            var acknowledgedTurnId = ReadString(result, "turnId");
            ResolveSteer(
                pendingSteer,
                string.Equals(acknowledgedTurnId, pendingSteer.ExpectedTurnId, StringComparison.Ordinal)
                    ? CodexSteerOutcome.Consumed
                    : CodexSteerOutcome.NotConsumed);
        }
    }

    private void HandleNotification(string method, JsonElement parameters)
    {
        if (method == ServerRequestResolvedMethod)
        {
            _logger.LogDebug("codex app-server resolved a server request: {Parameters}", parameters.GetRawText());
            return;
        }

        // Every notification is observed, including child-thread traffic that the turn filter below drops.
        ObserveAgentWork(method, parameters);

        var turn = _activeTurn;
        if (turn is null)
        {
            return;
        }

        FlushAgentWork(turn);

        if (method == "turn/started")
        {
            var startedTurnId = ReadTurnId(parameters);
            if (!string.IsNullOrWhiteSpace(startedTurnId))
            {
                lock (turn.LifecycleGate)
                {
                    turn.TurnId ??= startedTurnId;
                }
            }
        }

        ThreadEvent? threadEvent;
        try
        {
            threadEvent = turn.Mapper.Map(method, parameters, turn.TurnId);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not map codex app-server notification {Method}.", method);
            return;
        }

        if (threadEvent is not null)
        {
            var failedTurn = threadEvent is TurnFailedEvent;
            if (threadEvent is TurnCompletedEvent or TurnFailedEvent)
            {
                lock (turn.LifecycleGate)
                {
                    turn.IsClosing = true;
                }
            }

            if (failedTurn)
            {
                ResolvePendingSteers(turn);
            }

            turn.Events.Writer.TryWrite(threadEvent);
        }
    }

    private void ObserveAgentWork(string method, JsonElement parameters)
    {
        try
        {
            lock (_agentWorkLock)
            {
                _agentWork.Observe(method, parameters);
            }
        }
        catch (Exception exception)
        {
            // Observation is informational: a failure here must not change the transcript or the turn.
            _logger.LogWarning(exception, "Could not observe codex agent work in notification {Method}.", method);
        }
    }

    private void FlushAgentWork(ActiveTurn turn)
    {
        try
        {
            // The write happens under the lock so snapshots reach the stream in the order they were taken.
            lock (_agentWorkLock)
            {
                if (_agentWork.TakeSnapshot() is { } snapshot)
                {
                    turn.Events.Writer.TryWrite(new CodexAgentWorkSnapshotEvent(snapshot));
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not build the codex agent work snapshot.");
        }
    }

    private void HandleServerRequest(string method, JsonElement idElement, JsonElement parameters)
    {
        var rawId = JsonNode.Parse(idElement.GetRawText());
        var key = idElement.GetRawText();

        switch (method)
        {
            case UserInputRequestMethod:
                StartQuestion(key, rawId, parameters);
                return;

            case CommandApprovalMethod:
            case FileChangeApprovalMethod:
                // Interactive runs keep the approval policy at "never"; a stray approval is declined rather than left open.
                _ = WriteOnceAsync(key, BuildResultReply(rawId, new JsonObject { ["decision"] = "decline" }));
                return;

            default:
                _logger.LogWarning("codex app-server sent an unsupported server request {Method}.", method);
                _ = WriteOnceAsync(
                    key,
                    BuildErrorReply(rawId, MethodNotFoundErrorCode, string.Format(UnsupportedRequestMessageFormat, method)));
                return;
        }
    }

    private void StartQuestion(string key, JsonNode? rawId, JsonElement parameters)
    {
        CodexUserQuestionRequest request;
        try
        {
            request = ParseQuestionRequest(key, parameters);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or KeyNotFoundException)
        {
            _logger.LogWarning(exception, "codex app-server sent a malformed user-input request {RequestId}.", key);
            _ = WriteOnceAsync(key, BuildErrorReply(rawId, InvalidParamsErrorCode, "Malformed user-input request."));
            return;
        }

        if (!_serverRequests.TryRegister(key, rawId))
        {
            _logger.LogDebug("Ignoring a duplicate user-input request {RequestId}.", key);
            return;
        }

        if (_userInputHandler is null)
        {
            _ = WriteOnceAsync(key, BuildErrorReply(rawId, DeclinedErrorCode, DeclinedMessage));
            return;
        }

        // The question is announced in the turn's stream before its handler runs, so the caller shows it in order.
        _activeTurn?.Events.Writer.TryWrite(new CodexUserQuestionEvent(request));

        var runToken = _activeTurn?.CancellationToken ?? _shutdown.Token;
        var task = Task.Run(() => AnswerQuestionAsync(request, runToken));
        _questionTasks[task] = 0;
        _ = task.ContinueWith(static (finished, state) =>
        {
            if (state is ConcurrentDictionary<Task, byte> tasks)
            {
                tasks.TryRemove(finished, out _);
            }
        }, _questionTasks, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task AnswerQuestionAsync(CodexUserQuestionRequest request, CancellationToken runToken)
    {
        using var questionCts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
        if (_questionTimeout != Timeout.InfiniteTimeSpan)
        {
            questionCts.CancelAfter(_questionTimeout);
        }

        CodexUserQuestionResponse? response = null;
        try
        {
            var handlerTask = _userInputHandler!(request, questionCts.Token);
            response = await handlerTask.WaitAsync(questionCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runToken.IsCancellationRequested)
        {
            // The run was stopped; the question is declined below and the interrupt ends the turn.
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("User question {RequestId} timed out and was declined.", request.RequestId);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The user-input handler failed for question {RequestId}; it was declined.", request.RequestId);
        }

        try
        {
            await RespondToUserQuestionAsync(request.RequestId, response, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug(exception, "Could not deliver the reply to user question {RequestId}; the connection is closed.", request.RequestId);
        }
    }

    private async Task WriteOnceAsync(string key, JsonObject reply)
    {
        try
        {
            await WriteAsync(reply, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            _logger.LogDebug(exception, "Could not reply to codex app-server request {RequestId}.", key);
        }
    }

    private void FailPendingRequests(Exception exception)
    {
        foreach (var (id, completion) in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(id, out _))
            {
                completion.TrySetException(exception);
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed == 1, nameof(CodexAppServerClient));

    private static CodexUserQuestionRequest ParseQuestionRequest(string key, JsonElement parameters)
    {
        var questions = new List<CodexUserQuestion>();
        if (parameters.TryGetProperty("questions", out var questionArray) && questionArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var question in questionArray.EnumerateArray())
            {
                var options = new List<CodexUserQuestionOption>();
                if (question.TryGetProperty("options", out var optionArray) && optionArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var option in optionArray.EnumerateArray())
                    {
                        options.Add(new CodexUserQuestionOption(
                            ReadString(option, "label") ?? string.Empty,
                            ReadString(option, "description") ?? string.Empty));
                    }
                }

                questions.Add(new CodexUserQuestion(
                    ReadString(question, "id") ?? throw new InvalidOperationException("question id is missing"),
                    ReadString(question, "header") ?? string.Empty,
                    ReadString(question, "question") ?? string.Empty,
                    ReadBool(question, "isOther"),
                    ReadBool(question, "isSecret"),
                    options));
            }
        }

        // A request without isBlocking is treated as blocking, the pre-0.163.0 behavior (design A4).
        var isBlocking = !parameters.TryGetProperty("isBlocking", out var blockingElement)
                         || blockingElement.ValueKind != JsonValueKind.False;
        long? autoResolutionMs = parameters.TryGetProperty("autoResolutionMs", out var autoElement)
                                 && autoElement.ValueKind == JsonValueKind.Number
            ? autoElement.GetInt64()
            : null;

        return new CodexUserQuestionRequest(
            key,
            ReadString(parameters, "threadId") ?? string.Empty,
            ReadString(parameters, "turnId") ?? string.Empty,
            ReadString(parameters, "itemId") ?? string.Empty,
            isBlocking,
            questions,
            autoResolutionMs);
    }

    private static JsonObject BuildAnswerReply(JsonNode? rawId, CodexUserQuestionResponse response)
    {
        var answers = new JsonObject();
        foreach (var (questionId, values) in response.Answers)
        {
            var array = new JsonArray();
            foreach (var value in values)
            {
                array.Add(value);
            }

            answers[questionId] = new JsonObject { ["answers"] = array };
        }

        return BuildResultReply(rawId, new JsonObject { ["answers"] = answers });
    }

    private static JsonObject BuildResultReply(JsonNode? rawId, JsonObject result)
    {
        return new JsonObject
        {
            ["id"] = rawId?.DeepClone(),
            ["result"] = result
        };
    }

    private static JsonObject BuildErrorReply(JsonNode? rawId, int code, string message)
    {
        return new JsonObject
        {
            ["id"] = rawId?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };
    }

    private static string? ReadString(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(property, out var value)
               && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool ReadBool(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(property, out var value)
               && value.ValueKind == JsonValueKind.True;
    }

    private static string? ReadTurnId(JsonElement result)
    {
        return result.ValueKind == JsonValueKind.Object
               && result.TryGetProperty("turn", out var turn)
            ? ReadString(turn, "id")
            : null;
    }

    private static string ReadThreadId(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("thread", out var thread)
            && ReadString(thread, "id") is { Length: > 0 } id)
        {
            return id;
        }

        throw new InvalidOperationException("codex app-server did not return a thread id.");
    }

    private sealed class PendingSteer(
        long requestId,
        string steerId,
        string threadId,
        string expectedTurnId,
        ActiveTurn turn,
        CodexRunSteering steering)
    {
        private int _resolved;

        public long RequestId { get; } = requestId;

        public string SteerId { get; } = steerId;

        public string ThreadId { get; } = threadId;

        public string ExpectedTurnId { get; } = expectedTurnId;

        public ActiveTurn Turn { get; } = turn;

        public CodexRunSteering Steering { get; } = steering;

        public bool TryResolve() => Interlocked.Exchange(ref _resolved, 1) == 0;
    }

    /// <summary>
    /// The state of the one turn a client runs: its identity, event channel, and cancellation of its questions.
    /// </summary>
    private sealed class ActiveTurn : IDisposable
    {
        private readonly CancellationTokenSource _questionScope;

        public ActiveTurn(string threadId, CancellationToken cancellationToken)
        {
            ThreadId = threadId;
            _questionScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Events = Channel.CreateUnbounded<ThreadEvent>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
        }

        public CodexAppServerEventMapper Mapper { get; } = new();

        public object LifecycleGate { get; } = new();

        public string ThreadId { get; }

        public Channel<ThreadEvent> Events { get; }

        public string? TurnId { get; set; }

        public bool IsClosing { get; set; }

        public HashSet<string> SeenSteerIds { get; } = new(StringComparer.Ordinal);

        public CancellationToken CancellationToken => _questionScope.Token;

        public void Dispose()
        {
            _questionScope.Cancel();
            _questionScope.Dispose();
        }
    }
}
