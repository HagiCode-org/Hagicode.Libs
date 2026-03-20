using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.IFlow;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class IFlowProviderTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";
    private static readonly string[] IFlowExecutableCandidates = ["iflow", "iflow-cli"];

    [Fact]
    public async Task ExecuteAsync_uses_managed_bootstrap_and_streams_normalized_messages()
    {
        var bootstrapper = new FakeBootstrapper(managed: true);
        var provider = CreateProvider(bootstrapper: bootstrapper);
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new IFlowOptions
                           {
                               ExecutablePath = "/custom/iflow",
                               WorkingDirectory = "/tmp/project",
                               Model = "iflow/default",
                               EnvironmentVariables = new Dictionary<string, string?>
                               {
                                   ["IFLOW_TOKEN"] = "token"
                               }
                           },
                           "hello"))
        {
            messages.Add(message);
        }

        bootstrapper.Requests.ShouldHaveSingleItem();
        bootstrapper.Requests[0].ExecutablePath.ShouldBe("/custom/iflow");
        bootstrapper.Requests[0].WorkingDirectory.ShouldBe("/tmp/project");
        bootstrapper.Requests[0].EnvironmentVariables["IFLOW_TOKEN"].ShouldBe("token");
        provider.SessionClient!.ConnectCalls.ShouldBe(1);
        provider.SessionClient.InitializeCalls.ShouldBe(1);
        provider.SessionClient.StartSessionCalls.ShouldBe(1);
        provider.SessionClient.LastWorkingDirectory.ShouldBe("/tmp/project");
        provider.SessionClient.LastModel.ShouldBe("iflow/default");
        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
    }

    [Fact]
    public async Task ExecuteAsync_uses_explicit_endpoint_and_session_reuse()
    {
        var endpoint = new Uri("ws://127.0.0.1:7331/acp");
        var bootstrapper = new FakeBootstrapper(managed: false);
        var provider = CreateProvider(
            bootstrapper: bootstrapper,
            sessionClient: new FakeAcpSessionClient(resumedSessionId: "session-resume"));
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new IFlowOptions
                           {
                               Endpoint = endpoint,
                               SessionId = "session-resume"
                           },
                           "resume prompt"))
        {
            messages.Add(message);
        }

        bootstrapper.Requests.ShouldHaveSingleItem();
        bootstrapper.Requests[0].Endpoint.ShouldBe(endpoint);
        bootstrapper.Requests[0].ExecutablePath.ShouldBeNull();
        provider.SessionClient!.LastSessionId.ShouldBe("session-resume");
        messages.First().Type.ShouldBe("session.resumed");
    }

    [Fact]
    public async Task ExecuteAsync_supports_iflow_comment_preambles_from_acp_bootstrap()
    {
        var bootstrapper = new FakeBootstrapper(managed: false);
        var provider = new ScriptedIFlowProvider(new StubExecutableResolver(), bootstrapper, new StubRuntimeEnvironmentResolver());
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(new IFlowOptions { Endpoint = new Uri("ws://127.0.0.1:7331/acp") }, "hello"))
        {
            messages.Add(message);
        }

        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
        messages[1].Content.GetProperty("text").GetString().ShouldBe("pong");
    }

    [Fact]
    public async Task ExecuteAsync_falls_back_to_prompt_result_when_notification_loop_ends_without_terminal_update()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(emitNotifications: false, promptStopReason: "fallback"));
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(new IFlowOptions(), "hello"))
        {
            messages.Add(message);
        }

        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
        messages[1].Content.GetProperty("text").GetString().ShouldBe("pong");
    }

    [Fact]
    public async Task PingAsync_reports_initialize_details_when_bootstrap_succeeds()
    {
        var bootstrapper = new FakeBootstrapper(managed: true);
        var provider = CreateProvider(bootstrapper: bootstrapper);

        var result = await provider.PingAsync();

        result.Success.ShouldBeTrue();
        result.Version.ShouldNotBeNullOrWhiteSpace();
        result.Version!.ShouldContain("iflow");
        result.Version.ShouldContain("managed ACP bootstrap");
        provider.SessionClient!.InitializeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task PingAsync_returns_failure_when_bootstrap_fails()
    {
        var provider = CreateProvider(bootstrapper: new ThrowingBootstrapper("boom"));

        var result = await provider.PingAsync();

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
        result.ErrorMessage.ShouldContain("boom");
    }

    [Fact]
    public void NormalizeNotification_maps_tool_and_terminal_updates()
    {
        var notification = new AcpNotification(
            "session/update",
            JsonSerializer.SerializeToElement(new
            {
                sessionId = "session-1",
                update = new
                {
                    sessionUpdate = "tool_call_update",
                    title = "ReadFile"
                }
            }));

        var messages = IFlowAcpMessageMapper.NormalizeNotification(notification);

        messages.ShouldHaveSingleItem();
        messages[0].Type.ShouldBe("tool.update");
    }

    [Fact]
    public void NormalizeNotification_preserves_chunk_boundaries_without_inserting_spaces()
    {
        var notification = new AcpNotification(
            "session/update",
            JsonSerializer.SerializeToElement(new
            {
                sessionId = "session-1",
                update = new
                {
                    sessionUpdate = "agent_message_chunk",
                    content = new object[]
                    {
                        new { type = "text", text = "BLUE" },
                        new { type = "text", text = "PRINT-123" }
                    }
                }
            }));

        var messages = IFlowAcpMessageMapper.NormalizeNotification(notification);

        messages.ShouldHaveSingleItem();
        messages[0].Type.ShouldBe("assistant");
        messages[0].Content.GetProperty("text").GetString().ShouldBe("BLUEPRINT-123");
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task PingAsync_can_validate_installed_iflow_cli_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        var resolver = new CliExecutableResolver();
        var executablePath = resolver.ResolveFirstAvailablePath(IFlowExecutableCandidates);
        if (executablePath is null)
        {
            throw new InvalidOperationException("IFlow CLI was not found on PATH even though the real CLI validation path was enabled.");
        }

        var executableName = Path.GetFileNameWithoutExtension(executablePath);
        executableName.ShouldNotBeNullOrWhiteSpace();
        executableName.ShouldBeOneOf("iflow", "iflow-cli");

        var provider = new IFlowProvider(resolver, new IFlowProcessBootstrapper(new HagiCode.Libs.Core.Process.CliProcessManager()), null);

        provider.IsAvailable.ShouldBeTrue();

        var result = await provider.PingAsync();

        result.ProviderName.ShouldBe("iflow");
        result.Success.ShouldBeTrue();
        result.Version.ShouldNotBeNullOrWhiteSpace();
        result.ErrorMessage.ShouldBeNullOrWhiteSpace();
    }

    private static TestIFlowProvider CreateProvider(
        CliExecutableResolver? executableResolver = null,
        IIFlowAcpBootstrapper? bootstrapper = null,
        FakeAcpSessionClient? sessionClient = null)
    {
        return new TestIFlowProvider(
            executableResolver ?? new StubExecutableResolver(),
            bootstrapper ?? new FakeBootstrapper(),
            new StubRuntimeEnvironmentResolver(),
            sessionClient ?? new FakeAcpSessionClient());
    }

    private static bool IsRealCliTestsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(RealCliTestsEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestIFlowProvider(
        CliExecutableResolver executableResolver,
        IIFlowAcpBootstrapper bootstrapper,
        IRuntimeEnvironmentResolver runtimeEnvironmentResolver,
        FakeAcpSessionClient sessionClient)
        : IFlowProvider(executableResolver, bootstrapper, runtimeEnvironmentResolver)
    {
        public FakeAcpSessionClient? SessionClient { get; private set; }

        protected override IAcpSessionClient CreateSessionClient(Uri endpoint)
        {
            SessionClient = sessionClient;
            return sessionClient;
        }
    }

    private sealed class ScriptedIFlowProvider(
        CliExecutableResolver executableResolver,
        IIFlowAcpBootstrapper bootstrapper,
        IRuntimeEnvironmentResolver runtimeEnvironmentResolver)
        : IFlowProvider(executableResolver, bootstrapper, runtimeEnvironmentResolver)
    {
        protected override IAcpSessionClient CreateSessionClient(Uri endpoint)
        {
            return new AcpSessionClient(new ScriptedAcpTransport(request => request.Method switch
            {
                "initialize" => [CreateJsonRpcResult(request.Id, JsonSerializer.Serialize("// ready\n{\"protocolVersion\":1,\"agentInfo\":{\"name\":\"iflow\",\"version\":\"0.5.0\"}}"))],
                "session/new" => [CreateJsonRpcResult(request.Id, JsonSerializer.Serialize("// ready\n{\"sessionId\":\"session-123\"}"))],
                "session/prompt" =>
                [
                    "// ready",
                    "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"session-123\",\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"pong\"}}}}",
                    CreateJsonRpcResult(request.Id, "{\"stopReason\":\"end_turn\",\"outputText\":\"pong\"}")
                ],
                _ => throw new InvalidOperationException($"Unexpected ACP method: {request.Method}")
            }));
        }

        private static string CreateJsonRpcResult(string id, string rawResultJson)
        {
            return "{\"jsonrpc\":\"2.0\",\"id\":\"" + id + "\",\"result\":" + rawResultJson + "}";
        }
    }

    private sealed class FakeAcpSessionClient(
        string? resumedSessionId = null,
        bool emitNotifications = true,
        string? promptStopReason = "end_turn") : IAcpSessionClient
    {
        public int ConnectCalls { get; private set; }

        public int InitializeCalls { get; private set; }

        public int StartSessionCalls { get; private set; }

        public string? LastWorkingDirectory { get; private set; }

        public string? LastSessionId { get; private set; }

        public string? LastModel { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectCalls++;
            return Task.CompletedTask;
        }

        public Task<JsonElement> InitializeAsync(CancellationToken cancellationToken = default)
        {
            InitializeCalls++;
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                protocolVersion = 1,
                agentInfo = new
                {
                    name = "iflow",
                    version = "0.5.0"
                }
            }));
        }

        public Task<AcpSessionHandle> StartSessionAsync(string workingDirectory, string? sessionId, string? model, CancellationToken cancellationToken = default)
        {
            StartSessionCalls++;
            LastWorkingDirectory = workingDirectory;
            LastSessionId = sessionId;
            LastModel = model;
            var isResumed = !string.IsNullOrWhiteSpace(sessionId) || !string.IsNullOrWhiteSpace(resumedSessionId);
            var resolvedSessionId = sessionId ?? resumedSessionId ?? "session-1";
            return Task.FromResult(new AcpSessionHandle(resolvedSessionId, isResumed, JsonSerializer.SerializeToElement(new { sessionId = resolvedSessionId })));
        }

        public Task<JsonElement> SendPromptAsync(string sessionId, string prompt, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["stopReason"] = promptStopReason,
                ["outputText"] = prompt.Contains("resume", StringComparison.OrdinalIgnoreCase) ? "session ready" : "pong"
            }));
        }

        public async IAsyncEnumerable<AcpNotification> ReceiveNotificationsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!emitNotifications)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                yield break;
            }

            yield return new AcpNotification(
                "session/update",
                JsonSerializer.SerializeToElement(new
                {
                    sessionId = LastSessionId ?? resumedSessionId ?? "session-1",
                    update = new
                    {
                        sessionUpdate = "agent_message_chunk",
                        content = new
                        {
                            type = "text",
                            text = "pong"
                        }
                    }
                }));

            yield return new AcpNotification(
                "session/update",
                JsonSerializer.SerializeToElement(new
                {
                    sessionId = LastSessionId ?? resumedSessionId ?? "session-1",
                    update = new
                    {
                        sessionUpdate = "prompt_completed",
                        stopReason = "end_turn"
                    }
                }));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeBootstrapper(bool managed = true) : IIFlowAcpBootstrapper
    {
        public List<IFlowBootstrapRequest> Requests { get; } = [];

        public Task<IIFlowBootstrapLease> BootstrapAsync(IFlowBootstrapRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult<IIFlowBootstrapLease>(new FakeBootstrapLease(request.Endpoint ?? new Uri("ws://127.0.0.1:7331/acp"), managed));
        }
    }

    private sealed class ThrowingBootstrapper(string message) : IIFlowAcpBootstrapper
    {
        public Task<IIFlowBootstrapLease> BootstrapAsync(IFlowBootstrapRequest request, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeBootstrapLease(Uri endpoint, bool managed) : IIFlowBootstrapLease
    {
        public Uri Endpoint { get; } = endpoint;

        public bool IsManaged { get; } = managed;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubExecutableResolver : CliExecutableResolver
    {
        public override string? ResolveExecutablePath(string? executableName, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => executableName;

        public override string? ResolveFirstAvailablePath(IEnumerable<string> executableNames, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => executableNames.FirstOrDefault();
    }

    private sealed class StubRuntimeEnvironmentResolver : IRuntimeEnvironmentResolver
    {
        public Task<IReadOnlyDictionary<string, string?>> ResolveAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?>
            {
                ["PATH"] = "/tmp/bin"
            });
        }
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
}
