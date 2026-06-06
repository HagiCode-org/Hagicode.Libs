using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Reasonix;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class ReasonixProviderTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";
    private static readonly string[] ReasonixExecutableCandidates = ["reasonix"];

    [Fact]
    public void BuildCommandArguments_includes_managed_reasonix_flags_once()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new ReasonixOptions
        {
            WorkingDirectory = "/tmp/project",
            Model = "deepseek-v4-flash",
            Effort = "high",
            BudgetUsd = 1.25m,
            TranscriptPath = "/tmp/reasonix/transcript.jsonl",
            EnableYolo = true,
            McpServerSpecs = ["stdio:git", "stdio:search"],
            McpPrefix = "reasonix",
            ExtraArguments =
            [
                "acp",
                "--dir", "/tmp/ignored",
                "-m", "ignored-model",
                "--effort", "low",
                "--budget", "0.25",
                "--transcript", "/tmp/ignored.jsonl",
                "--mcp", "ignored",
                "--mcp-prefix", "ignored",
                "--yolo",
                "--no-proxy",
                "   "
            ]
        });

        arguments.ShouldBe(
        [
            "acp",
            "--dir", "/tmp/project",
            "-m", "deepseek-v4-flash",
            "--effort", "high",
            "--budget", "1.25",
            "--transcript", "/tmp/reasonix/transcript.jsonl",
            "--yolo",
            "--mcp", "stdio:git",
            "--mcp", "stdio:search",
            "--mcp-prefix", "reasonix",
            "--no-proxy"
        ]);
    }

    [Fact]
    public async Task ExecuteAsync_uses_custom_executable_and_streams_normalized_messages()
    {
        var provider = CreateProvider();
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new ReasonixOptions
                           {
                               ExecutablePath = "/custom/reasonix-dev",
                               WorkingDirectory = "/tmp/project",
                               Model = "deepseek-v4-flash",
                               Effort = "medium",
                               BudgetUsd = 2.5m,
                               EnableYolo = true,
                               EnvironmentVariables = new Dictionary<string, string?>
                               {
                                   ["REASONIX_TOKEN"] = "token"
                               },
                               ExtraArguments = ["--no-proxy"]
                           },
                           "hello"))
        {
            messages.Add(message);
        }

        provider.LastStartContext!.ExecutablePath.ShouldBe("/custom/reasonix-dev");
        provider.LastStartContext.WorkingDirectory.ShouldBe("/tmp/project");
        provider.LastStartContext.Arguments.ShouldBe(
        [
            "acp",
            "--dir", "/tmp/project",
            "-m", "deepseek-v4-flash",
            "--effort", "medium",
            "--budget", "2.5",
            "--yolo",
            "--no-proxy"
        ]);
        provider.LastStartContext.EnvironmentVariables!["REASONIX_TOKEN"].ShouldBe("token");
        provider.SessionClient!.ConnectCalls.ShouldBe(1);
        provider.SessionClient.InitializeCalls.ShouldBe(1);
        provider.SessionClient.StartSessionCalls.ShouldBe(1);
        provider.SessionClient.LastWorkingDirectory.ShouldBe("/tmp/project");
        provider.SessionClient.LastSessionId.ShouldBeNull();
        provider.SessionClient.LastModel.ShouldBeNull();
        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
        messages[1].Content.GetProperty("text").GetString().ShouldBe("pong");
    }

    [Fact]
    public async Task ExecuteAsync_streams_reasoning_chunks_as_assistant_thought_messages()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(includeThoughtChunks: true));
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(new ReasonixOptions(), "hello"))
        {
            messages.Add(message);
        }

        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant.thought", "assistant", "terminal.completed"]);
        messages[1].Content.GetProperty("text").GetString().ShouldBe("thinking...");
    }

    [Fact]
    public async Task ExecuteAsync_falls_back_to_prompt_result_when_notification_loop_ends_early()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(
            emitNotifications: false,
            promptStopReason: "fallback",
            promptOutputText: "pong"));
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(new ReasonixOptions(), "hello"))
        {
            messages.Add(message);
        }

        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
        messages[1].Content.GetProperty("text").GetString().ShouldBe("pong");
        messages[2].Content.GetProperty("text").GetString().ShouldBe("pong");
    }

    [Fact]
    public async Task PingAsync_reports_initialize_details_when_bootstrap_succeeds()
    {
        var provider = CreateProvider();

        var result = await provider.PingAsync();

        result.Success.ShouldBeTrue();
        result.Version.ShouldNotBeNullOrWhiteSpace();
        result.Version.ShouldContain("reasonix");
        result.Version.ShouldContain("Reasonix ACP bootstrap");
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
    public void NormalizeNotification_maps_agent_message_chunks_to_shared_assistant_message()
    {
        var notification = new AcpNotification(
            "session/update",
            JsonSerializer.SerializeToElement(new
            {
                sessionId = "session-1",
                update = new
                {
                    sessionUpdate = "agent_message_chunk",
                    content = new { type = "text", text = "pong" }
                }
            }));

        var messages = ReasonixAcpMessageMapper.NormalizeNotification(notification);

        messages.ShouldHaveSingleItem();
        messages[0].Type.ShouldBe("assistant");
        messages[0].Content.GetProperty("text").GetString().ShouldBe("pong");
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task PingAsync_can_validate_installed_reasonix_cli_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        var resolver = new CliExecutableResolver();
        var executablePath = resolver.ResolveFirstAvailablePath(ReasonixExecutableCandidates);
        if (executablePath is null)
        {
            throw new InvalidOperationException("Reasonix CLI was not found on PATH even though the real CLI validation path was enabled.");
        }

        var provider = new ReasonixProvider(resolver, new CliProcessManager(), null);

        provider.IsAvailable.ShouldBeTrue();

        var result = await provider.PingAsync();

        result.ProviderName.ShouldBe("reasonix");
        result.Success.ShouldBeTrue();
        result.Version.ShouldNotBeNullOrWhiteSpace();
        result.ErrorMessage.ShouldBeNullOrWhiteSpace();
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task ExecuteAsync_can_stream_real_reasonix_cli_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        var resolver = new CliExecutableResolver();
        var executablePath = resolver.ResolveFirstAvailablePath(ReasonixExecutableCandidates);
        if (executablePath is null)
        {
            throw new InvalidOperationException("Reasonix CLI was not found on PATH even though the real CLI execution path was enabled.");
        }

        using var workspace = new TemporaryDirectory();
        var provider = new ReasonixProvider(resolver, new CliProcessManager(), null);
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new ReasonixOptions
                           {
                               WorkingDirectory = workspace.Path,
                               Model = "deepseek-v4-flash",
                               Effort = "low",
                               EnableYolo = true,
                               StartupTimeout = TimeSpan.FromSeconds(20)
                           },
                           "Reply with exactly: PONG",
                           CancellationToken.None))
        {
            messages.Add(message);
        }

        messages.ShouldContain(static message => message.Type == "session.started");
        messages.ShouldContain(static message => message.Type == "terminal.completed");
        messages.ShouldNotContain(static message => message.Type == "terminal.failed");

        var visibleAssistantText = string.Concat(
            messages
                .Where(static message => message.Type == "assistant")
                .Select(static message => message.Content.TryGetProperty("text", out var textElement) ? textElement.GetString() : null)
                .Where(static text => !string.IsNullOrWhiteSpace(text)));

        visibleAssistantText.Trim().ShouldBe("PONG");
    }

    private static TestReasonixProvider CreateProvider(
        CliExecutableResolver? executableResolver = null,
        FakeAcpSessionClient? sessionClient = null)
    {
        return new TestReasonixProvider(
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

    private sealed class TestReasonixProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver runtimeEnvironmentResolver,
        FakeAcpSessionClient sessionClient)
        : ReasonixProvider(executableResolver, processManager, runtimeEnvironmentResolver)
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
        bool emitNotifications = true,
        bool includeThoughtChunks = false,
        string? promptStopReason = "end_turn",
        string promptOutputText = "pong") : IAcpSessionClient
    {
        public int ConnectCalls { get; private set; }
        public int InitializeCalls { get; private set; }
        public int StartSessionCalls { get; private set; }
        public int PromptCalls { get; private set; }
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
            return Task.FromResult(initializeResult ?? JsonSerializer.SerializeToElement(new
            {
                agentInfo = new { name = "reasonix", version = "0.53.2" }
            }));
        }

        public Task<JsonElement> InvokeBootstrapMethodAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(JsonSerializer.SerializeToElement(new { accepted = true }));
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

            return Task.FromResult(new AcpSessionHandle(
                "session-new",
                false,
                JsonSerializer.SerializeToElement(new { sessionId = "session-new" })));
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

            if (includeThoughtChunks)
            {
                yield return new AcpNotification(
                    "session/update",
                    JsonSerializer.SerializeToElement(new
                    {
                        sessionId = "session-new",
                        update = new
                        {
                            sessionUpdate = "agent_thought_chunk",
                            content = new { type = "text", text = "thinking..." }
                        }
                    }));
            }

            yield return new AcpNotification(
                "session/update",
                JsonSerializer.SerializeToElement(new
                {
                    sessionId = "session-new",
                    update = new
                    {
                        sessionUpdate = "agent_message_chunk",
                        content = new { type = "text", text = "pong" }
                    }
                }));

            yield return new AcpNotification(
                "session/update",
                JsonSerializer.SerializeToElement(new
                {
                    sessionId = "session-new",
                    update = new
                    {
                        sessionUpdate = "prompt_completed",
                        stopReason = promptStopReason
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

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hagicode-libs-reasonix-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            File.WriteAllText(System.IO.Path.Combine(Path, "README.md"), "# reasonix integration workspace");
        }

        public string Path { get; }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
            {
                return;
            }

            Directory.Delete(Path, recursive: true);
        }
    }
}
