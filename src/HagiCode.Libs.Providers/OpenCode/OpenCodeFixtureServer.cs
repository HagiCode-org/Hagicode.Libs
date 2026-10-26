using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace HagiCode.Libs.Providers.OpenCode;

/// <summary>
/// Options for the in-process OpenCode 2.0 simulation used by tests.
/// </summary>
public sealed class OpenCodeFixtureServerOptions
{
    public string Hostname { get; init; } = "127.0.0.1";

    public int? Port { get; init; }

    public string Version { get; init; } = "2.0.32";

    public string AssistantPrefix { get; init; } = "READY";

    /// <summary>
    /// The password the simulated server requires. When <c>null</c>, a random password is generated.
    /// </summary>
    public string? ServerPassword { get; init; }

    /// <summary>
    /// When <c>true</c> (the default, as in OpenCode 2.0), every <c>/api/*</c> request needs a valid Basic credential.
    /// </summary>
    public bool RequireAuthentication { get; init; } = true;

    /// <summary>Models whose prompt completes without any assistant text.</summary>
    public IReadOnlyCollection<string> EmptyTextModelIds { get; init; } = Array.Empty<string>();

    /// <summary>Models whose prompt answers with an empty body.</summary>
    public IReadOnlyCollection<string> EmptyBodyModelIds { get; init; } = Array.Empty<string>();

    /// <summary>Models rejected when they are selected for a session.</summary>
    public IReadOnlyCollection<string> InvalidModelIds { get; init; } = Array.Empty<string>();

    /// <summary>Models whose prompt answers with a non-JSON body.</summary>
    public IReadOnlyCollection<string> NonJsonModelIds { get; init; } = Array.Empty<string>();

    /// <summary>Models whose turn ends with <c>session.execution.failed</c>.</summary>
    public IReadOnlyCollection<string> FailingModelIds { get; init; } = Array.Empty<string>();

    /// <summary>Models whose prompt runs the <see cref="QuestionScript"/> turn: the agent opens forms and waits for their settlement.</summary>
    public IReadOnlyCollection<string> QuestionModelIds { get; init; } = Array.Empty<string>();

    /// <summary>The forms a question turn opens and how the turn proceeds.</summary>
    public OpenCodeFixtureQuestionScript QuestionScript { get; init; } = new();

    /// <summary>Models whose prompt runs the <see cref="ChildSessionScript"/> turn: the agent spawns a child session that outlives the parent's success.</summary>
    public IReadOnlyCollection<string> ChildSessionModelIds { get; init; } = Array.Empty<string>();

    /// <summary>The child-session turn the models in <see cref="ChildSessionModelIds"/> run.</summary>
    public OpenCodeFixtureChildSessionScript ChildSessionScript { get; init; } = new();

    /// <summary>Models whose prompt runs the <see cref="SteerScript"/> turn: a steer sent during it is delivered at the next step boundary.</summary>
    public IReadOnlyCollection<string> SteerModelIds { get; init; } = Array.Empty<string>();

    /// <summary>The steerable turn the models in <see cref="SteerModelIds"/> run.</summary>
    public OpenCodeFixtureSteerScript SteerScript { get; init; } = new();

    /// <summary>The models <c>GET /api/model</c> lists, with their input capabilities.</summary>
    public IReadOnlyCollection<OpenCodeFixtureModel> Models { get; init; } = Array.Empty<OpenCodeFixtureModel>();

    /// <summary>When <c>true</c>, <c>GET /api/model</c> answers HTTP 500 while the other routes keep working.</summary>
    public bool FailModelListing { get; init; }

    /// <summary>When set, <c>GET /api/model</c> waits this long before it answers, so a caller's timeout can be exercised.</summary>
    public TimeSpan? ModelListingDelay { get; init; }
}

/// <summary>
/// A model <c>GET /api/model</c> lists. A null <see cref="Input"/> means OpenCode reports no <c>capabilities</c> member.
/// </summary>
public sealed record OpenCodeFixtureModel(string ProviderId, string ModelId, IReadOnlyList<string>? Input);

/// <summary>
/// The script of a question turn: which forms the agent opens, and how the turn reacts to their settlement.
/// </summary>
public sealed class OpenCodeFixtureQuestionScript
{
    /// <summary>
    /// The forms, in the order they are opened. The default is the form the live probe recorded: a single-choice question and a
    /// multi-select question in one form.
    /// </summary>
    public IReadOnlyList<OpenCodeFixtureForm> Forms { get; init; } =
    [
        new OpenCodeFixtureForm
        {
            Fields =
            [
                new OpenCodeFixtureField("Color", "Which color do you prefer?", MultiSelect: false, Options: ["Red", "Blue"]),
                new OpenCodeFixtureField("Pets", "Which pets do you like?", MultiSelect: true, Options: ["Cats", "Dogs"]),
            ],
        },
    ];

    /// <summary>When true, every form is published before the first one is settled (a second question while the first is pending).</summary>
    public bool PublishAllAtOnce { get; init; }

    /// <summary>When true, the turn completes right after the forms are published, without waiting for them (the turn ends while they are pending).</summary>
    public bool CompleteWithoutWaiting { get; init; }

    /// <summary>When true, the turn publishes nothing after the forms settle (an agent that stalls after the answer).</summary>
    public bool StallAfterSettlement { get; init; }
}

/// <summary>
/// A scripted steerable turn, as OpenCode 2.0.32 reports it in the probe: a step runs for <see cref="StepDelay"/>, the steers that
/// arrived during it enter the turn at its end (<c>session.inbox.delivered</c> with the steer's id), and the turn then succeeds.
/// A steer that arrives after that point is not delivered and the turn ends without it.
/// </summary>
public sealed class OpenCodeFixtureSteerScript
{
    /// <summary>How long the first step runs before its boundary, which is when pending steers are delivered.</summary>
    public TimeSpan StepDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>When <c>false</c>, the step boundary delivers nothing, so every steer ends the turn not consumed.</summary>
    public bool DeliverSteers { get; init; } = true;

    /// <summary>When <c>true</c>, a steer sent to the running turn is refused with an <c>InvalidRequestError</c> and never enqueued.</summary>
    public bool RefuseSteers { get; init; }

    /// <summary>When <c>true</c>, the turn ends with <c>session.execution.failed</c> after its first step instead of succeeding.</summary>
    public bool FailTurn { get; init; }

    /// <summary>
    /// When <c>true</c>, a steer is delivered at once: <c>session.inbox.delivered</c> is published before the steer's response is written.
    /// The probe recorded the same ordering for an idle-session prompt, so this checks that a delivery event which arrives before the
    /// response still resolves the steer.
    /// </summary>
    public bool DeliverSteersImmediately { get; init; }
}

/// <summary>
/// A scripted turn in which the prompted session spawns a child session through the <c>task</c> tool, as OpenCode 2.0.32 reports it:
/// <c>session.created</c> carrying <c>parentID</c> and the task title, the child's execution and tool events, and the parent's own
/// success, which can come while the child still runs.
/// </summary>
public sealed class OpenCodeFixtureChildSessionScript
{
    /// <summary>The title of the child session, which is the task description.</summary>
    public string ChildTitle { get; init; } = "count files";

    /// <summary>The text the child emits. It must never appear in the prompted session's result.</summary>
    public string ChildText { get; init; } = "CHILD-ONLY-TEXT";

    /// <summary>The text the prompted session emits before it reports success.</summary>
    public string ParentText { get; init; } = "PARENT-READY";

    /// <summary>How long the child runs after the parent reports success.</summary>
    public TimeSpan ChildRunTime { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>When true, the child ends interrupted instead of succeeded.</summary>
    public bool ChildInterrupted { get; init; }

    /// <summary>When true, the child never ends, so only the wait bound stops the turn.</summary>
    public bool ChildNeverEnds { get; init; }

    /// <summary>When set, the prompted session starts a follow-up execution after the child ends and answers with this text.</summary>
    public string? FollowUpText { get; init; }

    /// <summary>When true, the follow-up execution asks one question first and waits for its answer before it answers with <see cref="FollowUpText"/>.</summary>
    public bool FollowUpQuestion { get; init; }

    /// <summary>When true, the prompted session reports interrupted while the child still runs, instead of succeeded.</summary>
    public bool ParentInterruptsWhileChildRuns { get; init; }

    /// <summary>When true, a session of another client on the same stream emits a child and text, which must be ignored.</summary>
    public bool IncludeForeignSession { get; init; } = true;
}

/// <summary>
/// One form a scripted turn opens. A <c>question</c> form with no <see cref="SessionId"/> belongs to the turn and the turn waits for it.
/// </summary>
public sealed class OpenCodeFixtureForm
{
    /// <summary>The form kind (<c>metadata.kind</c>). <c>question</c> by default; <c>mcp</c> models an elicitation form.</summary>
    public string Kind { get; init; } = "question";

    /// <summary>The session of the form. Null means the turn's own session; another id models a form of a different session.</summary>
    public string? SessionId { get; init; }

    public IReadOnlyList<OpenCodeFixtureField> Fields { get; init; } = [];
}

/// <summary>
/// One question of a scripted form: the header, the question text, whether it is multi-select, and the option labels.
/// </summary>
public sealed record OpenCodeFixtureField(string Header, string Question, bool MultiSelect, IReadOnlyList<string> Options);

/// <summary>
/// The state of one form of a fixture server, as the tests read it.
/// </summary>
/// <param name="Id">The form id.</param>
/// <param name="SessionId">The session of the form.</param>
/// <param name="Kind">The form kind.</param>
/// <param name="State">The form state: <see cref="OpenCodeFormStates"/>.</param>
/// <param name="Message">The message of a cancel, or null.</param>
/// <param name="AnswerJson">The answer of a reply as JSON, or null.</param>
public sealed record OpenCodeFixtureFormSnapshot(string Id, string SessionId, string Kind, string State, string? Message, string? AnswerJson);

/// <summary>
/// Simulates OpenCode 2.0: the startup lines, Basic authentication, the <c>/api/*</c> routes,
/// the <c>/api/event</c> stream, and the Web UI HTML shell (HTTP 200) on every other path.
/// </summary>
public static class OpenCodeFixtureServer
{
    private const string Username = "opencode";
    private const string LegacyHtml = "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>OpenCode</title></head><body><div id=\"root\"></div></body></html>";

    public static Task<OpenCodeFixtureServerHandle> StartAsync(
        OpenCodeFixtureServerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new OpenCodeFixtureServerOptions();
        var password = string.IsNullOrEmpty(options.ServerPassword)
            ? Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
            : options.ServerPassword;
        var port = options.Port ?? OpenCodeProcessLauncher.GetFreeTcpPort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://{options.Hostname}:{port}/");
        listener.Start();

        var baseUri = new Uri($"http://{options.Hostname}:{port}/");
        var state = new FixtureState(options, password, baseUri);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var loopTask = Task.Run(() => AcceptLoopAsync(listener, state, cts.Token), CancellationToken.None);
        var startupOutput = $"server listening on {baseUri.ToString().TrimEnd('/')}{Environment.NewLine}server password {password}{Environment.NewLine}";
        return Task.FromResult(new OpenCodeFixtureServerHandle(
            listener,
            baseUri,
            loopTask,
            cts,
            password,
            startupOutput,
            () => state.Forms.Values
                .Select(static form => new OpenCodeFixtureFormSnapshot(form.Id, form.SessionId, form.Kind, form.State, form.Message, form.AnswerJson))
                .ToArray(),
            () => Interlocked.Read(ref state.InterruptRequestCount),
            () => state.LastPromptBody,
            () => Interlocked.Read(ref state.ModelListingRequestCount),
            () => state.SteerRequestIds.ToArray()));
    }

    private static async Task AcceptLoopAsync(HttpListener listener, FixtureState state, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (context is not null)
            {
                _ = Task.Run(() => HandleAsync(context, state, cancellationToken), CancellationToken.None);
            }
        }
    }

    private static async Task HandleAsync(HttpListenerContext context, FixtureState state, CancellationToken cancellationToken)
    {
        try
        {
            var request = context.Request;
            var path = request.Url?.AbsolutePath ?? "/";
            var method = request.HttpMethod;

            if (!IsApiPath(path))
            {
                // OpenCode 2.0 serves the Web UI shell with HTTP 200 for every non-API route, including the legacy ones.
                await WriteAsync(context.Response, 200, "text/html; charset=utf-8", LegacyHtml, cancellationToken);
                return;
            }

            if (state.Options.RequireAuthentication && !IsAuthorized(request, state.Password))
            {
                await WriteAsync(
                    context.Response,
                    401,
                    "application/json",
                    JsonSerializer.Serialize(new { _tag = "UnauthorizedError", message = "Authentication required" }),
                    cancellationToken);
                return;
            }

            if (method == "GET" && path == "/api/info")
            {
                await WriteJsonAsync(context.Response, 200, new
                {
                    version = state.Options.Version,
                    pid = Environment.ProcessId,
                    urls = new[] { state.BaseUri.ToString().TrimEnd('/') },
                    paths = new { tmp = "/tmp/opencode" },
                    capabilities = new { persistentPty = true },
                }, cancellationToken);
                return;
            }

            if (method == "GET" && path == "/api/model")
            {
                Interlocked.Increment(ref state.ModelListingRequestCount);
                if (state.Options.ModelListingDelay is { } delay)
                {
                    await Task.Delay(delay, cancellationToken);
                }

                if (state.Options.FailModelListing)
                {
                    await WriteJsonAsync(context.Response, 500, new { _tag = "InternalError", message = "model listing failed" }, cancellationToken);
                    return;
                }

                await WriteJsonAsync(context.Response, 200, new
                {
                    data = state.Options.Models.Select(static model => BuildModelJson(model)).ToArray(),
                }, cancellationToken);
                return;
            }

            if (method == "GET" && path == "/api/project")
            {
                await WriteJsonAsync(context.Response, 200, Array.Empty<object>(), cancellationToken);
                return;
            }

            if (method == "GET" && path == "/api/fs/list")
            {
                await HandleFileListAsync(context.Response, request, cancellationToken);
                return;
            }

            if (method == "GET" && path == "/api/session")
            {
                var sessions = state.Sessions.Values.Select(session => session.Definition).ToArray();
                await WriteJsonAsync(context.Response, 200, new { data = sessions, cursor = new { previous = (string?)null, next = (string?)null } }, cancellationToken);
                return;
            }

            if (method == "POST" && path == "/api/session")
            {
                var body = await ReadBodyAsync(request, cancellationToken);
                using var document = body.Length == 0 ? null : JsonDocument.Parse(body);
                var title = TryGetString(document?.RootElement, "title");
                var directory = TryGetNestedString(document?.RootElement, "location", "directory");
                var number = Interlocked.Increment(ref state.NextSessionId);
                var newSessionId = $"ses_{number:D8}";
                var created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var definition = new
                {
                    id = newSessionId,
                    projectID = "fixture-project",
                    title = string.IsNullOrWhiteSpace(title) ? $"Fixture Session {number}" : title,
                    location = directory is null ? null : new { directory },
                    time = new { created, updated = created },
                };
                state.Sessions[newSessionId] = new FixtureSession(newSessionId, definition);
                await WriteJsonAsync(context.Response, 200, new { data = definition }, cancellationToken);
                return;
            }

            if (method == "GET" && path == "/api/event")
            {
                await StreamEventsAsync(context.Response, state, cancellationToken);
                return;
            }

            if (TryMatchFormRoute(path, out var formSessionId, out var formAction, out var formId))
            {
                if (!state.Sessions.TryGetValue(formSessionId, out var formSession))
                {
                    await WriteJsonAsync(context.Response, 404, new { _tag = "SessionNotFoundError", message = $"Session not found: {formSessionId}" }, cancellationToken);
                    return;
                }

                if (method == "GET" && formAction == "form")
                {
                    var pending = state.Forms.Values
                        .Where(form => form.SessionId == formSession.Id && form.IsPending)
                        .Select(static form => form.ToFormJson())
                        .ToArray();
                    await WriteJsonAsync(context.Response, 200, new { data = pending }, cancellationToken);
                    return;
                }

                if (method == "POST" && formAction == "reply")
                {
                    await HandleFormReplyAsync(context.Response, request, formSession, formId, state, cancellationToken);
                    return;
                }

                if (method == "DELETE" && formAction == "form")
                {
                    await HandleFormCancelAsync(context.Response, request, formSession, formId, state, cancellationToken);
                    return;
                }

                if (method == "POST" && formAction == "interrupt")
                {
                    await HandleInterruptAsync(context.Response, formSession, state, cancellationToken);
                    return;
                }
            }

            if (TryMatchSessionRoute(path, out var sessionId, out var action))
            {
                if (!state.Sessions.TryGetValue(sessionId, out var session))
                {
                    await WriteJsonAsync(context.Response, 404, new { _tag = "SessionNotFoundError", message = $"Session not found: {sessionId}" }, cancellationToken);
                    return;
                }

                if (method == "POST" && action == "model")
                {
                    await HandleSetModelAsync(context.Response, request, session, state, cancellationToken);
                    return;
                }

                if (method == "POST" && action == "prompt")
                {
                    await HandlePromptAsync(context.Response, request, session, state, cancellationToken);
                    return;
                }

                if (method == "GET" && action == "message")
                {
                    await WriteJsonAsync(context.Response, 200, new { data = session.Messages.ToArray(), cursor = new { previous = (string?)null, next = (string?)null } }, cancellationToken);
                    return;
                }
            }

            await WriteJsonAsync(context.Response, 404, new { _tag = "NotFoundError", message = $"Not found: {method} {path}" }, cancellationToken);
        }
        catch (Exception ex)
        {
            await WriteAsync(context.Response, 500, "text/plain; charset=utf-8", ex.Message, cancellationToken);
        }
        finally
        {
            if (context.Response.OutputStream.CanWrite)
            {
                context.Response.Close();
            }
        }
    }

    private static async Task HandleFormReplyAsync(
        HttpListenerResponse response,
        HttpListenerRequest request,
        FixtureSession session,
        string formId,
        FixtureState state,
        CancellationToken cancellationToken)
    {
        if (!state.Forms.TryGetValue(formId, out var form) || form.SessionId != session.Id)
        {
            await WriteJsonAsync(response, 404, new { _tag = "FormNotFoundError", id = formId, message = $"Form not found: {formId}" }, cancellationToken);
            return;
        }

        if (!form.IsPending)
        {
            await WriteJsonAsync(response, 409, new { _tag = "FormAlreadySettledError", id = formId, message = $"Form already settled: {formId}" }, cancellationToken);
            return;
        }

        using var document = JsonDocument.Parse(await ReadBodyAsync(request, cancellationToken));
        if (!document.RootElement.TryGetProperty("answer", out var answer) || answer.ValueKind != JsonValueKind.Object)
        {
            await WriteJsonAsync(response, 400, new { _tag = "InvalidRequestError", message = "Missing key\n  at [\"answer\"]", kind = "Payload" }, cancellationToken);
            return;
        }

        // The live probe: a string field takes a JSON string, a multiselect field takes an array of strings, and a wrong shape is
        // refused with the form still pending.
        for (var index = 0; index < form.Fields.Count; index++)
        {
            var key = "q" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!answer.TryGetProperty(key, out var value))
            {
                continue;
            }

            var valid = form.Fields[index].MultiSelect
                ? value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(static item => item.ValueKind == JsonValueKind.String)
                : value.ValueKind == JsonValueKind.String;
            if (!valid)
            {
                var expected = form.Fields[index].MultiSelect ? "Expected string array for form field: " : "Expected string for form field: ";
                await WriteJsonAsync(response, 400, new { _tag = "FormInvalidAnswerError", id = formId, message = expected + key }, cancellationToken);
                return;
            }
        }

        form.AnswerJson = answer.GetRawText();
        form.State = OpenCodeFormStates.Replied;
        state.Publish("form.replied", new { id = formId, sessionID = session.Id, answer = answer.Clone() });
        form.Settled.TrySetResult();
        response.StatusCode = 204;
    }

    private static async Task HandleFormCancelAsync(
        HttpListenerResponse response,
        HttpListenerRequest request,
        FixtureSession session,
        string formId,
        FixtureState state,
        CancellationToken cancellationToken)
    {
        if (!state.Forms.TryGetValue(formId, out var form) || form.SessionId != session.Id)
        {
            await WriteJsonAsync(response, 404, new { _tag = "FormNotFoundError", id = formId, message = $"Form not found: {formId}" }, cancellationToken);
            return;
        }

        if (!form.IsPending)
        {
            await WriteJsonAsync(response, 409, new { _tag = "FormAlreadySettledError", id = formId, message = $"Form already settled: {formId}" }, cancellationToken);
            return;
        }

        form.Message = request.QueryString["message"];
        CancelForm(form, state, session.Id);
        response.StatusCode = 204;
    }

    private static async Task HandleInterruptAsync(
        HttpListenerResponse response,
        FixtureSession session,
        FixtureState state,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref state.InterruptRequestCount);
        var activeTurn = Interlocked.Exchange(ref session.ActiveTurnId, 0);
        if (activeTurn != 0)
        {
            // OpenCode 2.0.32: the pending forms are cancelled, then the turn ends with session.execution.interrupted (reason user).
            Interlocked.Exchange(ref session.InterruptedTurnId, activeTurn);
            foreach (var form in state.Forms.Values.Where(form => form.SessionId == session.Id && form.IsPending))
            {
                CancelForm(form, state, session.Id);
            }

            state.Publish("session.execution.interrupted", new { sessionID = session.Id, reason = "user" });
        }

        await WriteJsonAsync(response, 200, new { interrupted = activeTurn != 0 }, cancellationToken);
    }

    private static void CancelForm(FixtureForm form, FixtureState state, string sessionId)
    {
        form.State = OpenCodeFormStates.Cancelled;
        state.Publish("form.cancelled", new { id = form.Id, sessionID = sessionId });
        form.Settled.TrySetResult();
    }

    /// <summary>
    /// Publishes the forms of a scripted question turn and reacts to their settlement, the way the probe's turn did.
    /// </summary>
    private static async Task RunQuestionTurnAsync(FixtureSession session, FixtureState state, OpenCodeFixtureQuestionScript script, long turnId)
    {
        try
        {
            var messageId = $"msg_{Guid.NewGuid():N}";
            var forms = new List<FixtureForm>();
            foreach (var definition in script.Forms)
            {
                var form = new FixtureForm(
                    $"frm_{Guid.NewGuid():N}",
                    definition.SessionId ?? session.Id,
                    definition.Kind,
                    messageId,
                    $"call_{Guid.NewGuid():N}",
                    definition.Fields);
                state.Forms[form.Id] = form;
                forms.Add(form);
            }

            if (script.PublishAllAtOnce)
            {
                foreach (var form in forms)
                {
                    state.Publish("form.created", new { form = form.ToFormJson() });
                }
            }

            foreach (var form in forms)
            {
                if (!script.PublishAllAtOnce)
                {
                    state.Publish("form.created", new { form = form.ToFormJson() });
                }

                var waitsForTurn = form.SessionId == session.Id && form.Kind == OpenCodeFormKinds.Question;
                if (waitsForTurn && !script.CompleteWithoutWaiting)
                {
                    await form.Settled.Task.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                }
            }

            if (Interlocked.Read(ref session.InterruptedTurnId) == turnId || script.StallAfterSettlement)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref session.ActiveTurnId, 0, turnId) != turnId)
            {
                return;
            }

            var text = "QUESTIONS: " + string.Join(" | ", forms.Where(form => form.SessionId == session.Id).Select(form => $"{form.State}:{form.AnswerJson ?? form.Message ?? string.Empty}"));
            var assistantMessageId = $"msg_{Guid.NewGuid():N}";
            state.Publish("session.text.ended", new { sessionID = session.Id, assistantMessageID = assistantMessageId, ordinal = 0, text });
            state.Publish("session.step.ended", new { sessionID = session.Id, assistantMessageID = assistantMessageId, finish = "stop", rawFinish = "stop" });
            state.Publish("session.execution.succeeded", new { sessionID = session.Id });
        }
        catch (TimeoutException)
        {
            // A form that nobody settled within the fixture's limit leaves the turn pending, like a stalled agent.
        }
    }

    private static async Task RunChildSessionTurnAsync(FixtureSession session, FixtureState state, OpenCodeFixtureChildSessionScript script)
    {
        var childId = $"ses_{Guid.NewGuid():N}";
        var childMessageId = $"msg_{Guid.NewGuid():N}";
        var parentMessageId = $"msg_{Guid.NewGuid():N}";
        var callId = $"call_{Guid.NewGuid():N}";

        if (script.IncludeForeignSession)
        {
            // Another client's session on the same server: a root session and a child of a session this turn never prompted.
            var foreignId = $"ses_{Guid.NewGuid():N}";
            state.Publish("session.created", new { sessionID = foreignId, title = "other client" });
            state.Publish("session.execution.started", new { sessionID = foreignId });
            state.Publish("session.text.ended", new { sessionID = foreignId, assistantMessageID = $"msg_{Guid.NewGuid():N}", ordinal = 0, text = "FOREIGN-TEXT" });
            state.Publish("session.created", new { sessionID = $"ses_{Guid.NewGuid():N}", parentID = foreignId, title = "foreign child" });
        }

        state.Publish("session.created", new { sessionID = childId, parentID = session.Id, title = script.ChildTitle, agent = "general" });
        state.Publish("session.execution.started", new { sessionID = childId });
        state.Publish("session.tool.called", new { sessionID = childId, assistantMessageID = childMessageId, id = callId, input = new { path = "." }, executed = false });
        state.Publish("session.tool.success", new { sessionID = childId, assistantMessageID = childMessageId, id = callId, content = new[] { new { type = "text", text = "listing" } }, executed = false });
        state.Publish("session.text.ended", new { sessionID = childId, assistantMessageID = childMessageId, ordinal = 0, text = script.ChildText });

        if (script.ParentInterruptsWhileChildRuns)
        {
            state.Publish("session.execution.interrupted", new { sessionID = session.Id });
            return;
        }

        state.Publish("session.text.ended", new { sessionID = session.Id, assistantMessageID = parentMessageId, ordinal = 0, text = script.ParentText });
        state.Publish("session.execution.succeeded", new { sessionID = session.Id });

        if (script.ChildNeverEnds)
        {
            return;
        }

        await Task.Delay(script.ChildRunTime).ConfigureAwait(false);
        state.Publish(
            script.ChildInterrupted ? "session.execution.interrupted" : "session.execution.succeeded",
            new { sessionID = childId });

        // OpenCode also tells the prompted session that the child ended: a synthetic inbox item (state "cancelled" for an interrupted child).
        state.Publish("session.inbox.enqueued", new
        {
            inboxID = $"msg_{Guid.NewGuid():N}",
            sessionID = session.Id,
            item = new
            {
                type = "synthetic",
                payload = new
                {
                    text = $"<subagent sessionID=\"{childId}\" state=\"{(script.ChildInterrupted ? "cancelled" : "completed")}\" description=\"{script.ChildTitle}\">",
                    description = script.ChildTitle,
                    metadata = new
                    {
                        source = "subagent",
                        childID = childId,
                        agent = "General",
                        state = script.ChildInterrupted ? "cancelled" : "completed",
                    },
                },
                delivery = "steer",
            },
        });

        if (script.FollowUpText is null)
        {
            return;
        }

        var followUpMessageId = $"msg_{Guid.NewGuid():N}";
        state.Publish("session.execution.started", new { sessionID = session.Id });
        if (script.FollowUpQuestion)
        {
            var form = new FixtureForm(
                $"frm_{Guid.NewGuid():N}",
                session.Id,
                OpenCodeFormKinds.Question,
                followUpMessageId,
                callId,
                [new OpenCodeFixtureField("Color", "Which color do you prefer?", MultiSelect: false, Options: ["Red", "Blue"])]);
            state.Forms[form.Id] = form;
            state.Publish("form.created", new { form = form.ToFormJson() });
            await form.Settled.Task.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        }

        state.Publish("session.text.ended", new { sessionID = session.Id, assistantMessageID = followUpMessageId, ordinal = 0, text = script.FollowUpText });
        state.Publish("session.execution.succeeded", new { sessionID = session.Id });
    }

    private static async Task HandleSetModelAsync(
        HttpListenerResponse response,
        HttpListenerRequest request,
        FixtureSession session,
        FixtureState state,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await ReadBodyAsync(request, cancellationToken));
        if (!document.RootElement.TryGetProperty("model", out var model))
        {
            await WriteJsonAsync(response, 400, new { _tag = "InvalidRequestError", message = "Missing key\n  at [\"model\"]", kind = "Payload" }, cancellationToken);
            return;
        }

        var providerId = TryGetString(model, "providerID") ?? string.Empty;
        var modelId = TryGetString(model, "id") ?? string.Empty;
        if (Matches(state.Options.InvalidModelIds, providerId, modelId))
        {
            await WriteJsonAsync(response, 400, new { _tag = "ProviderModelNotFoundError", message = $"ProviderModelNotFoundError: {providerId}/{modelId}" }, cancellationToken);
            return;
        }

        session.Model = (providerId, modelId);
        response.StatusCode = 204;
    }

    private static async Task HandlePromptAsync(
        HttpListenerResponse response,
        HttpListenerRequest request,
        FixtureSession session,
        FixtureState state,
        CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(request, cancellationToken);
        state.LastPromptBody = body;
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var promptText = TryGetString(root, "text") ?? string.Empty;
        var requestedId = TryGetString(root, "id");
        if (!string.IsNullOrWhiteSpace(requestedId) && !requestedId.StartsWith("msg", StringComparison.Ordinal))
        {
            await WriteJsonAsync(response, 400, new { _tag = "InvalidRequestError", message = $"Invalid string: must start with \"msg\" (id={requestedId})", kind = "Payload" }, cancellationToken);
            return;
        }

        if (!TryValidatePromptFiles(root, out var filesError))
        {
            await WriteJsonAsync(response, 400, new { _tag = "InvalidRequestError", message = filesError, field = "files" }, cancellationToken);
            return;
        }

        var (providerId, modelId) = session.Model ?? (string.Empty, string.Empty);
        var hasModel = session.Model is not null;
        if (hasModel && Matches(state.Options.NonJsonModelIds, providerId, modelId))
        {
            await WriteAsync(response, 200, "text/plain; charset=utf-8", "not-json", cancellationToken);
            return;
        }

        if (hasModel && Matches(state.Options.EmptyBodyModelIds, providerId, modelId))
        {
            await WriteAsync(response, 200, "application/json", string.Empty, cancellationToken);
            return;
        }

        var sessionId = session.Id;
        // OpenCode 2.0.32 uses the caller's message id as the inbox id, and echoes the requested delivery (steer when omitted).
        var inboxId = requestedId ?? $"msg_{Guid.NewGuid():N}";
        var delivery = TryGetString(root, "delivery") ?? "steer";
        if (string.Equals(delivery, "steer", StringComparison.Ordinal) && !string.IsNullOrEmpty(requestedId))
        {
            state.SteerRequestIds.Enqueue(requestedId);
        }

        var created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (hasModel && Matches(state.Options.SteerModelIds, providerId, modelId))
        {
            await HandleSteerablePromptAsync(response, session, state, inboxId, promptText, delivery, created, cancellationToken);
            return;
        }

        session.Messages.Add(new { id = inboxId, time = new { created }, type = "user", text = promptText });

        state.Publish("session.inbox.enqueued", new { inboxID = inboxId, sessionID = sessionId });
        state.Publish("session.execution.started", new { sessionID = sessionId });

        if (hasModel && Matches(state.Options.FailingModelIds, providerId, modelId))
        {
            state.Publish("session.execution.failed", new
            {
                sessionID = sessionId,
                error = new { type = "provider.no-route", message = $"Model unavailable: {providerId}/{modelId}" },
            });
        }
        else if (hasModel && Matches(state.Options.QuestionModelIds, providerId, modelId))
        {
            // The turn continues in the background: the prompt is acknowledged now and the forms are published after it.
            var turnId = Interlocked.Increment(ref session.LastTurnId);
            Interlocked.Exchange(ref session.ActiveTurnId, turnId);
            _ = Task.Run(() => RunQuestionTurnAsync(session, state, state.Options.QuestionScript, turnId), CancellationToken.None);
        }
        else if (hasModel && Matches(state.Options.ChildSessionModelIds, providerId, modelId))
        {
            // The turn continues in the background: the prompt is acknowledged now and the child session is published after it.
            _ = Task.Run(() => RunChildSessionTurnAsync(session, state, state.Options.ChildSessionScript), CancellationToken.None);
        }
        else
        {
            var assistantMessageId = $"msg_{Guid.NewGuid():N}";
            if (hasModel && Matches(state.Options.EmptyTextModelIds, providerId, modelId))
            {
                state.Publish("session.step.ended", new { sessionID = sessionId, assistantMessageID = assistantMessageId, finish = "stop", rawFinish = "stop" });
            }
            else
            {
                var assistantText = BuildAssistantText(promptText, state.Options.AssistantPrefix);
                state.Publish("session.text.started", new { sessionID = sessionId, assistantMessageID = assistantMessageId, ordinal = 0 });
                state.Publish("session.text.delta", new { sessionID = sessionId, assistantMessageID = assistantMessageId, ordinal = 0, delta = assistantText });
                state.Publish("session.text.ended", new { sessionID = sessionId, assistantMessageID = assistantMessageId, ordinal = 0, text = assistantText });
                state.Publish("session.step.ended", new { sessionID = sessionId, assistantMessageID = assistantMessageId, finish = "stop", rawFinish = "stop" });
                session.Messages.Add(new { id = assistantMessageId, time = new { created }, type = "assistant", content = new[] { new { type = "text", text = assistantText } }, finish = "stop" });
            }

            state.Publish("session.execution.succeeded", new { sessionID = sessionId });
        }

        await WriteJsonAsync(response, 200, new
        {
            data = new
            {
                id = inboxId,
                sessionID = sessionId,
                time = new { created },
                type = "user",
                payload = new { text = promptText },
                delivery,
            },
        }, cancellationToken);
    }

    /// <summary>
    /// Answers a prompt to a steerable model. With no steerable turn running, the prompt starts one. With one running, the prompt is a
    /// steer: it is enqueued for the running turn, or refused when the script says so. It is delivered at the turn's next step boundary.
    /// </summary>
    private static async Task HandleSteerablePromptAsync(
        HttpListenerResponse response,
        FixtureSession session,
        FixtureState state,
        string inboxId,
        string promptText,
        string delivery,
        long created,
        CancellationToken cancellationToken)
    {
        var sessionId = session.Id;
        var script = state.Options.SteerScript;
        var activeTurn = Interlocked.Read(ref session.ActiveTurnId);
        var enqueued = new { inboxID = inboxId, sessionID = sessionId, item = new { type = "user", payload = new { text = promptText }, delivery } };
        if (activeTurn != 0 && activeTurn == session.SteerTurnId)
        {
            if (script.RefuseSteers)
            {
                await WriteJsonAsync(response, 400, new { _tag = "InvalidRequestError", message = "Steer refused by the fixture", kind = "Payload" }, cancellationToken);
                return;
            }

            if (script.DeliverSteersImmediately)
            {
                state.Publish("session.inbox.enqueued", enqueued);
                state.Publish("session.inbox.delivered", new { sessionID = sessionId, inboxID = inboxId });
            }
            else
            {
                lock (session.SteerLock)
                {
                    session.PendingSteers.Add(inboxId);
                }

                state.Publish("session.inbox.enqueued", enqueued);
            }
        }
        else
        {
            // Idle session: the prompt is its own steer, delivered as the turn starts (OpenCode 2.0.32 probe).
            var turnId = Interlocked.Increment(ref session.LastTurnId);
            session.SteerTurnId = turnId;
            Interlocked.Exchange(ref session.ActiveTurnId, turnId);
            state.Publish("session.inbox.enqueued", enqueued);
            state.Publish("session.execution.started", new { sessionID = sessionId });
            state.Publish("session.inbox.delivered", new { sessionID = sessionId, inboxID = inboxId });
            _ = Task.Run(() => RunSteerTurnAsync(session, state, script, turnId), CancellationToken.None);
        }

        await WriteJsonAsync(response, 200, new
        {
            data = new
            {
                id = inboxId,
                sessionID = sessionId,
                time = new { created },
                type = "user",
                payload = new { text = promptText },
                delivery,
            },
        }, cancellationToken);
    }

    /// <summary>
    /// Runs the steerable turn: a first step, a step boundary that delivers the steers pending at that moment, and success. A steer that
    /// arrives after the boundary stays undelivered, and the turn ends without it, as the probe observed.
    /// </summary>
    private static async Task RunSteerTurnAsync(FixtureSession session, FixtureState state, OpenCodeFixtureSteerScript script, long turnId)
    {
        var sessionId = session.Id;
        await Task.Delay(script.StepDelay).ConfigureAwait(false);
        if (Interlocked.Read(ref session.ActiveTurnId) != turnId)
        {
            // Interrupted during the first step: the pending steers are never delivered.
            lock (session.SteerLock)
            {
                session.PendingSteers.Clear();
            }

            return;
        }

        var stepId = $"msg_{Guid.NewGuid():N}";
        state.Publish("session.text.ended", new { sessionID = sessionId, assistantMessageID = stepId, ordinal = 0, text = "STEER-STEP-1" });
        state.Publish("session.step.ended", new { sessionID = sessionId, assistantMessageID = stepId, finish = "stop", rawFinish = "stop" });

        var delivered = new List<string>();
        lock (session.SteerLock)
        {
            if (script.DeliverSteers)
            {
                delivered.AddRange(session.PendingSteers);
            }

            session.PendingSteers.Clear();
        }

        foreach (var inboxId in delivered)
        {
            state.Publish("session.inbox.delivered", new { sessionID = sessionId, inboxID = inboxId });
        }

        if (delivered.Count > 0)
        {
            // The steers join the turn's context, so one more step answers them before the turn ends.
            var answerId = $"msg_{Guid.NewGuid():N}";
            state.Publish("session.text.ended", new { sessionID = sessionId, assistantMessageID = answerId, ordinal = 0, text = "STEER-STEP-2" });
            state.Publish("session.step.ended", new { sessionID = sessionId, assistantMessageID = answerId, finish = "stop", rawFinish = "stop" });
        }

        if (Interlocked.CompareExchange(ref session.ActiveTurnId, 0, turnId) != turnId)
        {
            return;
        }

        lock (session.SteerLock)
        {
            session.PendingSteers.Clear();
        }

        if (script.FailTurn)
        {
            state.Publish("session.execution.failed", new { sessionID = sessionId, error = new { type = "provider.no-route", message = "Turn failed after the steer." } });
            return;
        }

        state.Publish("session.execution.succeeded", new { sessionID = sessionId });
    }

    private static async Task StreamEventsAsync(HttpListenerResponse response, FixtureState state, CancellationToken cancellationToken)
    {
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        var channel = Channel.CreateUnbounded<string>();
        state.Subscribe(channel);
        try
        {
            await using var writer = new StreamWriter(response.OutputStream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(new { id = $"evt_{Guid.NewGuid():N}", type = "server.connected", data = new { } })}\n\n");
            await writer.FlushAsync(cancellationToken);
            await foreach (var json in channel.Reader.ReadAllAsync(cancellationToken))
            {
                await writer.WriteAsync($"data: {json}\n\n");
                await writer.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (HttpListenerException)
        {
        }
        finally
        {
            state.Unsubscribe(channel);
        }
    }

    private static async Task HandleFileListAsync(HttpListenerResponse response, HttpListenerRequest request, CancellationToken cancellationToken)
    {
        var locationDirectory = request.QueryString["location[directory]"];
        var relativePath = request.QueryString["path"] ?? string.Empty;
        var root = string.IsNullOrWhiteSpace(locationDirectory) ? Directory.GetCurrentDirectory() : locationDirectory;
        var target = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!Directory.Exists(target))
        {
            await WriteJsonAsync(response, 404, new { _tag = "FileNotFoundError", path = target, message = $"Directory not found: {target}" }, cancellationToken);
            return;
        }

        var entries = Directory.EnumerateFileSystemEntries(target)
            .Select(entry => new
            {
                path = Path.GetRelativePath(target, entry),
                type = Directory.Exists(entry) ? "directory" : "file",
            })
            .ToArray();
        await WriteJsonAsync(response, 200, new { location = new { directory = root }, data = entries }, cancellationToken);
    }

    private static bool IsApiPath(string path)
    {
        return path == "/api" || path.StartsWith("/api/", StringComparison.Ordinal);
    }

    private static bool IsAuthorized(HttpListenerRequest request, string password)
    {
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{password}"));
        return string.Equals(request.Headers["Authorization"], expected, StringComparison.Ordinal);
    }

    private static bool TryMatchSessionRoute(string path, out string sessionId, out string action)
    {
        const string prefix = "/api/session/";
        sessionId = string.Empty;
        action = string.Empty;
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = path[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        sessionId = Uri.UnescapeDataString(parts[0]);
        action = parts[1];
        return true;
    }

    /// <summary>
    /// Matches the form and interrupt routes: <c>/api/session/{id}/form</c>, <c>/api/session/{id}/form/{formId}</c>,
    /// <c>/api/session/{id}/form/{formId}/reply</c> and <c>/api/session/{id}/interrupt</c>.
    /// </summary>
    private static bool TryMatchFormRoute(string path, out string sessionId, out string action, out string formId)
    {
        const string prefix = "/api/session/";
        sessionId = string.Empty;
        action = string.Empty;
        formId = string.Empty;
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = path[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        sessionId = parts.Length > 0 ? Uri.UnescapeDataString(parts[0]) : string.Empty;
        switch (parts)
        {
            case [_, "form"]:
                action = "form";
                return true;
            case [_, "interrupt"]:
                action = "interrupt";
                return true;
            case [_, "form", var id]:
                action = "form";
                formId = Uri.UnescapeDataString(id);
                return true;
            case [_, "form", var id, "reply"]:
                action = "reply";
                formId = Uri.UnescapeDataString(id);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Checks the <c>files</c> array the way OpenCode 2.0 does: every entry is an object whose <c>uri</c> is a base64
    /// <c>data:</c> URL. The error names the entry and never echoes its data.
    /// </summary>
    private static bool TryValidatePromptFiles(JsonElement root, out string? error)
    {
        error = null;
        if (!root.TryGetProperty("files", out var files))
        {
            return true;
        }

        if (files.ValueKind != JsonValueKind.Array)
        {
            error = "Expected an array\n  at [\"files\"]";
            return false;
        }

        var index = 0;
        foreach (var file in files.EnumerateArray())
        {
            var location = $"[\"files\"][{index++}]";
            if (file.ValueKind != JsonValueKind.Object)
            {
                error = $"Expected PromptInput.FileAttachment\n  at {location}";
                return false;
            }

            if (!file.TryGetProperty("uri", out var uriNode) || uriNode.ValueKind != JsonValueKind.String)
            {
                error = $"Missing key\n  at {location}[\"uri\"]";
                return false;
            }

            var uri = uriNode.GetString() ?? string.Empty;
            if (!uri.StartsWith("data:", StringComparison.Ordinal))
            {
                error = "Unsupported attachment URI: only data: URLs are accepted";
                return false;
            }

            var comma = uri.IndexOf(',', StringComparison.Ordinal);
            if (comma < 0 || !uri[..comma].EndsWith(";base64", StringComparison.Ordinal) || !IsBase64(uri[(comma + 1)..]))
            {
                error = $"Invalid attachment data URL: {TryGetString(file, "name") ?? "(unnamed)"}";
                return false;
            }
        }

        return true;
    }

    private static bool IsBase64(string payload)
    {
        try
        {
            _ = Convert.FromBase64String(payload);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// The JSON of one <c>GET /api/model</c> entry. A model without input capabilities omits the <c>capabilities</c> member.
    /// </summary>
    private static Dictionary<string, object?> BuildModelJson(OpenCodeFixtureModel model)
    {
        var json = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = model.ModelId,
            ["modelID"] = model.ModelId,
            ["providerID"] = model.ProviderId,
        };
        if (model.Input is not null)
        {
            json["capabilities"] = new { tools = true, input = model.Input, output = new[] { "text" } };
        }

        return json;
    }

    private static bool Matches(IReadOnlyCollection<string> configuredValues, string providerId, string modelId)
    {
        if (configuredValues.Count == 0)
        {
            return false;
        }

        var qualified = string.IsNullOrEmpty(providerId) ? modelId : $"{providerId}/{modelId}";
        return configuredValues.Any(candidate =>
            string.Equals(candidate, qualified, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate, modelId, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildAssistantText(string promptText, string assistantPrefix)
    {
        if (promptText.Contains("PCode.slnx", StringComparison.OrdinalIgnoreCase) &&
            promptText.Contains("PCode.Provider.Testing", StringComparison.OrdinalIgnoreCase))
        {
            return """
Repository analysis complete.

The solution file `PCode.slnx` is at the root of the `hagicode-core` repository.
The canonical OpenCode console project `HagiCode.Libs.OpenCode.Console` owns the provider validation scenarios.

- `HagiCode.Libs.OpenCode.Console` runs OpenCode integration scenarios such as bootstrap, prompt round-trip, repository analysis, and diagnostics.
- The OpenCode test console verifies live or fixture-backed repository investigation workflows without editing repository files.
""";
        }

        if (promptText.Contains("READY", StringComparison.OrdinalIgnoreCase))
        {
            return assistantPrefix;
        }

        return $"{assistantPrefix}: {promptText}";
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static string? TryGetString(JsonElement? element, string propertyName)
    {
        return element is { ValueKind: JsonValueKind.Object } value &&
               value.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string? TryGetNestedString(JsonElement? element, string outer, string inner)
    {
        if (element is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty(outer, out var nested))
        {
            return null;
        }

        return TryGetString(nested, inner);
    }

    private static Task WriteJsonAsync<T>(HttpListenerResponse response, int statusCode, T payload, CancellationToken cancellationToken)
    {
        return WriteAsync(response, statusCode, "application/json", JsonSerializer.Serialize(payload), cancellationToken);
    }

    private static async Task WriteAsync(
        HttpListenerResponse response,
        int statusCode,
        string contentType,
        string body,
        CancellationToken cancellationToken)
    {
        response.StatusCode = statusCode;
        response.ContentType = contentType;
        var bytes = Encoding.UTF8.GetBytes(body);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
    }

    private sealed class FixtureState
    {
        private readonly List<Channel<string>> _subscribers = [];
        private readonly object _subscriberLock = new();

        public FixtureState(OpenCodeFixtureServerOptions options, string password, Uri baseUri)
        {
            Options = options;
            Password = password;
            BaseUri = baseUri;
        }

        public OpenCodeFixtureServerOptions Options { get; }

        public string Password { get; }

        public Uri BaseUri { get; }

        public ConcurrentDictionary<string, FixtureSession> Sessions { get; } = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, FixtureForm> Forms { get; } = new(StringComparer.Ordinal);

        public long NextSessionId;

        private string? _lastPromptBody;

        /// <summary>The raw body of the last prompt the simulated server received, valid or not.</summary>
        public string? LastPromptBody
        {
            get => Volatile.Read(ref _lastPromptBody);
            set => Volatile.Write(ref _lastPromptBody, value);
        }

        public long InterruptRequestCount;

        /// <summary>The message ids of the prompts that asked for <c>delivery: "steer"</c>, in arrival order.</summary>
        public ConcurrentQueue<string> SteerRequestIds { get; } = new();

        public long ModelListingRequestCount;

        public void Subscribe(Channel<string> channel)
        {
            lock (_subscriberLock)
            {
                _subscribers.Add(channel);
            }
        }

        public void Unsubscribe(Channel<string> channel)
        {
            lock (_subscriberLock)
            {
                _subscribers.Remove(channel);
            }

            channel.Writer.TryComplete();
        }

        public void Publish(string type, object data)
        {
            var json = JsonSerializer.Serialize(new
            {
                id = $"evt_{Guid.NewGuid():N}",
                created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                type,
                data,
            });

            lock (_subscriberLock)
            {
                foreach (var subscriber in _subscribers)
                {
                    subscriber.Writer.TryWrite(json);
                }
            }
        }
    }

    private sealed class FixtureSession
    {
        public FixtureSession(string id, object definition)
        {
            Id = id;
            Definition = definition;
        }

        public object Definition { get; }

        public string Id { get; }

        public (string ProviderId, string ModelId)? Model { get; set; }

        public List<object> Messages { get; } = [];

        /// <summary>The id of the scripted question turn that is running, or 0.</summary>
        public long ActiveTurnId;

        /// <summary>The id of the last scripted turn that was interrupted.</summary>
        public long InterruptedTurnId;

        public long LastTurnId;

        /// <summary>The id of the running steerable turn, or 0. Steers reach the session only while it is the active turn.</summary>
        public long SteerTurnId;

        /// <summary>Steers accepted by the running steerable turn and not yet delivered at a step boundary. Guarded by <see cref="SteerLock"/>.</summary>
        public List<string> PendingSteers { get; } = [];

        public object SteerLock { get; } = new();

    }

    /// <summary>
    /// One form of a fixture session. It is settled by a reply or a cancel, and the scripted turn waits on <see cref="Settled"/>.
    /// </summary>
    private sealed class FixtureForm
    {
        public FixtureForm(string id, string sessionId, string kind, string messageId, string callId, IReadOnlyList<OpenCodeFixtureField> fields)
        {
            Id = id;
            SessionId = sessionId;
            Kind = kind;
            MessageId = messageId;
            CallId = callId;
            Fields = fields;
        }

        public string Id { get; }

        public string SessionId { get; }

        public string Kind { get; }

        public string MessageId { get; }

        public string CallId { get; }

        public IReadOnlyList<OpenCodeFixtureField> Fields { get; }

        public string State { get; set; } = OpenCodeFormStates.Pending;

        public string? Message { get; set; }

        public string? AnswerJson { get; set; }

        public TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsPending => State == OpenCodeFormStates.Pending;

        public object ToFormJson()
        {
            return new
            {
                id = Id,
                sessionID = SessionId,
                title = "Questions",
                metadata = new { kind = Kind, tool = new { messageID = MessageId, id = CallId } },
                fields = Fields.Select((field, index) => new
                {
                    key = "q" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    title = field.Header,
                    description = field.Question,
                    type = field.MultiSelect ? "multiselect" : "string",
                    options = field.Options.Select(label => new { value = label, label, description = string.Empty }).ToArray(),
                    custom = true,
                }).ToArray(),
            };
        }
    }
}

public sealed class OpenCodeFixtureServerHandle : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly Task _loopTask;
    private readonly CancellationTokenSource _cts;
    private readonly string _startupOutput;
    private readonly Func<IReadOnlyList<OpenCodeFixtureFormSnapshot>> _formSnapshot;
    private readonly Func<long> _interruptCount;
    private readonly Func<string?> _lastPromptBody;
    private readonly Func<long> _modelListingCount;
    private readonly Func<IReadOnlyList<string>> _steerRequestIds;
    private readonly object _sync = new();
    private int _disposed;

    internal OpenCodeFixtureServerHandle(
        HttpListener listener,
        Uri baseUri,
        Task loopTask,
        CancellationTokenSource cts,
        string serverPassword,
        string startupOutput,
        Func<IReadOnlyList<OpenCodeFixtureFormSnapshot>> formSnapshot,
        Func<long> interruptCount,
        Func<string?> lastPromptBody,
        Func<long> modelListingCount,
        Func<IReadOnlyList<string>> steerRequestIds)
    {
        _steerRequestIds = steerRequestIds;
        _listener = listener;
        BaseUri = baseUri;
        _loopTask = loopTask;
        _cts = cts;
        ServerPassword = serverPassword;
        _startupOutput = startupOutput;
        _formSnapshot = formSnapshot;
        _interruptCount = interruptCount;
        _lastPromptBody = lastPromptBody;
        _modelListingCount = modelListingCount;
    }

    /// <summary>
    /// The forms the simulated server has opened, with their current state. Tests read the settled outcome from it.
    /// </summary>
    public IReadOnlyList<OpenCodeFixtureFormSnapshot> GetForms() => _formSnapshot();

    /// <summary>
    /// The number of <c>POST /api/session/{id}/interrupt</c> requests the simulated server has received.
    /// </summary>
    public long InterruptRequestCount => _interruptCount();

    /// <summary>
    /// The raw body of the last <c>POST /api/session/{id}/prompt</c> the simulated server received, or <c>null</c> when none arrived.
    /// Tests parse it to check the <c>files</c> object that reached the server.
    /// </summary>
    public string? LastPromptBody => _lastPromptBody();

    /// <summary>
    /// The message ids of the prompts that asked for <c>delivery: "steer"</c>, in arrival order, including refused ones. A steer the
    /// caller never sent does not appear here.
    /// </summary>
    public IReadOnlyList<string> SteerRequestIds => _steerRequestIds();

    /// <summary>
    /// The number of <c>GET /api/model</c> requests the simulated server has received.
    /// </summary>
    public long ModelListingRequestCount => _modelListingCount();

    public Uri BaseUri { get; }

    /// <summary>
    /// Gets the password the simulated server requires. Test code uses it to configure attach mode.
    /// </summary>
    public string ServerPassword { get; }

    public int? ProcessId => null;

    /// <summary>
    /// Gets the startup output with the password redacted, in the same form an owned OpenCode 2.0 runtime prints.
    /// </summary>
    public string? CapturedOutput => OpenCodeSecretRedactor.Redact(_startupOutput, ServerPassword);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        lock (_sync)
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
        }

        try
        {
            await _loopTask;
        }
        catch
        {
        }
        finally
        {
            _cts.Dispose();
        }
    }
}
