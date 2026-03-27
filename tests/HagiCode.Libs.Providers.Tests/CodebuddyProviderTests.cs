using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Codebuddy;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class CodebuddyProviderTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";
    private static readonly string[] CodebuddyExecutableCandidates = ["codebuddy", "codebuddy-cli"];

    [Fact]
    public void BuildCommandArguments_includes_acp_and_appends_extra_arguments_once()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new CodebuddyOptions
        {
            ExtraArguments = ["--acp", "--verbose", "--profile", "ci"]
        });

        arguments.ShouldBe(["--acp", "--verbose", "--profile", "ci"]);
    }

    [Fact]
    public void BuildCommandArguments_trims_tokens_and_ignores_whitespace_only_entries()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new CodebuddyOptions
        {
            ExtraArguments = ["  --acp  ", "  --profile  ", "  ci smoke  ", "   ", "  --verbose  "]
        });

        arguments.ShouldBe(["--acp", "--profile", "ci smoke", "--verbose"]);
    }

    [Fact]
    public async Task ExecuteAsync_uses_custom_executable_and_streams_normalized_messages()
    {
        var provider = CreateProvider();
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new CodebuddyOptions
                           {
                               ExecutablePath = "/custom/codebuddy",
                               WorkingDirectory = "/tmp/project",
                               Model = "glm-4.7",
                               EnvironmentVariables = new Dictionary<string, string?>
                               {
                                   ["CODEBUDDY_TOKEN"] = "token"
                               }
                           },
                           "hello"))
        {
            messages.Add(message);
        }

        provider.LastStartContext!.ExecutablePath.ShouldBe("/custom/codebuddy");
        provider.LastStartContext.WorkingDirectory.ShouldBe("/tmp/project");
        provider.LastStartContext.EnvironmentVariables!["CODEBUDDY_TOKEN"].ShouldBe("token");
        provider.SessionClient!.ConnectCalls.ShouldBe(1);
        provider.SessionClient.InitializeCalls.ShouldBe(1);
        provider.SessionClient.StartSessionCalls.ShouldBe(1);
        provider.SessionClient.LastWorkingDirectory.ShouldBe("/tmp/project");
        provider.SessionClient.LastModel.ShouldBe("glm-4.7");
        provider.SessionClient.LastSessionId.ShouldBeNull();
        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
    }

    [Fact]
    public async Task ExecuteAsync_applies_mode_when_requested()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient());

        await foreach (var _ in provider.ExecuteAsync(
                           new CodebuddyOptions
                           {
                               ModeId = "plan"
                           },
                           "hello"))
        {
        }

        provider.SessionClient!.ModeUpdateCalls.ShouldBe(1);
        provider.SessionClient.LastModeId.ShouldBe("plan");
    }

    [Fact]
    public async Task ExecuteAsync_uses_supplied_session_identifier_for_session_reuse()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(resumedSessionId: "session-resume"));
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new CodebuddyOptions
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
        var firstMessages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new CodebuddyOptions { SessionId = "session-key" },
                           "first"))
        {
            firstMessages.Add(message);
        }

        var secondMessages = new List<CliMessage>();
        await foreach (var message in provider.ExecuteAsync(
                           new CodebuddyOptions { SessionId = "session-key" },
                           "second"))
        {
            secondMessages.Add(message);
        }

        provider.SessionClient!.ConnectCalls.ShouldBe(1);
        provider.SessionClient.StartSessionCalls.ShouldBe(2);
        provider.SessionClient.PromptCalls.ShouldBe(2);
        secondMessages.First().Type.ShouldBe("session.resumed");
    }

    [Fact]
    public async Task ExecuteAsync_uses_one_shot_path_when_pooling_is_disabled()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient());

        await foreach (var _ in provider.ExecuteAsync(
                           new CodebuddyOptions
                           {
                               SessionId = "session-key",
                               PoolSettings = new HagiCode.Libs.Core.Acp.CliPoolSettings
                               {
                                   Enabled = false
                               }
                           },
                           "first"))
        {
        }

        await foreach (var _ in provider.ExecuteAsync(
                           new CodebuddyOptions
                           {
                               SessionId = "session-key",
                               PoolSettings = new HagiCode.Libs.Core.Acp.CliPoolSettings
                               {
                                   Enabled = false
                               }
                           },
                           "second"))
        {
        }

        provider.SessionClient!.ConnectCalls.ShouldBe(2);
    }

    [Fact]
    public async Task ExecuteAsync_falls_back_to_prompt_result_when_notification_loop_ends_via_internal_cancellation()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(emitNotifications: false, promptStopReason: "fallback"));
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(new CodebuddyOptions(), "hello"))
        {
            messages.Add(message);
        }

        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
        messages[1].Content.GetProperty("text").GetString().ShouldBe("pong");
    }

    [Fact]
    public async Task ExecuteAsync_falls_back_to_multiline_prompt_result_without_flattening_line_breaks()
    {
        const string multilineResult = "Paragraph one.\n\n- item one\n- item two\n\n```md\ncode fence\n```";
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(
            emitNotifications: false,
            promptStopReason: "fallback",
            promptOutputText: multilineResult));
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(new CodebuddyOptions(), "hello"))
        {
            messages.Add(message);
        }

        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
        messages[1].Content.GetProperty("text").GetString().ShouldBe(multilineResult);
        messages[2].Content.GetProperty("text").GetString().ShouldBe(multilineResult);
    }

    [Fact]
    public async Task PingAsync_reports_initialize_details_when_bootstrap_succeeds()
    {
        var provider = CreateProvider();

        var result = await provider.PingAsync();

        result.Success.ShouldBeTrue();
        result.Version.ShouldNotBeNullOrWhiteSpace();
        result.Version.ShouldContain("codebuddy");
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

        var messages = CodebuddyAcpMessageMapper.NormalizeNotification(notification);

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

        var messages = CodebuddyAcpMessageMapper.NormalizeNotification(notification);

        messages.ShouldHaveSingleItem();
        messages[0].Type.ShouldBe("assistant");
        messages[0].Content.GetProperty("text").GetString().ShouldBe("BLUEPRINT-123");
    }

    [Fact]
    public void NormalizeNotification_preserves_space_only_and_newline_only_fragments()
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
                        new { type = "text", text = "foo" },
                        new { type = "text", text = " " },
                        new { type = "text", text = "bar" },
                        new { type = "text", text = "\n\n" },
                        new { type = "text", text = "baz" }
                    }
                }
            }));

        var messages = CodebuddyAcpMessageMapper.NormalizeNotification(notification);

        messages.ShouldHaveSingleItem();
        messages[0].Type.ShouldBe("assistant");
        messages[0].Content.GetProperty("text").GetString().ShouldBe("foo bar\n\nbaz");
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task PingAsync_can_validate_installed_codebuddy_cli_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        var resolver = new CliExecutableResolver();
        var executablePath = resolver.ResolveFirstAvailablePath(CodebuddyExecutableCandidates);
        if (executablePath is null)
        {
            throw new InvalidOperationException("CodeBuddy CLI was not found on PATH even though the real CLI validation path was enabled.");
        }

        var executableName = Path.GetFileNameWithoutExtension(executablePath);
        executableName.ShouldNotBeNullOrWhiteSpace();
        executableName.ShouldBeOneOf("codebuddy", "codebuddy-cli");

        var provider = new CodebuddyProvider(resolver, new CliProcessManager(), null);

        provider.IsAvailable.ShouldBeTrue();

        var result = await provider.PingAsync();

        result.ProviderName.ShouldBe("codebuddy");
        result.Success.ShouldBeTrue();
        result.Version.ShouldNotBeNullOrWhiteSpace();
        result.ErrorMessage.ShouldBeNullOrWhiteSpace();
    }

    private static TestCodebuddyProvider CreateProvider(
        CliExecutableResolver? executableResolver = null,
        FakeAcpSessionClient? sessionClient = null)
    {
        return new TestCodebuddyProvider(
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

    private sealed class TestCodebuddyProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver runtimeEnvironmentResolver,
        FakeAcpSessionClient sessionClient)
        : CodebuddyProvider(executableResolver, processManager, runtimeEnvironmentResolver)
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
        string? promptOutputText = null) : IAcpSessionClient
    {
        public int ConnectCalls { get; private set; }

        public int InitializeCalls { get; private set; }

        public int StartSessionCalls { get; private set; }

        public int PromptCalls { get; private set; }

        public string? LastWorkingDirectory { get; private set; }

        public string? LastSessionId { get; private set; }

        public string? LastModel { get; private set; }

        public int ModeUpdateCalls { get; private set; }

        public string? LastModeId { get; private set; }

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
                    name = "codebuddy",
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
            ModeUpdateCalls++;
            LastModeId = modeId;
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
            await Task.Yield();
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
