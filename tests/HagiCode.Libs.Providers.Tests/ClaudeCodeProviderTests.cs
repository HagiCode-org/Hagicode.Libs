using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Collections.ObjectModel;
using Shouldly;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.ClaudeCode;

namespace HagiCode.Libs.Providers.Tests;

public sealed class ClaudeCodeProviderTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";
    private const string WindowsTestLocaleEnvironmentVariable = "HAGICODE_WINDOWS_TEST_LOCALE";
    private static readonly string[] ClaudeExecutableCandidates = ["claude", "claude-code"];

    [Fact]
    public void BuildCommandArguments_includes_expected_switches()
    {
        var provider = CreateProvider();
        var arguments = provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            Model = "claude-sonnet",
            MaxTurns = 3,
            SystemPrompt = "system",
            AllowedTools = ["Read", "Write"],
            DisallowedTools = ["Bash"],
            PermissionMode = "plan",
            SessionId = "session-id",
            AddDirectories = ["/tmp/project"],
            ExtraArgs = new Dictionary<string, string?> { ["dangerously-skip-permissions"] = null }
        });

        arguments.ShouldContain("--output-format", "stream-json");
        arguments.ShouldContain("--model", "claude-sonnet");
        arguments.ShouldContain("--system-prompt", "system");
        arguments.ShouldContain("--max-turns", "3");
        arguments.ShouldContain("--session-id", "session-id");
        arguments.ShouldContain("--add-dir", "/tmp/project");
    }

    [Fact]
    public void BuildCommandArguments_omits_session_id_when_resume_is_specified()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            Resume = "resume-id",
            SessionId = "session-id"
        });

        arguments.ShouldContain("--resume", "resume-id");
        arguments.ShouldNotContain("--session-id");
    }

    [Fact]
    public void BuildCommandArguments_trims_optional_values_and_omits_empty_after_trim_pairs()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            Model = "  Claude Sonnet 4.5  ",
            AddDirectories = ["  /tmp/my repo  ", "   "],
            ExtraArgs = new Dictionary<string, string?>
            {
                ["settings"] = "  balanced mode  ",
                ["ignored"] = "   ",
                ["dangerously-skip-permissions"] = null
            }
        });

        arguments.ShouldBe(
        [
            "--output-format",
            "stream-json",
            "--verbose",
            "--input-format",
            "stream-json",
            "--model",
            "Claude Sonnet 4.5",
            "--add-dir",
            "/tmp/my repo",
            "--settings",
            "balanced mode",
            "--dangerously-skip-permissions"
        ]);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("session-id", null)]
    [InlineData(null, "resume-id")]
    [InlineData("session-id", "resume-id")]
    public void BuildCommandArguments_never_emits_continue(string? sessionId, string? resume)
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            SessionId = sessionId,
            Resume = resume
        });

        arguments.ShouldNotContain("--continue");
        arguments.ShouldNotContain("-c");
    }

    [Fact]
    public void BuildCommandArguments_sends_only_resume_when_resume_and_session_id_are_both_set()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            Resume = "resume-id",
            SessionId = "resume-id"
        });

        arguments.ShouldContain("--resume", "resume-id");
        arguments.ShouldNotContain("--session-id");
        arguments.ShouldNotContain("--continue");
    }

    [Theory]
    [InlineData("continue")]
    [InlineData("Continue")]
    [InlineData("CONTINUE")]
    public void BuildCommandArguments_rejects_continue_in_extra_args(string key)
    {
        var provider = CreateProvider();

        var exception = Should.Throw<ArgumentException>(() => provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            ExtraArgs = new Dictionary<string, string?> { [key] = null }
        }));

        exception.Message.ShouldContain("SessionId");
        exception.Message.ShouldContain("Resume");
    }

    [Fact]
    public void BuildCommandArguments_forwards_append_system_prompt_unchanged()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            WorkingDirectory = "/repo/project",
            AppendSystemPrompt = "Keep responses terse."
        });

        arguments.ShouldContain("--append-system-prompt", "Keep responses terse.");
        arguments.Count(static argument => argument == "--append-system-prompt").ShouldBe(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void BuildCommandArguments_omits_append_system_prompt_when_not_supplied(string? appendSystemPrompt)
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            WorkingDirectory = "/repo/project",
            AppendSystemPrompt = appendSystemPrompt
        });

        arguments.ShouldNotContain("--append-system-prompt");
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public void BuildCommandArguments_forwards_each_supported_effort_level(string level)
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            Effort = level
        });

        arguments.ShouldContain("--effort", level);
    }

    [Fact]
    public void BuildCommandArguments_normalizes_effort_casing_and_whitespace()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            Effort = "  XHigh "
        });

        arguments.ShouldContain("--effort", "xhigh");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildCommandArguments_omits_effort_when_value_is_empty(string? effort)
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            Effort = effort
        });

        arguments.ShouldNotContain("--effort");
    }

    [Fact]
    public void BuildCommandArguments_throws_for_unsupported_effort_level()
    {
        var provider = CreateProvider();

        var exception = Should.Throw<ArgumentException>(() => provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            Effort = "ultra"
        }));

        exception.Message.ShouldContain("ultra");
        exception.Message.ShouldContain("low");
        exception.Message.ShouldContain("max");
    }

    [Fact]
    public void AllowedEffortLevels_contains_exactly_the_supported_levels()
    {
        var expected = new[]
        {
            "low",
            "medium",
            "high",
            "xhigh",
            "max"
        };

        ClaudeCodeProvider.AllowedEffortLevels
            .Select(static level => level.ToLowerInvariant())
            .OrderBy(static level => level, StringComparer.Ordinal)
            .ShouldBe(expected.OrderBy(static level => level, StringComparer.Ordinal));

        // Matching is case-insensitive.
        ClaudeCodeProvider.AllowedEffortLevels.Contains("XHIGH").ShouldBeTrue();
        ClaudeCodeProvider.AllowedEffortLevels.Contains("Max").ShouldBeTrue();
        ClaudeCodeProvider.AllowedEffortLevels.Contains("ULTRA").ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_forwards_effort_to_the_launched_process_arguments()
    {
        var provider = CreateProvider();

        await foreach (var _ in provider.ExecuteAsync(
                           new ClaudeCodeOptions
                           {
                               Effort = "XHigh",
                               SessionId = "effort-session"
                           },
                           "hello"))
        {
        }

        provider.LastStartContext.ShouldNotBeNull();
        provider.LastStartContext.Arguments.ShouldContain("--effort", "xhigh");
    }

    [Fact]
    public async Task ExecuteAsync_omits_effort_argument_when_not_configured()
    {
        var provider = CreateProvider();

        await foreach (var _ in provider.ExecuteAsync(
                           new ClaudeCodeOptions
                           {
                               SessionId = "effort-session"
                           },
                           "hello"))
        {
        }

        provider.LastStartContext.ShouldNotBeNull();
        provider.LastStartContext.Arguments.ShouldNotContain("--effort");
    }

    [Theory]
    [InlineData("summarized")]
    [InlineData("omitted")]
    [InlineData("highlights")]
    public void BuildCommandArguments_forwards_each_supported_thinking_display_mode(string mode)
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            ThinkingDisplay = mode
        });

        arguments.ShouldContain("--thinking-display", mode);
        arguments.Count(static argument => argument == "--thinking-display").ShouldBe(1);
    }

    [Fact]
    public void BuildCommandArguments_normalizes_thinking_display_casing_and_whitespace()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            ThinkingDisplay = "  Summarized "
        });

        arguments.ShouldContain("--thinking-display", "summarized");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildCommandArguments_omits_thinking_display_when_value_is_empty(string? thinkingDisplay)
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            ThinkingDisplay = thinkingDisplay
        });

        arguments.ShouldNotContain("--thinking-display");
    }

    [Fact]
    public void BuildCommandArguments_throws_for_unsupported_thinking_display_mode()
    {
        var provider = CreateProvider();

        var exception = Should.Throw<ArgumentException>(() => provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            ThinkingDisplay = "verbose"
        }));

        exception.Message.ShouldContain("verbose");
        exception.Message.ShouldContain("summarized");
        exception.Message.ShouldContain("omitted");
        exception.Message.ShouldContain("highlights");
    }

    [Fact]
    public void AllowedThinkingDisplayModes_contains_exactly_the_supported_modes()
    {
        ClaudeCodeProvider.AllowedThinkingDisplayModes
            .Select(static mode => mode.ToLowerInvariant())
            .OrderBy(static mode => mode, StringComparer.Ordinal)
            .ShouldBe(["highlights", "omitted", "summarized"]);

        ClaudeCodeProvider.AllowedThinkingDisplayModes.Contains("SUMMARIZED").ShouldBeTrue();
        ClaudeCodeProvider.AllowedThinkingDisplayModes.Contains("verbose").ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_rejects_unsupported_thinking_display_before_starting_any_process()
    {
        var provider = CreateProvider();

        await Should.ThrowAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in provider.ExecuteAsync(
                               new ClaudeCodeOptions
                               {
                                   ExecutablePath = UniqueExecutablePath(),
                                   ThinkingDisplay = "verbose"
                               },
                               "hello"))
            {
            }
        });

        provider.CreatedTransportCount.ShouldBe(0);
        provider.SentMessages.ShouldBeEmpty();
    }

    [Fact]
    public void BuildCommandArguments_does_not_derive_thinking_display_from_the_settings_argument()
    {
        var provider = CreateProvider();
        const string settings = """{"showThinkingSummaries":true}""";

        var arguments = provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            ExtraArgs = new Dictionary<string, string?> { ["settings"] = settings }
        });

        arguments.ShouldContain("--settings", settings);
        arguments.ShouldNotContain("--thinking-display");
    }

    [Fact]
    public async Task ExecuteAsync_forwards_thinking_display_to_the_launched_process_arguments()
    {
        var provider = CreateProvider();

        await foreach (var _ in provider.ExecuteAsync(
                           new ClaudeCodeOptions
                           {
                               ExecutablePath = UniqueExecutablePath(),
                               ThinkingDisplay = "Summarized"
                           },
                           "hello"))
        {
        }

        provider.LastStartContext.ShouldNotBeNull();
        provider.LastStartContext.Arguments.ShouldContain("--thinking-display", "summarized");
    }

    [Fact]
    public void RemoveThinkingDisplayArguments_removes_typed_extra_and_bare_flags_but_keeps_everything_else()
    {
        var provider = CreateProvider();
        var arguments = provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            Model = "claude-sonnet",
            ThinkingDisplay = "summarized",
            ExtraArgs = new Dictionary<string, string?>
            {
                ["thinking-display"] = "omitted",
                ["settings"] = "balanced mode",
                ["dangerously-skip-permissions"] = null
            }
        });
        arguments.Count(static argument => argument == "--thinking-display").ShouldBe(2);

        var filtered = ClaudeCodeProvider.RemoveThinkingDisplayArguments(arguments);

        filtered.ShouldNotContain("--thinking-display");
        filtered.ShouldNotContain("summarized");
        filtered.ShouldNotContain("omitted");
        filtered.ShouldBe(
        [
            "--output-format",
            "stream-json",
            "--verbose",
            "--input-format",
            "stream-json",
            "--model",
            "claude-sonnet",
            "--settings",
            "balanced mode",
            "--dangerously-skip-permissions"
        ]);

        // A bare switch must not swallow the next flag.
        ClaudeCodeProvider.RemoveThinkingDisplayArguments(["--thinking-display", "--model", "claude-sonnet"])
            .ShouldBe(["--model", "claude-sonnet"]);
    }

    [Fact]
    public async Task ExecuteAsync_relaunches_once_without_thinking_display_when_the_cli_rejects_it_on_receive()
    {
        var executablePath = UniqueExecutablePath();
        var provider = CreateProvider(
            messageBatches: [[], DefaultSuccessBatch()],
            transportFailures: [new TransportFailure(OnReceive: ThinkingDisplayRejection())]);

        var messages = await CollectAsync(provider, executablePath, "summarized");

        messages.Select(static message => message.Type).ShouldBe(["assistant", "result"]);
        provider.CreatedTransportCount.ShouldBe(2);
        provider.DisposedTransportCount.ShouldBe(2);
        provider.StartContexts[0].Arguments.ShouldContain("--thinking-display", "summarized");
        provider.StartContexts[1].Arguments.ShouldNotContain("--thinking-display");
        provider.StartContexts[1].Arguments
            .ShouldBe(ClaudeCodeProvider.RemoveThinkingDisplayArguments(provider.StartContexts[0].Arguments));
        provider.StartContexts[1].ExecutablePath.ShouldBe(executablePath);
        provider.StartContexts[1].WorkingDirectory.ShouldBe(provider.StartContexts[0].WorkingDirectory);
    }

    [Fact]
    public async Task ExecuteAsync_relaunches_once_without_thinking_display_when_the_cli_rejects_it_on_send()
    {
        var provider = CreateProvider(
            messageBatches: [DefaultSuccessBatch()],
            transportFailures: [new TransportFailure(OnSend: ThinkingDisplayRejection())]);

        var messages = await CollectAsync(provider, UniqueExecutablePath(), "summarized");

        messages.Select(static message => message.Type).ShouldBe(["assistant", "result"]);
        provider.CreatedTransportCount.ShouldBe(2);
        provider.StartContexts[1].Arguments.ShouldNotContain("--thinking-display");
        provider.SentMessages.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ExecuteAsync_does_not_ask_a_rejecting_executable_for_thinking_display_again()
    {
        var executablePath = UniqueExecutablePath();
        var provider = CreateProvider(
            messageBatches: [[], DefaultSuccessBatch(), DefaultSuccessBatch()],
            transportFailures: [new TransportFailure(OnReceive: ThinkingDisplayRejection())]);

        await CollectAsync(provider, executablePath, "summarized");
        provider.CreatedTransportCount.ShouldBe(2);

        await CollectAsync(provider, executablePath, "summarized");

        provider.CreatedTransportCount.ShouldBe(3);
        provider.StartContexts[2].Arguments.ShouldNotContain("--thinking-display");
    }

    [Fact]
    public async Task ExecuteAsync_keeps_sending_thinking_display_to_other_executables_after_one_rejected_it()
    {
        var rejectingPath = UniqueExecutablePath();
        var otherPath = UniqueExecutablePath();
        var provider = CreateProvider(
            messageBatches: [[], DefaultSuccessBatch(), DefaultSuccessBatch()],
            transportFailures: [new TransportFailure(OnReceive: ThinkingDisplayRejection())]);

        await CollectAsync(provider, rejectingPath, "summarized");
        await CollectAsync(provider, otherPath, "summarized");

        provider.StartContexts[2].ExecutablePath.ShouldBe(otherPath);
        provider.StartContexts[2].Arguments.ShouldContain("--thinking-display", "summarized");
    }

    [Fact]
    public async Task ExecuteAsync_does_not_mask_unrelated_launch_failures_with_a_relaunch()
    {
        var executablePath = UniqueExecutablePath();
        var failure = new InvalidOperationException(
            "The subprocess exited unexpectedly with code 1: error: unknown option '--bogus'");
        var provider = CreateProvider(
            messageBatches: [[], DefaultSuccessBatch()],
            transportFailures: [new TransportFailure(OnReceive: failure)]);

        var thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => CollectAsync(provider, executablePath, "summarized"));

        thrown.ShouldBeSameAs(failure);
        provider.CreatedTransportCount.ShouldBe(1);
        provider.DisposedTransportCount.ShouldBe(1);

        // The executable must not have been recorded as rejecting the flag.
        await CollectAsync(provider, executablePath, "summarized");
        provider.StartContexts[1].Arguments.ShouldContain("--thinking-display", "summarized");
    }

    [Fact]
    public async Task ExecuteAsync_does_not_relaunch_when_the_flag_was_never_sent()
    {
        var failure = ThinkingDisplayRejection();
        var provider = CreateProvider(
            messageBatches: [[], DefaultSuccessBatch()],
            transportFailures: [new TransportFailure(OnReceive: failure)]);

        var thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => CollectAsync(provider, UniqueExecutablePath(), thinkingDisplay: null));

        thrown.ShouldBeSameAs(failure);
        provider.CreatedTransportCount.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_does_not_relaunch_after_a_message_was_already_streamed()
    {
        var executablePath = UniqueExecutablePath();
        var failure = ThinkingDisplayRejection();
        var provider = CreateProvider(
            messageBatches: [[StreamMessage("system", new { subtype = "init" })]],
            transportFailures: [new TransportFailure(OnReceive: failure)]);
        var messages = new List<CliMessage>();

        var thrown = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var message in provider.ExecuteAsync(
                               new ClaudeCodeOptions { ExecutablePath = executablePath, ThinkingDisplay = "summarized" },
                               "hello"))
            {
                messages.Add(message);
            }
        });

        thrown.ShouldBeSameAs(failure);
        messages.ShouldHaveSingleItem();
        provider.CreatedTransportCount.ShouldBe(1);
        provider.SentMessages.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ExecuteAsync_surfaces_a_terminal_error_message_after_output_without_relaunching()
    {
        var provider = CreateProvider(
            messageBatches:
            [
                [
                    StreamMessage("system", new { subtype = "init" }),
                    new CliMessage("error", JsonSerializer.SerializeToElement(new { type = "error", message = "unknown option '--thinking-display'" }))
                ]
            ]);

        var messages = await CollectAsync(provider, UniqueExecutablePath(), "summarized");

        DescribeTypes(messages).ShouldBe(["system:init", "error"]);
        provider.CreatedTransportCount.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_real_subprocess_that_rejects_thinking_display_completes_through_the_fallback()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Mimics an older CLI: commander-style rejection of the unknown flag before the prompt is read. The transport
        // reports that exit from SendAsync or ReceiveAsync depending on timing, and both must be handled. The first run
        // goes through the fallback; later runs hit the cache and launch without the flag.
        var directory = Path.Combine(Path.GetTempPath(), $"hagicode-fake-claude-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var scriptPath = Path.Combine(directory, "claude");
            await File.WriteAllTextAsync(
                scriptPath,
                """
                #!/bin/sh
                for arg in "$@"; do
                  if [ "$arg" = "--thinking-display" ]; then
                    echo "error: unknown option '--thinking-display'" >&2
                    exit 1
                  fi
                done
                read -r line
                echo '{"type":"assistant","message":{"content":[]}}'
                echo '{"type":"result","is_error":false}'
                """.ReplaceLineEndings("\n"));
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            await using var provider = new ClaudeCodeProvider(
                new CliExecutableResolver(),
                new CliProcessManager(),
                new StaticRuntimeEnvironmentResolver(new Dictionary<string, string?>()));

            for (var run = 0; run < 3; run++)
            {
                var messages = await CollectAsync(provider, scriptPath, "summarized", directory);

                messages.Select(static message => message.Type).ShouldBe(["assistant", "result"]);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_runs_at_most_one_replacement_attempt()
    {
        var provider = CreateProvider(
            messageBatches: [[], []],
            transportFailures:
            [
                new TransportFailure(OnReceive: ThinkingDisplayRejection()),
                new TransportFailure(OnReceive: ThinkingDisplayRejection())
            ]);

        await Should.ThrowAsync<InvalidOperationException>(
            () => CollectAsync(provider, UniqueExecutablePath(), "summarized"));

        provider.CreatedTransportCount.ShouldBe(2);
    }

    [Fact]
    public async Task ExecuteAsync_uses_custom_executable_and_streams_messages()
    {
        var provider = CreateProvider();
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new ClaudeCodeOptions
                           {
                               ExecutablePath = "/custom/claude",
                               SessionId = "session-1",
                               ApiKey = "token"
                           },
                           "hello"))
        {
            messages.Add(message);
        }

        provider.LastStartContext!.ExecutablePath.ShouldBe("/custom/claude");
        provider.LastStartContext.EnvironmentVariables!["ANTHROPIC_AUTH_TOKEN"].ShouldBe("token");
        provider.LastStartContext.EnvironmentVariables["CLAUDE_CODE_ENTRYPOINT"].ShouldBe("sdk-csharp");
        messages.Select(static message => message.Type).ShouldBe(["assistant", "result"]);
        provider.SentMessages.ShouldHaveSingleItem();
        provider.SentMessages[0].Content.GetProperty("message").GetProperty("content").GetString().ShouldBe("hello");
    }

    [Fact]
    public async Task ExecuteAsync_starts_the_process_in_the_working_directory()
    {
        var provider = CreateProvider();

        await foreach (var _ in provider.ExecuteAsync(
                           new ClaudeCodeOptions
                           {
                               WorkingDirectory = "/repo/project",
                               SessionId = "session-1"
                           },
                           "hello"))
        {
        }

        provider.LastStartContext.ShouldNotBeNull();
        provider.LastStartContext.WorkingDirectory.ShouldBe("/repo/project");
    }

    [Fact]
    public async Task ExecuteAsync_uses_utf8_without_bom_for_stream_json_transport()
    {
        var provider = CreateProvider();

        await foreach (var _ in provider.ExecuteAsync(new ClaudeCodeOptions(), "hello"))
        {
        }

        provider.LastStartContext.ShouldNotBeNull();
        provider.LastStartContext.InputEncoding.WebName.ShouldBe(Encoding.UTF8.WebName);
        provider.LastStartContext.InputEncoding.GetPreamble().ShouldBeEmpty();
        provider.LastStartContext.OutputEncoding.WebName.ShouldBe(Encoding.UTF8.WebName);
        provider.LastStartContext.OutputEncoding.GetPreamble().ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_creates_fresh_transport_for_each_invocation_even_when_session_id_matches()
    {
        var provider = CreateProvider();

        await foreach (var _ in provider.ExecuteAsync(
                           new ClaudeCodeOptions
                           {
                               SessionId = "session-1",
                               WorkingDirectory = "/tmp/project"
                           },
                           "hello"))
        {
        }

        await foreach (var _ in provider.ExecuteAsync(
                           new ClaudeCodeOptions
                           {
                               SessionId = "session-1",
                               WorkingDirectory = "/tmp/project"
                           },
                           "follow up"))
        {
        }

        provider.CreatedTransportCount.ShouldBe(2);
        provider.DisposedTransportCount.ShouldBe(2);
        provider.SentMessages.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ExecuteAsync_emits_one_shot_debug_metadata_without_pooling_fields()
    {
        var provider = CreateProvider();
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new ClaudeCodeOptions
                           {
                               SessionId = "session-1",
                               WorkingDirectory = "/tmp/project"
                           },
                           "hello"))
        {
            messages.Add(message);
        }

        messages[0].Content.GetProperty("requested_session_id").GetString().ShouldBe("session-1");
        messages[0].Content.GetProperty("runtime_fingerprint").GetString().ShouldNotBeNullOrWhiteSpace();
        messages[0].Content.GetProperty("event_timestamp").GetString().ShouldNotBeNullOrWhiteSpace();
        messages[0].Content.TryGetProperty("binding_key", out _).ShouldBeFalse();
        messages[0].Content.TryGetProperty("pool_fingerprint", out _).ShouldBeFalse();
        messages[0].Content.TryGetProperty("resume_mode", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_keeps_event_timestamp_unchanged_and_mirrors_it_into_payload()
    {
        var eventTime = new DateTimeOffset(2026, 10, 6, 14, 32, 5, TimeSpan.FromHours(8));
        var provider = CreateProvider(messageBatches:
        [
            [
                new CliMessage("assistant", JsonSerializer.SerializeToElement(new { type = "assistant" }))
                {
                    EventTimestamp = eventTime
                }
            ]
        ]);
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new ClaudeCodeOptions { SessionId = "session-1", WorkingDirectory = "/tmp/project" },
                           "hello"))
        {
            messages.Add(message);
        }

        messages[0].EventTimestamp.ShouldBe(eventTime.ToUniversalTime());
        messages[0].EventTimestamp.Offset.ShouldBe(TimeSpan.Zero);
        DateTimeOffset.Parse(messages[0].Content.GetProperty("event_timestamp").GetString()!)
            .ShouldBe(messages[0].EventTimestamp);
    }

    [Fact]
    public void CliMessage_stamps_read_time_once_in_utc_when_not_supplied()
    {
        var before = DateTimeOffset.UtcNow;
        var message = new CliMessage("assistant", JsonSerializer.SerializeToElement(new { type = "assistant" }));
        var after = DateTimeOffset.UtcNow;

        message.EventTimestamp.ShouldBeGreaterThanOrEqualTo(before);
        message.EventTimestamp.ShouldBeLessThanOrEqualTo(after);
        message.EventTimestamp.Offset.ShouldBe(TimeSpan.Zero);
        (message with { Content = message.Content }).EventTimestamp.ShouldBe(message.EventTimestamp);
    }

    [Theory]
    [InlineData("""{"type":"a","event_timestamp":"2026-10-06T06:32:05Z"}""", true)]
    [InlineData("""{"type":"a","timestamp":"2026-10-06T14:32:05+08:00"}""", true)]
    [InlineData("""{"type":"a","timestamp":"not a time"}""", false)]
    [InlineData("""{"type":"a","timestamp":"0001-01-01T00:00:00Z"}""", false)]
    [InlineData("""{"type":"a"}""", false)]
    public void TryReadPayloadTimestamp_accepts_only_valid_non_default_times(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);

        CliMessage.TryReadPayloadTimestamp(document.RootElement, out var timestamp).ShouldBe(expected);
        if (expected)
        {
            timestamp.ShouldBe(new DateTimeOffset(2026, 10, 6, 6, 32, 5, TimeSpan.Zero));
        }
    }

    [Fact]
    public async Task ExecuteAsync_surfaces_error_once_without_local_retry_when_session_context_is_available()
    {
        var provider = CreateProvider(messageBatches:
        [
            [
                new CliMessage("error", JsonSerializer.SerializeToElement(new { type = "error", message = "stream dropped" }))
            ]
        ]);
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new ClaudeCodeOptions
                           {
                               SessionId = "session-retry",
                               WorkingDirectory = "/tmp/project"
                           },
                           "continue the task"))
        {
            messages.Add(message);
        }

        messages.Select(static message => message.Type).ShouldBe(["error"]);
        provider.CreatedTransportCount.ShouldBe(1);
        provider.DisposedTransportCount.ShouldBe(1);
        provider.SentMessages.Count.ShouldBe(1);
        provider.SentMessages[0].Content.GetProperty("message").GetProperty("content").GetString().ShouldBe("continue the task");
    }

    [Fact]
    public async Task ExecuteAsync_surfaces_error_once_when_session_context_is_missing()
    {
        var provider = CreateProvider(messageBatches:
        [
            [
                new CliMessage("error", JsonSerializer.SerializeToElement(new { type = "error", message = "stream dropped" }))
            ]
        ]);
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new ClaudeCodeOptions
                           {
                               WorkingDirectory = "/tmp/project"
                           },
                           "no context"))
        {
            messages.Add(message);
        }

        messages.Select(static message => message.Type).ShouldBe(["error"]);
        provider.DisposedTransportCount.ShouldBe(1);
        provider.SentMessages.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ExecuteAsync_stops_at_single_turn_result_and_does_not_read_further()
    {
        var provider = CreateProvider(messageBatches:
        [
            [
                StreamMessage("system", new { subtype = "init" }),
                StreamMessage("assistant", new { }),
                StreamMessage("result", new { is_error = false }),
                StreamMessage("assistant", new { marker = "after-final-result" })
            ]
        ]);

        var messages = await CollectAsync(provider);

        DescribeTypes(messages).ShouldBe(["system:init", "assistant", "result"]);
    }

    [Fact]
    public async Task ExecuteAsync_keeps_streaming_past_launch_turn_result_while_subagent_is_running()
    {
        // Real stream order for `Agent` with is_backgrounded=true: the turn that launched the subagent ends with its own
        // "result" while the subagent is still working; the CLI reports back through task_notification and a follow-up turn.
        var provider = CreateProvider(messageBatches:
        [
            [
                StreamMessage("system", new { subtype = "init" }),
                StreamMessage("system", new { subtype = "background_tasks_changed", tasks = SubagentTasks("agent-1") }),
                StreamMessage("system", new { subtype = "task_started", task_id = "agent-1", task_type = "local_agent" }),
                StreamMessage("assistant", new { parent_tool_use_id = (string?)null }),
                StreamMessage("result", new { is_error = false, result = "agent is running in the background" }),
                StreamMessage("assistant", new { parent_tool_use_id = "toolu_1" }),
                StreamMessage("system", new { subtype = "task_notification", task_id = "agent-1", status = "completed" }),
                StreamMessage("system", new { subtype = "background_tasks_changed", tasks = SubagentTasks() }),
                StreamMessage("system", new { subtype = "init" }),
                StreamMessage("assistant", new { parent_tool_use_id = (string?)null }),
                StreamMessage("result", new { is_error = false, result = "DONE" }),
                StreamMessage("assistant", new { marker = "after-final-result" })
            ]
        ]);

        var messages = await CollectAsync(provider);

        DescribeTypes(messages).ShouldBe(
        [
            "system:init",
            "system:background_tasks_changed",
            "system:task_started",
            "assistant",
            "result",
            "assistant",
            "system:task_notification",
            "system:background_tasks_changed",
            "system:init",
            "assistant",
            "result"
        ]);
        messages[^1].Content.GetProperty("result").GetString().ShouldBe("DONE");
        provider.DisposedTransportCount.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_waits_for_follow_up_turn_result_when_subagent_finished_before_launch_turn_result()
    {
        // A fast subagent completes before the launch turn's "result" is flushed: the follow-up turn has already started
        // (second init) when the first "result" arrives, so the first "result" is not the final one.
        var provider = CreateProvider(messageBatches:
        [
            [
                StreamMessage("system", new { subtype = "init" }),
                StreamMessage("system", new { subtype = "background_tasks_changed", tasks = SubagentTasks("agent-1") }),
                StreamMessage("system", new { subtype = "task_notification", task_id = "agent-1", status = "completed" }),
                StreamMessage("system", new { subtype = "background_tasks_changed", tasks = SubagentTasks() }),
                StreamMessage("system", new { subtype = "init" }),
                StreamMessage("assistant", new { }),
                StreamMessage("result", new { is_error = false, result = "agent is running in the background" }),
                StreamMessage("result", new { is_error = false, result = "DONE" }),
                StreamMessage("assistant", new { marker = "after-final-result" })
            ]
        ]);

        var messages = await CollectAsync(provider);

        DescribeTypes(messages).Count(static type => type == "result").ShouldBe(2);
        messages[^1].Content.GetProperty("result").GetString().ShouldBe("DONE");
        messages.Any(static message => message.Content.TryGetProperty("marker", out _)).ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_does_not_wait_for_background_shell_tasks()
    {
        var provider = CreateProvider(messageBatches:
        [
            [
                StreamMessage("system", new { subtype = "init" }),
                StreamMessage(
                    "system",
                    new
                    {
                        subtype = "background_tasks_changed",
                        tasks = new[] { new { task_id = "bash-1", task_type = "local_bash" } }
                    }),
                StreamMessage("result", new { is_error = false, result = "dev server started" }),
                StreamMessage("assistant", new { marker = "after-final-result" })
            ]
        ]);

        var messages = await CollectAsync(provider);

        DescribeTypes(messages).ShouldBe(["system:init", "system:background_tasks_changed", "result"]);
    }

    [Fact]
    public async Task ExecuteAsync_stops_at_error_result_even_when_subagent_is_still_running()
    {
        var provider = CreateProvider(messageBatches:
        [
            [
                StreamMessage("system", new { subtype = "init" }),
                StreamMessage("system", new { subtype = "background_tasks_changed", tasks = SubagentTasks("agent-1") }),
                StreamMessage("result", new { is_error = true, subtype = "error_during_execution" }),
                StreamMessage("assistant", new { marker = "after-final-result" })
            ]
        ]);

        var messages = await CollectAsync(provider);

        DescribeTypes(messages).ShouldBe(["system:init", "system:background_tasks_changed", "result"]);
    }

    [Fact]
    public async Task PingAsync_reports_version_when_process_succeeds()
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResult = new ProcessResult(0, "1.2.3", string.Empty)
        };
        var provider = CreateProvider(processManager: processManager);

        var result = await provider.PingAsync();

        result.Success.ShouldBeTrue();
        result.Version.ShouldBe("1.2.3");
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
    [Trait("Category", "WindowsOnly")]
    public async Task ExecuteAsync_real_windows_cmd_shim_under_whitespace_path_round_trips_utf8_prompt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var fixture = await WindowsBatchClaudeEchoFixture.CreateAsync();
        await using var provider = new ClaudeCodeProvider(
            new CliExecutableResolver(),
            new CliProcessManager(),
            new StaticRuntimeEnvironmentResolver(fixture.RuntimeEnvironment));

        const string prompt = "继续用中文回复，确认 Windows cmd shim 收到了这个 prompt。";
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new ClaudeCodeOptions
                           {
                               WorkingDirectory = fixture.WorkingDirectory,
                               SessionId = "windows-session"
                           },
                           prompt))
        {
            messages.Add(message);
        }

        messages.Select(static message => message.Type).ShouldBe(["user", "result"]);
        messages[0].Content.GetProperty("message").GetProperty("content").GetString().ShouldBe(prompt);
    }

    [Fact]
    [Trait("Category", "RealCli")]
    [Trait("Category", "RealCliInvocationContract")]
    public async Task ExecuteAsync_real_cli_returns_actionable_authentication_failure_when_credentials_are_absent()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        using var sandbox = new RealCliInvocationSandbox();
        await using var provider = new ClaudeCodeProvider(new CliExecutableResolver(), new CliProcessManager(), sandbox);

        var failureMessage = await RealCliInvocationTestHarness.CaptureFailureMessageAsync(
            provider,
            new ClaudeCodeOptions
            {
                WorkingDirectory = sandbox.WorkingDirectory,
                AddDirectories = [sandbox.WorkingDirectory],
                PermissionMode = "plan"
            },
            "Reply with exactly the word 'pong'.",
            TimeSpan.FromSeconds(45));

        RealCliInvocationTestHarness.AssertActionableFailure("claude-code", failureMessage);
    }

    [Fact]
    [Trait("Category", "RealCli")]
    [Trait("Category", "RealCliInvocationContract")]
    public async Task ExecuteAsync_real_cli_accepts_effort_flag_without_syntax_error()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        using var sandbox = new RealCliInvocationSandbox();
        await using var provider = new ClaudeCodeProvider(new CliExecutableResolver(), new CliProcessManager(), sandbox);

        var failureMessage = await RealCliInvocationTestHarness.CaptureFailureMessageAsync(
            provider,
            new ClaudeCodeOptions
            {
                WorkingDirectory = sandbox.WorkingDirectory,
                AddDirectories = [sandbox.WorkingDirectory],
                PermissionMode = "plan",
                Effort = "high"
            },
            "Reply with exactly the word 'pong'.",
            TimeSpan.FromSeconds(45));

        RealCliInvocationTestHarness.AssertActionableFailure("claude-code/effort-flag", failureMessage);
    }

    [Fact]
    [Trait("Category", "RealCli")]
    [Trait("Category", "RealCliInvocationContract")]
    public async Task ExecuteAsync_real_cli_can_resolve_from_hagicode_agent_cli_path_when_path_does_not_include_cli()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        var executableResolver = new CliExecutableResolver();
        var realClaudePath = executableResolver.ResolveFirstAvailablePath(ClaudeExecutableCandidates);
        realClaudePath.ShouldNotBeNullOrWhiteSpace("The real CLI lane must install Claude Code before running the HAGICODE_AGENT_CLI_PATH integration test.");

        using var sandbox = new RealCliInvocationSandbox();
        var executableDirectory = Path.GetDirectoryName(realClaudePath!)
            ?? throw new InvalidOperationException("The real Claude executable path must include a parent directory.");
        var runtimeEnvironment = sandbox.CreateEnvironmentWithAgentCliPath(executableDirectory);

        if (runtimeEnvironment.TryGetValue("PATH", out var pathValue) && !string.IsNullOrWhiteSpace(pathValue))
        {
            pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(pathEntry => string.Equals(
                    pathEntry,
                    executableDirectory,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                .ShouldBeFalse();
        }

        await using var provider = new ClaudeCodeProvider(
            executableResolver,
            new CliProcessManager(),
            new StaticRuntimeEnvironmentResolver(runtimeEnvironment));

        var failureMessage = await RealCliInvocationTestHarness.CaptureFailureMessageAsync(
            provider,
            new ClaudeCodeOptions
            {
                WorkingDirectory = sandbox.WorkingDirectory,
                AddDirectories = [sandbox.WorkingDirectory],
                PermissionMode = "plan"
            },
            "Reply with exactly the word 'pong'.",
            TimeSpan.FromSeconds(45));

        RealCliInvocationTestHarness.AssertActionableFailure("claude-code/hagicode-agent-cli-path", failureMessage);
    }

    [Fact]
    [Trait("Category", "RealCli")]
    [Trait("Category", "RealCliInvocationContract")]
    [Trait("Category", "RealCliWindowsZhCnWhitespaceShim")]
    public async Task ExecuteAsync_real_cli_windows_zh_cn_whitespace_cmd_shim_surfaces_actionable_failure_without_mojibake()
    {
        if (!IsRealCliTestsEnabled()
            || !OperatingSystem.IsWindows()
            || !string.Equals(Environment.GetEnvironmentVariable(WindowsTestLocaleEnvironmentVariable), "zh-CN", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var executableResolver = new CliExecutableResolver();
        var realClaudePath = executableResolver.ResolveFirstAvailablePath(ClaudeExecutableCandidates);
        realClaudePath.ShouldNotBeNullOrWhiteSpace("The Windows zh-CN real CLI lane must install Claude Code before running this test.");

        using var sandbox = new RealCliInvocationSandbox();
        using var shimSandbox = new RealCliWhitespaceWrapperSandbox(realClaudePath!, sandbox);
        await using var provider = new ClaudeCodeProvider(
            executableResolver,
            new CliProcessManager(),
            shimSandbox);

        var failureMessage = await RealCliInvocationTestHarness.CaptureFailureMessageAsync(
            provider,
            new ClaudeCodeOptions
            {
                ExecutablePath = shimSandbox.WrapperPath,
                WorkingDirectory = sandbox.WorkingDirectory,
                AddDirectories = [sandbox.WorkingDirectory],
                PermissionMode = "plan"
            },
            "请只回复 pong。",
            TimeSpan.FromSeconds(45));

        RealCliInvocationTestHarness.AssertActionableFailure("claude-code/windows-zh-cn-whitespace-shim", failureMessage);
    }

    [Fact]
    public void RealCliWhitespaceWrapperSandbox_on_windows_prefers_cmd_alias_for_extensionless_npm_shims()
    {
        using var fixture = TemporaryWrapperTargetFixture.Create();
        var aliasDirectory = fixture.CreateDirectory("Node Global Tools");

        var extensionlessShimPath = fixture.CreateFile("claude");
        var cmdShimPath = fixture.CreateFile(Path.Combine("Node Global Tools", "claude.cmd"));
        fixture.CreateFile(Path.Combine("Node Global Tools", "claude.ps1"));

        var resolvedPath = RealCliWhitespaceWrapperSandbox.ResolveAliasedExecutablePath(
            aliasDirectory,
            extensionlessShimPath,
            isWindows: true);

        resolvedPath.ShouldBe(cmdShimPath);
    }

    [Fact]
    public void RealCliWhitespaceWrapperSandbox_on_windows_preserves_cmd_alias_for_cmd_shims()
    {
        using var fixture = TemporaryWrapperTargetFixture.Create();
        var aliasDirectory = fixture.CreateDirectory("Node Global Tools");

        var realCmdShimPath = fixture.CreateFile("claude.cmd");
        var aliasedCmdShimPath = fixture.CreateFile(Path.Combine("Node Global Tools", "claude.cmd"));
        fixture.CreateFile(Path.Combine("Node Global Tools", "claude.ps1"));

        var resolvedPath = RealCliWhitespaceWrapperSandbox.ResolveAliasedExecutablePath(
            aliasDirectory,
            realCmdShimPath,
            isWindows: true);

        resolvedPath.ShouldBe(aliasedCmdShimPath);
    }

    private static async Task<List<CliMessage>> CollectAsync(ClaudeCodeProvider provider)
    {
        var messages = new List<CliMessage>();
        await foreach (var message in provider.ExecuteAsync(
                           new ClaudeCodeOptions { SessionId = "session-1", WorkingDirectory = "/tmp/project" },
                           "hello"))
        {
            messages.Add(message);
        }

        return messages;
    }

    private static async Task<List<CliMessage>> CollectAsync(
        ClaudeCodeProvider provider,
        string executablePath,
        string? thinkingDisplay,
        string workingDirectory = "/tmp/project")
    {
        var messages = new List<CliMessage>();
        await foreach (var message in provider.ExecuteAsync(
                           new ClaudeCodeOptions
                           {
                               ExecutablePath = executablePath,
                               ThinkingDisplay = thinkingDisplay,
                               SessionId = "session-1",
                               WorkingDirectory = workingDirectory
                           },
                           "hello"))
        {
            messages.Add(message);
        }

        return messages;
    }

    // The fallback cache is process-wide and keyed by executable path, so each test uses its own path.
    private static string UniqueExecutablePath() => $"/custom/claude-{Guid.NewGuid():N}";

    private static InvalidOperationException ThinkingDisplayRejection() => new(
        "The subprocess exited unexpectedly with code 1: error: unknown option '--thinking-display'");

    private static IReadOnlyList<CliMessage> DefaultSuccessBatch() =>
    [
        StreamMessage("assistant", new { content = "hi" }),
        StreamMessage("result", new { is_error = false })
    ];

    private static CliMessage StreamMessage(string type, object payload)
    {
        var content = JsonSerializer.SerializeToElement(payload);
        using var document = JsonDocument.Parse(content.GetRawText());
        var properties = new Dictionary<string, JsonElement> { ["type"] = JsonSerializer.SerializeToElement(type) };
        foreach (var property in document.RootElement.EnumerateObject())
        {
            properties[property.Name] = property.Value.Clone();
        }

        return new CliMessage(type, JsonSerializer.SerializeToElement(properties));
    }

    private static object[] SubagentTasks(params string[] taskIds)
    {
        return taskIds.Select(static taskId => (object)new { task_id = taskId, task_type = "local_agent" }).ToArray();
    }

    private static List<string> DescribeTypes(IEnumerable<CliMessage> messages)
    {
        return messages
            .Select(static message =>
                message.Type == "system" && message.Content.TryGetProperty("subtype", out var subtype)
                    ? $"system:{subtype.GetString()}"
                    : message.Type)
            .ToList();
    }

    private static TestClaudeCodeProvider CreateProvider(
        CliExecutableResolver? executableResolver = null,
        CliProcessManager? processManager = null,
        IReadOnlyList<IReadOnlyList<CliMessage>>? messageBatches = null,
        IReadOnlyList<TransportFailure?>? transportFailures = null)
    {
        return new TestClaudeCodeProvider(
            executableResolver ?? new StubExecutableResolver(),
            processManager ?? new StubCliProcessManager(),
            new StubRuntimeEnvironmentResolver(),
            messageBatches,
            transportFailures);
    }

    /// <summary>
    /// Scripts how one transport attempt fails. <see cref="OnSend" /> throws from SendAsync before the prompt is recorded;
    /// <see cref="OnReceive" /> throws from ReceiveAsync after the attempt's message batch has been yielded.
    /// </summary>
    private sealed record TransportFailure(Exception? OnSend = null, Exception? OnReceive = null);

    private sealed class TestClaudeCodeProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver runtimeEnvironmentResolver,
        IReadOnlyList<IReadOnlyList<CliMessage>>? messageBatches,
        IReadOnlyList<TransportFailure?>? transportFailures)
        : ClaudeCodeProvider(executableResolver, processManager, runtimeEnvironmentResolver)
    {
        private readonly Queue<IReadOnlyList<CliMessage>> _messageBatches = new(messageBatches ?? []);
        private readonly Queue<TransportFailure?> _transportFailures = new(transportFailures ?? []);
        private static readonly IReadOnlyList<CliMessage> DefaultMessageBatch =
        [
            new CliMessage("assistant", JsonSerializer.SerializeToElement(new { type = "assistant", content = "hi" })),
            new CliMessage("result", JsonSerializer.SerializeToElement(new { type = "result", done = true }))
        ];

        public ProcessStartContext? LastStartContext { get; private set; }
        public List<ProcessStartContext> StartContexts { get; } = [];
        public List<CliMessage> SentMessages { get; } = [];
        public int CreatedTransportCount { get; private set; }
        public int DisposedTransportCount { get; private set; }

        protected override ICliTransport CreateTransport(ProcessStartContext startContext)
        {
            LastStartContext = startContext;
            StartContexts.Add(startContext);
            CreatedTransportCount++;
            var failure = _transportFailures.Count > 0 ? _transportFailures.Dequeue() : null;
            return new StubTransport(SentMessages, GetNextMessageBatch, () => DisposedTransportCount++, failure);
        }

        private IReadOnlyList<CliMessage> GetNextMessageBatch()
        {
            return _messageBatches.Count > 0
                ? _messageBatches.Dequeue()
                : DefaultMessageBatch;
        }
    }

    private sealed class StubTransport(
        List<CliMessage> sentMessages,
        Func<IReadOnlyList<CliMessage>> getNextMessageBatch,
        Action onDispose,
        TransportFailure? failure = null) : ICliTransport
    {
        public bool IsConnected { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            onDispose();
            IsConnected = false;
            return ValueTask.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public Task InterruptAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async IAsyncEnumerable<CliMessage> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var batch = getNextMessageBatch();
            for (var index = 0; index < batch.Count; index++)
            {
                yield return batch[index];
                if (index < batch.Count - 1)
                {
                    await Task.Yield();
                }
            }

            if (failure?.OnReceive is not null)
            {
                throw failure.OnReceive;
            }
        }

        public Task SendAsync(CliMessage message, CancellationToken cancellationToken = default)
        {
            if (failure?.OnSend is not null)
            {
                throw failure.OnSend;
            }

            sentMessages.Add(message);
            return Task.CompletedTask;
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

    private sealed class StaticRuntimeEnvironmentResolver(IReadOnlyDictionary<string, string?> environment) : IRuntimeEnvironmentResolver
    {
        public Task<IReadOnlyDictionary<string, string?>> ResolveAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(environment);
        }
    }

    private sealed class StubCliProcessManager : CliProcessManager
    {
        public ProcessResult ExecuteResult { get; init; } = new(0, "1.0.0", string.Empty);

        public override Task<ProcessResult> ExecuteAsync(ProcessStartContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ExecuteResult);
        }
    }

    private sealed class RealCliWhitespaceWrapperSandbox : IRuntimeEnvironmentResolver, IDisposable
    {
        private readonly string _rootDirectory;
        private readonly IReadOnlyDictionary<string, string?> _environment;
        private bool _disposed;

        public RealCliWhitespaceWrapperSandbox(string realExecutablePath, RealCliInvocationSandbox innerSandbox)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(realExecutablePath);
            ArgumentNullException.ThrowIfNull(innerSandbox);

            _rootDirectory = Path.Combine(innerSandbox.TempDirectory, "Claude Wrapper With Spaces");
            Directory.CreateDirectory(_rootDirectory);

            if (OperatingSystem.IsWindows())
            {
                var executableDirectory = Path.GetDirectoryName(realExecutablePath)
                    ?? throw new InvalidOperationException("The real Claude executable path must include a parent directory.");
                var aliasDirectory = Path.Combine(_rootDirectory, "Node Global Tools");
                CreateDirectoryJunction(aliasDirectory, executableDirectory);
                WrapperPath = ResolveAliasedExecutablePath(aliasDirectory, realExecutablePath, isWindows: true);
            }
            else
            {
                WrapperPath = realExecutablePath;
            }

            var mergedEnvironment = innerSandbox.ResolveAsync().GetAwaiter().GetResult()
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
            var wrapperPathValue = string.Join(
                Path.PathSeparator,
                new[]
                {
                    _rootDirectory,
                    mergedEnvironment.TryGetValue("PATH", out var existingPath) ? existingPath : null
                }.Where(static value => !string.IsNullOrWhiteSpace(value)));

            mergedEnvironment["PATH"] = wrapperPathValue;
            mergedEnvironment["HAGICODE_REAL_CLI_WHITESPACE_WRAPPER"] = WrapperPath;
            _environment = new ReadOnlyDictionary<string, string?>(mergedEnvironment);
        }

        public string WrapperPath { get; }

        internal static string ResolveAliasedExecutablePath(string aliasDirectory, string realExecutablePath, bool isWindows)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(aliasDirectory);
            ArgumentException.ThrowIfNullOrWhiteSpace(realExecutablePath);

            if (!isWindows)
            {
                return Path.Combine(aliasDirectory, Path.GetFileName(realExecutablePath));
            }

            var extension = Path.GetExtension(realExecutablePath);
            var fileName = Path.GetFileName(realExecutablePath);
            if (!string.IsNullOrWhiteSpace(extension))
            {
                var exactAliasPath = Path.Combine(aliasDirectory, fileName);
                if (File.Exists(exactAliasPath))
                {
                    return exactAliasPath;
                }
            }

            var baseName = Path.GetFileNameWithoutExtension(realExecutablePath);
            foreach (var fallbackExtension in new[] { ".cmd", ".ps1", ".bat", ".exe", string.Empty })
            {
                var candidatePath = Path.Combine(aliasDirectory, baseName + fallbackExtension);
                if (File.Exists(candidatePath))
                {
                    return candidatePath;
                }
            }

            return Path.Combine(aliasDirectory, fileName);
        }

        private static void CreateDirectoryJunction(string linkPath, string targetPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(linkPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

            Directory.CreateDirectory(Path.GetDirectoryName(linkPath) ?? throw new InvalidOperationException("The junction path must include a parent directory."));

            var cmdAttempt = TryRunDirectoryJunctionCommand(
                Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                "/d",
                "/c",
                $"mklink /J \"{linkPath}\" \"{targetPath}\"");
            if (cmdAttempt.Success && Directory.Exists(linkPath))
            {
                return;
            }

            var powerShellAttempt = TryRunDirectoryJunctionCommand(
                ResolvePowerShellExecutablePath(),
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-Command",
                BuildPowerShellJunctionCommand(linkPath, targetPath));
            if (powerShellAttempt.Success && Directory.Exists(linkPath))
            {
                return;
            }

            throw new InvalidOperationException(
                $$"""
                Failed to create the whitespace-path junction for the Claude wrapper sandbox.
                cmd: {{cmdAttempt.Diagnostic}}
                powershell: {{powerShellAttempt.Diagnostic}}
                """);
        }

        private static DirectoryJunctionAttemptResult TryRunDirectoryJunctionCommand(string fileName, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start '{fileName}' while creating the whitespace-path junction.");
            process.WaitForExit();
            var standardOutput = process.StandardOutput.ReadToEnd().Trim();
            var standardError = process.StandardError.ReadToEnd().Trim();

            return new DirectoryJunctionAttemptResult(
                process.ExitCode == 0,
                $"exit={process.ExitCode}, stdout={FormatDiagnosticText(standardOutput)}, stderr={FormatDiagnosticText(standardError)}");
        }

        private static string ResolvePowerShellExecutablePath()
        {
            var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                var powerShellCorePath = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
                if (File.Exists(powerShellCorePath))
                {
                    return powerShellCorePath;
                }
            }

            return "powershell.exe";
        }

        private static string BuildPowerShellJunctionCommand(string linkPath, string targetPath)
        {
            return FormattableString.Invariant(
                $"New-Item -ItemType Junction -Path '{EscapePowerShellSingleQuotedString(linkPath)}' -Target '{EscapePowerShellSingleQuotedString(targetPath)}' -Force | Out-Null");
        }

        private static string EscapePowerShellSingleQuotedString(string value)
        {
            return value.Replace("'", "''", StringComparison.Ordinal);
        }

        private static string FormatDiagnosticText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "<empty>" : value;
        }

        public Task<IReadOnlyDictionary<string, string?>> ResolveAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_environment);
        }

        private readonly record struct DirectoryJunctionAttemptResult(bool Success, string Diagnostic);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            RealCliInvocationSandbox.DeleteDirectoryWithRetries(_rootDirectory);
        }
    }

    private sealed class TemporaryWrapperTargetFixture(string rootDirectory) : IDisposable
    {
        public static TemporaryWrapperTargetFixture Create()
        {
            var rootDirectory = Path.Combine(Path.GetTempPath(), $"hagicode-libs-claude-wrapper-{Guid.NewGuid():N}");
            Directory.CreateDirectory(rootDirectory);
            return new TemporaryWrapperTargetFixture(rootDirectory);
        }

        public string CreateDirectory(string relativePath)
        {
            var fullPath = Path.Combine(rootDirectory, relativePath);
            Directory.CreateDirectory(fullPath);
            return fullPath;
        }

        public string CreateFile(string relativePath)
        {
            var fullPath = Path.Combine(rootDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, string.Empty, new UTF8Encoding(false));
            return fullPath;
        }

        public void Dispose()
        {
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }

    private sealed class WindowsBatchClaudeEchoFixture(string rootDirectory, string workingDirectory, IReadOnlyDictionary<string, string?> runtimeEnvironment) : IAsyncDisposable
    {
        public string WorkingDirectory { get; } = workingDirectory;

        public IReadOnlyDictionary<string, string?> RuntimeEnvironment { get; } = runtimeEnvironment;

        public static async Task<WindowsBatchClaudeEchoFixture> CreateAsync()
        {
            var rootDirectory = Path.Combine(
                Path.GetTempPath(),
                $"HagiCode Claude Shim Fixture {Guid.NewGuid():N}");
            var shimDirectory = Path.Combine(rootDirectory, "Node Global Tools");
            var workingDirectory = Path.Combine(rootDirectory, "workspace");

            Directory.CreateDirectory(shimDirectory);
            Directory.CreateDirectory(workingDirectory);

            var shimScriptPath = Path.Combine(shimDirectory, "claude.cmd");
            var echoScriptPath = Path.Combine(shimDirectory, "claude-echo.ps1");

            await File.WriteAllTextAsync(
                echoScriptPath,
                """
                [Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
                [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

                $line = [Console]::In.ReadLine()
                if ($null -eq $line) {
                    exit 0
                }

                [Console]::Out.WriteLine($line)

                $result = @{
                    type = "result"
                    subtype = "success"
                    is_error = $false
                    result = "ok"
                }

                [Console]::Out.WriteLine(($result | ConvertTo-Json -Compress -Depth 4))
                """,
                new UTF8Encoding(false));

            await File.WriteAllTextAsync(
                shimScriptPath,
                """
                @echo off
                setlocal
                powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0claude-echo.ps1" %*
                exit /b %ERRORLEVEL%
                """,
                new UTF8Encoding(false));

            var runtimeEnvironment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = string.Join(
                    Path.PathSeparator,
                    new[]
                    {
                        shimDirectory,
                        Environment.GetEnvironmentVariable("PATH")
                    }.Where(static value => !string.IsNullOrWhiteSpace(value))),
                ["PATHEXT"] = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD"
            };

            return new WindowsBatchClaudeEchoFixture(rootDirectory, workingDirectory, runtimeEnvironment);
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (Directory.Exists(rootDirectory))
                {
                    Directory.Delete(rootDirectory, recursive: true);
                }
            }
            catch
            {
            }

            return ValueTask.CompletedTask;
        }
    }

    private static bool IsRealCliTestsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(RealCliTestsEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}