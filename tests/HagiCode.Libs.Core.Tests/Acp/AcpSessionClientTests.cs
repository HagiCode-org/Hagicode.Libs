using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using HagiCode.Libs.Core.Acp;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Acp;

public sealed class AcpSessionClientTests
{
    [Fact]
    public async Task StartSessionAsync_bootstraps_initialize_and_parses_string_session_result()
    {
        var transport = new ScriptedAcpTransport(request => request.Method switch
        {
            "initialize" => [CreateJsonRpcResult(request.Id, """{"protocolVersion":1,"agentInfo":{"name":"codebuddy","version":"1.0.0"}}""")],
            "session/new" => [CreateJsonRpcResult(request.Id, JsonSerializer.Serialize("// ready\n{\"sessionId\":\"session-123\"}"))],
            _ => throw new InvalidOperationException($"Unexpected ACP method: {request.Method}")
        });

        await using var client = new AcpSessionClient(transport);
        await client.ConnectAsync();

        var initializeResult = await client.InitializeAsync();
        var session = await client.StartSessionAsync("/tmp/project", null, null);

        initializeResult.GetProperty("protocolVersion").GetInt32().ShouldBe(1);
        session.SessionId.ShouldBe("session-123");
        session.IsResumed.ShouldBeFalse();
        transport.SentMethods.ShouldBe(["initialize", "session/new"]);
        transport.Requests[1].Params.GetProperty("cwd").GetString().ShouldBe("/tmp/project");
    }

    [Fact]
    public async Task InitializeAsync_honors_cancellation_when_transport_never_responds()
    {
        var transport = new HangingAcpTransport();
        await using var client = new AcpSessionClient(transport);
        await client.ConnectAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Should.ThrowAsync<TaskCanceledException>(async () => await client.InitializeAsync(cts.Token));
    }

    [Fact]
    public async Task SendPromptAsync_enqueues_synthetic_prompt_completed_notification_for_end_turn_results()
    {
        var transport = new ScriptedAcpTransport(request => request.Method switch
        {
            "session/prompt" => [CreateJsonRpcResult(request.Id, """{"stopReason":"end_turn","outputText":"pong"}""")],
            _ => throw new InvalidOperationException($"Unexpected ACP method: {request.Method}")
        });

        await using var client = new AcpSessionClient(transport);
        await client.ConnectAsync();

        var promptResult = await client.SendPromptAsync("session-123", "hello");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await using var enumerator = client.ReceiveNotificationsAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        promptResult.GetProperty("stopReason").GetString().ShouldBe("end_turn");
        enumerator.Current.Method.ShouldBe("session/update");
        enumerator.Current.Parameters.GetProperty("sessionId").GetString().ShouldBe("session-123");
        enumerator.Current.Parameters.GetProperty("update").GetProperty("sessionUpdate").GetString().ShouldBe("prompt_completed");
    }

    [Fact]
    public async Task SetModeAsync_invokes_session_set_mode()
    {
        var transport = new ScriptedAcpTransport(request => request.Method switch
        {
            "initialize" => [CreateJsonRpcResult(request.Id, """{"protocolVersion":1}""")],
            "session/set_mode" => [CreateJsonRpcResult(request.Id, "{}")],
            _ => throw new InvalidOperationException($"Unexpected ACP method: {request.Method}")
        });

        await using var client = new AcpSessionClient(transport);
        await client.ConnectAsync();

        await client.SetModeAsync("session-123", "yolo");

        transport.SentMethods.ShouldBe(["initialize", "session/set_mode"]);
        transport.Requests[1].Params.GetProperty("sessionId").GetString().ShouldBe("session-123");
        transport.Requests[1].Params.GetProperty("modeId").GetString().ShouldBe("yolo");
    }

    [Fact]
    public void SanitizeIncomingMessage_strips_comment_preamble()
    {
        var payload = "// ready\n{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{}}";

        var sanitized = AcpTransportMessageParser.SanitizeIncomingMessage(payload, out var ignoredComment);

        sanitized.ShouldBe("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{}}");
        ignoredComment.ShouldBe("// ready");
    }

    private static string CreateJsonRpcResult(string id, string rawResultJson)
    {
        return "{\"jsonrpc\":\"2.0\",\"id\":\"" + id + "\",\"result\":" + rawResultJson + "}";
    }

    private sealed record RpcRequest(string Id, string Method, JsonElement Params);

    private sealed class ScriptedAcpTransport(Func<RpcRequest, IReadOnlyList<string>> responseFactory) : IAcpTransport
    {
        private readonly Channel<string> _messages = Channel.CreateUnbounded<string>();
        private bool _completed;

        public bool IsConnected { get; private set; }

        public List<RpcRequest> Requests { get; } = [];

        public List<string> SentMethods { get; } = [];

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task SendMessageAsync(string message, CancellationToken cancellationToken = default)
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            var request = new RpcRequest(
                root.GetProperty("id").GetString()!,
                root.GetProperty("method").GetString()!,
                root.TryGetProperty("params", out var parameters) ? parameters.Clone() : default);
            Requests.Add(request);
            SentMethods.Add(request.Method);

            foreach (var response in responseFactory(request))
            {
                _messages.Writer.TryWrite(response);
            }

            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<string> ReceiveMessagesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var message in _messages.Reader.ReadAllAsync(cancellationToken))
            {
                yield return message;
            }
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            Complete();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            Complete();
            return ValueTask.CompletedTask;
        }

        private void Complete()
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            _messages.Writer.TryComplete();
        }
    }

    private sealed class HangingAcpTransport : IAcpTransport
    {
        public bool IsConnected { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task SendMessageAsync(string message, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<string> ReceiveMessagesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }
}
