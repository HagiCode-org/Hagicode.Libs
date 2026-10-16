using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Gemini;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class GeminiProviderTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";
    private static readonly string[] GeminiExecutableCandidates = ["gemini", "gemini-cli"];

    [Test]
    public void BuildCommandArguments_trims_tokens_and_omits_duplicate_acp_entries()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new GeminiOptions
        {
            ExtraArguments = ["  acp  ", "  --acp  ", "  --profile  ", "  smoke  ", "   "]
        });

        arguments.ShouldBe(["--acp", "--profile", "smoke"]);
    }

    [Test]
    public async Task ExecuteAsync_uses_custom_executable_authenticates_and_streams_normalized_messages()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(advertiseAuth: true));
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new GeminiOptions
                           {
                               ExecutablePath = "/custom/gemini",
                               WorkingDirectory = "/tmp/project",
                               Model = "gemini-k2.5",
                               AuthenticationMethod = "token",
                               AuthenticationToken = "secret-token",
                               AuthenticationInfo = new Dictionary<string, string?>
                               {
                                   ["region"] = "cn"
                               },
                               EnvironmentVariables = new Dictionary<string, string?>
                               {
                                   ["GEMINI_PROFILE"] = "ci"
                               },
                               ExtraArguments = ["--profile", "ci"]
                           },
                           "hello"))
        {
            messages.Add(message);
        }

        provider.LastStartContext!.ExecutablePath.ShouldBe("/custom/gemini");
        provider.LastStartContext.WorkingDirectory.ShouldBe("/tmp/project");
        provider.LastStartContext.Arguments.ShouldBe(["--acp", "--profile", "ci"]);
        provider.LastStartContext.EnvironmentVariables!["GEMINI_PROFILE"].ShouldBe("ci");
        provider.SessionClient!.ConnectCalls.ShouldBe(1);
        provider.SessionClient.InitializeCalls.ShouldBe(1);
        provider.SessionClient.BootstrapInvocations.Count.ShouldBe(1);
        provider.SessionClient.BootstrapInvocations[0].Method.ShouldBe("authenticate");
        provider.SessionClient.BootstrapInvocations[0].Parameters.GetProperty("methodId").GetString().ShouldBe("token");
        provider.SessionClient.BootstrapInvocations[0].Parameters.GetProperty("methodInfo").GetProperty("token").GetString().ShouldBe("secret-token");
        provider.SessionClient.BootstrapInvocations[0].Parameters.GetProperty("methodInfo").GetProperty("region").GetString().ShouldBe("cn");
        provider.SessionClient.StartSessionCalls.ShouldBe(1);
        provider.SessionClient.LastWorkingDirectory.ShouldBe("/tmp/project");
        provider.SessionClient.LastModel.ShouldBe("gemini-k2.5");
        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
    }

    [Test]
    public async Task ExecuteAsync_uses_supplied_session_identifier_for_session_reuse()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(resumedSessionId: "session-resume"));
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new GeminiOptions
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

    [Test]
    public async Task ExecuteAsync_creates_fresh_session_client_for_each_execution()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient());

        await foreach (var _ in provider.ExecuteAsync(new GeminiOptions { SessionId = "session-key" }, "first"))
        {
        }

        await foreach (var _ in provider.ExecuteAsync(new GeminiOptions { SessionId = "session-key" }, "second"))
        {
        }

        provider.SessionClient!.ConnectCalls.ShouldBe(2);
        provider.SessionClient.StartSessionCalls.ShouldBe(2);
        provider.SessionClient.PromptCalls.ShouldBe(2);
    }

    [Test]
    public async Task ExecuteAsync_recreates_session_when_runtime_inputs_change_but_session_id_matches()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient());

        await foreach (var _ in provider.ExecuteAsync(
                           new GeminiOptions
                           {
                               SessionId = "session-key",
                               WorkingDirectory = "/tmp/project-a",
                               Model = "gemini-2.5-pro"
                           },
                           "first"))
        {
        }

        await foreach (var _ in provider.ExecuteAsync(
                           new GeminiOptions
                           {
                               SessionId = "session-key",
                               WorkingDirectory = "/tmp/project-b",
                               Model = "gemini-2.5-flash"
                           },
                           "second"))
        {
        }

        provider.SessionClient!.ConnectCalls.ShouldBe(2);
        provider.SessionClient.StartSessionCalls.ShouldBe(2);
        provider.SessionClient.LastSessionId.ShouldBe("session-key");
        provider.SessionClient.LastWorkingDirectory.ShouldBe("/tmp/project-b");
        provider.SessionClient.LastModel.ShouldBe("gemini-2.5-flash");
    }

    [Test]
    public async Task ExecuteAsync_resumed_session_filters_non_streamed_replay_chunks_before_emitting_current_turn()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(
            resumedSessionId: "session-resume",
            promptOutputText: "CURRENT-TURN",
            historicalReplayChunks: ["PREVIOUS-TURN"],
            assistantChunks: ["CURRENT-TURN"]));
        var assistantMessages = new List<string>();

        await foreach (var message in provider.ExecuteAsync(
                           new GeminiOptions
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

    [Test]
    public async Task ExecuteAsync_authentication_failure_surfaces_bootstrap_context()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(advertiseAuth: true, authenticationAccepted: false));

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in provider.ExecuteAsync(new GeminiOptions(), "hello"))
            {
            }
        });

        exception.Message.ShouldContain("during authentication");
        exception.Message.ShouldContain("rejected");
    }

    [Test]
    public async Task PingAsync_attempts_authentication_when_initialize_requires_it()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(advertiseAuth: true));

        var result = await provider.PingAsync();

        result.Success.ShouldBeTrue();
        result.ErrorMessage.ShouldBeNullOrWhiteSpace();
        provider.SessionClient!.BootstrapInvocations.Count.ShouldBe(1);
        provider.SessionClient.BootstrapInvocations[0].Method.ShouldBe("authenticate");
        provider.SessionClient.BootstrapInvocations[0].Parameters.GetProperty("methodId").GetString().ShouldBe("token");
    }

    [Test]
    public async Task PingAsync_returns_actionable_failure_when_authentication_bootstrap_is_rejected()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(advertiseAuth: true, authenticationAccepted: false));

        var result = await provider.PingAsync();

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
        result.ErrorMessage.ShouldContain("during authentication");
        result.ErrorMessage.ShouldContain("rejected");
    }

    [Test]
    public void NormalizeNotification_maps_assistant_kind_to_shared_assistant_message()
    {
        var notification = new AcpNotification(
            "session/update",
            JsonSerializer.SerializeToElement(new
            {
                sessionId = "session-1",
                update = new
                {
                    kind = "assistant",
                    text = "pong"
                }
            }));

        var messages = GeminiAcpMessageMapper.NormalizeNotification(notification);

        messages.ShouldHaveSingleItem();
        messages[0].Type.ShouldBe("assistant");
        messages[0].Content.GetProperty("text").GetString().ShouldBe("pong");
    }

    [Test]
    public void NormalizeNotification_preserves_multiline_content_arrays_and_newline_only_fragments()
    {
        var notification = new AcpNotification(
            "session/update",
            JsonSerializer.SerializeToElement(new
            {
                sessionId = "session-1",
                update = new
                {
                    kind = "assistant",
                    content = new object[]
                    {
                        new { type = "text", text = "Paragraph one." },
                        new { type = "text", text = "\n\n" },
                        new { type = "text", text = "- item one\n- item two" }
                    }
                }
            }));

        var messages = GeminiAcpMessageMapper.NormalizeNotification(notification);

        messages.ShouldHaveSingleItem();
        messages[0].Type.ShouldBe("assistant");
        messages[0].Content.GetProperty("text").GetString().ShouldBe("Paragraph one.\n\n- item one\n- item two");
    }

    [Test]
    public void NormalizeNotification_preserves_space_only_fragments_inside_content_arrays()
    {
        var notification = new AcpNotification(
            "session/update",
            JsonSerializer.SerializeToElement(new
            {
                sessionId = "session-1",
                update = new
                {
                    kind = "assistant",
                    content = new object[]
                    {
                        new { type = "text", text = "foo" },
                        new { type = "text", text = " " },
                        new { type = "text", text = "bar" }
                    }
                }
            }));

        var messages = GeminiAcpMessageMapper.NormalizeNotification(notification);

        messages.ShouldHaveSingleItem();
        messages[0].Type.ShouldBe("assistant");
        messages[0].Content.GetProperty("text").GetString().ShouldBe("foo bar");
    }

    [Test]
    public void NormalizeNotification_maps_error_updates_to_terminal_failure()
    {
        var notification = new AcpNotification(
            "session/update",
            JsonSerializer.SerializeToElement(new
            {
                sessionId = "session-1",
                update = new
                {
                    kind = "error",
                    message = "auth denied"
                }
            }));

        var messages = GeminiAcpMessageMapper.NormalizeNotification(notification);

        messages.ShouldHaveSingleItem();
        messages[0].Type.ShouldBe("terminal.failed");
        messages[0].Content.GetProperty("message").GetString().ShouldBe("auth denied");
    }

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task PingAsync_can_validate_installed_gemini_cli_when_opted_in()
    {
        ExternalGate.Require(RealCliTestsEnvironmentVariable);

        var resolver = new CliExecutableResolver();
        var executablePath = resolver.ResolveFirstAvailablePath(GeminiExecutableCandidates);
        if (executablePath is null)
        {
            throw new InvalidOperationException("Gemini CLI was not found on PATH even though the real CLI validation path was enabled.");
        }

        var provider = new GeminiProvider(resolver, new CliProcessManager(), null);

        provider.IsAvailable.ShouldBeTrue();

        var result = await provider.PingAsync();

        result.ProviderName.ShouldBe("gemini");
        result.Success.ShouldBeTrue();
        result.Version.ShouldNotBeNullOrWhiteSpace();
        result.ErrorMessage.ShouldBeNullOrWhiteSpace();
    }

    /// <summary>The shared ACP contract row for gemini (see AcpProviderContractTests).</summary>
    internal static AcpProviderContractRow CreateContractRow() => new GeminiAcpContractRow();

    private sealed class GeminiAcpContractRow : AcpProviderContractRow
    {
        public override string ProviderId => "gemini";

        public override Type ProviderType => typeof(GeminiProvider);

        public override string VersionFragment => "gemini";

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
            return AcpExecutionObservation.CollectAsync(provider.ExecuteAsync(new GeminiOptions(), "hello"));
        }
    }

    private static TestGeminiProvider CreateProvider(
        CliExecutableResolver? executableResolver = null,
        FakeAcpSessionClient? sessionClient = null)
    {
        return new TestGeminiProvider(
            executableResolver ?? new StubExecutableResolver(),
            new CliProcessManager(),
            new StubRuntimeEnvironmentResolver(),
            sessionClient ?? new FakeAcpSessionClient());
    }

    private sealed class TestGeminiProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver runtimeEnvironmentResolver,
        FakeAcpSessionClient sessionClient)
        : GeminiProvider(executableResolver, processManager, runtimeEnvironmentResolver)
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
        bool isAuthenticated = false,
        bool authenticationAccepted = true,
        bool authenticationThrows = false,
        IReadOnlyList<string>? historicalReplayChunks = null,
        IReadOnlyList<string>? assistantChunks = null) : IAcpSessionClient
    {
        public int ConnectCalls { get; private set; }

        public int InitializeCalls { get; private set; }

        public int StartSessionCalls { get; private set; }

        public int PromptCalls { get; private set; }

        public List<BootstrapInvocation> BootstrapInvocations { get; } = [];

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
            return Task.FromResult(JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["protocolVersion"] = 1,
                ["agentInfo"] = new
                {
                    name = "gemini",
                    version = "1.0.0"
                },
                ["isAuthenticated"] = isAuthenticated,
                ["authMethods"] = advertiseAuth
                    ? new object[]
                    {
                        new
                        {
                            id = "token",
                            name = "Token"
                        }
                    }
                    : Array.Empty<object>()
            }));
        }

        public Task<JsonElement> InvokeBootstrapMethodAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            BootstrapInvocations.Add(new BootstrapInvocation(method, JsonSerializer.SerializeToElement(parameters ?? new { })));
            if (authenticationThrows)
            {
                throw new InvalidOperationException("network timeout");
            }

            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                accepted = authenticationAccepted
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

        public Task<JsonElement> SetModeAsync(string sessionId, string modeId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        }

        public Task<JsonElement> SendPromptAsync(string sessionId, string prompt, CancellationToken cancellationToken = default)
        {
            PromptCalls++;
            return Task.FromResult(JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["stopReason"] = promptStopReason,
                ["outputText"] = promptOutputText ?? (prompt.Contains("resume", StringComparison.OrdinalIgnoreCase) ? "session ready" : "pong")
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
                        sessionId = LastSessionId ?? resumedSessionId ?? "session-1",
                        _meta = new Dictionary<string, object?>
                        {
                            ["ai-coding/message-end"] = true,
                            ["ai-coding/request-id"] = string.Empty,
                            ["ai-coding/streamed"] = false,
                            ["ai-coding/turn-id"] = string.Empty
                        },
                        update = new
                        {
                            sessionUpdate = "agent_message_chunk",
                            content = new
                            {
                                type = "text",
                                text = replayChunk
                            }
                        }
                    }));
                await Task.Yield();
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