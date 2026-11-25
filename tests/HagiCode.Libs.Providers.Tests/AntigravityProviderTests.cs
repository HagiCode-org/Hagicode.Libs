using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Antigravity;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Provider-logic tests for the Antigravity ACP provider. They run against a fake ACP session client, so they
/// check how HagiCode drives the protocol, not what the real <c>agy</c> binary does. Real-CLI behaviour is recorded
/// separately in <c>Hagicode.Libs/docs/antigravity-feature-matrix.md</c>.
/// </summary>
public sealed class AntigravityProviderTests
{
    private const string SignInRequiredRpcError = "ACP method 'session/new' failed with code -32000: Authentication required";

    /// <summary>The shared ACP contract row for antigravity (see AcpProviderContractTests).</summary>
    internal static AcpProviderContractRow CreateContractRow() => new AntigravityAcpContractRow();

    [Test]
    public void BuildCommandArguments_starts_with_acp_stdio_and_appends_raw_arguments()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new AntigravityOptions
        {
            ExtraArguments = ["  --log-file=/tmp/agy.log  ", "   "]
        });

        arguments.ShouldBe(["acp", "--stdio", "--log-file=/tmp/agy.log"]);
    }

    [Test]
    public void BuildCommandArguments_omits_duplicate_managed_launch_arguments_case_insensitively()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new AntigravityOptions
        {
            ExtraArguments = ["acp", " --STDIO ", "--agent", "reviewer"]
        });

        arguments.ShouldBe(["acp", "--stdio", "--agent", "reviewer"]);
    }

    [Test]
    public async Task PingAsync_starts_acp_stdio_and_reports_the_server_version()
    {
        var provider = CreateProvider();

        var result = await provider.PingAsync();

        result.Success.ShouldBeTrue();
        result.Version.ShouldBe("Antigravity ACP Server 1.5.17 via Antigravity ACP stdio");
        provider.LastStartContext!.Arguments.ShouldBe(["acp", "--stdio"]);
    }

    [Test]
    public async Task PingAsync_reports_a_missing_executable_with_install_guidance()
    {
        var provider = CreateProvider(executableResolver: new MissingExecutableResolver());

        var result = await provider.PingAsync();

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("'agy'");
    }

    [Test]
    public async Task ExecuteAsync_streams_session_start_assistant_text_and_completion_in_order()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(assistantChunks: ["pong"]));

        var messages = await CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "ping"));

        messages.Select(message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
    }

    [Test]
    public async Task ExecuteAsync_resumes_a_stored_session_with_session_load()
    {
        var sessionClient = new FakeAcpSessionClient(resumedSessionId: "stored-1", assistantChunks: ["session ready"]);
        var provider = CreateProvider(sessionClient: sessionClient);

        var messages = await CollectAsync(provider.ExecuteAsync(new AntigravityOptions { SessionId = "stored-1" }, "continue"));

        sessionClient.LastSessionId.ShouldBe("stored-1");
        messages.First().Type.ShouldBe("session.resumed");
    }

    [Test]
    public async Task ExecuteAsync_never_starts_an_interactive_sign_in_on_its_own()
    {
        var sessionClient = new FakeAcpSessionClient(advertiseAuth: true);
        var provider = CreateProvider(sessionClient: sessionClient);

        await CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "hello"));

        sessionClient.BootstrapInvocations.ShouldBeEmpty();
    }

    [Test]
    public async Task ExecuteAsync_invokes_the_requested_advertised_authentication_method()
    {
        var sessionClient = new FakeAcpSessionClient(advertiseAuth: true);
        var provider = CreateProvider(sessionClient: sessionClient);

        await CollectAsync(provider.ExecuteAsync(new AntigravityOptions { AuthenticationMethod = "oauth-personal" }, "hello"));

        var invocation = sessionClient.BootstrapInvocations.ShouldHaveSingleItem();
        invocation.Method.ShouldBe("authenticate");
        invocation.Parameters.GetProperty("methodId").GetString().ShouldBe("oauth-personal");
    }

    [Test]
    public async Task ExecuteAsync_rejects_a_requested_authentication_method_the_cli_did_not_advertise()
    {
        var sessionClient = new FakeAcpSessionClient(advertiseAuth: true);
        var provider = CreateProvider(sessionClient: sessionClient);

        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => CollectAsync(provider.ExecuteAsync(new AntigravityOptions { AuthenticationMethod = "unknown-method" }, "hello")));

        error.Message.ShouldContain("unknown-method");
        sessionClient.BootstrapInvocations.ShouldBeEmpty();
    }

    [Test]
    public async Task ExecuteAsync_explains_sign_in_when_the_cli_refuses_the_session_as_unauthenticated()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(startSessionError: SignInRequiredRpcError));

        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "hello")));

        error.Message.ShouldContain("agy acp --login");
        error.InnerException.ShouldNotBeNull();
        error.InnerException!.Message.ShouldContain("Authentication required");
    }

    [Test]
    public async Task ExecuteAsync_keeps_the_original_failure_when_the_session_cannot_start_for_another_reason()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(startSessionError: "ACP method 'session/new' failed with code -32603: process exited"));

        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "hello")));

        error.Message.ShouldContain("process exited");
        error.Message.ShouldNotContain("agy acp --login");
    }

    [Test]
    public async Task ExecuteAsync_honours_cancellation_before_the_connection_is_made()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var provider = CreateProvider();

        await Should.ThrowAsync<OperationCanceledException>(
            () => CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "hello", cancellation.Token)));
    }

    [Test]
    public async Task ExecuteAsync_ends_with_cancellation_when_the_turn_is_cancelled_while_streaming()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(emitNotifications: false));

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in provider.ExecuteAsync(new AntigravityOptions(), "hello", cancellation.Token))
            {
                await cancellation.CancelAsync();
            }
        });
    }

    [Test]
    public async Task PingAsync_passes_the_runtime_environment_to_the_process()
    {
        var provider = CreateProvider();

        await provider.PingAsync();

        provider.LastStartContext!.EnvironmentVariables!["PATH"].ShouldBe("/tmp/bin");
    }

    private static async Task<List<CliMessage>> CollectAsync(IAsyncEnumerable<CliMessage> messages)
    {
        var collected = new List<CliMessage>();
        await foreach (var message in messages)
        {
            collected.Add(message);
        }

        return collected;
    }

    private static TestAntigravityProvider CreateProvider(
        CliExecutableResolver? executableResolver = null,
        FakeAcpSessionClient? sessionClient = null)
    {
        return new TestAntigravityProvider(
            executableResolver ?? new StubExecutableResolver(),
            new CliProcessManager(),
            new StubRuntimeEnvironmentResolver(),
            sessionClient ?? new FakeAcpSessionClient());
    }

    private sealed class AntigravityAcpContractRow : AcpProviderContractRow
    {
        public override string ProviderId => "antigravity";

        public override Type ProviderType => typeof(AntigravityProvider);

        public override string VersionFragment => "Antigravity";

        public override IReadOnlyList<string> ExtraVersionFragments { get; } = [];

        public override string MultilineFallbackText => "Paragraph one.\n\n- item one\n- item two\n\n| Repo | Value |\n| --- | --- |\n| core | yes |";

        public override async Task<AcpPingObservation> PingWithMissingExecutableAsync()
        {
            var provider = CreateProvider(executableResolver: new MissingExecutableResolver());
            var result = await provider.PingAsync();
            return new AcpPingObservation(result.Success, result.ErrorMessage, result.Version, 0);
        }

        public override async Task<AcpPingObservation> PingAsync()
        {
            var provider = CreateProvider();
            var result = await provider.PingAsync();
            return new AcpPingObservation(result.Success, result.ErrorMessage, result.Version, provider.SessionClient!.InitializeCalls);
        }

        public override Task<AcpExecutionObservation> ExecuteWithoutNotificationsAsync(string? promptOutputText)
        {
            var provider = promptOutputText is null
                ? CreateProvider(sessionClient: new FakeAcpSessionClient(emitNotifications: false, promptStopReason: "fallback"))
                : CreateProvider(sessionClient: new FakeAcpSessionClient(emitNotifications: false, promptStopReason: "fallback", promptOutputText: promptOutputText));
            return AcpExecutionObservation.CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "hello"));
        }
    }

    private sealed class TestAntigravityProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver runtimeEnvironmentResolver,
        FakeAcpSessionClient sessionClient)
        : AntigravityProvider(executableResolver, processManager, runtimeEnvironmentResolver)
    {
        public ProcessStartContext? LastStartContext { get; private set; }

        public FakeAcpSessionClient? SessionClient { get; private set; }

        protected override IAcpSessionClient CreateSessionClient(ProcessStartContext startContext)
        {
            LastStartContext = startContext;
            SessionClient = sessionClient;
            return sessionClient;
        }
    }

    private sealed class FakeAcpSessionClient(
        string? resumedSessionId = null,
        bool emitNotifications = true,
        string? promptStopReason = "end_turn",
        string? promptOutputText = null,
        bool advertiseAuth = false,
        string? startSessionError = null,
        IReadOnlyList<string>? assistantChunks = null) : IAcpSessionClient
    {
        public int InitializeCalls { get; private set; }

        public List<BootstrapInvocation> BootstrapInvocations { get; } = [];

        public string? LastSessionId { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<JsonElement> InitializeAsync(CancellationToken cancellationToken = default)
        {
            InitializeCalls++;
            return Task.FromResult(JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["protocolVersion"] = 1,
                ["agentInfo"] = new
                {
                    name = "Antigravity ACP Server",
                    version = "1.5.17"
                },
                ["authMethods"] = advertiseAuth
                    ? new object[]
                    {
                        new { id = "oauth-personal", name = "Log in with Google" },
                        new { id = "gemini-api-key", name = "Gemini API key" }
                    }
                    : Array.Empty<object>()
            }));
        }

        public Task<JsonElement> InvokeBootstrapMethodAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            BootstrapInvocations.Add(new BootstrapInvocation(method, JsonSerializer.SerializeToElement(parameters ?? new { })));
            return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        }

        public Task<AcpSessionHandle> StartSessionAsync(string workingDirectory, string? sessionId, string? model, CancellationToken cancellationToken = default)
        {
            if (startSessionError is not null)
            {
                throw new InvalidOperationException(startSessionError);
            }

            LastSessionId = sessionId;
            var isResumed = !string.IsNullOrWhiteSpace(sessionId) || !string.IsNullOrWhiteSpace(resumedSessionId);
            var resolvedSessionId = sessionId ?? resumedSessionId ?? "session-1";
            return Task.FromResult(new AcpSessionHandle(resolvedSessionId, isResumed, JsonSerializer.SerializeToElement(new { sessionId = resolvedSessionId })));
        }

        public Task<JsonElement> SetModeAsync(string sessionId, string modeId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        }

        public Task<JsonElement> SendPromptAsync(string sessionId, string prompt, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["stopReason"] = promptStopReason,
                ["outputText"] = promptOutputText ?? "pong"
            }));
        }

        public async IAsyncEnumerable<AcpNotification> ReceiveNotificationsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!emitNotifications)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                yield break;
            }

            foreach (var assistantChunk in assistantChunks ?? ["pong"])
            {
                yield return new AcpNotification(
                    "session/update",
                    JsonSerializer.SerializeToElement(new
                    {
                        sessionId = LastSessionId ?? resumedSessionId ?? "session-1",
                        update = new
                        {
                            kind = "assistant",
                            text = assistantChunk
                        }
                    }));
                await Task.Yield();
            }

            yield return new AcpNotification(
                "session/update",
                JsonSerializer.SerializeToElement(new
                {
                    sessionId = LastSessionId ?? resumedSessionId ?? "session-1",
                    update = new
                    {
                        kind = "prompt_completed",
                        stopReason = "end_turn"
                    }
                }));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public string? GetDiagnosticSummary() => null;
    }

    private sealed record BootstrapInvocation(string Method, JsonElement Parameters);

    private sealed class StubExecutableResolver : CliExecutableResolver
    {
        public override string? ResolveExecutablePath(string? executableName, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => executableName;

        public override string? ResolveFirstAvailablePath(IEnumerable<string> executableNames, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => executableNames.FirstOrDefault();
    }

    private sealed class MissingExecutableResolver : CliExecutableResolver
    {
        public override string? ResolveExecutablePath(string? executableName, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => null;

        public override string? ResolveFirstAvailablePath(IEnumerable<string> executableNames, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => null;
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
}
