using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Kiro;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class KiroProviderTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";
    private static readonly string[] KiroExecutableCandidates = ["kiro", "kiro-cli"];

    [Fact]
    public void BuildCommandArguments_includes_acp_and_appends_extra_arguments_once()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new KiroOptions
        {
            ExtraArguments = ["--acp", "--verbose", "--profile", "ci"]
        });

        arguments.ShouldBe(["acp", "--verbose", "--profile", "ci"]);
    }

    [Fact]
    public void BuildCommandArguments_trims_tokens_and_ignores_whitespace_only_entries()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new KiroOptions
        {
            ExtraArguments = ["  --acp  ", "  --profile  ", "  ci smoke  ", "   ", "  --verbose  "]
        });

        arguments.ShouldBe(["acp", "--profile", "ci smoke", "--verbose"]);
    }

    [Fact]
    public async Task ExecuteAsync_uses_custom_executable_and_streams_normalized_messages()
    {
        var provider = CreateProvider();
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new KiroOptions
                           {
                               ExecutablePath = "/custom/kiro",
                               WorkingDirectory = "/tmp/project",
                               Model = "kiro-default",
                               EnvironmentVariables = new Dictionary<string, string?>
                               {
                                   ["KIRO_TOKEN"] = "token"
                               }
                           },
                           "hello"))
        {
            messages.Add(message);
        }

        provider.LastStartContext!.ExecutablePath.ShouldBe("/custom/kiro");
        provider.LastStartContext.WorkingDirectory.ShouldBe("/tmp/project");
        provider.LastStartContext.EnvironmentVariables!["KIRO_TOKEN"].ShouldBe("token");
        provider.SessionClient!.ConnectCalls.ShouldBe(1);
        provider.SessionClient.InitializeCalls.ShouldBe(1);
        provider.SessionClient.StartSessionCalls.ShouldBe(1);
        provider.SessionClient.LastWorkingDirectory.ShouldBe("/tmp/project");
        provider.SessionClient.LastModel.ShouldBe("kiro-default");
        provider.SessionClient.LastSessionId.ShouldBeNull();
        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
    }

    [Fact]
    public async Task ExecuteAsync_performs_authentication_before_creating_a_session()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(
            initializeResult: JsonSerializer.SerializeToElement(new
            {
                agentInfo = new { name = "kiro", version = "0.1.0" },
                authMethods = new object[]
                {
                    new { id = "token" }
                }
            })));

        await foreach (var _ in provider.ExecuteAsync(
                           new KiroOptions
                           {
                               AuthenticationToken = "secret",
                               AuthenticationInfo = new Dictionary<string, string?>
                               {
                                   ["scope"] = "workspace"
                               }
                           },
                           "hello"))
        {
        }

        provider.SessionClient!.BootstrapCalls.ShouldHaveSingleItem();
        provider.SessionClient.BootstrapCalls[0].Method.ShouldBe("authenticate");
        provider.SessionClient.BootstrapCalls[0].Parameters.GetProperty("methodId").GetString().ShouldBe("token");
        provider.SessionClient.BootstrapCalls[0].Parameters.GetProperty("methodInfo").GetProperty("token").GetString().ShouldBe("secret");
        provider.SessionClient.BootstrapCalls[0].Parameters.GetProperty("methodInfo").GetProperty("scope").GetString().ShouldBe("workspace");
        provider.SessionClient.StartSessionCalls.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_skips_bootstrap_for_informational_kiro_login_auth_method()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(
            initializeResult: JsonSerializer.SerializeToElement(new
            {
                agentInfo = new { name = "kiro", version = "0.1.0" },
                authMethods = new object[]
                {
                    new { id = "kiro-login" }
                }
            })));

        await foreach (var _ in provider.ExecuteAsync(new KiroOptions(), "hello"))
        {
        }

        provider.SessionClient!.BootstrapCalls.ShouldBeEmpty();
        provider.SessionClient.StartSessionCalls.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_honors_explicit_bootstrap_method_and_payload_overrides()
    {
        var provider = CreateProvider();

        await foreach (var _ in provider.ExecuteAsync(
                           new KiroOptions
                           {
                               BootstrapMethod = "kiro/bootstrap",
                               BootstrapParameters = new Dictionary<string, string?>
                               {
                                   ["scope"] = "workspace"
                               },
                               AuthenticationMethod = "token",
                               AuthenticationToken = "secret"
                           },
                           "hello"))
        {
        }

        provider.SessionClient!.BootstrapCalls.ShouldHaveSingleItem();
        provider.SessionClient.BootstrapCalls[0].Method.ShouldBe("kiro/bootstrap");
        provider.SessionClient.BootstrapCalls[0].Parameters.GetProperty("scope").GetString().ShouldBe("workspace");
        provider.SessionClient.BootstrapCalls[0].Parameters.GetProperty("methodId").GetString().ShouldBe("token");
        provider.SessionClient.BootstrapCalls[0].Parameters.GetProperty("methodInfo").GetProperty("token").GetString().ShouldBe("secret");
    }

    [Fact]
    public async Task ExecuteAsync_uses_supplied_session_identifier_for_session_reuse()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(resumedSessionId: "session-resume"));
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new KiroOptions
                           {
                               SessionId = "session-resume"
                           },
                           "resume prompt"))
        {
            messages.Add(message);
        }

        provider.SessionClient!.LastSessionId.ShouldBe("session-resume");
        messages.First().Type.ShouldBe("session.resumed");
    }

    [Fact]
    public async Task ExecuteAsync_reuses_pooled_session_without_reconnecting_when_pooling_is_enabled()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient());

        await foreach (var _ in provider.ExecuteAsync(new KiroOptions { SessionId = "session-key" }, "first"))
        {
        }

        await foreach (var _ in provider.ExecuteAsync(new KiroOptions { SessionId = "session-key" }, "second"))
        {
        }

        provider.SessionClient!.ConnectCalls.ShouldBe(1);
        provider.SessionClient.StartSessionCalls.ShouldBe(2);
        provider.SessionClient.PromptCalls.ShouldBe(2);
    }

    [Fact]
    public async Task ExecuteAsync_uses_one_shot_path_when_pooling_is_disabled()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient());

        await foreach (var _ in provider.ExecuteAsync(
                           new KiroOptions
                           {
                               SessionId = "session-key",
                               PoolSettings = new HagiCode.Libs.Core.Acp.CliPoolSettings { Enabled = false }
                           },
                           "first"))
        {
        }

        await foreach (var _ in provider.ExecuteAsync(
                           new KiroOptions
                           {
                               SessionId = "session-key",
                               PoolSettings = new HagiCode.Libs.Core.Acp.CliPoolSettings { Enabled = false }
                           },
                           "second"))
        {
        }

        provider.SessionClient!.ConnectCalls.ShouldBe(2);
    }

    [Fact]
    public async Task ExecuteAsync_resumed_session_filters_non_streamed_replay_chunks_before_emitting_current_turn()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(
            resumedSessionId: "session-resume",
            promptOutputText: "CURRENT-TURN",
            historicalReplayChunks: ["PREVIOUS-TURN"],
            assistantChunks: ["CURRENT-TURN"]));
        var assistantMessages = new List<string>();

        await foreach (var message in provider.ExecuteAsync(
                           new KiroOptions
                           {
                               SessionId = "session-resume"
                           },
                           "resume prompt"))
        {
            if (message.Type == "assistant")
            {
                assistantMessages.Add(message.Content.GetProperty("text").GetString()!);
            }
        }

        assistantMessages.ShouldBe(["CURRENT-TURN"]);
    }

    [Fact]
    public async Task ExecuteAsync_falls_back_to_prompt_result_when_notification_loop_ends_via_internal_cancellation()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(emitNotifications: false, promptStopReason: "fallback"));
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(new KiroOptions(), "hello"))
        {
            messages.Add(message);
        }

        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
        messages[1].Content.GetProperty("text").GetString().ShouldBe("pong");
    }

    [Fact]
    public async Task PingAsync_reports_initialize_details_when_bootstrap_succeeds()
    {
        var provider = CreateProvider();

        var result = await provider.PingAsync();

        result.Success.ShouldBeTrue();
        result.Version.ShouldNotBeNullOrWhiteSpace();
        result.Version.ShouldContain("kiro");
        result.Version.ShouldContain("Kiro ACP bootstrap");
        provider.SessionClient!.InitializeCalls.ShouldBe(1);
        provider.SessionClient.BootstrapCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task PingAsync_skips_bootstrap_for_informational_kiro_login_auth_method()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(
            initializeResult: JsonSerializer.SerializeToElement(new
            {
                agentInfo = new { name = "kiro", version = "0.1.0" },
                authMethods = new object[]
                {
                    new { id = "kiro-login" }
                }
            })));

        var result = await provider.PingAsync();

        result.Success.ShouldBeTrue();
        provider.SessionClient!.BootstrapCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task PingAsync_returns_failure_when_executable_is_missing()
    {
        var provider = CreateProvider(executableResolver: new MissingExecutableResolver());

        var result = await provider.PingAsync();

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
        result.ErrorMessage.ShouldContain("not found");
    }

    [Fact]
    public async Task PingAsync_returns_actionable_failure_when_authentication_is_required()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(
            initializeResult: JsonSerializer.SerializeToElement(new
            {
                agentInfo = new { name = "kiro", version = "0.1.0" },
                authRequired = true,
                authMethods = new object[]
                {
                    new { id = "token" }
                }
            })));

        var result = await provider.PingAsync();

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("authentication");
        result.ErrorMessage.ShouldContain("token");
        provider.SessionClient!.BootstrapCalls.ShouldBeEmpty();
    }

    [Fact]
    public void NormalizeNotification_maps_prompt_completed_to_terminal_message()
    {
        var notification = new AcpNotification(
            "session/update",
            JsonSerializer.SerializeToElement(new
            {
                sessionId = "session-1",
                update = new
                {
                    sessionUpdate = "prompt_completed",
                    stopReason = "end_turn"
                }
            }));

        var messages = KiroAcpMessageMapper.NormalizeNotification(notification);

        messages.ShouldHaveSingleItem();
        messages[0].Type.ShouldBe("terminal.completed");
        messages[0].Content.GetProperty("session_id").GetString().ShouldBe("session-1");
    }

    [Fact]
    public void NormalizeNotification_maps_error_update_to_terminal_failure_message()
    {
        var notification = new AcpNotification(
            "session/update",
            JsonSerializer.SerializeToElement(new
            {
                sessionId = "session-1",
                update = new
                {
                    sessionUpdate = "error",
                    message = "bootstrap failed"
                }
            }));

        var messages = KiroAcpMessageMapper.NormalizeNotification(notification);

        messages.ShouldHaveSingleItem();
        messages[0].Type.ShouldBe("terminal.failed");
        messages[0].Content.GetProperty("message").GetString().ShouldBe("bootstrap failed");
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

        var messages = KiroAcpMessageMapper.NormalizeNotification(notification);

        messages.ShouldHaveSingleItem();
        messages[0].Type.ShouldBe("assistant");
        messages[0].Content.GetProperty("text").GetString().ShouldBe("BLUEPRINT-123");
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task PingAsync_can_validate_installed_kiro_cli_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        var resolver = new CliExecutableResolver();
        var executablePath = resolver.ResolveFirstAvailablePath(KiroExecutableCandidates);
        if (executablePath is null)
        {
            throw new InvalidOperationException("Kiro CLI was not found on PATH even though the real CLI validation path was enabled.");
        }

        var executableName = Path.GetFileNameWithoutExtension(executablePath);
        executableName.ShouldNotBeNullOrWhiteSpace();
        executableName.ShouldBeOneOf("kiro", "kiro-cli");

        var provider = new KiroProvider(resolver, new CliProcessManager(), null);

        provider.IsAvailable.ShouldBeTrue();

        var result = await provider.PingAsync();

        result.ProviderName.ShouldBe("kiro");
        result.Success.ShouldBeTrue();
        result.Version.ShouldNotBeNullOrWhiteSpace();
        result.ErrorMessage.ShouldBeNullOrWhiteSpace();
    }

    private static TestKiroProvider CreateProvider(
        CliExecutableResolver? executableResolver = null,
        FakeAcpSessionClient? sessionClient = null)
    {
        return new TestKiroProvider(
            executableResolver ?? new StubExecutableResolver(),
            new CliProcessManager(),
            new StubRuntimeEnvironmentResolver(),
            sessionClient ?? new FakeAcpSessionClient());
    }

    private static bool IsRealCliTestsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(RealCliTestsEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestKiroProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver runtimeEnvironmentResolver,
        FakeAcpSessionClient sessionClient)
        : KiroProvider(executableResolver, processManager, runtimeEnvironmentResolver)
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
        JsonElement? initializeResult = null,
        JsonElement? bootstrapResult = null,
        string? resumedSessionId = null,
        bool emitNotifications = true,
        string? promptStopReason = "end_turn",
        string promptOutputText = "pong",
        IReadOnlyList<string>? assistantChunks = null,
        IReadOnlyList<string>? historicalReplayChunks = null) : IAcpSessionClient
    {
        public int ConnectCalls { get; private set; }
        public int InitializeCalls { get; private set; }
        public int StartSessionCalls { get; private set; }
        public int PromptCalls { get; private set; }
        public string? LastWorkingDirectory { get; private set; }
        public string? LastSessionId { get; private set; }
        public string? LastModel { get; private set; }
        public List<BootstrapInvocation> BootstrapCalls { get; } = [];

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectCalls++;
            return Task.CompletedTask;
        }

        public Task<JsonElement> InitializeAsync(CancellationToken cancellationToken = default)
        {
            InitializeCalls++;
            return Task.FromResult(initializeResult ?? JsonSerializer.SerializeToElement(new
            {
                agentInfo = new { name = "kiro", version = "0.1.0" }
            }));
        }

        public Task<JsonElement> InvokeBootstrapMethodAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            BootstrapCalls.Add(new BootstrapInvocation(
                method,
                JsonSerializer.SerializeToElement(parameters ?? new Dictionary<string, object?>())));
            return Task.FromResult(bootstrapResult ?? JsonSerializer.SerializeToElement(new { accepted = true }));
        }

        public Task<AcpSessionHandle> StartSessionAsync(
            string workingDirectory,
            string? sessionId,
            string? model,
            CancellationToken cancellationToken = default)
        {
            StartSessionCalls++;
            LastWorkingDirectory = workingDirectory;
            LastSessionId = sessionId;
            LastModel = model;

            var resolvedSessionId = resumedSessionId ?? sessionId ?? "session-new";
            return Task.FromResult(new AcpSessionHandle(
                resolvedSessionId,
                sessionId is not null,
                JsonSerializer.SerializeToElement(new { sessionId = resolvedSessionId })));
        }

        public Task<JsonElement> SetModeAsync(string sessionId, string modeId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(JsonSerializer.SerializeToElement(new { sessionId, modeId }));
        }

        public Task<JsonElement> SendPromptAsync(string sessionId, string prompt, CancellationToken cancellationToken = default)
        {
            PromptCalls++;
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                stopReason = promptStopReason,
                outputText = promptOutputText
            }));
        }

        public async IAsyncEnumerable<AcpNotification> ReceiveNotificationsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!emitNotifications)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                yield break;
            }

            foreach (var replayChunk in historicalReplayChunks ?? [])
            {
                yield return new AcpNotification(
                    "session/update",
                    JsonSerializer.SerializeToElement(new
                    {
                        sessionId = resumedSessionId ?? "session-new",
                        update = new
                        {
                            sessionUpdate = "agent_message_chunk",
                            content = new[]
                            {
                                new { type = "text", text = replayChunk }
                            }
                        },
                        _meta = new Dictionary<string, object?>
                        {
                            ["ai-coding/streamed"] = false,
                            ["ai-coding/request-id"] = null,
                            ["ai-coding/turn-id"] = null
                        }
                    }));
            }

            foreach (var assistantChunk in assistantChunks ?? ["pong"])
            {
                yield return new AcpNotification(
                    "session/update",
                    JsonSerializer.SerializeToElement(new
                    {
                        sessionId = resumedSessionId ?? "session-new",
                        update = new
                        {
                            sessionUpdate = "agent_message_chunk",
                            content = new[]
                            {
                                new { type = "text", text = assistantChunk }
                            }
                        },
                        _meta = new Dictionary<string, object?>
                        {
                            ["ai-coding/streamed"] = true,
                            ["ai-coding/request-id"] = "request-1",
                            ["ai-coding/turn-id"] = "turn-1"
                        }
                    }));
            }

            yield return new AcpNotification(
                "session/update",
                JsonSerializer.SerializeToElement(new
                {
                    sessionId = resumedSessionId ?? "session-new",
                    update = new
                    {
                        sessionUpdate = "prompt_completed",
                        stopReason = promptStopReason
                    }
                }));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
