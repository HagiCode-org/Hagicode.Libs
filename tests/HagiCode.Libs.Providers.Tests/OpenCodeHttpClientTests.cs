using System.Net;
using System.Text;
using System.Text.Json;
using HagiCode.Libs.Providers.OpenCode;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Pins the OpenCode 2.0 wire contract of <see cref="OpenCodeHttpClient"/> with a scripted handler (no server).
/// </summary>
public sealed class OpenCodeHttpClientTests
{
    private const string Password = "stub-secret";
    private const string SessionId = "ses_1";

    [Test]
    public async Task Requests_use_api_routes_with_basic_credentials_and_send_the_directory_in_the_body()
    {
        var handler = new ScriptedHandler(request => request.Method == "POST"
            ? Json($"{{\"data\":{{\"id\":\"{SessionId}\",\"location\":{{\"directory\":\"/work/project\"}}}}}}")
            : Json("{\"version\":\"2.0.32\",\"pid\":1,\"urls\":[],\"paths\":{\"tmp\":\"/tmp\"}}"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        using var client = new OpenCodeHttpClient(http, "/work/project", "legacy-workspace", Password);

        var health = await client.HealthAsync();
        var session = await client.CreateSessionAsync("title");

        health.Healthy.ShouldBeTrue();
        session.Location!.Directory.ShouldBe("/work/project");
        handler.Requests.Select(static request => request.PathAndQuery).ShouldBe(["/api/info", "/api/session"]);
        handler.Requests.All(static request => request.Authorization == ExpectedBasic()).ShouldBeTrue();
        handler.Requests[1].Body.ShouldContain("\"location\":{\"directory\":\"/work/project\"}");
        handler.Requests[1].Body.ShouldNotContain("workspace");
        handler.Requests[1].Body.ShouldNotContain("legacy-workspace");
    }

    [Test]
    public async Task Requests_carry_no_credential_when_no_password_is_configured()
    {
        var handler = new ScriptedHandler(static _ => Json("{\"version\":\"2.0.32\"}"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        using var client = new OpenCodeHttpClient(http, directory: null, workspace: null);

        await client.HealthAsync();

        handler.Requests.Single().Authorization.ShouldBeNull();
    }

    [Test]
    public async Task Unauthorized_response_is_reported_as_an_authentication_failure_without_the_password()
    {
        var handler = new ScriptedHandler(static _ => Status(HttpStatusCode.Unauthorized, "{\"_tag\":\"UnauthorizedError\",\"message\":\"Authentication required\"}"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        using var client = new OpenCodeHttpClient(http, directory: null, workspace: null, Password);

        var exception = await Should.ThrowAsync<OpenCodeAuthenticationException>(async () => await client.HealthAsync());

        exception.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        exception.Message.ShouldContain("authentication failed");
        exception.Message.ShouldNotContain(Password);
    }

    [Test]
    public async Task Unauthorized_response_without_a_configured_password_asks_for_one()
    {
        var handler = new ScriptedHandler(static _ => Status(HttpStatusCode.Unauthorized, "{}"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        using var client = new OpenCodeHttpClient(http, directory: null, workspace: null);

        var exception = await Should.ThrowAsync<OpenCodeAuthenticationException>(async () => await client.HealthAsync());

        exception.Message.ShouldContain("server password is missing or wrong");
        handler.Requests.Single().Authorization.ShouldBeNull();
    }

    [Test]
    public async Task Html_body_with_success_status_is_reported_as_an_unsupported_api()
    {
        var handler = new ScriptedHandler(static _ => Html("<!doctype html><html><body>OpenCode</body></html>"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        using var client = new OpenCodeHttpClient(http, directory: null, workspace: null, Password);

        var exception = await Should.ThrowAsync<OpenCodeUnsupportedApiException>(async () => await client.HealthAsync());

        exception.Message.ShouldContain("/api/info");
        exception.Message.ShouldContain("not the OpenCode 2.0 API");
        exception.ResponseBody.ShouldContain("<!doctype html>");
    }

    [Test]
    public async Task Listing_files_and_sessions_uses_the_2_0_routes()
    {
        var handler = new ScriptedHandler(request => request.PathAndQuery.StartsWith("/api/fs/list", StringComparison.Ordinal)
            ? Json("{\"location\":{\"directory\":\"/work\"},\"data\":[{\"path\":\"src\",\"type\":\"directory\"}]}")
            : Json($"{{\"data\":[{{\"id\":\"{SessionId}\"}}],\"cursor\":{{\"previous\":null,\"next\":null}}}}"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        using var client = new OpenCodeHttpClient(http, directory: "/work", workspace: null, Password);

        var files = await client.ListFilesAsync(".");
        var sessions = await client.ListSessionsAsync();

        files.Single().Path.ShouldBe("src");
        files.Single().Type.ShouldBe("directory");
        sessions.Single().Id.ShouldBe(SessionId);
        handler.Requests[0].PathAndQuery.ShouldBe("/api/fs/list?path=.&location%5Bdirectory%5D=%2Fwork");
        handler.Requests[1].PathAndQuery.ShouldBe("/api/session");
    }

    [Test]
    public async Task Prompt_assembles_the_assistant_text_from_the_event_stream()
    {
        var handler = PromptScript(Events(
            Event("server.connected", new { }),
            Event("session.inbox.enqueued", new { sessionID = SessionId }),
            Event("session.execution.started", new { sessionID = SessionId }),
            Event("session.text.ended", new { sessionID = "ses_other", assistantMessageID = "msg_other", text = "WRONG" }),
            Event("session.text.ended", new { sessionID = SessionId, assistantMessageID = "msg_a", ordinal = 0, text = "Part one" }),
            Event("session.text.ended", new { sessionID = SessionId, assistantMessageID = "msg_a", ordinal = 1, text = "Part two" }),
            Event("session.execution.succeeded", new { sessionID = SessionId }),
            Event("session.usage.updated", new { sessionID = SessionId })));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        using var client = new OpenCodeHttpClient(http, directory: null, workspace: null, Password);

        var response = await client.PromptAsync(SessionId, OpenCodeSessionPromptRequest.FromText("hi"));

        response.GetTextContent().ShouldBe($"Part one{Environment.NewLine}Part two");
        response.MessageId.ShouldBe("msg_a");
        response.Role.ShouldBe("assistant");
        response.SessionId.ShouldBe(SessionId);
    }

    [Test]
    public async Task Prompt_subscribes_to_events_before_posting_the_prompt()
    {
        var handler = PromptScript(Events(
            Event("server.connected", new { }),
            Event("session.text.ended", new { sessionID = SessionId, assistantMessageID = "msg_a", text = "pong" }),
            Event("session.execution.succeeded", new { sessionID = SessionId })));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        using var client = new OpenCodeHttpClient(http, directory: null, workspace: null, Password);

        await client.PromptAsync(SessionId, OpenCodeSessionPromptRequest.FromText("hi"));

        var order = handler.Requests.Select(static request => request.PathAndQuery).ToArray();
        order.ShouldBe(["/api/event", $"/api/session/{SessionId}/prompt"]);
        handler.Requests[1].Body.ShouldContain("\"text\":\"hi\"");
    }

    [Test]
    public async Task Failed_turn_returns_a_diagnostic_envelope_that_names_the_failure()
    {
        var handler = PromptScript(Events(
            Event("server.connected", new { }),
            Event("session.execution.failed", new { sessionID = SessionId, error = new { type = "provider.no-route", message = "Model unavailable: a/b" } })));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        using var client = new OpenCodeHttpClient(http, directory: null, workspace: null, Password);

        var response = await client.PromptAsync(SessionId, OpenCodeSessionPromptRequest.FromText("hi"));

        response.GetTextContent().ShouldBeEmpty();
        var summary = response.BuildDiagnosticSummary();
        summary.ShouldContain("failed turn");
        summary.ShouldContain("error.message=Model unavailable: a/b");
        summary.ShouldContain("error.reason=provider.no-route");
    }

    [Test]
    public async Task Successful_turn_without_text_is_distinguishable_from_a_failure()
    {
        var handler = PromptScript(Events(
            Event("server.connected", new { }),
            Event("session.execution.succeeded", new { sessionID = SessionId })));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        using var client = new OpenCodeHttpClient(http, directory: null, workspace: null, Password);

        var response = await client.PromptAsync(SessionId, OpenCodeSessionPromptRequest.FromText("hi"));

        response.GetTextContent().ShouldBeEmpty();
        var summary = response.BuildDiagnosticSummary();
        summary.ShouldContain("successful response with no readable text content");
        summary.ShouldContain("partTypes=(none)");
    }

    [Test]
    public async Task Prompt_with_a_cancelled_token_throws_cancellation()
    {
        var handler = PromptScript(Events(Event("server.connected", new { })));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        using var client = new OpenCodeHttpClient(http, directory: null, workspace: null, Password);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await client.PromptAsync(SessionId, OpenCodeSessionPromptRequest.FromText("hi"), cancelled.Token));
    }

    [Test]
    public async Task Prompt_that_never_finishes_times_out_with_a_timeout_not_a_protocol_error()
    {
        var handler = new ScriptedHandler(request => request.PathAndQuery == "/api/event"
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new HangingEventStream(Encoding.UTF8.GetBytes($"data: {Event("server.connected", new { })}\n\n")))
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream") },
                },
            }
            : Json("{\"data\":{\"id\":\"msg_1\"}}"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/"), Timeout = TimeSpan.FromMilliseconds(300) };
        using var client = new OpenCodeHttpClient(http, directory: null, workspace: null, Password);

        var exception = await Should.ThrowAsync<TimeoutException>(async () =>
            await client.PromptAsync(SessionId, OpenCodeSessionPromptRequest.FromText("hi")));

        exception.Message.ShouldContain("did not finish");
    }

    private static string ExpectedBasic()
    {
        return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"opencode:{Password}"));
    }

    private static string Event(string type, object data)
    {
        return JsonSerializer.Serialize(new { id = $"evt_{Guid.NewGuid():N}", type, data });
    }

    private static string Events(params string[] events)
    {
        return string.Concat(events.Select(static json => $"data: {json}\n\n"));
    }

    private static ScriptedHandler PromptScript(string eventsBody)
    {
        return new ScriptedHandler(request => request.PathAndQuery switch
        {
            "/api/event" => Sse(eventsBody),
            _ when request.Method == "POST" && request.PathAndQuery == $"/api/session/{SessionId}/prompt" =>
                Json($"{{\"data\":{{\"id\":\"msg_1\",\"sessionID\":\"{SessionId}\",\"type\":\"user\",\"payload\":{{\"text\":\"hi\"}},\"delivery\":\"steer\"}}}}"),
            _ => Status(HttpStatusCode.NotFound, "{}"),
        });
    }

    private static HttpResponseMessage Json(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static HttpResponseMessage Html(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };
    }

    private static HttpResponseMessage Sse(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
    }

    private static HttpResponseMessage Status(HttpStatusCode statusCode, string body)
    {
        return new HttpResponseMessage(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed record RecordedRequest(string Method, string PathAndQuery, string? Authorization, string Body);

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<RecordedRequest, HttpResponseMessage> _responder;
        private readonly List<RecordedRequest> _requests = [];

        public ScriptedHandler(Func<RecordedRequest, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public IReadOnlyList<RecordedRequest> Requests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToArray();
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var recorded = new RecordedRequest(
                request.Method.Method,
                request.RequestUri!.PathAndQuery,
                request.Headers.Authorization?.ToString(),
                body);
            lock (_requests)
            {
                _requests.Add(recorded);
            }

            return _responder(recorded);
        }
    }

    /// <summary>
    /// Returns the first chunk, then blocks until the read is cancelled, like an event stream that never ends.
    /// </summary>
    private sealed class HangingEventStream : Stream
    {
        private readonly byte[] _first;
        private bool _sentFirst;

        public HangingEventStream(byte[] first)
        {
            _first = first;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sentFirst)
            {
                _sentFirst = true;
                _first.CopyTo(buffer);
                return _first.Length;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
