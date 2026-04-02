using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.DeepAgents.Console;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.DeepAgents;
using Shouldly;

namespace HagiCode.Libs.ConsoleTesting.Tests;

public sealed class DeepAgentsConsoleIntegrationTests
{
    [Fact]
    public async Task DispatchAsync_runs_deepagents_default_suite_with_fake_provider()
    {
        var provider = new FakeDeepAgentsProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new DeepAgentsConsoleRunner(DeepAgentsConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync([], DeepAgentsConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(0);
        var rendered = output.ToString();
        rendered.ShouldContain("[PASS] deepagents / Ping");
        rendered.ShouldContain("[PASS] deepagents / Simple Prompt");
        rendered.ShouldContain("[PASS] deepagents / Complex Prompt");
        rendered.ShouldContain("[PASS] deepagents / Session Resume");
        rendered.ShouldContain("Summary: 4/4 passed");
    }

    [Fact]
    public async Task DispatchAsync_shows_provider_specific_help_text()
    {
        var provider = new FakeDeepAgentsProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new DeepAgentsConsoleRunner(DeepAgentsConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(["--help"], DeepAgentsConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(0);
        var rendered = output.ToString();
        rendered.ShouldContain("--test-provider");
        rendered.ShouldContain("--test-provider-full");
        rendered.ShouldContain("--test-all");
        rendered.ShouldContain("--workspace <path>");
        rendered.ShouldContain("--toolcall");
        rendered.ShouldContain("--toolcall-case <name>");
        rendered.ShouldContain("deepagents-acp");
    }

    [Fact]
    public async Task DispatchAsync_normalizes_the_deepagents_acp_alias()
    {
        var provider = new FakeDeepAgentsProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new DeepAgentsConsoleRunner(DeepAgentsConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(["--test-provider", "deepagents-acp"], DeepAgentsConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(0);
        output.ToString().ShouldContain("[PASS] deepagents / Ping");
    }

    [Fact]
    public async Task DispatchAsync_rejects_foreign_provider_names()
    {
        var provider = new FakeDeepAgentsProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new DeepAgentsConsoleRunner(DeepAgentsConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(["--test-provider", "codex"], DeepAgentsConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(1);
        output.ToString().ShouldContain("dedicated provider console");
    }

    [Fact]
    public async Task DispatchAsync_reports_configuration_failures_for_unknown_deepagents_options()
    {
        var provider = new FakeDeepAgentsProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new DeepAgentsConsoleRunner(DeepAgentsConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(
            ["--test-provider-full", "--unknown-option"],
            DeepAgentsConsoleDefinition.Instance,
            runner,
            output);

        exitCode.ShouldBe(1);
        output.ToString().ShouldContain("Unknown option: --unknown-option");
    }

    [Fact]
    public async Task DispatchAsync_passes_runtime_options_to_scenarios()
    {
        var provider = new FakeDeepAgentsProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new DeepAgentsConsoleRunner(DeepAgentsConsoleDefinition.Instance, provider, formatter);
        var workspacePath = Path.Combine(Path.GetTempPath(), $"deepagents-console-workspace-{Guid.NewGuid():N}");
        var repositoryPath = Path.Combine(Path.GetTempPath(), $"deepagents-console-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);
        Directory.CreateDirectory(repositoryPath);

        try
        {
            var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(
                [
                    "--test-provider-full",
                    "--workspace", workspacePath,
                    "--repo", repositoryPath,
                    "--model", "glm-5.1",
                    "--name", "deepagents-bot",
                    "--description", "DeepAgents test bot",
                    "--skill", "./skills",
                    "--memory", "./AGENTS.md",
                    "--executable", "/tmp/deepagents",
                    "--arg", "--debug"
                ],
                DeepAgentsConsoleDefinition.Instance,
                runner,
                output);

            exitCode.ShouldBe(0);
            provider.ReceivedOptions.Count.ShouldBe(5);
            provider.ReceivedOptions[0].Model.ShouldBe("glm-5.1");
            provider.ReceivedOptions[0].WorkingDirectory.ShouldBe(workspacePath);
            provider.ReceivedOptions[0].WorkspaceRoot.ShouldBe(workspacePath);
            provider.ReceivedOptions[0].AgentName.ShouldBe("deepagents-bot");
            provider.ReceivedOptions[0].AgentDescription.ShouldBe("DeepAgents test bot");
            provider.ReceivedOptions[0].SkillsDirectories.ShouldBe(["./skills"]);
            provider.ReceivedOptions[0].MemoryFiles.ShouldBe(["./AGENTS.md"]);
            provider.ReceivedOptions[0].ExecutablePath.ShouldBe("/tmp/deepagents");
            provider.ReceivedOptions[0].ExtraArguments.ShouldBe(["--debug"]);
            provider.ReceivedOptions[3].SessionId.ShouldNotBeNullOrWhiteSpace();
            provider.ReceivedOptions[4].WorkspaceRoot.ShouldBe(repositoryPath);
            output.ToString().ShouldContain("[PASS] deepagents / Repository Summary");
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task DispatchAsync_includes_bypass_network_scenario_when_mode_id_requests_bypass()
    {
        var provider = new FakeDeepAgentsProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new DeepAgentsConsoleRunner(DeepAgentsConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(
            [
                "--test-provider-full",
                "--mode-id", "bypass"
            ],
            DeepAgentsConsoleDefinition.Instance,
            runner,
            output);

        exitCode.ShouldBe(0);
        provider.ReceivedOptions.Count.ShouldBe(5);
        provider.ReceivedOptions.Select(static options => options.ModeId).ShouldBe(
        [
            "bypassPermissions",
            "bypassPermissions",
            "bypassPermissions",
            "bypassPermissions",
            "bypassPermissions"
        ]);
        output.ToString().ShouldContain("[PASS] deepagents / Bypass Bash Ping");
        output.ToString().ShouldContain("Summary: 5/5 passed");
    }

    [Fact]
    public async Task DispatchAsync_appends_toolcall_scenarios_when_enabled()
    {
        var provider = new FakeDeepAgentsProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new DeepAgentsConsoleRunner(DeepAgentsConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(
            [
                "--test-provider-full",
                "--toolcall"
            ],
            DeepAgentsConsoleDefinition.Instance,
            runner,
            output);

        exitCode.ShouldBe(0);
        provider.ReceivedOptions.Count.ShouldBe(7);
        var rendered = output.ToString();
        rendered.ShouldContain("[PASS] deepagents / Toolcall Parsing");
        rendered.ShouldContain("[PASS] deepagents / Toolcall Failure");
        rendered.ShouldContain("[PASS] deepagents / Toolcall Mixed Transcript");
        rendered.ShouldContain("Summary: 7/7 passed");
    }

    [Fact]
    public async Task DispatchAsync_toolcall_case_filters_to_the_named_scenario()
    {
        var provider = new FakeDeepAgentsProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new DeepAgentsConsoleRunner(DeepAgentsConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(
            [
                "--test-provider-full",
                "--toolcall-case", "mixed"
            ],
            DeepAgentsConsoleDefinition.Instance,
            runner,
            output);

        exitCode.ShouldBe(0);
        provider.ReceivedOptions.Count.ShouldBe(5);
        var rendered = output.ToString();
        rendered.ShouldContain("[PASS] deepagents / Toolcall Mixed Transcript");
        rendered.ShouldNotContain("Toolcall Parsing");
        rendered.ShouldNotContain("Toolcall Failure");
        rendered.ShouldContain("Summary: 5/5 passed");
    }

    [Fact]
    public async Task DispatchAsync_reports_available_toolcall_cases_when_selection_is_unknown()
    {
        var provider = new FakeDeepAgentsProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new DeepAgentsConsoleRunner(DeepAgentsConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(
            [
                "--test-provider-full",
                "--toolcall-case", "missing"
            ],
            DeepAgentsConsoleDefinition.Instance,
            runner,
            output);

        exitCode.ShouldBe(1);
        var rendered = output.ToString();
        rendered.ShouldContain("Unknown DeepAgents toolcall case 'missing'");
        rendered.ShouldContain("parsing, failure, mixed");
    }

    [Fact]
    public async Task DispatchAsync_renders_verbose_toolcall_trace_details()
    {
        var provider = new FakeDeepAgentsProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new DeepAgentsConsoleRunner(DeepAgentsConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(
            [
                "--test-provider-full",
                "--toolcall-case", "parsing",
                "--verbose"
            ],
            DeepAgentsConsoleDefinition.Instance,
            runner,
            output);

        exitCode.ShouldBe(0);
        var rendered = output.ToString();
        rendered.ShouldContain("LifecycleTrace: session.started -> assistant -> tool.call -> tool.permission -> tool.update -> tool.completed -> assistant -> terminal.completed");
        rendered.ShouldContain("RawMessageTypes: session.started -> assistant -> tool.call -> tool.permission -> tool.update -> tool.completed -> assistant -> terminal.completed");
        rendered.ShouldContain("Tool[1]: stage=tool.call, name=bash, id=tool-parse-1");
        rendered.ShouldContain("Tool[2]: stage=tool.permission, name=bash, id=tool-parse-1");
        rendered.ShouldContain("Tool[4]: stage=tool.completed, name=bash, id=tool-parse-1");
    }

    private sealed class FakeDeepAgentsProvider : ICliProvider<DeepAgentsOptions>
    {
        private readonly Dictionary<string, string> _sessionSecrets = [];

        public string Name => "deepagents";

        public bool IsAvailable => true;

        public List<DeepAgentsOptions> ReceivedOptions { get; } = [];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<CliProviderTestResult> PingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CliProviderTestResult
            {
                ProviderName = Name,
                Success = true,
                Version = "deepagents-test-0.1.7"
            });
        }

        public async IAsyncEnumerable<CliMessage> ExecuteAsync(
            DeepAgentsOptions options,
            string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReceivedOptions.Add(options);
            var sessionId = options.SessionId ?? $"session-{ReceivedOptions.Count}";
            var lifecycleType = options.SessionId is null ? "session.started" : "session.resumed";

            yield return CreateMessage(
                lifecycleType,
                new
                {
                    type = lifecycleType,
                    session_id = sessionId
                });

            if (TryGetToolcallTranscript(prompt, sessionId, out var toolcallMessages))
            {
                foreach (var message in toolcallMessages)
                {
                    yield return message;
                }

                await Task.Yield();
                yield break;
            }

            var response = BuildResponse(prompt, options, sessionId);
            foreach (var chunk in SplitResponse(response))
            {
                yield return CreateMessage(
                    "assistant",
                    new
                    {
                        type = "assistant",
                        session_id = sessionId,
                        text = chunk
                    });
            }

            yield return CreateMessage(
                "terminal.completed",
                new
                {
                    type = "terminal.completed",
                    session_id = sessionId,
                    stop_reason = "end_turn"
                });

            await Task.Yield();
        }

        private static bool TryGetToolcallTranscript(
            string prompt,
            string sessionId,
            out IReadOnlyList<CliMessage> messages)
        {
            if (prompt.Contains("successful lifecycle validation", StringComparison.OrdinalIgnoreCase) ||
                prompt.Contains("dotnet --version", StringComparison.OrdinalIgnoreCase))
            {
                messages =
                [
                    CreateMessage("assistant", new
                    {
                        type = "assistant",
                        session_id = sessionId,
                        text = "Preparing tool call."
                    }),
                    CreateMessage("tool.call", new
                    {
                        type = "tool.call",
                        session_id = sessionId,
                        tool_name = "bash",
                        tool_call_id = "tool-parse-1",
                        update = new
                        {
                            kind = "tool_call",
                            toolName = "bash",
                            toolCallId = "tool-parse-1",
                            summary = "ping -c 1 1.1.1.1"
                        }
                    }),
                    CreateMessage("tool.permission", new
                    {
                        type = "tool.permission",
                        session_id = sessionId,
                        tool_call_id = "tool-parse-1",
                        title = "Approve bash command"
                    }),
                    CreateMessage("tool.update", new
                    {
                        type = "tool.update",
                        session_id = sessionId,
                        tool_name = "bash",
                        tool_call_id = "tool-parse-1",
                        update = new
                        {
                            kind = "tool_call_update",
                            toolName = "bash",
                            toolCallId = "tool-parse-1",
                            status = "running",
                            summary = "tool is executing"
                        }
                    }),
                    CreateMessage("tool.completed", new
                    {
                        type = "tool.completed",
                        session_id = sessionId,
                        tool_name = "bash",
                        tool_call_id = "tool-parse-1",
                        status = "completed",
                        update = new
                        {
                            kind = "tool_call_update",
                            toolName = "bash",
                            toolCallId = "tool-parse-1",
                            status = "completed",
                            summary = "ping finished"
                        }
                    }),
                    CreateMessage("assistant", new
                    {
                        type = "assistant",
                        session_id = sessionId,
                        text = "Tool completed successfully."
                    }),
                    CreateMessage("terminal.completed", new
                    {
                        type = "terminal.completed",
                        session_id = sessionId,
                        stop_reason = "end_turn"
                    })
                ];
                return true;
            }

            if (prompt.Contains("failure lifecycle fixture", StringComparison.OrdinalIgnoreCase))
            {
                messages =
                [
                    CreateMessage("assistant", new
                    {
                        type = "assistant",
                        session_id = sessionId,
                        text = "Attempting privileged tool call."
                    }),
                    CreateMessage("tool.call", new
                    {
                        type = "tool.call",
                        session_id = sessionId,
                        tool_name = "bash",
                        tool_call_id = "tool-fail-1",
                        update = new
                        {
                            kind = "tool_call",
                            toolName = "bash",
                            toolCallId = "tool-fail-1",
                            summary = "cat /root/secret"
                        }
                    }),
                    CreateMessage("tool.failed", new
                    {
                        type = "tool.failed",
                        session_id = sessionId,
                        tool_name = "bash",
                        tool_call_id = "tool-fail-1",
                        status = "failed",
                        update = new
                        {
                            kind = "tool_call_update",
                            toolName = "bash",
                            toolCallId = "tool-fail-1",
                            status = "failed",
                            message = "permission denied"
                        }
                    }),
                    CreateMessage("terminal.failed", new
                    {
                        type = "terminal.failed",
                        session_id = sessionId,
                        message = "permission denied"
                    })
                ];
                return true;
            }

            if (prompt.Contains("mixed transcript fixture", StringComparison.OrdinalIgnoreCase))
            {
                messages =
                [
                    CreateMessage("assistant", new
                    {
                        type = "assistant",
                        session_id = sessionId,
                        text = "Preparing tool call. "
                    }),
                    CreateMessage("tool.call", new
                    {
                        type = "tool.call",
                        session_id = sessionId,
                        tool_name = "grep",
                        tool_call_id = "tool-mixed-1",
                        update = new
                        {
                            kind = "tool_call",
                            toolName = "grep",
                            toolCallId = "tool-mixed-1",
                            summary = "grep -n TODO src"
                        }
                    }),
                    CreateMessage("assistant", new
                    {
                        type = "assistant",
                        session_id = sessionId,
                        text = "tool is still running. "
                    }),
                    CreateMessage("tool.update", new
                    {
                        type = "tool.update",
                        session_id = sessionId,
                        tool_name = "grep",
                        tool_call_id = "tool-mixed-1",
                        update = new
                        {
                            kind = "tool_call_update",
                            toolName = "grep",
                            toolCallId = "tool-mixed-1",
                            status = "running",
                            summary = "scanned 12 files"
                        }
                    }),
                    CreateMessage("assistant", new
                    {
                        type = "assistant",
                        session_id = sessionId,
                        text = "tool output received. "
                    }),
                    CreateMessage("tool.completed", new
                    {
                        type = "tool.completed",
                        session_id = sessionId,
                        tool_name = "grep",
                        tool_call_id = "tool-mixed-1",
                        status = "completed",
                        update = new
                        {
                            kind = "tool_call_update",
                            toolName = "grep",
                            toolCallId = "tool-mixed-1",
                            status = "completed",
                            summary = "found 3 TODO entries"
                        }
                    }),
                    CreateMessage("assistant", new
                    {
                        type = "assistant",
                        session_id = sessionId,
                        text = "assistant wrap-up after tool completed."
                    }),
                    CreateMessage("terminal.completed", new
                    {
                        type = "terminal.completed",
                        session_id = sessionId,
                        stop_reason = "end_turn"
                    })
                ];
                return true;
            }

            messages = [];
            return false;
        }

        private string BuildResponse(string prompt, DeepAgentsOptions options, string sessionId)
        {
            if (prompt.Contains("Reply with exactly the word 'pong'", StringComparison.OrdinalIgnoreCase))
            {
                return "pong";
            }

            if (prompt.Contains("Give two short bullet points about software testing", StringComparison.OrdinalIgnoreCase))
            {
                return "Advantage: catches regressions early.\nTrade-off: requires ongoing maintenance.";
            }

            if (prompt.Contains("Remember the secret word:", StringComparison.OrdinalIgnoreCase))
            {
                var marker = "Remember the secret word:";
                var startIndex = prompt.IndexOf(marker, StringComparison.OrdinalIgnoreCase) + marker.Length;
                var endIndex = prompt.IndexOf('.', startIndex);
                var secret = prompt[startIndex..endIndex].Trim();
                _sessionSecrets[sessionId] = secret;
                return "ACK";
            }

            if (prompt.Contains("What was the secret word", StringComparison.OrdinalIgnoreCase))
            {
                return _sessionSecrets.TryGetValue(sessionId, out var secret) ? secret : "UNKNOWN";
            }

            if (prompt.Contains("ping -c 1 1.1.1.1", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(options.ModeId, "bypassPermissions", StringComparison.Ordinal)
                    ? "PING_OK"
                    : "PING_FAIL";
            }

            if (prompt.Contains("repository summary", StringComparison.OrdinalIgnoreCase) ||
                prompt.Contains("Provide a brief repository summary", StringComparison.OrdinalIgnoreCase))
            {
                var repoPath = options.WorkspaceRoot ?? options.WorkingDirectory ?? Directory.GetCurrentDirectory();
                var repoName = new DirectoryInfo(repoPath).Name;
                return $"{repoName}: src, tests, docs, .cs files, and JSON configs are present.";
            }

            return $"Workspace={(options.WorkspaceRoot ?? options.WorkingDirectory ?? "(none)")}; Agent={options.AgentName ?? "deepagents"}";
        }

        private static CliMessage CreateMessage(string type, object payload)
            => new(type, JsonSerializer.SerializeToElement(payload));

        private static IReadOnlyList<string> SplitResponse(string response)
        {
            if (response.Length <= 32)
            {
                return [response];
            }

            var midpoint = response.Length / 2;
            return [response[..midpoint], response[midpoint..]];
        }
    }
}
