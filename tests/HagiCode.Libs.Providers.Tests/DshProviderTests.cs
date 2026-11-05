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

    [Test]
    public void Name_is_dsh_and_availability_follows_executable_resolution()
    {
        CreateProvider().Name.ShouldBe("dsh");
        CreateProvider().IsAvailable.ShouldBeTrue();
        CreateProvider(executableResolver: new MissingExecutableResolver()).IsAvailable.ShouldBeFalse();
    }

    [Test]
    public void BuildCommandArguments_starts_with_managed_acp_profile_and_trims_extra_arguments()
    {
        var provider = CreateProvider();

        provider.BuildCommandArguments(new DshOptions()).ShouldBe(["--profile", "acp"]);
        provider.BuildCommandArguments(new DshOptions
        {
            ExtraArguments = ["  --verbose  ", "   ", "--workspace", "/tmp/ws"]
        }).ShouldBe(["--profile", "acp", "--verbose", "--workspace", "/tmp/ws"]);
    }

    [Test]
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

    [Test]
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

    [Test]
    public async Task ExecuteAsync_throws_when_executable_is_missing()
    {
        var provider = CreateProvider(executableResolver: new MissingExecutableResolver());

        await Should.ThrowAsync<FileNotFoundException>(async () =>
        {
            await CollectAsync(provider.ExecuteAsync(new DshOptions(), "hello"));
        });
    }

    [Test]
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

    [Test]
    public async Task ExecuteAsync_forwards_model_only_when_supplied()
    {
        var provider = CreateProvider();

        await CollectAsync(provider.ExecuteAsync(new DshOptions(), "hello"));
        provider.SessionClient!.LastModel.ShouldBeNull();

        await CollectAsync(provider.ExecuteAsync(new DshOptions { Provider = ConfiguredProvider, Model = ConfiguredModel }, "hello"));
        provider.SessionClient!.LastModel.ShouldBe(ConfiguredModel);
    }

    [Test]
    public async Task ExecuteAsync_resumes_supplied_session_identifier()
    {
        var provider = CreateProvider();

        var messages = await CollectAsync(provider.ExecuteAsync(new DshOptions { SessionId = "session-resume" }, "resume prompt"));

        provider.SessionClient!.LastSessionId.ShouldBe("session-resume");
        messages.First().Type.ShouldBe("session.resumed");
    }

    [Test]
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

    [Test]
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

    [Test]
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

    [Test]
    public async Task ExecuteAsync_reports_startup_timeout_as_terminal_failure()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(hangOnInitialize: true));

        var messages = await CollectAsync(provider.ExecuteAsync(
            new DshOptions { StartupTimeout = TimeSpan.FromMilliseconds(50) },
            "hello"));

        messages.Select(static message => message.Type).ShouldBe(["terminal.failed"]);
        messages[0].Content.GetProperty("message").GetString()!.ShouldContain("timed out");
    }

    [Test]
    public async Task ExecuteAsync_reports_stream_failure_as_terminal_failure()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(receiveException: new IOException("socket closed")));

        var messages = await CollectAsync(provider.ExecuteAsync(new DshOptions(), "hello"));

        messages.Select(static message => message.Type).ShouldBe(["session.started", "terminal.failed"]);
        messages[1].Content.GetProperty("message").GetString()!.ShouldContain("dsh stream ended unexpectedly: socket closed");
    }

    [Test]
    public async Task ExecuteAsync_maps_failed_prompt_stop_reason_to_terminal_failure()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(promptStopReason: "error", emitNotifications: false));

        var messages = await CollectAsync(provider.ExecuteAsync(new DshOptions(), "hello"));

        messages.Select(static message => message.Type).ShouldBe(["session.started", "terminal.failed"]);
    }

    [Test]
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

    [Test]
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

    [Test]
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

    [Test]
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

    [Test]
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

    [Test]
    public async Task PingAsync_reports_missing_executable()
    {
        var provider = CreateProvider(executableResolver: new MissingExecutableResolver());

        var result = await provider.PingAsync();

        result.Success.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("was not found");
    }

    [Test]
    public async Task PingAsync_reports_version_failure()
    {
        var provider = CreateProvider(processManager: new StubProbeProcessManager(
            new ProcessResult(0, DeepSeekHarnessHelp, string.Empty),
            new ProcessResult(1, string.Empty, "unknown flag")));

        var result = await provider.PingAsync();

        result.Success.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("did not report a version");
    }

    [Test]
    public async Task PingAsync_reports_acp_handshake_failure()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(
            connectException: new InvalidOperationException("initialize refused")));

        var result = await provider.PingAsync();

        result.Success.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("dsh ACP handshake failed: initialize refused");
    }

    [Test]
    public async Task ExecuteAsync_sends_inline_image_block_after_text_when_connection_advertises_images()
    {
        var transport = new DshImageScriptedTransport(advertiseImage: true);
        var provider = CreateImageProvider(transport);
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var base64 = Convert.ToBase64String(bytes);
        var attachment = new DshImageAttachment("image-1", "image/png", bytes, "- shot.png | image/png | session=s | image=image-1 | path=/tmp/shot.png");

        var messages = await CollectAsync(provider.ExecuteAsync(ImageOptions(attachment), "describe"));

        var blocks = transport.PromptRequests.Single()["params"]!["prompt"]!.AsArray();
        blocks.Count.ShouldBe(2);
        blocks[0]!["type"]!.GetValue<string>().ShouldBe("text");
        blocks[0]!["text"]!.GetValue<string>().ShouldBe("describe");
        blocks[1]!["type"]!.GetValue<string>().ShouldBe("image");
        blocks[1]!["data"]!.GetValue<string>().ShouldBe(base64);
        blocks[1]!["mimeType"]!.GetValue<string>().ShouldBe("image/png");

        messages.Select(static message => message.Type).ShouldBe(["session.started", "image.delivery", "assistant", "terminal.completed"]);
        var delivery = messages.Single(static message => message.Type == "image.delivery").Content;
        delivery.GetProperty("images")[0].GetProperty("delivery").GetString().ShouldBe("inline");
        delivery.GetProperty("images")[0].GetProperty("byte_count").GetInt64().ShouldBe(bytes.Length);

        // Image bytes and base64 must not reach any message the caller can log or store.
        messages.ShouldAllBe(message => !message.Content.GetRawText().Contains(base64, StringComparison.Ordinal));
    }

    [Test]
    public async Task ExecuteAsync_lists_images_by_path_without_image_block_when_connection_does_not_advertise_images()
    {
        var transport = new DshImageScriptedTransport(advertiseImage: false);
        var provider = CreateImageProvider(transport);

        var messages = await CollectAsync(provider.ExecuteAsync(ImageOptions(PngAttachment()), "describe"));

        var prompt = transport.PromptRequests.Single()["params"]!["prompt"]!.AsArray();
        prompt.Count.ShouldBe(1);
        prompt[0]!["text"]!.GetValue<string>().ShouldContain("| path=/tmp/shot.png");
        messages.Select(static message => message.Type).ShouldNotContain("terminal.failed");
        messages.Last().Type.ShouldBe("terminal.completed");
        var delivery = messages.Single(static message => message.Type == "image.delivery").Content;
        delivery.GetProperty("images")[0].GetProperty("reason").GetString().ShouldBe("connection_not_advertised");
    }

    [Test]
    public async Task ExecuteAsync_retries_once_as_text_when_dsh_refuses_inline_images()
    {
        var transport = new DshImageScriptedTransport(advertiseImage: true, rejectInlinePrompts: true);
        var provider = CreateImageProvider(transport);

        var messages = await CollectAsync(provider.ExecuteAsync(ImageOptions(PngAttachment()), "describe"));

        transport.PromptRequests.Count.ShouldBe(2);
        transport.PromptRequests[0]["params"]!["prompt"]!.AsArray().Count.ShouldBe(2);
        var retry = transport.PromptRequests[1]["params"]!["prompt"]!.AsArray();
        retry.Count.ShouldBe(1);
        retry[0]!["text"]!.GetValue<string>().ShouldContain("| path=/tmp/shot.png");
        messages.Select(static message => message.Type).ShouldNotContain("terminal.failed");
        messages.Last().Type.ShouldBe("terminal.completed");
        var lastDelivery = messages.Last(static message => message.Type == "image.delivery").Content;
        lastDelivery.GetProperty("images")[0].GetProperty("reason").GetString().ShouldBe("inline_rejected");
    }

    [Test]
    public async Task ExecuteAsync_does_not_retry_when_the_prompt_fails_for_another_reason()
    {
        var transport = new DshImageScriptedTransport(advertiseImage: true, promptErrorMessage: "upstream model unavailable");
        var provider = CreateImageProvider(transport);

        var messages = await CollectAsync(provider.ExecuteAsync(ImageOptions(PngAttachment()), "describe"));

        transport.PromptRequests.Count.ShouldBe(1);
        messages.Select(static message => message.Type).ShouldContain("terminal.failed");
    }

    [Test]
    public async Task ExecuteAsync_without_images_sends_text_only_and_reports_no_delivery()
    {
        var transport = new DshImageScriptedTransport(advertiseImage: true);
        var provider = CreateImageProvider(transport);

        var messages = await CollectAsync(provider.ExecuteAsync(ImageOptions(), "describe"));

        var prompt = transport.PromptRequests.Single()["params"]!["prompt"]!.AsArray();
        prompt.Count.ShouldBe(1);
        prompt[0]!["text"]!.GetValue<string>().ShouldBe("describe");
        messages.Select(static message => message.Type).ShouldNotContain("image.delivery");
    }

    [Test]
    public async Task ProbeImageInputAsync_reports_supported_without_starting_a_session_or_prompt()
    {
        var transport = new DshImageScriptedTransport(advertiseImage: true);
        var provider = CreateImageProvider(transport);

        var result = await provider.ProbeImageInputAsync(new DshOptions { ExecutablePath = "/custom/dsh" });

        result.State.ShouldBe(DshImageInputState.Supported);
        result.Reason.ShouldBeNull();
        transport.SentFrames.Select(static frame => frame["method"]!.GetValue<string>()).ShouldBe(["initialize"]);
    }

    [Test]
    public async Task ProbeImageInputAsync_reports_not_advertised_when_connection_does_not_broadcast_images()
    {
        var transport = new DshImageScriptedTransport(advertiseImage: false);
        var provider = CreateImageProvider(transport);

        var result = await provider.ProbeImageInputAsync(new DshOptions { ExecutablePath = "/custom/dsh" });

        result.State.ShouldBe(DshImageInputState.NotAdvertised);
        transport.PromptRequests.ShouldBeEmpty();
    }

    [Test]
    public async Task ProbeImageInputAsync_reports_unknown_instead_of_throwing_when_dsh_is_missing()
    {
        var transport = new DshImageScriptedTransport(advertiseImage: true);
        var provider = new ScriptedImageDshProvider(
            new MissingExecutableResolver(),
            new StubProbeProcessManager(
                new ProcessResult(0, DeepSeekHarnessHelp, string.Empty),
                new ProcessResult(0, "0.2.0-rc.2\n", string.Empty)),
            new StubRuntimeEnvironmentResolver(),
            transport);

        var result = await provider.ProbeImageInputAsync(new DshOptions());

        result.State.ShouldBe(DshImageInputState.Unknown);
        result.Reason.ShouldNotBeNullOrWhiteSpace();
        transport.SentFrames.ShouldBeEmpty();
    }

    private static ScriptedImageDshProvider CreateImageProvider(DshImageScriptedTransport transport)
    {
        return new ScriptedImageDshProvider(
            new StubExecutableResolver(),
            new StubProbeProcessManager(
                new ProcessResult(0, DeepSeekHarnessHelp, string.Empty),
                new ProcessResult(0, "0.2.0-rc.2\n", string.Empty)),
            new StubRuntimeEnvironmentResolver(),
            transport);
    }

    private static DshOptions ImageOptions(params DshImageAttachment[] attachments)
    {
        return new DshOptions
        {
            ExecutablePath = "/custom/dsh",
            WorkingDirectory = "/tmp/project",
            Provider = ConfiguredProvider,
            Model = ConfiguredModel,
            ImageAttachments = attachments
        };
    }

    private static DshImageAttachment PngAttachment()
    {
        return new DshImageAttachment(
            "image-1",
            "image/png",
            [0x89, 0x50, 0x4E, 0x47],
            "- shot.png | image/png | session=s | image=image-1 | path=/tmp/shot.png");
    }

    private sealed class ScriptedImageDshProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver runtimeEnvironmentResolver,
        DshImageScriptedTransport transport)
        : DshProvider(executableResolver, processManager, runtimeEnvironmentResolver)
    {
        protected override IAcpSessionClient CreateSessionClient(ProcessStartContext startContext, string? provider)
        {
            return new DshAcpSessionClient(new AcpSessionClient(transport), provider);
        }
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
