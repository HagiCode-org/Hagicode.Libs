using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Dsh;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class DshProviderTests
{
    private const string DeepSeekHarnessHelp = "Usage: dsh [--profile] <name> [options] [app-args...]\n\ndsh: boot a DeepSeek Harness profile\n";
    private const string ConfiguredProvider = "deepseek-official";
    private const string ConfiguredModel = "deepseek-v4-flash";

    [Fact]
    public void Name_is_dsh_and_availability_follows_executable_resolution()
    {
        CreateProvider().Name.ShouldBe("dsh");
        CreateProvider().IsAvailable.ShouldBeTrue();
        CreateProvider(executableResolver: new MissingExecutableResolver()).IsAvailable.ShouldBeFalse();
    }

    [Fact]
    public void BuildCommandArguments_starts_with_managed_acp_profile_and_trims_extra_arguments()
    {
        var provider = CreateProvider();

        provider.BuildCommandArguments(new DshOptions()).ShouldBe(["--profile", "acp"]);
        provider.BuildCommandArguments(new DshOptions
        {
            ExtraArguments = ["  --verbose  ", "   ", "--workspace", "/tmp/ws"]
        }).ShouldBe(["--profile", "acp", "--verbose", "--workspace", "/tmp/ws"]);
    }

    [Fact]
    public void BuildCommandArguments_rejects_caller_supplied_profile_overrides()
    {
        var provider = CreateProvider();

        Should.Throw<ArgumentException>(() =>
        {
            provider.BuildCommandArguments(new DshOptions { ExtraArguments = ["--profile", "ci"] });
        });
        Should.Throw<ArgumentException>(() =>
        {
            provider.BuildCommandArguments(new DshOptions { ExtraArguments = ["  --PROFILE=ci  "] });
        });
    }

    [Fact]
    public async Task ExecuteAsync_rejects_blank_prompt_before_launch()
    {
        var provider = CreateProvider();

        await Should.ThrowAsync<ArgumentException>(async () =>
        {
            await CollectAsync(provider.ExecuteAsync(new DshOptions(), "   "));
        });

        provider.LastStartContext.ShouldBeNull();
        provider.SessionClient.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_throws_when_executable_is_missing()
    {
        var provider = CreateProvider(executableResolver: new MissingExecutableResolver());

        await Should.ThrowAsync<FileNotFoundException>(async () =>
        {
            await CollectAsync(provider.ExecuteAsync(new DshOptions(), "hello"));
        });
    }

    [Fact]
    public async Task ExecuteAsync_launches_managed_profile_and_streams_session_and_prompt_messages()
    {
        var provider = CreateProvider();

        var messages = await CollectAsync(provider.ExecuteAsync(
            new DshOptions
            {
                ExecutablePath = "/custom/dsh",
                WorkingDirectory = "/tmp/project",
                Provider = ConfiguredProvider,
                Model = ConfiguredModel,
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["DSH_HOME"] = "/tmp/dsh-home"
                },
                ExtraArguments = ["--verbose"]
            },
            "hello"));

        provider.LastStartContext!.ExecutablePath.ShouldBe("/custom/dsh");
        provider.LastStartContext.WorkingDirectory.ShouldBe("/tmp/project");
        provider.LastStartContext.Arguments.ShouldBe(["--profile", "acp", "--verbose"]);
        provider.LastStartContext.EnvironmentVariables!["DSH_HOME"].ShouldBe("/tmp/dsh-home");
        provider.LastStartContext.EnvironmentVariables["PATH"].ShouldBe("/tmp/bin");
        provider.SessionClient!.ConnectCalls.ShouldBe(1);
        provider.SessionClient.InitializeCalls.ShouldBe(1);
        provider.SessionClient.StartSessionCalls.ShouldBe(1);
        provider.SessionClient.LastWorkingDirectory.ShouldBe("/tmp/project");
        provider.SessionClient.LastSessionId.ShouldBeNull();
        provider.LastProvider.ShouldBe(ConfiguredProvider);
        provider.SessionClient.LastModel.ShouldBe(ConfiguredModel);
        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
    }

    [Fact]
    public async Task ExecuteAsync_forwards_model_only_when_supplied()
    {
        var provider = CreateProvider();

        await CollectAsync(provider.ExecuteAsync(new DshOptions(), "hello"));
        provider.SessionClient!.LastModel.ShouldBeNull();

        await CollectAsync(provider.ExecuteAsync(new DshOptions { Provider = ConfiguredProvider, Model = ConfiguredModel }, "hello"));
        provider.SessionClient!.LastModel.ShouldBe(ConfiguredModel);
    }

    [Fact]
    public async Task ExecuteAsync_resumes_supplied_session_identifier()
    {
        var provider = CreateProvider();

        var messages = await CollectAsync(provider.ExecuteAsync(new DshOptions { SessionId = "session-resume" }, "resume prompt"));

        provider.SessionClient!.LastSessionId.ShouldBe("session-resume");
        messages.First().Type.ShouldBe("session.resumed");
    }

    [Fact]
    public async Task ExecuteAsync_forwards_environment_only_when_supplied()
    {
        var provider = CreateProvider();

        await CollectAsync(provider.ExecuteAsync(new DshOptions(), "hello"));
        provider.LastStartContext!.EnvironmentVariables!.Keys.ShouldBe(["PATH"]);

        await CollectAsync(provider.ExecuteAsync(
            new DshOptions
            {
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["DSH_HOME"] = "/tmp/dsh-home",
                    ["PATH"] = null
                }
            },
            "hello"));

        var environment = provider.LastStartContext!.EnvironmentVariables!;
        environment["DSH_HOME"].ShouldBe("/tmp/dsh-home");
        environment.ContainsKey("PATH").ShouldBeTrue();
        environment["PATH"].ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_reports_startup_failure_as_terminal_failure_with_diagnostics()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(
            connectException: new InvalidOperationException("dsh exited before ACP was ready"),
            diagnosticSummary: "stderr: DSH_HOME is not set"));

        var messages = await CollectAsync(provider.ExecuteAsync(new DshOptions(), "hello"));

        messages.Select(static message => message.Type).ShouldBe(["terminal.failed"]);
        var message = messages[0].Content.GetProperty("message").GetString()!;
        message.ShouldContain("dsh ACP startup failed: dsh exited before ACP was ready");
        message.ShouldContain("stderr: DSH_HOME is not set");
    }

    [Fact]
    public async Task ExecuteAsync_redacts_caller_environment_values_from_failure_diagnostics()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(
            connectException: new InvalidOperationException("dsh exited before ACP was ready"),
            diagnosticSummary: "stderr: token=dsh-secret-value rejected"));

        var messages = await CollectAsync(provider.ExecuteAsync(
            new DshOptions
            {
                EnvironmentVariables = new Dictionary<string, string?> { ["DSH_TOKEN"] = "dsh-secret-value" }
            },
            "hello"));

        var message = messages[0].Content.GetProperty("message").GetString()!;
        message.ShouldContain("stderr: token=[redacted] rejected");
        message.ShouldNotContain("dsh-secret-value");
    }

    [Fact]
    public async Task ExecuteAsync_reports_startup_timeout_as_terminal_failure()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(hangOnInitialize: true));

        var messages = await CollectAsync(provider.ExecuteAsync(
            new DshOptions { StartupTimeout = TimeSpan.FromMilliseconds(50) },
            "hello"));

        messages.Select(static message => message.Type).ShouldBe(["terminal.failed"]);
        messages[0].Content.GetProperty("message").GetString()!.ShouldContain("timed out");
    }

    [Fact]
    public async Task ExecuteAsync_reports_stream_failure_as_terminal_failure()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(receiveException: new IOException("socket closed")));

        var messages = await CollectAsync(provider.ExecuteAsync(new DshOptions(), "hello"));

        messages.Select(static message => message.Type).ShouldBe(["session.started", "terminal.failed"]);
        messages[1].Content.GetProperty("message").GetString()!.ShouldContain("dsh stream ended unexpectedly: socket closed");
    }

    [Fact]
    public async Task ExecuteAsync_maps_failed_prompt_stop_reason_to_terminal_failure()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(promptStopReason: "error", emitNotifications: false));

        var messages = await CollectAsync(provider.ExecuteAsync(new DshOptions(), "hello"));

        messages.Select(static message => message.Type).ShouldBe(["session.started", "terminal.failed"]);
    }

    [Fact]
    public async Task ExecuteAsync_yields_terminal_failure_for_binary_that_does_not_identify_as_dsh_without_starting_acp()
    {
        var sessionClient = new FakeAcpSessionClient();
        var provider = CreateProvider(
            processManager: new StubProbeProcessManager(
                new ProcessResult(0, "Usage: dsh-like-tool [options]\n", string.Empty),
                new ProcessResult(0, "9.9.9\n", string.Empty)),
            sessionClient: sessionClient);

        var messages = await CollectAsync(provider.ExecuteAsync(
            new DshOptions { ExecutablePath = "/custom/dsh-like-tool" },
            "hello"));

        messages.Select(static message => message.Type).ShouldBe(["terminal.failed"]);
        var message = messages[0].Content.GetProperty("message").GetString()!;
        message.ShouldContain("'/custom/dsh-like-tool' is not DeepSeek Harness");
        sessionClient.ConnectCalls.ShouldBe(0);
        sessionClient.InitializeCalls.ShouldBe(0);
        sessionClient.StartSessionCalls.ShouldBe(0);
        provider.LastStartContext.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_reaches_acp_when_help_banner_identifies_dsh()
    {
        var sessionClient = new FakeAcpSessionClient();
        var provider = CreateProvider(
            processManager: new StubProbeProcessManager(
                new ProcessResult(0, DeepSeekHarnessHelp, string.Empty),
                new ProcessResult(0, "0.2.0-rc.2\n", string.Empty)),
            sessionClient: sessionClient);

        var messages = await CollectAsync(provider.ExecuteAsync(new DshOptions(), "hello"));

        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
        sessionClient.ConnectCalls.ShouldBe(1);
        sessionClient.InitializeCalls.ShouldBe(1);
        sessionClient.StartSessionCalls.ShouldBe(1);
    }

    [Fact]
    public async Task PingAsync_reports_version_after_identity_and_acp_handshake()
    {
        var provider = CreateProvider(processManager: new StubProbeProcessManager(
            new ProcessResult(0, DeepSeekHarnessHelp, string.Empty),
            new ProcessResult(0, "0.2.0-rc.2\n", string.Empty)));

        var result = await provider.PingAsync();

        result.ProviderName.ShouldBe("dsh");
        result.Success.ShouldBeTrue();
        result.Version.ShouldBe("0.2.0-rc.2");
        result.ErrorMessage.ShouldBeNullOrWhiteSpace();
        provider.SessionClient!.ConnectCalls.ShouldBe(1);
        provider.SessionClient.InitializeCalls.ShouldBe(1);
        provider.SessionClient.StartSessionCalls.ShouldBe(0);
    }

    [Fact]
    public async Task PingAsync_rejects_binary_that_does_not_identify_as_dsh_without_starting_acp()
    {
        var provider = CreateProvider(processManager: new StubProbeProcessManager(
            new ProcessResult(0, "Usage: dsh-like-tool [options]\n", string.Empty),
            new ProcessResult(0, "9.9.9\n", string.Empty)));

        var result = await provider.PingAsync();

        result.Success.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("is not DeepSeek Harness");
        provider.SessionClient.ShouldBeNull();
    }

    [Fact]
    public async Task PingAsync_reports_help_timeout_without_starting_acp()
    {
        var provider = CreateProvider(processManager: new StubProbeProcessManager(
            new ProcessResult(-1, string.Empty, string.Empty) { TimedOut = true },
            new ProcessResult(0, "0.2.0-rc.2\n", string.Empty)));

        var result = await provider.PingAsync();

        result.Success.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("timed out");
        provider.SessionClient.ShouldBeNull();
    }

    [Fact]
    public async Task PingAsync_reports_missing_executable()
    {
        var provider = CreateProvider(executableResolver: new MissingExecutableResolver());

        var result = await provider.PingAsync();

        result.Success.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("was not found");
    }

    [Fact]
    public async Task PingAsync_reports_version_failure()
    {
        var provider = CreateProvider(processManager: new StubProbeProcessManager(
            new ProcessResult(0, DeepSeekHarnessHelp, string.Empty),
            new ProcessResult(1, string.Empty, "unknown flag")));

        var result = await provider.PingAsync();

        result.Success.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("did not report a version");
    }

    [Fact]
    public async Task PingAsync_reports_acp_handshake_failure()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(
            connectException: new InvalidOperationException("initialize refused")));

        var result = await provider.PingAsync();

        result.Success.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("dsh ACP handshake failed: initialize refused");
    }

    private static TestDshProvider CreateProvider(
        CliExecutableResolver? executableResolver = null,
        CliProcessManager? processManager = null,
        FakeAcpSessionClient? sessionClient = null)
    {
        return new TestDshProvider(
            executableResolver ?? new StubExecutableResolver(),
            processManager ?? new StubProbeProcessManager(
                new ProcessResult(0, DeepSeekHarnessHelp, string.Empty),
                new ProcessResult(0, "0.2.0-rc.2\n", string.Empty)),
            new StubRuntimeEnvironmentResolver(),
            sessionClient ?? new FakeAcpSessionClient());
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

    private sealed class TestDshProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver runtimeEnvironmentResolver,
        FakeAcpSessionClient sessionClient)
        : DshProvider(executableResolver, processManager, runtimeEnvironmentResolver)
    {
        public ProcessStartContext? LastStartContext { get; private set; }

        public FakeAcpSessionClient? SessionClient { get; private set; }

        public string? LastProvider { get; private set; }

        protected override IAcpSessionClient CreateSessionClient(ProcessStartContext startContext, string? provider)
        {
            LastStartContext = startContext;
            LastProvider = provider;
            SessionClient = sessionClient;
            return sessionClient;
        }
    }

    private sealed class FakeAcpSessionClient(
        string promptStopReason = "end_turn",
        bool emitNotifications = true,
        Exception? connectException = null,
        bool hangOnInitialize = false,
        Exception? receiveException = null,
        string? diagnosticSummary = null) : IAcpSessionClient
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
            return connectException is null ? Task.CompletedTask : Task.FromException(connectException);
        }

        public async Task<JsonElement> InitializeAsync(CancellationToken cancellationToken = default)
        {
            InitializeCalls++;
            if (hangOnInitialize)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return JsonSerializer.SerializeToElement(new
            {
                protocolVersion = 1,
                agentInfo = new { name = "deepseek-harness-acp", version = "0.0.1" },
                authMethods = Array.Empty<object>()
            });
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
            var resolvedSessionId = sessionId ?? "session-1";
            return Task.FromResult(new AcpSessionHandle(
                resolvedSessionId,
                !string.IsNullOrWhiteSpace(sessionId),
                JsonSerializer.SerializeToElement(new { sessionId = resolvedSessionId })));
        }

        public Task<JsonElement> SetModeAsync(string sessionId, string modeId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        }

        public Task<JsonElement> SendPromptAsync(string sessionId, string prompt, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["stopReason"] = promptStopReason
            }));
        }

        public async IAsyncEnumerable<AcpNotification> ReceiveNotificationsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (receiveException is not null)
            {
                throw receiveException;
            }

            if (!emitNotifications)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                yield break;
            }

            yield return new AcpNotification(
                "session/update",
                JsonSerializer.SerializeToElement(new
                {
                    sessionId = LastSessionId ?? "session-1",
                    update = new
                    {
                        sessionUpdate = "agent_message_chunk",
                        content = new { type = "text", text = "pong" }
                    }
                }));
        }

        public string? GetDiagnosticSummary()
        {
            return diagnosticSummary;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
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

    private sealed class StubProbeProcessManager(ProcessResult helpResult, ProcessResult versionResult) : CliProcessManager
    {
        public override Task<ProcessResult> ExecuteAsync(ProcessStartContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(context.Arguments.Single() == "--help" ? helpResult : versionResult);
        }
    }
}
