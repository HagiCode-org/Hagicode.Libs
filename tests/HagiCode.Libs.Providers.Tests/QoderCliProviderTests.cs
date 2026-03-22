using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.QoderCli;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class QoderCliProviderTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";
    private static readonly string[] QoderCliExecutableCandidates = ["qodercli"];

    [Fact]
    public void BuildCommandArguments_includes_acp_and_appends_extra_arguments_once()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new QoderCliOptions
        {
            ExtraArguments = ["--acp", "--verbose", "--profile", "ci"]
        });

        arguments.ShouldBe(["--acp", "--verbose", "--profile", "ci"]);
    }

    [Fact]
    public void BuildCommandArguments_trims_tokens_and_omits_empty_or_blocked_entries()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new QoderCliOptions
        {
            ExtraArguments = ["  --acp  ", "  --profile  ", "  ci smoke  ", "   ", "  --dangerously-skip-permissions  ", "  --verbose  "]
        });

        arguments.ShouldBe(["--acp", "--profile", "ci smoke", "--verbose"]);
    }

    [Fact]
    public async Task ExecuteAsync_always_sets_qodercli_sessions_to_yolo_mode()
    {
        var provider = CreateProvider();

        await foreach (var _ in provider.ExecuteAsync(
                           new QoderCliOptions
                           {
                               ExtraArguments = ["--profile", "ci"]
                           },
                           "hello"))
        {
        }

        provider.LastStartContext!.Arguments.ShouldBe(["--acp", "--profile", "ci"]);
        provider.SessionClient!.SetModeCalls.ShouldBe(["yolo"]);
    }

    [Fact]
    public async Task ExecuteAsync_uses_custom_executable_and_streams_normalized_messages()
    {
        var provider = CreateProvider();
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new QoderCliOptions
                           {
                               ExecutablePath = "/custom/qodercli",
                               WorkingDirectory = "/tmp/project",
                               Model = "qoder-max",
                               EnvironmentVariables = new Dictionary<string, string?>
                               {
                                   ["QODERCLI_TOKEN"] = "token"
                               }
                           },
                           "hello"))
        {
            messages.Add(message);
        }

        provider.LastStartContext!.ExecutablePath.ShouldBe("/custom/qodercli");
        provider.LastStartContext.WorkingDirectory.ShouldBe("/tmp/project");
        provider.LastStartContext.EnvironmentVariables!["QODERCLI_TOKEN"].ShouldBe("token");
        provider.SessionClient!.ConnectCalls.ShouldBe(1);
        provider.SessionClient.InitializeCalls.ShouldBe(1);
        provider.SessionClient.StartSessionCalls.ShouldBe(1);
        provider.SessionClient.SetModeCalls.ShouldBe(["yolo"]);
        provider.SessionClient.LastWorkingDirectory.ShouldBe("/tmp/project");
        provider.SessionClient.LastModel.ShouldBe("qoder-max");
        provider.SessionClient.LastSessionId.ShouldBeNull();
        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
    }

    [Fact]
    public async Task ExecuteAsync_uses_supplied_session_identifier_for_session_reuse()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(resumedSessionId: "session-resume"));
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new QoderCliOptions
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
    public async Task ExecuteAsync_resumed_session_filters_non_streamed_replay_chunks_before_emitting_current_turn()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(
            resumedSessionId: "session-resume",
            promptOutputText: "CURRENT-TURN",
            historicalReplayChunks: ["PREVIOUS-TURN"],
            assistantChunks: ["CURRENT-TURN"]));
        var assistantMessages = new List<string>();

        await foreach (var message in provider.ExecuteAsync(
                           new QoderCliOptions
                           {
                               SessionId = "session-resume"
                           },
                           "resume prompt"))
        {
            if (message.Type == "assistant")
            {
                message.Content.GetProperty("text").GetString().ShouldNotBeNull();
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

        await foreach (var message in provider.ExecuteAsync(new QoderCliOptions(), "hello"))
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
        result.Version.ShouldContain("qodercli");
        provider.SessionClient!.InitializeCalls.ShouldBe(1);
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

        var messages = QoderCliAcpMessageMapper.NormalizeNotification(notification);

        messages.ShouldHaveSingleItem();
        messages[0].Type.ShouldBe("terminal.completed");
        messages[0].Content.GetProperty("session_id").GetString().ShouldBe("session-1");
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

        var messages = QoderCliAcpMessageMapper.NormalizeNotification(notification);

        messages.ShouldHaveSingleItem();
        messages[0].Type.ShouldBe("assistant");
        messages[0].Content.GetProperty("text").GetString().ShouldBe("BLUEPRINT-123");
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task PingAsync_can_validate_installed_qodercli_cli_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        var resolver = new CliExecutableResolver();
        var executablePath = resolver.ResolveFirstAvailablePath(QoderCliExecutableCandidates);
        if (executablePath is null)
        {
            throw new InvalidOperationException("QoderCLI CLI was not found on PATH even though the real CLI validation path was enabled.");
        }

        var executableName = Path.GetFileNameWithoutExtension(executablePath);
        executableName.ShouldNotBeNullOrWhiteSpace();
        executableName.ShouldBe("qodercli");

        var provider = new QoderCliProvider(resolver, new CliProcessManager(), null);

        provider.IsAvailable.ShouldBeTrue();

        var result = await provider.PingAsync();

        result.ProviderName.ShouldBe("qodercli");
        result.Success.ShouldBeTrue();
        result.Version.ShouldNotBeNullOrWhiteSpace();
        result.ErrorMessage.ShouldBeNullOrWhiteSpace();
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task ExecuteAsync_real_cli_resume_should_not_replay_prior_assistant_output_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        var resolver = new CliExecutableResolver();
        var executablePath = resolver.ResolveFirstAvailablePath(QoderCliExecutableCandidates);
        if (executablePath is null)
        {
            throw new InvalidOperationException("QoderCLI CLI was not found on PATH even though the real CLI validation path was enabled.");
        }

        var provider = new QoderCliProvider(resolver, new CliProcessManager(), null);
        var firstToken = $"TRACE-FIRST-{Guid.NewGuid():N}";
        var secondToken = $"TRACE-SECOND-{Guid.NewGuid():N}";

        var firstResult = await ReadExecutionResultAsync(
            provider,
            new QoderCliOptions
            {
                ExecutablePath = executablePath
            },
            $"Reply with exactly {firstToken} and nothing else.");

        firstResult.SessionId.ShouldNotBeNullOrWhiteSpace();
        firstResult.AssistantText.ShouldContain(firstToken);

        var secondResult = await ReadExecutionResultAsync(
            provider,
            new QoderCliOptions
            {
                ExecutablePath = executablePath,
                SessionId = firstResult.SessionId
            },
            $"Reply with exactly {secondToken} and nothing else.");

        secondResult.AssistantText.ShouldContain(secondToken);
        secondResult.AssistantText.ShouldNotContain(firstToken);
    }

    private static TestQoderCliProvider CreateProvider(
        CliExecutableResolver? executableResolver = null,
        FakeAcpSessionClient? sessionClient = null)
    {
        return new TestQoderCliProvider(
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

    private static async Task<(string AssistantText, string? SessionId)> ReadExecutionResultAsync(
        ICliProvider<QoderCliOptions> provider,
        QoderCliOptions options,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var assistantText = new List<string>();
        string? sessionId = null;

        await foreach (var message in provider.ExecuteAsync(options, prompt, cancellationToken))
        {
            if (message.Content.ValueKind == JsonValueKind.Object &&
                message.Content.TryGetProperty("session_id", out var sessionIdElement) &&
                sessionIdElement.ValueKind == JsonValueKind.String)
            {
                sessionId ??= sessionIdElement.GetString();
            }

            if (message.Type == "assistant" &&
                message.Content.ValueKind == JsonValueKind.Object &&
                message.Content.TryGetProperty("text", out var textElement) &&
                textElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(textElement.GetString()))
            {
                assistantText.Add(textElement.GetString()!);
            }
        }

        return (string.Concat(assistantText), sessionId);
    }

    private sealed class TestQoderCliProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver runtimeEnvironmentResolver,
        FakeAcpSessionClient sessionClient)
        : QoderCliProvider(executableResolver, processManager, runtimeEnvironmentResolver)
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
        IReadOnlyList<string>? historicalReplayChunks = null,
        IReadOnlyList<string>? assistantChunks = null) : IAcpSessionClient
    {
        public int ConnectCalls { get; private set; }

        public int InitializeCalls { get; private set; }

        public int StartSessionCalls { get; private set; }

        public List<string> SetModeCalls { get; } = [];

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
                    name = "qodercli",
                    version = "1.0.0"
                }
            }));
        }

        public Task<JsonElement> InvokeBootstrapMethodAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
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
            SetModeCalls.Add(modeId);
            return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        }

        public Task<JsonElement> SendPromptAsync(string sessionId, string prompt, CancellationToken cancellationToken = default)
        {
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
                        _meta = new Dictionary<string, object?>
                        {
                            ["ai-coding/message-end"] = false,
                            ["ai-coding/request-id"] = "request-1",
                            ["ai-coding/streamed"] = true,
                            ["ai-coding/turn-id"] = "turn-1"
                        },
                        update = new
                        {
                            sessionUpdate = "agent_message_chunk",
                            content = new
                            {
                                type = "text",
                                text = assistantChunk
                            }
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
                        sessionUpdate = "prompt_completed",
                        stopReason = "end_turn"
                    }
                }));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

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
