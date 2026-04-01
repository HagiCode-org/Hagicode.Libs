using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.DeepAgents;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class DeepAgentsProviderTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";

    [Fact]
    public void BuildCommandArguments_normalizes_workspace_metadata_and_skips_managed_duplicates()
    {
        var provider = CreateProvider();
        var skillsA = Path.Combine(Path.GetTempPath(), "deepagents-skill-a");
        var skillsB = Path.Combine(Path.GetTempPath(), "deepagents-skill-b");
        var memoryFile = Path.Combine(Path.GetTempPath(), "deepagents-agents.md");
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "deepagents-workspace");

        var arguments = provider.BuildCommandArguments(new DeepAgentsOptions
        {
            WorkspaceRoot = workspaceRoot,
            AgentName = "coding-assistant",
            AgentDescription = "Deep workspace helper",
            SkillsDirectories = [skillsA, "  ", skillsB],
            MemoryFiles = [memoryFile, " "],
            ExtraArguments = ["deepagents-acp", "--name", "ignored", "--workspace=/tmp/ignored", "--debug", "--custom=1", "   "]
        });

        arguments.ShouldBe(
        [
            "--name", "coding-assistant",
            "--description", "Deep workspace helper",
            "--workspace", Path.GetFullPath(workspaceRoot),
            "--skills", $"{Path.GetFullPath(skillsA)},{Path.GetFullPath(skillsB)}",
            "--memory", Path.GetFullPath(memoryFile),
            "--debug",
            "--custom=1"
        ]);
    }

    [Fact]
    public async Task ExecuteAsync_uses_explicit_executable_and_streams_normalized_messages()
    {
        var provider = CreateProvider();
        var messages = new List<CliMessage>();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "deepagents-project");

        await foreach (var message in provider.ExecuteAsync(
                           new DeepAgentsOptions
                           {
                               ExecutablePath = "/custom/deepagents-acp",
                               WorkspaceRoot = workspaceRoot,
                               AgentName = "agent-one",
                               AgentDescription = "analysis helper",
                               SkillsDirectories = ["/skills/a", "/skills/b"],
                               MemoryFiles = ["/tmp/AGENTS.md"],
                               EnvironmentVariables = new Dictionary<string, string?>
                               {
                                   ["ANTHROPIC_API_KEY"] = "test-key"
                               },
                               ExtraArguments = ["--debug"]
                           },
                           "hello"))
        {
            messages.Add(message);
        }

        provider.LastStartContext!.ExecutablePath.ShouldBe("/custom/deepagents-acp");
        provider.LastStartContext.WorkingDirectory.ShouldBe(Path.GetFullPath(workspaceRoot));
        provider.LastStartContext.Arguments.ShouldBe(
        [
            "--name", "agent-one",
            "--description", "analysis helper",
            "--workspace", Path.GetFullPath(workspaceRoot),
            "--skills", "/skills/a,/skills/b",
            "--memory", "/tmp/AGENTS.md",
            "--debug"
        ]);
        provider.LastStartContext.EnvironmentVariables!["ANTHROPIC_API_KEY"].ShouldBe("test-key");
        provider.CreatedSessionClients.ShouldHaveSingleItem();
        provider.CreatedSessionClients[0].ConnectCalls.ShouldBe(1);
        provider.CreatedSessionClients[0].InitializeCalls.ShouldBe(1);
        provider.CreatedSessionClients[0].StartSessionCalls.ShouldBe(1);
        provider.CreatedSessionClients[0].LastWorkingDirectory.ShouldBe(Path.GetFullPath(workspaceRoot));
        provider.CreatedSessionClients[0].LastModel.ShouldBeNull();
        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
    }

    [Fact]
    public async Task ExecuteAsync_uses_npx_fallback_when_direct_binary_is_unavailable()
    {
        var provider = CreateProvider(executableResolver: new NpxOnlyExecutableResolver());

        await foreach (var _ in provider.ExecuteAsync(new DeepAgentsOptions(), "hello"))
        {
        }

        provider.LastStartContext!.ExecutablePath.ShouldBe("/usr/bin/npx");
        provider.LastStartContext.Arguments.ShouldBe(["deepagents-acp"]);
    }

    [Fact]
    public async Task ExecuteAsync_does_not_fallback_when_explicit_executable_is_missing()
    {
        var provider = CreateProvider(executableResolver: new ExplicitMissingButNpxExecutableResolver());

        var exception = await Should.ThrowAsync<FileNotFoundException>(async () =>
        {
            await foreach (var _ in provider.ExecuteAsync(
                               new DeepAgentsOptions
                               {
                                   ExecutablePath = "/missing/deepagents-acp"
                               },
                               "hello"))
            {
            }
        });

        exception.Message.ShouldContain("DeepAgents launcher");
    }

    [Fact]
    public void IsAvailable_returns_true_when_npx_fallback_is_available()
    {
        var provider = CreateProvider(executableResolver: new NpxOnlyExecutableResolver());

        provider.IsAvailable.ShouldBeTrue();
    }

    [Fact]
    public async Task PingAsync_uses_npx_fallback_and_reports_initialize_details()
    {
        var provider = CreateProvider(executableResolver: new NpxOnlyExecutableResolver());

        var result = await provider.PingAsync();

        result.Success.ShouldBeTrue();
        result.Version.ShouldContain("deepagents");
        provider.LastStartContext!.ExecutablePath.ShouldBe("/usr/bin/npx");
        provider.LastStartContext.Arguments.ShouldBe(["deepagents-acp"]);
    }

    [Fact]
    public async Task PingAsync_returns_actionable_failure_when_launcher_is_missing()
    {
        var provider = CreateProvider(executableResolver: new MissingExecutableResolver());

        var result = await provider.PingAsync();

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("Install 'deepagents-acp'");
    }

    [Fact]
    public async Task ExecuteAsync_reuses_pooled_session_when_workspace_and_arguments_match()
    {
        var provider = CreateProvider();

        await foreach (var _ in provider.ExecuteAsync(
                           new DeepAgentsOptions
                           {
                               SessionId = "session-1",
                               WorkspaceRoot = "/tmp/workspace",
                               AgentName = "assistant"
                           },
                           "first"))
        {
        }

        await foreach (var _ in provider.ExecuteAsync(
                           new DeepAgentsOptions
                           {
                               SessionId = "session-1",
                               WorkspaceRoot = "/tmp/workspace",
                               AgentName = "assistant"
                           },
                           "second"))
        {
        }

        provider.CreatedSessionClients.Count.ShouldBe(1);
        provider.CreatedSessionClients[0].ConnectCalls.ShouldBe(1);
        provider.CreatedSessionClients[0].StartSessionCalls.ShouldBe(2);
        provider.CreatedSessionClients[0].PromptCalls.ShouldBe(2);
    }

    [Fact]
    public async Task ExecuteAsync_creates_a_new_pooled_session_when_workspace_changes_for_same_session_id()
    {
        var provider = CreateProvider();

        await foreach (var _ in provider.ExecuteAsync(
                           new DeepAgentsOptions
                           {
                               SessionId = "session-1",
                               WorkspaceRoot = "/tmp/workspace-a"
                           },
                           "first"))
        {
        }

        await foreach (var _ in provider.ExecuteAsync(
                           new DeepAgentsOptions
                           {
                               SessionId = "session-1",
                               WorkspaceRoot = "/tmp/workspace-b"
                           },
                           "second"))
        {
        }

        provider.CreatedSessionClients.Count.ShouldBe(2);
        provider.CreatedSessionClients[0].LastWorkingDirectory.ShouldBe("/tmp/workspace-a");
        provider.CreatedSessionClients[1].LastWorkingDirectory.ShouldBe("/tmp/workspace-b");
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
                    kind = "prompt_completed",
                    stopReason = "end_turn"
                }
            }));

        var messages = DeepAgentsAcpMessageMapper.NormalizeNotification(notification);

        messages.ShouldHaveSingleItem();
        messages[0].Type.ShouldBe("terminal.completed");
        messages[0].Content.GetProperty("session_id").GetString().ShouldBe("session-1");
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task PingAsync_can_validate_installed_deepagents_cli_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        var descriptor = CliInstallRegistry.Descriptors.Single(static d => d.ProviderName == "DeepAgents");
        var resolver = new CliExecutableResolver();
        var executablePath = resolver.ResolveFirstAvailablePath(descriptor.ExecutableCandidates);
        if (executablePath is null)
        {
            throw new InvalidOperationException("DeepAgents CLI was not found on PATH even though the real CLI validation path was enabled.");
        }

        var provider = new DeepAgentsProvider(resolver, new CliProcessManager(), null);

        provider.IsAvailable.ShouldBeTrue();
        var result = await provider.PingAsync();

        result.ProviderName.ShouldBe("deepagents");
        result.Success.ShouldBeTrue();
        result.Version.ShouldNotBeNullOrWhiteSpace();
        result.ErrorMessage.ShouldBeNullOrWhiteSpace();
    }

    private static TestDeepAgentsProvider CreateProvider(CliExecutableResolver? executableResolver = null)
    {
        return new TestDeepAgentsProvider(
            executableResolver ?? new StubExecutableResolver(),
            new CliProcessManager(),
            new StubRuntimeEnvironmentResolver(),
            _ => new FakeAcpSessionClient());
    }

    private static bool IsRealCliTestsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(RealCliTestsEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestDeepAgentsProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver runtimeEnvironmentResolver,
        Func<ProcessStartContext, FakeAcpSessionClient> sessionClientFactory)
        : DeepAgentsProvider(executableResolver, processManager, runtimeEnvironmentResolver)
    {
        public ProcessStartContext? LastStartContext { get; private set; }

        public List<FakeAcpSessionClient> CreatedSessionClients { get; } = [];

        protected override IAcpSessionClient CreateSessionClient(ProcessStartContext startContext)
        {
            LastStartContext = startContext;
            var client = sessionClientFactory(startContext);
            CreatedSessionClients.Add(client);
            return client;
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
                    name = "deepagents",
                    version = "0.1.7"
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
            return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        }

        public Task<JsonElement> SendPromptAsync(string sessionId, string prompt, CancellationToken cancellationToken = default)
        {
            PromptCalls++;
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

            yield return new AcpNotification(
                "session/update",
                JsonSerializer.SerializeToElement(new
                {
                    sessionId = LastSessionId ?? resumedSessionId ?? "session-1",
                    update = new
                    {
                        kind = "assistant",
                        text = "pong"
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
                        kind = "prompt_completed",
                        stopReason = "end_turn"
                    }
                }));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubExecutableResolver : CliExecutableResolver
    {
        public override string? ResolveExecutablePath(string? executableName, IReadOnlyDictionary<string, string?>? environmentVariables = null)
        {
            if (string.IsNullOrWhiteSpace(executableName))
            {
                return null;
            }

            return string.Equals(executableName, "npx", StringComparison.OrdinalIgnoreCase)
                ? "/usr/bin/npx"
                : executableName;
        }

        public override string? ResolveFirstAvailablePath(IEnumerable<string> executableNames, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => executableNames.Select(candidate => ResolveExecutablePath(candidate, environmentVariables)).FirstOrDefault(static value => value is not null);
    }

    private sealed class NpxOnlyExecutableResolver : CliExecutableResolver
    {
        public override string? ResolveExecutablePath(string? executableName, IReadOnlyDictionary<string, string?>? environmentVariables = null)
        {
            if (string.IsNullOrWhiteSpace(executableName))
            {
                return null;
            }

            return executableName.StartsWith("npx", StringComparison.OrdinalIgnoreCase) ? "/usr/bin/npx" : null;
        }

        public override string? ResolveFirstAvailablePath(IEnumerable<string> executableNames, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => executableNames.Select(candidate => ResolveExecutablePath(candidate, environmentVariables)).FirstOrDefault(static value => value is not null);
    }

    private sealed class ExplicitMissingButNpxExecutableResolver : CliExecutableResolver
    {
        public override string? ResolveExecutablePath(string? executableName, IReadOnlyDictionary<string, string?>? environmentVariables = null)
        {
            if (string.IsNullOrWhiteSpace(executableName))
            {
                return null;
            }

            if (string.Equals(executableName, "npx", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(executableName, "npx.cmd", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(executableName, "npx.exe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(executableName, "npx.bat", StringComparison.OrdinalIgnoreCase))
            {
                return "/usr/bin/npx";
            }

            return null;
        }

        public override string? ResolveFirstAvailablePath(IEnumerable<string> executableNames, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => executableNames.Select(candidate => ResolveExecutablePath(candidate, environmentVariables)).FirstOrDefault(static value => value is not null);
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
