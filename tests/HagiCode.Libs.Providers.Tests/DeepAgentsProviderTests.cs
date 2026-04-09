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
    public void BuildCommandArguments_omits_workspace_switch_and_skips_managed_duplicates()
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
            ExtraArguments = ["deepagents", "--acp", "--name", "ignored", "--workspace=/tmp/ignored", "--debug", "--custom=1", "   "]
        });

        arguments.ShouldBe(
        [
            "--name", "coding-assistant",
            "--description", "Deep workspace helper",
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
                               ExecutablePath = "/custom/deepagents",
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

        provider.LastStartContext!.ExecutablePath.ShouldBe("/custom/deepagents");
        provider.LastStartContext.WorkingDirectory.ShouldBe(Path.GetFullPath(workspaceRoot));
        provider.LastStartContext.Arguments.ShouldBe(
        [
            "--acp",
            "--name", "agent-one",
            "--description", "analysis helper",
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
    public async Task ExecuteAsync_uses_uvx_fallback_when_direct_binary_is_unavailable()
    {
        var provider = CreateProvider(executableResolver: new UvxOnlyExecutableResolver());

        await foreach (var _ in provider.ExecuteAsync(new DeepAgentsOptions(), "hello"))
        {
        }

        provider.LastStartContext!.ExecutablePath.ShouldBe("/usr/bin/uvx");
        provider.LastStartContext.Arguments.ShouldBe(["--from", "deepagents-cli", "deepagents", "--acp"]);
    }

    [Fact]
    public async Task ExecuteAsync_does_not_fallback_when_explicit_executable_is_missing()
    {
        var provider = CreateProvider(executableResolver: new ExplicitMissingButUvxExecutableResolver());

        var exception = await Should.ThrowAsync<FileNotFoundException>(async () =>
        {
            await foreach (var _ in provider.ExecuteAsync(
                               new DeepAgentsOptions
                               {
                                   ExecutablePath = "/missing/deepagents"
                               },
                               "hello"))
            {
            }
        });

        exception.Message.ShouldContain("DeepAgents launcher");
    }

    [Fact]
    public void IsAvailable_returns_true_when_uvx_fallback_is_available()
    {
        var provider = CreateProvider(executableResolver: new UvxOnlyExecutableResolver());

        provider.IsAvailable.ShouldBeTrue();
    }

    [Fact]
    public async Task PingAsync_uses_uvx_fallback_and_reports_initialize_details()
    {
        var provider = CreateProvider(executableResolver: new UvxOnlyExecutableResolver());

        var result = await provider.PingAsync();

        result.Success.ShouldBeTrue();
        result.Version.ShouldContain("deepagents");
        provider.LastStartContext!.ExecutablePath.ShouldBe("/usr/bin/uvx");
        provider.LastStartContext.Arguments.ShouldBe(["--from", "deepagents-cli", "deepagents", "--acp"]);
    }

    [Fact]
    public async Task PingAsync_returns_actionable_failure_when_launcher_is_missing()
    {
        var provider = CreateProvider(executableResolver: new MissingExecutableResolver());

        var result = await provider.PingAsync();

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("Install 'deepagents-cli'");
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
    public async Task ExecuteAsync_applies_bypass_mode_before_prompt_for_one_shot_execution()
    {
        var provider = CreateProvider();

        await foreach (var _ in provider.ExecuteAsync(
                           new DeepAgentsOptions
                           {
                               ModeId = "bypassPermissions",
                               PoolSettings = new CliPoolSettings
                               {
                                   Enabled = false
                               }
                           },
                           "hello"))
        {
        }

        provider.CreatedSessionClients.ShouldHaveSingleItem();
        provider.CreatedSessionClients[0].SetModeCalls.ShouldBe(["bypassPermissions"]);
    }

    [Fact]
    public async Task ExecuteAsync_reapplies_bypass_mode_when_reusing_warm_pooled_session()
    {
        var provider = CreateProvider();

        await foreach (var _ in provider.ExecuteAsync(
                           new DeepAgentsOptions
                           {
                               SessionId = "session-1",
                               ModeId = "bypassPermissions"
                           },
                           "first"))
        {
        }

        await foreach (var _ in provider.ExecuteAsync(
                           new DeepAgentsOptions
                           {
                               SessionId = "session-1",
                               ModeId = "bypassPermissions"
                           },
                           "second"))
        {
        }

        provider.CreatedSessionClients.ShouldHaveSingleItem();
        provider.CreatedSessionClients[0].SetModeCalls.ShouldBe(["bypassPermissions", "bypassPermissions"]);
        provider.CreatedSessionClients[0].PromptCalls.ShouldBe(2);
    }

    [Fact]
    public async Task ExecuteAsync_bypass_mode_streams_prompt_result_without_notifications()
    {
        var provider = new TestDeepAgentsProvider(
            new StubExecutableResolver(),
            new CliProcessManager(),
            new StubRuntimeEnvironmentResolver(),
            _ => new FakeAcpSessionClient(emitNotifications: false, promptOutputText: "pong"));
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new DeepAgentsOptions
                           {
                               ModeId = "bypassPermissions",
                               PoolSettings = new CliPoolSettings
                               {
                                   Enabled = false
                               }
                           },
                           "hello"))
        {
            messages.Add(message);
        }

        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
        provider.CreatedSessionClients.ShouldHaveSingleItem();
        provider.CreatedSessionClients[0].SetModeCalls.ShouldBe(["bypassPermissions"]);
    }

    [Fact]
    public async Task ExecuteAsync_reuses_warm_session_by_native_session_id_when_load_session_is_unsupported()
    {
        var provider = new TestDeepAgentsProvider(
            new StubExecutableResolver(),
            new CliProcessManager(),
            new StubRuntimeEnvironmentResolver(),
            _ => new FakeAcpSessionClient(loadSessionSupported: false));

        string? resolvedSessionId = null;
        await foreach (var message in provider.ExecuteAsync(new DeepAgentsOptions(), "first"))
        {
            if (message.Type == "session.started")
            {
                resolvedSessionId = message.Content.GetProperty("session_id").GetString();
            }
        }

        resolvedSessionId.ShouldNotBeNullOrWhiteSpace();

        await foreach (var _ in provider.ExecuteAsync(
                           new DeepAgentsOptions
                           {
                               SessionId = resolvedSessionId
                           },
                           "second"))
        {
        }

        provider.CreatedSessionClients.Count.ShouldBe(1);
        provider.CreatedSessionClients[0].StartSessionCalls.ShouldBe(1);
        provider.CreatedSessionClients[0].PromptCalls.ShouldBe(2);
    }

    [Fact]
    public async Task ExecuteAsync_returns_actionable_error_for_cold_resume_when_load_session_is_unsupported()
    {
        var provider = new TestDeepAgentsProvider(
            new StubExecutableResolver(),
            new CliProcessManager(),
            new StubRuntimeEnvironmentResolver(),
            _ => new FakeAcpSessionClient(loadSessionSupported: false));

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in provider.ExecuteAsync(
                               new DeepAgentsOptions
                               {
                                   SessionId = "session-123"
                               },
                               "hello"))
            {
            }
        });

        exception.Message.ShouldContain("does not advertise session/load support");
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
    public void NormalizeNotification_maps_completed_tool_call_updates_to_tool_completed_message()
    {
        var notification = new AcpNotification(
            "session/update",
            JsonSerializer.SerializeToElement(new
            {
                sessionId = "session-1",
                update = new
                {
                    kind = "tool_call_update",
                    toolName = "bash",
                    toolCallId = "tool-1",
                    status = "completed",
                    message = "ping finished"
                }
            }));

        var messages = DeepAgentsAcpMessageMapper.NormalizeNotification(notification);

        messages.ShouldHaveSingleItem();
        messages[0].Type.ShouldBe("tool.completed");
        messages[0].Content.GetProperty("tool_name").GetString().ShouldBe("bash");
        messages[0].Content.GetProperty("tool_call_id").GetString().ShouldBe("tool-1");
    }

    [Fact]
    public void NormalizeNotification_maps_real_deepagents_tool_call_notifications_using_session_update_before_kind()
    {
        var notification = new AcpNotification(
            "session/update",
            JsonSerializer.SerializeToElement(new
            {
                sessionId = "session-1",
                update = new
                {
                    sessionUpdate = "tool_call",
                    kind = "execute",
                    toolCallId = "tool-real-1",
                    title = "Execute: `dotnet --version`"
                }
            }));

        var messages = DeepAgentsAcpMessageMapper.NormalizeNotification(notification);

        messages.ShouldHaveSingleItem();
        messages[0].Type.ShouldBe("tool.call");
        messages[0].Content.GetProperty("tool_name").GetString().ShouldBe("execute");
        messages[0].Content.GetProperty("tool_call_id").GetString().ShouldBe("tool-real-1");
    }

    [Fact]
    public void NormalizeNotification_maps_failed_tool_call_updates_to_tool_failed_message()
    {
        var notification = new AcpNotification(
            "session/update",
            JsonSerializer.SerializeToElement(new
            {
                sessionId = "session-1",
                update = new
                {
                    kind = "tool_call_update",
                    toolName = "bash",
                    toolCallId = "tool-2",
                    status = "failed",
                    message = "permission denied"
                }
            }));

        var messages = DeepAgentsAcpMessageMapper.NormalizeNotification(notification);

        messages.ShouldHaveSingleItem();
        messages[0].Type.ShouldBe("tool.failed");
        messages[0].Content.GetProperty("tool_name").GetString().ShouldBe("bash");
        messages[0].Content.GetProperty("tool_call_id").GetString().ShouldBe("tool-2");
    }

    [Fact]
    public void NormalizeNotification_maps_permission_requests_to_tool_permission_message()
    {
        var notification = new AcpNotification(
            "session/request_permission",
            JsonSerializer.SerializeToElement(new
            {
                sessionId = "session-1",
                toolCall = new
                {
                    title = "Approve bash command",
                    toolCallId = "tool-3"
                },
                options = new object[]
                {
                    new { optionId = "approve", name = "Approve" }
                }
            }));

        var messages = DeepAgentsAcpMessageMapper.NormalizeNotification(notification);

        messages.ShouldHaveSingleItem();
        messages[0].Type.ShouldBe("tool.permission");
        messages[0].Content.GetProperty("session_id").GetString().ShouldBe("session-1");
        messages[0].Content.GetProperty("tool_call_id").GetString().ShouldBe("tool-3");
        messages[0].Content.GetProperty("title").GetString().ShouldBe("Approve bash command");
    }

    [Fact]
    public async Task ExecuteAsync_streams_toolcall_lifecycle_in_order()
    {
        var provider = CreateProvider(
            sessionClient: new FakeAcpSessionClient(
                scriptedNotifications:
                [
                    CreateNotification("session-1", new
                    {
                        kind = "assistant",
                        text = "Preparing tool call."
                    }),
                    CreateNotification("session-1", new
                    {
                        kind = "tool_call",
                        toolName = "bash",
                        toolCallId = "tool-1",
                        summary = "ping -c 1 1.1.1.1"
                    }),
                    CreatePermissionNotification("session-1", "tool-1", "Approve bash command"),
                    CreateNotification("session-1", new
                    {
                        kind = "tool_call_update",
                        toolName = "bash",
                        toolCallId = "tool-1",
                        status = "running",
                        summary = "tool is executing"
                    }),
                    CreateNotification("session-1", new
                    {
                        kind = "tool_call_update",
                        toolName = "bash",
                        toolCallId = "tool-1",
                        status = "completed",
                        message = "tool finished"
                    }),
                    CreateNotification("session-1", new
                    {
                        kind = "assistant",
                        text = "Tool completed successfully."
                    }),
                    CreateNotification("session-1", new
                    {
                        kind = "prompt_completed",
                        stopReason = "end_turn"
                    })
                ]));
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(new DeepAgentsOptions(), "toolcall success"))
        {
            messages.Add(message);
        }

        messages.Select(static message => message.Type).ShouldBe(
        [
            "session.started",
            "assistant",
            "tool.call",
            "tool.permission",
            "tool.update",
            "tool.completed",
            "assistant",
            "terminal.completed"
        ]);
    }

    [Fact]
    public async Task ExecuteAsync_streams_real_deepagents_toolcall_shape_in_order()
    {
        var provider = CreateProvider(
            sessionClient: new FakeAcpSessionClient(
                scriptedNotifications:
                [
                    CreateNotification("session-1", new
                    {
                        sessionUpdate = "tool_call",
                        kind = "execute",
                        toolCallId = "tool-real-1",
                        title = "Execute: `dotnet --version`"
                    }),
                    CreatePermissionNotification("session-1", "tool-real-1", "Execute: `dotnet --version`"),
                    CreateNotification("session-1", new
                    {
                        sessionUpdate = "tool_call_update",
                        kind = "execute",
                        toolCallId = "tool-real-1",
                        status = "completed",
                        content = new object[]
                        {
                            new
                            {
                                type = "text",
                                text = "8.0.412"
                            }
                        }
                    }),
                    CreateNotification("session-1", new
                    {
                        kind = "assistant",
                        text = "tool completed successfully"
                    }),
                    CreateNotification("session-1", new
                    {
                        kind = "prompt_completed",
                        stopReason = "end_turn"
                    })
                ]));
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(new DeepAgentsOptions(), "toolcall real shape"))
        {
            messages.Add(message);
        }

        messages.Select(static message => message.Type).ShouldBe(
        [
            "session.started",
            "tool.call",
            "tool.permission",
            "tool.completed",
            "assistant",
            "terminal.completed"
        ]);
        messages[1].Content.GetProperty("tool_name").GetString().ShouldBe("execute");
        messages[3].Content.GetProperty("tool_name").GetString().ShouldBe("execute");
    }

    [Fact]
    public async Task ExecuteAsync_surfaces_failed_toolcall_and_terminal_failure()
    {
        var provider = CreateProvider(
            sessionClient: new FakeAcpSessionClient(
                scriptedNotifications:
                [
                    CreateNotification("session-1", new
                    {
                        kind = "assistant",
                        text = "Attempting privileged tool call."
                    }),
                    CreateNotification("session-1", new
                    {
                        kind = "tool_call",
                        toolName = "bash",
                        toolCallId = "tool-2",
                        summary = "cat /root/secret"
                    }),
                    CreatePermissionNotification("session-1", "tool-2", "Approve privileged bash command"),
                    CreateNotification("session-1", new
                    {
                        kind = "tool_call_update",
                        toolName = "bash",
                        toolCallId = "tool-2",
                        status = "failed",
                        message = "permission denied"
                    }),
                    CreateNotification("session-1", new
                    {
                        kind = "error",
                        message = "permission denied"
                    })
                ]));
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(new DeepAgentsOptions(), "toolcall failure"))
        {
            messages.Add(message);
        }

        messages.Select(static message => message.Type).ShouldBe(
        [
            "session.started",
            "assistant",
            "tool.call",
            "tool.permission",
            "tool.failed",
            "terminal.failed"
        ]);
        messages.Last().Content.GetProperty("message").GetString().ShouldContain("permission denied");
    }

    [Fact(Skip = "DeepAgents real CLI validation is excluded by default because CI does not guarantee a deepagents binary on PATH.")]
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

    private static TestDeepAgentsProvider CreateProvider(
        CliExecutableResolver? executableResolver = null,
        FakeAcpSessionClient? sessionClient = null)
    {
        return new TestDeepAgentsProvider(
            executableResolver ?? new StubExecutableResolver(),
            new CliProcessManager(),
            new StubRuntimeEnvironmentResolver(),
            _ => sessionClient ?? new FakeAcpSessionClient());
    }

    private static bool IsRealCliTestsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(RealCliTestsEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static AcpNotification CreateNotification(string sessionId, object update)
    {
        return new AcpNotification(
            "session/update",
            JsonSerializer.SerializeToElement(new
            {
                sessionId,
                update
            }));
    }

    private static AcpNotification CreatePermissionNotification(string sessionId, string toolCallId, string title)
    {
        return new AcpNotification(
            "session/request_permission",
            JsonSerializer.SerializeToElement(new
            {
                sessionId,
                toolCall = new
                {
                    title,
                    toolCallId
                },
                options = new object[]
                {
                    new { optionId = "approve", name = "Approve" },
                    new { optionId = "reject", name = "Reject" }
                }
            }));
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
        string? promptOutputText = null,
        bool? loadSessionSupported = null,
        IReadOnlyList<AcpNotification>? scriptedNotifications = null) : IAcpSessionClient
    {
        private JsonElement? _cachedInitializeResult;

        public int ConnectCalls { get; private set; }

        public int InitializeCalls { get; private set; }

        public int StartSessionCalls { get; private set; }

        public int PromptCalls { get; private set; }

        public string? LastWorkingDirectory { get; private set; }

        public string? LastSessionId { get; private set; }

        public string? LastModel { get; private set; }

        public List<string> SetModeCalls { get; } = [];

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectCalls++;
            return Task.CompletedTask;
        }

        public Task<JsonElement> InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_cachedInitializeResult is { } cached)
            {
                return Task.FromResult(cached);
            }

            InitializeCalls++;
            var result = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["protocolVersion"] = 1,
                ["agentCapabilities"] = loadSessionSupported is null ? null : new
                {
                    loadSession = loadSessionSupported.Value
                },
                ["agentInfo"] = new
                {
                    name = "deepagents",
                    version = "0.1.7"
                }
            });
            _cachedInitializeResult = result;
            return Task.FromResult(result);
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

            if (scriptedNotifications is { Count: > 0 })
            {
                foreach (var notification in scriptedNotifications)
                {
                    yield return notification;
                    await Task.Yield();
                }

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

    private sealed class UvxOnlyExecutableResolver : CliExecutableResolver
    {
        public override string? ResolveExecutablePath(string? executableName, IReadOnlyDictionary<string, string?>? environmentVariables = null)
        {
            if (string.IsNullOrWhiteSpace(executableName))
            {
                return null;
            }

            return executableName.StartsWith("uvx", StringComparison.OrdinalIgnoreCase) ? "/usr/bin/uvx" : null;
        }

        public override string? ResolveFirstAvailablePath(IEnumerable<string> executableNames, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => executableNames.Select(candidate => ResolveExecutablePath(candidate, environmentVariables)).FirstOrDefault(static value => value is not null);
    }

    private sealed class ExplicitMissingButUvxExecutableResolver : CliExecutableResolver
    {
        public override string? ResolveExecutablePath(string? executableName, IReadOnlyDictionary<string, string?>? environmentVariables = null)
        {
            if (string.IsNullOrWhiteSpace(executableName))
            {
                return null;
            }

            if (string.Equals(executableName, "uvx", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(executableName, "uvx.cmd", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(executableName, "uvx.exe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(executableName, "uvx.bat", StringComparison.OrdinalIgnoreCase))
            {
                return "/usr/bin/uvx";
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
