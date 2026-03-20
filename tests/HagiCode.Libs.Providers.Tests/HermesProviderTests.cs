using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Hermes;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class HermesProviderTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";
    private static readonly string[] HermesExecutableCandidates = ["hermes", "hermes-cli"];

    [Fact]
    public void BuildCommandArguments_uses_acp_by_default_and_honors_explicit_overrides()
    {
        var provider = CreateProvider();

        provider.BuildCommandArguments(new HermesOptions()).ShouldBe(["acp"]);
        provider.BuildCommandArguments(new HermesOptions
        {
            Arguments = ["acp", "--profile", "smoke"]
        }).ShouldBe(["acp", "--profile", "smoke"]);
    }

    [Fact]
    public async Task ExecuteAsync_uses_custom_executable_and_streams_normalized_messages()
    {
        var provider = CreateProvider();
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new HermesOptions
                           {
                               ExecutablePath = "/custom/hermes",
                               WorkingDirectory = "/tmp/project",
                               Model = "hermes/default",
                               Arguments = ["acp", "--profile", "smoke"],
                               EnvironmentVariables = new Dictionary<string, string?>
                               {
                                   ["HERMES_TOKEN"] = "token"
                               }
                           },
                           "hello"))
        {
            messages.Add(message);
        }

        provider.CreatedSessionClients.Count.ShouldBe(1);
        provider.LastStartContext!.ExecutablePath.ShouldBe("/custom/hermes");
        provider.LastStartContext.Arguments.ShouldBe(["acp", "--profile", "smoke"]);
        provider.LastStartContext.WorkingDirectory.ShouldBe("/tmp/project");
        provider.LastStartContext.EnvironmentVariables!["HERMES_TOKEN"].ShouldBe("token");
        provider.CreatedSessionClients[0].ConnectCalls.ShouldBe(1);
        provider.CreatedSessionClients[0].InitializeCalls.ShouldBe(1);
        provider.CreatedSessionClients[0].StartSessionCalls.ShouldBe(1);
        provider.CreatedSessionClients[0].LastWorkingDirectory.ShouldBe("/tmp/project");
        provider.CreatedSessionClients[0].LastModel.ShouldBe("hermes/default");
        provider.CreatedSessionClients[0].LastSessionId.ShouldBeNull();
        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
    }

    [Fact]
    public async Task ExecuteAsync_reuses_in_memory_session_collection_without_restarting_transport()
    {
        var provider = CreateProvider(sessionClientFactory: _ => new FakeAcpSessionClient(emitNotifications: false));
        var firstMessages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(new HermesOptions(), "Remember the secret word: BLUEPRINT-123. Reply with exactly ACK."))
        {
            firstMessages.Add(message);
        }

        var sessionId = firstMessages[0].Content.GetProperty("session_id").GetString();
        sessionId.ShouldNotBeNullOrWhiteSpace();

        var secondMessages = new List<CliMessage>();
        await foreach (var message in provider.ExecuteAsync(
                           new HermesOptions { SessionId = sessionId },
                           "What was the secret word I told you earlier? Reply with just the word."))
        {
            secondMessages.Add(message);
        }

        provider.CreatedSessionClients.Count.ShouldBe(1);
        provider.CreatedSessionClients[0].StartSessionCalls.ShouldBe(1);
        provider.CreatedSessionClients[0].PromptCalls.ShouldBe(2);
        secondMessages.First().Type.ShouldBe("session.reused");
        var reusedText = secondMessages[1].Content.GetProperty("text").GetString();
        reusedText.ShouldNotBeNull();
        reusedText.ShouldContain("BLUEPRINT-123");
    }

    [Fact]
    public async Task ExecuteAsync_falls_back_to_prompt_result_when_notification_loop_ends_via_internal_cancellation()
    {
        var provider = CreateProvider(sessionClientFactory: _ => new FakeAcpSessionClient(emitNotifications: false, promptStopReason: "fallback"));
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(new HermesOptions(), "hello"))
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
        result.Version.ShouldContain("hermes");
        result.Version.ShouldContain("managed ACP bootstrap");
        provider.CreatedSessionClients[0].InitializeCalls.ShouldBe(1);
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

        var messages = HermesAcpMessageMapper.NormalizeNotification(notification);

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

        var messages = HermesAcpMessageMapper.NormalizeNotification(notification);

        messages.ShouldHaveSingleItem();
        messages[0].Type.ShouldBe("assistant");
        messages[0].Content.GetProperty("text").GetString().ShouldBe("BLUEPRINT-123");
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task PingAsync_can_validate_installed_hermes_cli_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        var resolver = new CliExecutableResolver();
        var executablePath = resolver.ResolveFirstAvailablePath(HermesExecutableCandidates);
        if (executablePath is null)
        {
            throw new InvalidOperationException("Hermes CLI was not found on PATH even though the real CLI validation path was enabled.");
        }

        var executableName = Path.GetFileNameWithoutExtension(executablePath);
        executableName.ShouldNotBeNullOrWhiteSpace();
        executableName.ShouldBeOneOf("hermes", "hermes-cli");

        var provider = new HermesProvider(resolver, new CliProcessManager(), null);

        provider.IsAvailable.ShouldBeTrue();

        var result = await provider.PingAsync();

        result.ProviderName.ShouldBe("hermes");
        result.Success.ShouldBeTrue();
        result.Version.ShouldNotBeNullOrWhiteSpace();
        result.ErrorMessage.ShouldBeNullOrWhiteSpace();
    }

    private static TestHermesProvider CreateProvider(
        CliExecutableResolver? executableResolver = null,
        Func<int, FakeAcpSessionClient>? sessionClientFactory = null)
    {
        return new TestHermesProvider(
            executableResolver ?? new StubExecutableResolver(),
            new CliProcessManager(),
            new StubRuntimeEnvironmentResolver(),
            sessionClientFactory ?? (_ => new FakeAcpSessionClient()));
    }

    private static bool IsRealCliTestsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(RealCliTestsEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestHermesProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver runtimeEnvironmentResolver,
        Func<int, FakeAcpSessionClient> sessionClientFactory)
        : HermesProvider(executableResolver, processManager, runtimeEnvironmentResolver)
    {
        private readonly Func<int, FakeAcpSessionClient> _sessionClientFactory = sessionClientFactory;

        public ProcessStartContext? LastStartContext { get; private set; }

        public List<FakeAcpSessionClient> CreatedSessionClients { get; } = [];

        protected override IAcpSessionClient CreateSessionClient(ProcessStartContext startContext)
        {
            LastStartContext = startContext;
            var sessionClient = _sessionClientFactory(CreatedSessionClients.Count + 1);
            CreatedSessionClients.Add(sessionClient);
            return sessionClient;
        }
    }

    private sealed class FakeAcpSessionClient(
        bool emitNotifications = true,
        string? promptStopReason = "end_turn") : IAcpSessionClient
    {
        private readonly Dictionary<string, string> _sessionSecrets = [];

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
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                protocolVersion = 1,
                agentInfo = new
                {
                    name = "hermes",
                    version = "0.4.0"
                }
            }));
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

            var resolvedSessionId = sessionId ?? "session-1";
            return Task.FromResult(new AcpSessionHandle(
                resolvedSessionId,
                false,
                JsonSerializer.SerializeToElement(new
                {
                    sessionId = resolvedSessionId
                })));
        }

        public Task<JsonElement> SendPromptAsync(string sessionId, string prompt, CancellationToken cancellationToken = default)
        {
            PromptCalls++;
            var outputText = BuildPromptResult(sessionId, prompt);
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                sessionId,
                stopReason = promptStopReason,
                outputText
            }));
        }

        public async IAsyncEnumerable<AcpNotification> ReceiveNotificationsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (emitNotifications)
            {
                yield return new AcpNotification(
                    "session/update",
                    JsonSerializer.SerializeToElement(new
                    {
                        sessionId = LastSessionId ?? "session-1",
                        update = new
                        {
                            sessionUpdate = "agent_message_chunk",
                            content = new { text = "pong" }
                        }
                    }));

                yield return new AcpNotification(
                    "session/update",
                    JsonSerializer.SerializeToElement(new
                    {
                        sessionId = LastSessionId ?? "session-1",
                        update = new
                        {
                            sessionUpdate = "prompt_completed",
                            stopReason = "end_turn"
                        }
                    }));
            }

            await Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private string BuildPromptResult(string sessionId, string prompt)
        {
            if (prompt.Contains("Remember the secret word:", StringComparison.OrdinalIgnoreCase))
            {
                var secret = ExtractSecret(prompt);
                if (!string.IsNullOrWhiteSpace(secret))
                {
                    _sessionSecrets[sessionId] = secret;
                }

                return "ACK";
            }

            if (prompt.Contains("What was the secret word I told you earlier", StringComparison.OrdinalIgnoreCase))
            {
                return _sessionSecrets.TryGetValue(sessionId, out var secret) ? secret : "UNKNOWN";
            }

            return "pong";
        }

        private static string? ExtractSecret(string prompt)
        {
            const string marker = "Remember the secret word:";
            var markerIndex = prompt.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return null;
            }

            var value = prompt[(markerIndex + marker.Length)..].Trim();
            var periodIndex = value.IndexOf('.');
            return periodIndex >= 0 ? value[..periodIndex].Trim() : value;
        }
    }

    private sealed class StubExecutableResolver : CliExecutableResolver
    {
        public override string? ResolveExecutablePath(string? executablePath, IReadOnlyDictionary<string, string?>? environmentVariables = null)
        {
            return executablePath;
        }

        public override string? ResolveFirstAvailablePath(IEnumerable<string> candidateNames, IReadOnlyDictionary<string, string?>? environmentVariables = null)
        {
            return candidateNames.First();
        }
    }

    private sealed class MissingExecutableResolver : CliExecutableResolver
    {
        public override string? ResolveExecutablePath(string? executablePath, IReadOnlyDictionary<string, string?>? environmentVariables = null)
        {
            return null;
        }

        public override string? ResolveFirstAvailablePath(IEnumerable<string> candidateNames, IReadOnlyDictionary<string, string?>? environmentVariables = null)
        {
            return null;
        }
    }

    private sealed class StubRuntimeEnvironmentResolver : IRuntimeEnvironmentResolver
    {
        public Task<IReadOnlyDictionary<string, string?>> ResolveAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyDictionary<string, string?> environment = new Dictionary<string, string?>
            {
                ["PATH"] = "/usr/bin"
            };
            return Task.FromResult(environment);
        }
    }
}
