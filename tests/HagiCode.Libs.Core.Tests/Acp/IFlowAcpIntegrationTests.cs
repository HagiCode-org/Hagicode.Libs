using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Process;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Acp;

public sealed class IFlowAcpIntegrationTests
{
    [Fact]
    public async Task InitializeAsync_ignores_iflow_comment_preamble_before_json_result()
    {
        var transport = new ScriptedAcpTransport(request => request.Method switch
        {
            "initialize" => ["// iflow ready\n" + CreateJsonRpcResult(request.Id, "{\"protocolVersion\":1,\"agentInfo\":{\"name\":\"iflow\",\"version\":\"0.5.0\"}}")],
            _ => throw new InvalidOperationException($"Unexpected ACP method: {request.Method}")
        });

        await using var client = new AcpSessionClient(transport);
        await client.ConnectAsync();

        var initializeResult = await client.InitializeAsync();

        initializeResult.GetProperty("agentInfo").GetProperty("name").GetString().ShouldBe("iflow");
        initializeResult.GetProperty("agentInfo").GetProperty("version").GetString().ShouldBe("0.5.0");
    }

    [Fact]
    public async Task InitializeAsync_surfaces_malformed_payload_diagnostics()
    {
        var transport = new MalformedAcpTransport();
        await using var client = new AcpSessionClient(transport);
        await client.ConnectAsync();

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () => await client.InitializeAsync());

        exception.Message.ShouldContain("malformed payloads");
        exception.Message.ShouldContain("this-is-not-json");
    }

    [Fact]
    public async Task BootstrapAsync_surfaces_managed_startup_failure_with_actionable_diagnostics()
    {
        var bootstrapper = new IFlowProcessBootstrapper(new CliProcessManager());

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await bootstrapper.BootstrapAsync(new IFlowBootstrapRequest
            {
                ExecutablePath = "dotnet",
                WorkingDirectory = Directory.GetCurrentDirectory(),
                StartupTimeout = TimeSpan.FromSeconds(5)
            }));

        exception.Message.ShouldContain("managed startup");
        exception.Message.ShouldContain("Process exited with code");
    }

    [Fact]
    public async Task BootstrapAsync_returns_explicit_endpoint_without_managing_a_process()
    {
        var bootstrapper = new IFlowProcessBootstrapper(new CliProcessManager());

        await using var lease = await bootstrapper.BootstrapAsync(new IFlowBootstrapRequest
        {
            Endpoint = new Uri("ws://127.0.0.1:7331/acp")
        });

        lease.Endpoint.ShouldBe(new Uri("ws://127.0.0.1:7331/acp"));
        lease.IsManaged.ShouldBeFalse();
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

    private sealed class MalformedAcpTransport : IAcpTransport
    {
        private readonly Channel<string> _messages = Channel.CreateUnbounded<string>();
        private bool _completed;

        public bool IsConnected { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task SendMessageAsync(string message, CancellationToken cancellationToken = default)
        {
            _messages.Writer.TryWrite("this-is-not-json");
            Complete();
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
}
