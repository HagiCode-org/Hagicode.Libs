using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace HagiCode.Libs.Providers.OpenCode;

/// <summary>
/// Talks to the OpenCode 2.0 <c>/api/*</c> HTTP interface with Basic credentials (username <c>opencode</c>).
/// </summary>
public sealed class OpenCodeHttpClient : IDisposable
{
    internal const string BasicUsername = "opencode";

    private const int BodyExcerptLength = 200;

    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string? _directory;

    /// <param name="httpClient">The connection's HTTP client. Basic credentials are set on it when a password is supplied.</param>
    /// <param name="directory">Working directory sent as <c>location.directory</c> when a session is created.</param>
    /// <param name="workspace">Accepted for source compatibility. OpenCode 2.0 has no workspace query, so it is not sent.</param>
    /// <param name="serverPassword">The server password. When <c>null</c> or empty, requests carry no credential.</param>
    public OpenCodeHttpClient(HttpClient httpClient, string? directory, string? workspace, string? serverPassword = null)
    {
        _ = workspace;
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _directory = string.IsNullOrWhiteSpace(directory) ? null : directory.Trim();
        _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
        };

        if (!string.IsNullOrEmpty(serverPassword))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{BasicUsername}:{serverPassword}"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
    }

    public Uri BaseUri => _httpClient.BaseAddress ?? new Uri("http://127.0.0.1/");

    public Task<OpenCodeHealthResponse> HealthAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync<OpenCodeHealthResponse>(HttpMethod.Get, BuildUri("/api/info"), null, cancellationToken);
    }

    public Task<IReadOnlyList<OpenCodeProject>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync<IReadOnlyList<OpenCodeProject>>(HttpMethod.Get, BuildUri("/api/project"), null, cancellationToken);
    }

    public async Task<IReadOnlyList<OpenCodeSession>> ListSessionsAsync(
        string? search = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var page = await SendAsync<OpenCodeSessionPage>(
            HttpMethod.Get,
            BuildUri("/api/session", Query(("search", search), ("limit", limit))),
            null,
            cancellationToken).ConfigureAwait(false);
        return page.Data;
    }

    public Task<OpenCodeSession> CreateSessionAsync(string? title, CancellationToken cancellationToken = default)
    {
        return CreateSessionAsync(
            new OpenCodeSessionCreateRequest { Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim() },
            cancellationToken);
    }

    public async Task<OpenCodeSession> CreateSessionAsync(
        OpenCodeSessionCreateRequest? request,
        CancellationToken cancellationToken = default)
    {
        var body = new OpenCodeSessionCreateRequest
        {
            ParentId = request?.ParentId,
            Title = request?.Title,
            Location = request?.Location ?? DirectoryLocation(),
            Permissions = request?.Permissions,
        };

        var response = await SendAsync<OpenCodeDataResponse<OpenCodeSession>>(
            HttpMethod.Post,
            BuildUri("/api/session"),
            body,
            cancellationToken).ConfigureAwait(false);
        return response.Data ?? throw new InvalidOperationException("OpenCode API session creation returned no session.");
    }

    /// <summary>
    /// Sets the model used by a session (<c>POST /api/session/{id}/model</c>). OpenCode 2.0 ignores a model sent on the prompt.
    /// </summary>
    public Task SetSessionModelAsync(
        string sessionId,
        OpenCodeModelSelection model,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(model);

        return SendForBodyAsync(
            HttpMethod.Post,
            BuildUri($"/api/session/{Uri.EscapeDataString(sessionId)}/model"),
            new OpenCodeSessionModelRequest { Model = OpenCodeModelRef.From(model) },
            cancellationToken);
    }

    public async Task<IReadOnlyList<OpenCodeMessageEnvelope>> GetMessagesAsync(
        string sessionId,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var root = await SendAsync<JsonElement>(
            HttpMethod.Get,
            BuildUri($"/api/session/{Uri.EscapeDataString(sessionId)}/message", Query(("limit", limit))),
            null,
            cancellationToken).ConfigureAwait(false);

        var messages = new List<OpenCodeMessageEnvelope>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return messages;
        }

        foreach (var item in data.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idNode) ? idNode.GetString() : null;
            var type = item.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : null;
            var parts = new List<JsonElement>();
            if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                parts.AddRange(content.EnumerateArray().Select(static part => part.Clone()));
            }
            else if (item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                parts.Add(JsonSerializer.SerializeToElement(new { type = "text", text = text.GetString() }));
            }

            messages.Add(new OpenCodeMessageEnvelope
            {
                Info = JsonSerializer.SerializeToElement(new { id, sessionID = sessionId, role = type }),
                Parts = parts,
            });
        }

        return messages;
    }

    /// <summary>
    /// Runs one prompt and returns the assistant reply assembled from <c>GET /api/event</c>.
    /// The event stream is opened and confirmed before the prompt is posted, so early events of the turn are not lost.
    /// A question the agent asks is declined at once, because this overload has no handler.
    /// </summary>
    public Task<OpenCodeMessageEnvelope> PromptAsync(
        string sessionId,
        OpenCodeSessionPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        return PromptAsync(sessionId, request, interaction: null, cancellationToken);
    }

    /// <summary>
    /// Runs one prompt. When <paramref name="interaction"/> is set, each question the agent asks is handed to its handler on a
    /// background task while the turn keeps reading events. The request timeout does not count the time a question is pending;
    /// the question timeout bounds that wait instead.
    /// </summary>
    /// <param name="sessionId">The session to prompt.</param>
    /// <param name="request">The prompt.</param>
    /// <param name="interaction">The question handler and its timeout, or <see langword="null"/> to decline every question.</param>
    /// <param name="cancellationToken">Cancels the turn. An interactive turn that is cancelled also cancels its pending forms and interrupts the session.</param>
    public Task<OpenCodeMessageEnvelope> PromptAsync(
        string sessionId,
        OpenCodeSessionPromptRequest request,
        OpenCodeTurnInteraction? interaction,
        CancellationToken cancellationToken = default)
    {
        return PromptAsync(sessionId, request, interaction, agentWork: null, cancellationToken);
    }

    /// <summary>
    /// Runs one prompt with the agent work of the run reported to <paramref name="agentWork"/>. The child sessions the prompted
    /// session spawns are tracked (see <see cref="OpenCodeAgentWorkTracker"/>), and the turn is held open after the prompted session
    /// reports success while a tracked child is still active.
    /// </summary>
    public async Task<OpenCodeMessageEnvelope> PromptAsync(
        string sessionId,
        OpenCodeSessionPromptRequest request,
        OpenCodeTurnInteraction? interaction,
        OpenCodeAgentWorkTurn? agentWork,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);
        if (interaction is not null)
        {
            ArgumentNullException.ThrowIfNull(interaction.Handler, "interaction");
            OpenCodeUserInput.ValidateQuestionTimeout(interaction.QuestionTimeout, "QuestionTimeout");
        }

        agentWork?.Validate();

        using var turnSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // The event read has its own source, so a read still waiting when the turn ends can be stopped before the enumerator is disposed.
        using var readSource = CancellationTokenSource.CreateLinkedTokenSource(turnSource.Token);
        var requestTimeout = _httpClient.Timeout;
        var deadline = new TurnDeadline(turnSource, requestTimeout);
        var questions = new OpenCodeQuestionScope(this, sessionId, interaction, deadline, turnSource.Token);
        var promptPosted = false;
        var turnToken = turnSource.Token;
        try
        {
            await using var events = SubscribeEventsAsync(readSource.Token).GetAsyncEnumerator(readSource.Token);
            if (!await events.MoveNextAsync().ConfigureAwait(false))
            {
                throw new InvalidOperationException("OpenCode event stream closed before the prompt was sent.");
            }

            var ack = await SendAsync<OpenCodeDataResponse<OpenCodeInboxUser>>(
                HttpMethod.Post,
                BuildUri($"/api/session/{Uri.EscapeDataString(sessionId)}/prompt"),
                request,
                turnToken).ConfigureAwait(false);
            promptPosted = true;

            return await AwaitTurnAsync(events, sessionId, ack.Data?.Id, questions, agentWork, deadline, readSource, turnToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"OpenCode prompt for session '{sessionId}' did not finish within {requestTimeout}.");
        }
        finally
        {
            // Runs on every exit: the pending forms are closed, and an interactive turn the caller cancelled interrupts its session.
            await questions.CloseAsync(interruptSession: promptPosted && cancellationToken.IsCancellationRequested).ConfigureAwait(false);
            questions.Dispose();
        }
    }

    /// <summary>
    /// Lists the pending forms of a session (<c>GET /api/session/{id}/form</c>).
    /// </summary>
    public async Task<IReadOnlyList<OpenCodeFormInfo>> ListFormsAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var response = await SendAsync<OpenCodeDataResponse<List<OpenCodeFormInfo>>>(
            HttpMethod.Get,
            BuildUri($"/api/session/{Uri.EscapeDataString(sessionId)}/form"),
            null,
            cancellationToken).ConfigureAwait(false);
        return response.Data ?? [];
    }

    /// <summary>
    /// Answers a form (<c>POST /api/session/{id}/form/{formId}/reply</c>). The answer is keyed by field key. A form that is already
    /// settled answers <see cref="OpenCodeFormOutcome.AlreadySettled"/> instead of failing.
    /// </summary>
    /// <exception cref="OpenCodeApiException">OpenCode refused the answer, for example <c>400 FormInvalidAnswerError</c>; the form stays pending.</exception>
    public async Task<OpenCodeFormOutcome> ReplyFormAsync(
        string sessionId,
        string formId,
        IReadOnlyDictionary<string, object> answer,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);
        ArgumentNullException.ThrowIfNull(answer);

        try
        {
            await SendForBodyAsync(
                HttpMethod.Post,
                BuildUri($"/api/session/{Uri.EscapeDataString(sessionId)}/form/{Uri.EscapeDataString(formId)}/reply"),
                new OpenCodeFormReplyRequest { Answer = answer },
                cancellationToken).ConfigureAwait(false);
            return OpenCodeFormOutcome.Accepted;
        }
        catch (OpenCodeApiException exception) when (IsFormAlreadySettled(exception))
        {
            return OpenCodeFormOutcome.AlreadySettled;
        }
    }

    /// <summary>
    /// Cancels a form with a message the agent receives (<c>DELETE /api/session/{id}/form/{formId}?message=</c>). A form that is
    /// already settled answers <see cref="OpenCodeFormOutcome.AlreadySettled"/> instead of failing.
    /// </summary>
    public async Task<OpenCodeFormOutcome> CancelFormAsync(
        string sessionId,
        string formId,
        string? message,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);

        try
        {
            await SendForBodyAsync(
                HttpMethod.Delete,
                BuildUri(
                    $"/api/session/{Uri.EscapeDataString(sessionId)}/form/{Uri.EscapeDataString(formId)}",
                    Query(("message", string.IsNullOrEmpty(message) ? null : message))),
                null,
                cancellationToken).ConfigureAwait(false);
            return OpenCodeFormOutcome.Accepted;
        }
        catch (OpenCodeApiException exception) when (IsFormAlreadySettled(exception))
        {
            return OpenCodeFormOutcome.AlreadySettled;
        }
    }

    /// <summary>
    /// Interrupts a session (<c>POST /api/session/{id}/interrupt</c>). Returns whether OpenCode reports the session interrupted.
    /// A pending form must be cancelled first to release a tool that is blocked on it, though the interrupt also ends the turn.
    /// </summary>
    public async Task<bool> InterruptSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var response = await SendAsync<OpenCodeSessionInterruptResponse>(
            HttpMethod.Post,
            BuildUri($"/api/session/{Uri.EscapeDataString(sessionId)}/interrupt"),
            null,
            cancellationToken).ConfigureAwait(false);
        return response.Interrupted;
    }

    public async Task<IReadOnlyList<OpenCodeFileNode>> ListFilesAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var listing = await SendAsync<OpenCodeFileSystemListResponse>(
            HttpMethod.Get,
            BuildUri("/api/fs/list", Query(("path", path), ("location[directory]", _directory))),
            null,
            cancellationToken).ConfigureAwait(false);

        return listing.Data
            .Select(entry => new OpenCodeFileNode
            {
                Name = Path.GetFileName(entry.Path),
                Path = entry.Path,
                Type = entry.Type,
            })
            .ToArray();
    }

    public async IAsyncEnumerable<OpenCodeEventEnvelope> SubscribeEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri("/api/event"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, request.RequestUri!, cancellationToken).ConfigureAwait(false);
        EnsureNotHtml(response, request.RequestUri!, body: null);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        string? eventName = null;
        var dataBuilder = new StringBuilder();

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (eventName is not null || dataBuilder.Length > 0)
                {
                    yield return CreateEvent(eventName, dataBuilder.ToString());
                    eventName = null;
                    dataBuilder.Clear();
                }

                continue;
            }

            if (line.StartsWith(':'))
            {
                // Comment or heartbeat line.
                continue;
            }

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventName = line[6..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (dataBuilder.Length > 0)
                {
                    dataBuilder.AppendLine();
                }

                dataBuilder.Append(line[5..].TrimStart());
            }
        }

        if (eventName is not null || dataBuilder.Length > 0)
        {
            yield return CreateEvent(eventName, dataBuilder.ToString());
        }
    }

    private async Task<OpenCodeMessageEnvelope> AwaitTurnAsync(
        IAsyncEnumerator<OpenCodeEventEnvelope> events,
        string sessionId,
        string? promptMessageId,
        OpenCodeQuestionScope questions,
        OpenCodeAgentWorkTurn? agentWork,
        TurnDeadline deadline,
        CancellationTokenSource readSource,
        CancellationToken cancellationToken)
    {
        var textSegments = new List<string>();
        string? assistantMessageId = null;

        // Agent work is observed for every envelope, before the session filter, so child sessions never reach the parent switch.
        var tracker = agentWork is null ? null : new OpenCodeAgentWorkTracker(sessionId);

        // Hold-open state (design D5). The turn is held only after the prompted session reports success while a tracked child is active.
        var holding = false;
        var parentRunning = false;
        var holdStartedAt = 0L;
        long? idleSince = null;
        Task<bool>? pendingRead = null;

        void EmitAgentWork()
        {
            var snapshot = tracker?.TakeSnapshot();
            if (snapshot is not null)
            {
                agentWork!.Emit(snapshot);
            }
        }

        void EndAgentWork()
        {
            tracker?.StopActive();
            EmitAgentWork();
        }

        void SettleHold()
        {
            // The hold ends only once no child is active and the prompted session has been idle for the settle interval.
            if (!holding)
            {
                return;
            }

            if (parentRunning || tracker!.HasActiveWork)
            {
                idleSince = null;
            }
            else
            {
                idleSince ??= Stopwatch.GetTimestamp();
            }
        }

        OpenCodeMessageEnvelope Result(List<JsonElement> parts)
        {
            return BuildAssistantEnvelope(sessionId, assistantMessageId ?? promptMessageId, parts);
        }

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (holding)
                {
                    if (HasElapsed(holdStartedAt, agentWork!.WaitTimeout))
                    {
                        // The wait bound passed: items still running are reported stopped and the turn returns what it has.
                        EndAgentWork();
                        return Result(TextParts(textSegments));
                    }

                    if (idleSince is { } idleAt && HasElapsed(idleAt, agentWork!.SettleInterval))
                    {
                        // Every tracked child ended and the prompted session did not start again.
                        tracker!.ReleaseHold();
                        EmitAgentWork();
                        return Result(TextParts(textSegments));
                    }
                }

                pendingRead ??= events.MoveNextAsync().AsTask();
                if (holding && !pendingRead.IsCompleted)
                {
                    var wait = UntilElapsed(holdStartedAt, agentWork!.WaitTimeout);
                    if (idleSince is { } settleAt)
                    {
                        var settleWait = UntilElapsed(settleAt, agentWork.SettleInterval);
                        wait = wait == Timeout.InfiniteTimeSpan || settleWait < wait ? settleWait : wait;
                    }

                    if (await Task.WhenAny(pendingRead, Task.Delay(wait, cancellationToken)).ConfigureAwait(false) != pendingRead)
                    {
                        // A timer fired before the next event. The loop re-checks the bounds at the top.
                        continue;
                    }
                }

                if (!await pendingRead.ConfigureAwait(false))
                {
                    break;
                }

                pendingRead = null;
                var envelope = events.Current;
                if (tracker is not null)
                {
                    ObserveAgentWork(tracker, envelope);
                    EmitAgentWork();
                }

                if (!string.Equals(envelope.SessionId, sessionId, StringComparison.Ordinal))
                {
                    SettleHold();
                    continue;
                }

                switch (envelope.EventType)
                {
                    case "form.created":
                        if (envelope.Form is { } form)
                        {
                            await questions.OnFormCreatedAsync(form, cancellationToken).ConfigureAwait(false);
                        }

                        break;

                    case "session.text.ended":
                        assistantMessageId = TryGetString(envelope.Payload, "assistantMessageID") ?? assistantMessageId;
                        var text = TryGetString(envelope.Payload, "text");
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            textSegments.Add(text);
                        }

                        break;

                    case "session.execution.started":
                        parentRunning = true;
                        break;

                    case "session.execution.succeeded":
                        if (!holding)
                        {
                            if (tracker is null || !tracker.HasActiveWork)
                            {
                                return Result(TextParts(textSegments));
                            }

                            // Success while a child still runs: keep the turn open. The request deadline does not count the wait.
                            holding = true;
                            holdStartedAt = Stopwatch.GetTimestamp();
                            deadline.Pause();
                            tracker.HoldActive();
                        }

                        parentRunning = false;
                        break;

                    case "session.execution.failed":
                        EndAgentWork();
                        var error = envelope.Payload.ValueKind == JsonValueKind.Object && envelope.Payload.TryGetProperty("error", out var errorNode)
                            ? errorNode
                            : default;
                        var errorPart = JsonSerializer.SerializeToElement(new
                        {
                            type = "error",
                            reason = TryGetString(error, "type") ?? "unknown",
                            message = TryGetString(error, "message") ?? "OpenCode reported a failed turn.",
                        });
                        return Result([errorPart]);

                    case "session.execution.interrupted":
                        // Terminal as well: OpenCode emits it after POST /interrupt (or a user stop) and sends no succeeded/failed.
                        EndAgentWork();
                        var interruptedPart = JsonSerializer.SerializeToElement(new
                        {
                            type = "error",
                            reason = TryGetString(envelope.Payload, "reason") ?? "interrupted",
                            message = "OpenCode interrupted the turn.",
                        });
                        return Result([interruptedPart]);
                }

                SettleHold();
                EmitAgentWork();
            }
        }
        finally
        {
            if (pendingRead is { IsCompleted: false })
            {
                // The turn is over while a read still waits for an event. Stop that read, so the enumerator is not disposed while it runs.
                readSource.Cancel();
                try
                {
                    await pendingRead.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected: the read was stopped above.
                }
            }
        }

        throw new InvalidOperationException("OpenCode event stream ended before the prompt turn finished.");
    }

    /// <summary>
    /// Passes one envelope to the agent work tracker. Tracking never fails or ends the turn, so a failure here is ignored and the
    /// tracker keeps the list it had.
    /// </summary>
    private static void ObserveAgentWork(OpenCodeAgentWorkTracker tracker, OpenCodeEventEnvelope envelope)
    {
        try
        {
            tracker.Observe(envelope);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Tracking is best effort: the event is dropped and the turn carries on.
        }
    }

    private static List<JsonElement> TextParts(IEnumerable<string> segments)
    {
        return segments.Select(static segment => JsonSerializer.SerializeToElement(new { type = "text", text = segment })).ToList();
    }

    /// <summary>Whether a window that started at <paramref name="startedAt"/> has passed. An infinite window never passes.</summary>
    private static bool HasElapsed(long startedAt, TimeSpan window)
    {
        return window != Timeout.InfiniteTimeSpan && Stopwatch.GetElapsedTime(startedAt) >= window;
    }

    /// <summary>The time left in a window that started at <paramref name="startedAt"/>, or infinite when the window is.</summary>
    private static TimeSpan UntilElapsed(long startedAt, TimeSpan window)
    {
        if (window == Timeout.InfiniteTimeSpan)
        {
            return Timeout.InfiniteTimeSpan;
        }

        var left = window - Stopwatch.GetElapsedTime(startedAt);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private static bool IsFormAlreadySettled(OpenCodeApiException exception)
    {
        return exception.StatusCode == HttpStatusCode.Conflict &&
               exception.ResponseBody?.Contains("FormAlreadySettledError", StringComparison.Ordinal) == true;
    }

    private static OpenCodeMessageEnvelope BuildAssistantEnvelope(string sessionId, string? messageId, List<JsonElement> parts)
    {
        return new OpenCodeMessageEnvelope
        {
            Info = JsonSerializer.SerializeToElement(new
            {
                id = messageId ?? "(unknown)",
                sessionID = sessionId,
                role = "assistant",
            }),
            Parts = parts,
        };
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        Uri requestUri,
        object? body,
        CancellationToken cancellationToken)
    {
        var payload = await SendForBodyAsync(method, requestUri, body, cancellationToken).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<T>(payload, _jsonOptions);
        return result ?? throw new InvalidOperationException($"OpenCode API request to '{requestUri}' returned an empty JSON payload.");
    }

    private async Task<string> SendForBodyAsync(
        HttpMethod method,
        Uri requestUri,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, requestUri);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: _jsonOptions);
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, requestUri, cancellationToken).ConfigureAwait(false);

        var payload = response.Content is null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureNotHtml(response, requestUri, payload);
        return payload;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, Uri requestUri, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new OpenCodeAuthenticationException(requestUri.ToString());
        }

        var payload = response.Content is null
            ? null
            : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new OpenCodeApiException(
            response.StatusCode,
            $"OpenCode API request to '{requestUri}' failed with status {(int)response.StatusCode} ({response.StatusCode}).",
            NormalizeBody(payload));
    }

    /// <summary>
    /// The 1.x and 2.x legacy routes answer with the Web UI HTML shell and HTTP 200, so an HTML body is treated as an unsupported API.
    /// </summary>
    private static void EnsureNotHtml(HttpResponseMessage response, Uri requestUri, string? body)
    {
        var isHtmlContentType = string.Equals(
            response.Content?.Headers.ContentType?.MediaType,
            "text/html",
            StringComparison.OrdinalIgnoreCase);
        var isHtmlBody = body is not null && body.TrimStart().StartsWith('<');
        if (isHtmlContentType || isHtmlBody)
        {
            throw new OpenCodeUnsupportedApiException(requestUri.ToString(), BuildExcerpt(body));
        }
    }

    private static string? BuildExcerpt(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var trimmed = body.Trim();
        return trimmed.Length <= BodyExcerptLength ? trimmed : trimmed[..BodyExcerptLength] + "...";
    }

    private static string? NormalizeBody(string? payload)
    {
        return string.IsNullOrWhiteSpace(payload) ? null : payload.Trim();
    }

    private OpenCodeLocationRef? DirectoryLocation()
    {
        return _directory is null ? null : new OpenCodeLocationRef { Directory = _directory };
    }

    private static OpenCodeEventEnvelope CreateEvent(string? eventName, string payload)
    {
        JsonElement data;
        if (string.IsNullOrWhiteSpace(payload))
        {
            data = JsonSerializer.SerializeToElement(new { });
        }
        else
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                data = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                data = JsonSerializer.SerializeToElement(payload);
            }
        }

        return new OpenCodeEventEnvelope
        {
            EventName = eventName,
            Data = data,
        };
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static Uri BuildUri(string path, string? query = null)
    {
        var normalized = string.IsNullOrWhiteSpace(query) ? path : $"{path}?{query}";
        return new Uri(normalized, UriKind.Relative);
    }

    private static string? Query(params (string Key, object? Value)[] values)
    {
        var parts = values
            .Where(static entry => entry.Value is not null)
            .Select(static entry =>
                $"{Uri.EscapeDataString(entry.Key)}={Uri.EscapeDataString(Convert.ToString(entry.Value, CultureInfo.InvariantCulture) ?? string.Empty)}")
            .ToArray();

        return parts.Length == 0 ? null : string.Join("&", parts);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
