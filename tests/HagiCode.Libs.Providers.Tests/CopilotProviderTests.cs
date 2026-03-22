using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Copilot;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class CopilotProviderTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";
    private const string RealCopilotSessionTestsEnvironmentVariable = "HAGICODE_REAL_CLI_COPILOT_SESSION_TESTS";
    private static readonly string[] CopilotExecutableCandidates = ["copilot"];

    [Fact]
    public void BuildSdkRequest_includes_typed_runtime_fields_and_filtered_args()
    {
        var provider = CreateProvider();
        var request = provider.BuildSdkRequest(
            new CopilotOptions
            {
                ExecutablePath = "/custom/copilot",
                Model = "claude-sonnet-4.5",
                WorkingDirectory = "/tmp/project",
                SessionId = "copilot-session-123",
                Timeout = TimeSpan.FromMinutes(3),
                StartupTimeout = TimeSpan.FromSeconds(15),
                AuthSource = CopilotAuthSource.GitHubToken,
                GitHubToken = "ghu_test",
                Permissions = new CopilotPermissionOptions
                {
                    AllowAllTools = true,
                    AllowedPaths = ["/tmp/project"],
                    AllowedTools = ["grep"],
                    DeniedTools = ["rm"],
                    DeniedUrls = ["example.com"]
                },
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["CUSTOM_FLAG"] = "1"
                },
                AdditionalArgs = ["--config-dir", "/tmp/copilot", "--headless"]
            },
            "hello",
            "/custom/copilot",
            new Dictionary<string, string?>
            {
                ["PATH"] = "/tmp/bin"
            });

        request.CliPath.ShouldBe("/custom/copilot");
        request.Model.ShouldBe("claude-sonnet-4.5");
        request.WorkingDirectory.ShouldBe("/tmp/project");
        request.SessionId.ShouldBe("copilot-session-123");
        request.Timeout.ShouldBe(TimeSpan.FromMinutes(3));
        request.StartupTimeout.ShouldBe(TimeSpan.FromSeconds(15));
        request.GitHubToken.ShouldBe("ghu_test");
        request.UseLoggedInUser.ShouldBeFalse();
        request.CliArgs.ShouldBe(
        [
            "--allow-all-tools",
            "--no-ask-user",
            "--add-dir",
            "/tmp/project",
            "--available-tools",
            "grep",
            "--deny-tool",
            "rm",
            "--deny-url",
            "example.com",
            "--config-dir",
            "/tmp/copilot"
        ]);
        request.EnvironmentVariables["CUSTOM_FLAG"].ShouldBe("1");
        request.EnvironmentVariables["COPILOT_INTERNAL_ORIGINATOR_OVERRIDE"].ShouldBe("hagicode_libs_csharp");
    }

    [Fact]
    public void BuildCliArgs_filters_unsupported_flags_and_records_diagnostics()
    {
        var result = CopilotCliCompatibility.BuildCliArgs(new CopilotOptions
        {
            NoAskUser = false,
            AdditionalArgs = ["--experimental", "--config-dir", "/tmp/copilot", "--headless", "--prompt", "hello", "stray-value"]
        });

        result.CliArgs.ShouldBe(
        [
            "--experimental",
            "--config-dir",
            "/tmp/copilot"
        ]);
        result.Diagnostics.Count.ShouldBe(4);
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.Contains("--headless", StringComparison.Ordinal));
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.Contains("--prompt", StringComparison.Ordinal));
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.Contains("stray-value", StringComparison.Ordinal));
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.Contains("hello", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_normalizes_sdk_stream_events_into_cli_messages()
    {
        var provider = CreateProvider(gateway: new StubCopilotSdkGateway(
        [
            new CopilotSdkStreamEvent(CopilotSdkStreamEventType.TextDelta, Content: "pong"),
            new CopilotSdkStreamEvent(CopilotSdkStreamEventType.ReasoningDelta, Content: "thinking"),
            new CopilotSdkStreamEvent(CopilotSdkStreamEventType.ToolExecutionStart, ToolName: "grep", ToolCallId: "tool-1"),
            new CopilotSdkStreamEvent(CopilotSdkStreamEventType.ToolExecutionEnd, Content: "completed successfully", ToolName: "grep", ToolCallId: "tool-1"),
            new CopilotSdkStreamEvent(CopilotSdkStreamEventType.Completed)
        ]));
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new CopilotOptions
                           {
                               ExecutablePath = "/custom/copilot",
                               AdditionalArgs = ["--headless"]
                           },
                           "hello"))
        {
            messages.Add(message);
        }

        messages.Select(static message => message.Type).ShouldBe([
            "diagnostic",
            "session.started",
            "assistant",
            "reasoning",
            "tool.started",
            "tool.completed",
            "result"
        ]);
        messages[0].Content.GetProperty("message").GetString()!.ShouldContain("--headless");
        messages[1].Content.GetProperty("session_id").GetString().ShouldBe("copilot-session-1");
        messages[2].Content.GetProperty("text").GetString().ShouldBe("pong");
        messages[2].Content.GetProperty("session_id").GetString().ShouldBe("copilot-session-1");
        messages[3].Content.GetProperty("text").GetString().ShouldBe("thinking");
        messages[4].Content.GetProperty("tool_name").GetString().ShouldBe("grep");
        messages[4].Content.GetProperty("session_id").GetString().ShouldBe("copilot-session-1");
        messages[5].Content.GetProperty("failed").GetBoolean().ShouldBeFalse();
        messages[6].Content.GetProperty("status").GetString().ShouldBe("completed");
        messages[6].Content.GetProperty("session_id").GetString().ShouldBe("copilot-session-1");
    }

    [Fact]
    public async Task ExecuteAsync_uses_explicit_session_id_for_provider_native_resume_contract()
    {
        var gateway = new StubCopilotSdkGateway(
        [
            new CopilotSdkStreamEvent(CopilotSdkStreamEventType.TextDelta, Content: "remembered"),
            new CopilotSdkStreamEvent(CopilotSdkStreamEventType.Completed)
        ],
        lifecycleType: CopilotSdkStreamEventType.SessionResumed,
        sessionId: "resume-session");
        var provider = CreateProvider(gateway: gateway);
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new CopilotOptions
                           {
                               SessionId = "resume-session"
                           },
                           "what did I ask earlier"))
        {
            messages.Add(message);
        }

        gateway.CreateRequests.ShouldHaveSingleItem();
        gateway.CreateRequests[0].SessionId.ShouldBe("resume-session");
        messages.First().Type.ShouldBe("session.resumed");
        messages.First().Content.GetProperty("session_id").GetString().ShouldBe("resume-session");
        messages.First().Content.GetProperty("requested_session_id").GetString().ShouldBe("resume-session");
    }

    [Fact]
    public async Task ExecuteAsync_reuses_warm_copilot_runtime_for_same_explicit_session_id()
    {
        var gateway = new StubCopilotSdkGateway(
        [
            new CopilotSdkStreamEvent(CopilotSdkStreamEventType.TextDelta, Content: "pong"),
            new CopilotSdkStreamEvent(CopilotSdkStreamEventType.Completed)
        ],
        sessionId: "session-key");
        var provider = CreateProvider(gateway: gateway);
        var firstMessages = new List<CliMessage>();
        var secondMessages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(new CopilotOptions { SessionId = "session-key" }, "first"))
        {
            firstMessages.Add(message);
        }

        await foreach (var message in provider.ExecuteAsync(new CopilotOptions { SessionId = "session-key" }, "second"))
        {
            secondMessages.Add(message);
        }

        gateway.CreatedRuntimeCount.ShouldBe(1);
        gateway.SendPromptCallCount.ShouldBe(2);
        firstMessages.First().Type.ShouldBe("session.started");
        secondMessages.First().Type.ShouldBe("session.reused");
        secondMessages.First().Content.GetProperty("session_id").GetString().ShouldBe("session-key");
        secondMessages.First().Content.GetProperty("requested_session_id").GetString().ShouldBe("session-key");
    }

    [Fact]
    public async Task ExecuteAsync_reuses_warm_copilot_runtime_for_same_working_directory()
    {
        var gateway = new StubCopilotSdkGateway(
        [
            new CopilotSdkStreamEvent(CopilotSdkStreamEventType.TextDelta, Content: "pong"),
            new CopilotSdkStreamEvent(CopilotSdkStreamEventType.Completed)
        ]);
        var provider = CreateProvider(gateway: gateway);
        var secondMessages = new List<CliMessage>();

        await foreach (var _ in provider.ExecuteAsync(new CopilotOptions { WorkingDirectory = "/tmp/project" }, "first"))
        {
        }

        await foreach (var message in provider.ExecuteAsync(new CopilotOptions { WorkingDirectory = "/tmp/project" }, "second"))
        {
            secondMessages.Add(message);
        }

        gateway.CreatedRuntimeCount.ShouldBe(1);
        gateway.SendPromptCallCount.ShouldBe(2);
        secondMessages.First().Type.ShouldBe("session.reused");
        secondMessages.First().Content.GetProperty("session_id").GetString().ShouldBe("copilot-session-1");
    }

    [Fact]
    public async Task ExecuteAsync_uses_one_shot_copilot_path_when_pooling_is_disabled()
    {
        var gateway = new StubCopilotSdkGateway(
        [
            new CopilotSdkStreamEvent(CopilotSdkStreamEventType.TextDelta, Content: "pong"),
            new CopilotSdkStreamEvent(CopilotSdkStreamEventType.Completed)
        ]);
        var provider = CreateProvider(gateway: gateway);

        await foreach (var _ in provider.ExecuteAsync(
                           new CopilotOptions
                           {
                               WorkingDirectory = "/tmp/project",
                               PoolSettings = new HagiCode.Libs.Core.Acp.CliPoolSettings { Enabled = false }
                           },
                           "first"))
        {
        }

        await foreach (var _ in provider.ExecuteAsync(
                           new CopilotOptions
                           {
                               WorkingDirectory = "/tmp/project",
                               PoolSettings = new HagiCode.Libs.Core.Acp.CliPoolSettings { Enabled = false }
                           },
                           "second"))
        {
        }

        gateway.CreatedRuntimeCount.ShouldBe(2);
    }

    [Fact]
    public async Task ExecuteAsync_emits_error_terminal_message_when_gateway_fails()
    {
        var provider = CreateProvider(gateway: new StubCopilotSdkGateway(
        [
            new CopilotSdkStreamEvent(CopilotSdkStreamEventType.Error, ErrorMessage: "startup failed")
        ],
        lifecycleType: null));
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(new CopilotOptions { ExecutablePath = "/custom/copilot" }, "hello"))
        {
            messages.Add(message);
        }

        messages.Select(static message => message.Type).ShouldBe(["error"]);
        messages[0].Content.GetProperty("message").GetString().ShouldBe("startup failed");
    }

    [Fact]
    public async Task PingAsync_reports_version_when_process_succeeds()
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResults = new Queue<ProcessResult>(
            [
                new ProcessResult(0, "copilot 1.0.10", string.Empty)
            ])
        };
        var provider = CreateProvider(processManager: processManager);

        var result = await provider.PingAsync();

        result.ProviderName.ShouldBe("copilot");
        result.Success.ShouldBeTrue();
        result.Version.ShouldBe("copilot 1.0.10");
    }

    [Fact]
    public async Task PingAsync_returns_failure_when_executable_is_missing()
    {
        var provider = CreateProvider(executableResolver: new MissingExecutableResolver());

        var result = await provider.PingAsync();

        result.Success.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("not found", Case.Insensitive);
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task PingAsync_can_validate_installed_copilot_cli_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        var resolver = new CliExecutableResolver();
        var executablePath = resolver.ResolveFirstAvailablePath(CopilotExecutableCandidates);
        if (executablePath is null)
        {
            throw new InvalidOperationException("Copilot CLI was not found on PATH even though the real CLI validation path was enabled.");
        }

        Path.GetFileNameWithoutExtension(executablePath).ShouldBe("copilot");

        var provider = new CopilotProvider(resolver, new CliProcessManager(), runtimeEnvironmentResolver: null);

        provider.IsAvailable.ShouldBeTrue();

        var result = await provider.PingAsync();

        result.ProviderName.ShouldBe("copilot");
        result.Success.ShouldBeTrue();
        result.Version.ShouldNotBeNullOrWhiteSpace();
        result.ErrorMessage.ShouldBeNullOrWhiteSpace();
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task ExecuteAsync_real_cli_can_resume_provider_native_session_when_opted_in()
    {
        if (!IsRealCliTestsEnabled() || !IsRealCopilotSessionTestsEnabled())
        {
            return;
        }

        var resolver = new CliExecutableResolver();
        var executablePath = resolver.ResolveFirstAvailablePath(CopilotExecutableCandidates);
        if (executablePath is null)
        {
            throw new InvalidOperationException("Copilot CLI was not found on PATH even though the real CLI validation path was enabled.");
        }

        var provider = new CopilotProvider(resolver, new CliProcessManager(), runtimeEnvironmentResolver: null);
        var rememberedToken = $"TRACE-{Guid.NewGuid():N}";
        var workingDirectory = Directory.GetCurrentDirectory();

        var firstResult = await ReadExecutionResultAsync(
            provider,
            new CopilotOptions
            {
                ExecutablePath = executablePath,
                WorkingDirectory = workingDirectory
            },
            $"Remember this exact token for the next turn: {rememberedToken}. Reply with exactly ACK.");

        firstResult.SessionId.ShouldNotBeNullOrWhiteSpace();
        firstResult.AssistantText.ShouldContain("ACK", Case.Insensitive);

        var secondResult = await ReadExecutionResultAsync(
            provider,
            new CopilotOptions
            {
                ExecutablePath = executablePath,
                WorkingDirectory = workingDirectory,
                SessionId = firstResult.SessionId
            },
            "What exact token did I ask you to remember in the previous turn? Reply with just the token.");

        secondResult.AssistantText.ShouldContain(rememberedToken);
    }

    private static TestCopilotProvider CreateProvider(
        CliExecutableResolver? executableResolver = null,
        CliProcessManager? processManager = null,
        ICopilotSdkGateway? gateway = null)
    {
        return new TestCopilotProvider(
            executableResolver ?? new StubExecutableResolver(),
            processManager ?? new StubCliProcessManager(),
            gateway ?? new StubCopilotSdkGateway([]),
            new StubRuntimeEnvironmentResolver());
    }

    private static bool IsRealCliTestsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(RealCliTestsEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRealCopilotSessionTestsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(RealCopilotSessionTestsEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(string AssistantText, string? SessionId)> ReadExecutionResultAsync(
        ICliProvider<CopilotOptions> provider,
        CopilotOptions options,
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

    private sealed class TestCopilotProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        ICopilotSdkGateway gateway,
        IRuntimeEnvironmentResolver runtimeEnvironmentResolver)
        : CopilotProvider(executableResolver, processManager, gateway, runtimeEnvironmentResolver)
    {
    }

    private sealed class StubCopilotSdkGateway(
        IReadOnlyList<CopilotSdkStreamEvent> promptEvents,
        CopilotSdkStreamEventType? lifecycleType = CopilotSdkStreamEventType.SessionStarted,
        string sessionId = "copilot-session-1") : ICopilotSdkGateway
    {
        public int CreatedRuntimeCount { get; private set; }

        public int SendPromptCallCount { get; private set; }

        public List<CopilotSdkRequest> CreateRequests { get; } = [];

        public Task<ICopilotSdkRuntime> CreateRuntimeAsync(
            CopilotSdkRequest request,
            CancellationToken cancellationToken = default)
        {
            CreatedRuntimeCount++;
            CreateRequests.Add(request);
            return Task.FromResult<ICopilotSdkRuntime>(new StubCopilotSdkRuntime(promptEvents, lifecycleType, request.SessionId ?? sessionId, this));
        }

        public async IAsyncEnumerable<CopilotSdkStreamEvent> SendPromptAsync(
            CopilotSdkRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await using var runtime = await CreateRuntimeAsync(request, cancellationToken);
            await foreach (var eventData in runtime.SendPromptAsync(request, cancellationToken))
            {
                yield return eventData;
            }
        }

        private sealed class StubCopilotSdkRuntime(
            IReadOnlyList<CopilotSdkStreamEvent> promptEvents,
            CopilotSdkStreamEventType? lifecycleType,
            string sessionId,
            StubCopilotSdkGateway owner) : ICopilotSdkRuntime
        {
            private bool _lifecycleSent;

            public string SessionId => sessionId;

            public async IAsyncEnumerable<CopilotSdkStreamEvent> SendPromptAsync(
                CopilotSdkRequest request,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                owner.SendPromptCallCount++;
                if (!_lifecycleSent && lifecycleType is not null)
                {
                    _lifecycleSent = true;
                    yield return new CopilotSdkStreamEvent(lifecycleType.Value, SessionId: sessionId, RequestedSessionId: request.SessionId);
                    await Task.Yield();
                }

                foreach (var eventData in promptEvents)
                {
                    var normalizedEvent = eventData with
                    {
                        SessionId = eventData.SessionId ?? sessionId,
                        RequestedSessionId = eventData.RequestedSessionId ?? request.SessionId
                    };
                    yield return normalizedEvent;
                    await Task.Yield();
                }
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
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

    private sealed class StubCliProcessManager : CliProcessManager
    {
        public Queue<ProcessResult> ExecuteResults { get; init; } = new([
            new ProcessResult(0, "copilot 1.0.10", string.Empty)
        ]);

        public override Task<ProcessResult> ExecuteAsync(ProcessStartContext context, CancellationToken cancellationToken = default)
        {
            if (ExecuteResults.Count == 0)
            {
                return Task.FromResult(new ProcessResult(1, string.Empty, "missing result"));
            }

            return Task.FromResult(ExecuteResults.Dequeue());
        }
    }
}
