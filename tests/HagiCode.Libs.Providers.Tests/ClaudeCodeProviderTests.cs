using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using Shouldly;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.ClaudeCode;

namespace HagiCode.Libs.Providers.Tests;

[Category("Integration")]
public sealed partial class ClaudeCodeProviderTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";
    private const string WindowsTestLocaleEnvironmentVariable = "HAGICODE_WINDOWS_TEST_LOCALE";
    private static readonly string[] ClaudeExecutableCandidates = ["claude", "claude-code"];

    [Test]
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
        arguments.ShouldContain("--replay-user-messages");
    }

    [TestCase(null, null)]
    [TestCase("session-id", null)]
    [TestCase(null, "resume-id")]
    public void BuildCommandArguments_without_a_handler_neither_prompts_nor_disallows_ask_user_question(string? sessionId, string? resume)
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            SessionId = sessionId,
            Resume = resume
        });

        arguments.ShouldNotContain("--permission-prompt-tool");
        arguments.ShouldNotContain("--disallowedTools");
        arguments.Any(static argument => argument.Contains("AskUserQuestion", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Test]
    public void BuildCommandArguments_with_a_handler_prompts_through_stdio_and_adds_no_disallowed_tools()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            UserInputHandler = static (_, _) => Task.FromResult<ClaudeUserInputResponse?>(null)
        });

        var index = arguments.ToList().IndexOf("--permission-prompt-tool");
        index.ShouldBeGreaterThan(0);
        arguments[index + 1].ShouldBe("stdio");
        arguments.ShouldNotContain("--disallowedTools");
        arguments.Any(static argument => argument.Contains("AskUserQuestion", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Test]
    public void BuildCommandArguments_passes_caller_disallowed_tools_through_untouched_with_or_without_a_handler()
    {
        var provider = CreateProvider();
        string[] disallowed = ["AskUserQuestion", "Bash"];

        var withoutHandler = provider.BuildCommandArguments(new ClaudeCodeOptions { DisallowedTools = disallowed });
        var withHandler = provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            DisallowedTools = disallowed,
            UserInputHandler = static (_, _) => Task.FromResult<ClaudeUserInputResponse?>(null)
        });

        foreach (var arguments in new[] { withoutHandler, withHandler })
        {
            var index = arguments.ToList().IndexOf("--disallowedTools");
            index.ShouldBeGreaterThanOrEqualTo(0);
            arguments[index + 1].ShouldBe("AskUserQuestion,Bash");
            arguments.Count(static argument => argument == "--disallowedTools").ShouldBe(1);
        }
    }

    [Test]
    public async Task ExecuteAsync_launches_without_a_permission_prompt_tool_or_ask_user_question_exclusion_when_no_handler_is_set()
    {
        var provider = CreateProvider();

        await foreach (var _ in provider.ExecuteAsync(
                           new ClaudeCodeOptions { SessionId = "ask-session" },
                           "hello"))
        {
        }

        provider.LastStartContext.ShouldNotBeNull();
        provider.LastStartContext.Arguments.ShouldNotContain("--permission-prompt-tool");
        provider.LastStartContext.Arguments.ShouldNotContain("--disallowedTools");
    }

    [Test]
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

    [Test]
    public void BuildCommandArguments_adds_debug_file_only_when_set()
    {
        var provider = CreateProvider();
        var withoutDebugFile = provider.BuildCommandArguments(new ClaudeCodeOptions { SessionId = "session-id" });

        withoutDebugFile.ShouldNotContain("--debug-file");

        var withDebugFile = provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            SessionId = "session-id",
            DebugFile = "/private/claude-debug/claude-debug.log"
        });

        var index = withDebugFile.ToList().IndexOf("--debug-file");
        index.ShouldBeGreaterThan(0);
        withDebugFile[index + 1].ShouldBe("/private/claude-debug/claude-debug.log");
        withDebugFile.Count.ShouldBe(withoutDebugFile.Count + 2);
    }

    [Test]
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
            "--replay-user-messages",
            "--model",
            "Claude Sonnet 4.5",
            "--add-dir",
            "/tmp/my repo",
            "--settings",
            "balanced mode",
            "--dangerously-skip-permissions"
        ]);
    }

    [TestCase(null, null)]
    [TestCase("session-id", null)]
    [TestCase(null, "resume-id")]
    [TestCase("session-id", "resume-id")]
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

    [Test]
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

    [TestCase("continue")]
    [TestCase("Continue")]
    [TestCase("CONTINUE")]
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

    [Test]
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

    [TestCase(null)]
    [TestCase("   ")]
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

    [TestCase("low")]
    [TestCase("medium")]
    [TestCase("high")]
    [TestCase("xhigh")]
    [TestCase("max")]
    public void BuildCommandArguments_forwards_each_supported_effort_level(string level)
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            Effort = level
        });

        arguments.ShouldContain("--effort", level);
    }

    [Test]
    public void BuildCommandArguments_normalizes_effort_casing_and_whitespace()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            Effort = "  XHigh "
        });

        arguments.ShouldContain("--effort", "xhigh");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void BuildCommandArguments_omits_effort_when_value_is_empty(string? effort)
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            Effort = effort
        });

        arguments.ShouldNotContain("--effort");
    }

    [Test]
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

    [Test]
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

    [Test]
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

    [Test]
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

    [TestCase("summarized")]
    [TestCase("omitted")]
    [TestCase("highlights")]
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

    [Test]
    public void BuildCommandArguments_normalizes_thinking_display_casing_and_whitespace()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            ThinkingDisplay = "  Summarized "
        });

        arguments.ShouldContain("--thinking-display", "summarized");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void BuildCommandArguments_omits_thinking_display_when_value_is_empty(string? thinkingDisplay)
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            ThinkingDisplay = thinkingDisplay
        });

        arguments.ShouldNotContain("--thinking-display");
    }

    [Test]
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

    [Test]
    public void AllowedThinkingDisplayModes_contains_exactly_the_supported_modes()
    {
        ClaudeCodeProvider.AllowedThinkingDisplayModes
            .Select(static mode => mode.ToLowerInvariant())
            .OrderBy(static mode => mode, StringComparer.Ordinal)
            .ShouldBe(["highlights", "omitted", "summarized"]);

        ClaudeCodeProvider.AllowedThinkingDisplayModes.Contains("SUMMARIZED").ShouldBeTrue();
        ClaudeCodeProvider.AllowedThinkingDisplayModes.Contains("verbose").ShouldBeFalse();
    }

    [Test]
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

    [Test]
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

    [Test]
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

    [Test]
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
            "--replay-user-messages",
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

    [Test]
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

    [Test]
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

    [Test]
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

    [Test]
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

    [Test]
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

    [Test]
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

    [Test]
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

    [Test]
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

    [Test]
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

    [Test]
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

    [Test]
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

    [Test]
    public async Task ExecuteAsync_passes_runtime_resolver_variables_to_the_start_context()
    {
        var provider = CreateProviderWithRuntimeEnvironment(new Dictionary<string, string?>
        {
            ["HAGI_TEST_CONTEXT_MARKER"] = "marker-claude-code"
        });

        await foreach (var _ in provider.ExecuteAsync(new ClaudeCodeOptions(), "hello"))
        {
        }

        provider.LastStartContext.ShouldNotBeNull();
        provider.LastStartContext!.EnvironmentVariables["HAGI_TEST_CONTEXT_MARKER"].ShouldBe("marker-claude-code");
    }

    [Test]
    public async Task ExecuteAsync_passes_runtime_resolver_variables_to_the_start_context_when_resuming()
    {
        var provider = CreateProviderWithRuntimeEnvironment(new Dictionary<string, string?>
        {
            ["HAGI_TEST_CONTEXT_MARKER"] = "marker-claude-code"
        });

        await foreach (var _ in provider.ExecuteAsync(new ClaudeCodeOptions { Resume = "resume-session-1" }, "hello"))
        {
        }

        provider.LastStartContext.ShouldNotBeNull();
        provider.LastStartContext.Arguments.ShouldContain("--resume");
        provider.LastStartContext.Arguments.ShouldContain("resume-session-1");
        provider.LastStartContext!.EnvironmentVariables["HAGI_TEST_CONTEXT_MARKER"].ShouldBe("marker-claude-code");
    }

    [Test]
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

    [Test]
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

    [Test]
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

    [Test]
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

    [Test]
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

    [Test]
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

    [TestCase("""{"type":"a","event_timestamp":"2026-10-06T06:32:05Z"}""", true)]
    [TestCase("""{"type":"a","timestamp":"2026-10-06T14:32:05+08:00"}""", true)]
    [TestCase("""{"type":"a","timestamp":"not a time"}""", false)]
    [TestCase("""{"type":"a","timestamp":"0001-01-01T00:00:00Z"}""", false)]
    [TestCase("""{"type":"a"}""", false)]
    public void TryReadPayloadTimestamp_accepts_only_valid_non_default_times(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);

        CliMessage.TryReadPayloadTimestamp(document.RootElement, out var timestamp).ShouldBe(expected);
        if (expected)
        {
            timestamp.ShouldBe(new DateTimeOffset(2026, 10, 6, 6, 32, 5, TimeSpan.Zero));
        }
    }

    [Test]
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

    [Test]
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

    [Test]
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

    [Test]
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

        // The agent work snapshots that follow the task events are covered by their own tests.
        DescribeTypes(messages.Where(static message => message.Type != HagiCode.Libs.Core.AgentWork.AgentWorkSnapshot.MessageType)).ShouldBe(
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

    [Test]
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

    [Test]
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

    [Test]
    public async Task ExecuteAsync_keeps_streaming_while_a_background_command_without_a_timeout_is_running()
    {
        // The agent starts a build in the background without a timeout and ends its turn to wait for the exit notification.
        // Ending the run at that "result" kills the CLI and the command, and the agent's promised follow-up never happens.
        var provider = CreateProvider(messageBatches:
        [
            [
                StreamMessage("system", new { subtype = "init" }),
                BackgroundBashToolUse(timeoutMilliseconds: null),
                StreamMessage("system", new { subtype = "background_tasks_changed", tasks = BackgroundCommandTasks("bash-1") }),
                StreamMessage(
                    "system",
                    new { subtype = "task_started", task_id = "bash-1", task_type = "local_bash", tool_use_id = "toolu-bash" }),
                StreamMessage("result", new { is_error = false, result = "waiting for the build" }),
                StreamMessage("system", new { subtype = "task_notification", task_id = "bash-1", status = "completed" }),
                StreamMessage("system", new { subtype = "background_tasks_changed", tasks = BackgroundCommandTasks() }),
                StreamMessage("system", new { subtype = "init" }),
                StreamMessage("result", new { is_error = false, result = "build finished" }),
                StreamMessage("assistant", new { marker = "after-final-result" })
            ]
        ]);

        var messages = await CollectAsync(provider);

        messages.Count(static message => message.Type == "result").ShouldBe(2);
        messages[^1].Content.GetProperty("result").GetString().ShouldBe("build finished");
        messages.Any(static message => message.Content.TryGetProperty("marker", out _)).ShouldBeFalse();
    }

    [Test]
    public async Task ExecuteAsync_stops_waiting_for_a_background_command_without_a_timeout_when_the_cap_passes()
    {
        // The CLI never ends such a command, so the host's own cap is the only bound; a dev server must not hold the run
        // open for good.
        var provider = CreateProvider(
            messageBatches:
            [
                [
                    StreamMessage("system", new { subtype = "init" }),
                    BackgroundBashToolUse(timeoutMilliseconds: null),
                    StreamMessage("system", new { subtype = "background_tasks_changed", tasks = BackgroundCommandTasks("bash-1") }),
                    StreamMessage(
                        "system",
                        new { subtype = "task_started", task_id = "bash-1", task_type = "local_bash", tool_use_id = "toolu-bash" }),
                    StreamMessage("result", new { is_error = false, result = "dev server started" })
                ]
            ],
            transportFailures: [new TransportFailure(HangAfterBatch: true)]);
        var options = new ClaudeCodeOptions
        {
            SessionId = "session-1",
            WorkingDirectory = "/tmp/project",
            UntimedBackgroundCommandTimeout = TimeSpan.FromMilliseconds(250)
        };

        var stopwatch = Stopwatch.StartNew();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var messages = await CollectAsync(provider, options, guard.Token);

        guard.IsCancellationRequested.ShouldBeFalse();
        // Without the wait the run would end at the first "result"; the grace period must not be added on top of the cap.
        stopwatch.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(200));
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
        messages[^1].Type.ShouldBe("result");
        messages[^1].Content.GetProperty("result").GetString().ShouldBe("dev server started");
        provider.DisposedTransportCount.ShouldBe(1);
    }

    [Test]
    public async Task ExecuteAsync_does_not_wait_for_a_background_command_without_a_timeout_when_the_cap_is_zero()
    {
        var provider = CreateProvider(messageBatches:
        [
            [
                StreamMessage("system", new { subtype = "init" }),
                BackgroundBashToolUse(timeoutMilliseconds: null),
                StreamMessage("system", new { subtype = "background_tasks_changed", tasks = BackgroundCommandTasks("bash-1") }),
                StreamMessage(
                    "system",
                    new { subtype = "task_started", task_id = "bash-1", task_type = "local_bash", tool_use_id = "toolu-bash" }),
                StreamMessage("result", new { is_error = false, result = "dev server started" }),
                StreamMessage("assistant", new { marker = "after-final-result" })
            ]
        ]);

        var messages = await CollectAsync(
            provider,
            new ClaudeCodeOptions
            {
                SessionId = "session-1",
                WorkingDirectory = "/tmp/project",
                UntimedBackgroundCommandTimeout = TimeSpan.Zero
            });

        messages[^1].Type.ShouldBe("result");
        messages.Any(static message => message.Content.TryGetProperty("marker", out _)).ShouldBeFalse();
    }

    [Test]
    public void Tracker_bounds_a_background_command_without_a_timeout_by_the_cap_alone()
    {
        var tracker = new ClaudeStreamCompletionTracker(
            TimeSpan.FromMinutes(1),
            untimedBackgroundCommandTimeout: TimeSpan.FromMinutes(30));

        ObserveUntimedBackgroundCommandTurn(tracker).ShouldBeFalse();

        tracker.CurrentWait.ShouldBe(ClaudeStreamCompletionTracker.PendingWait.MainAgentBackgroundWork);
        // No grace period on top: the CLI has no timeout of its own to report, so the cap is the whole bound.
        tracker.RemainingBackgroundWork.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(30));
        tracker.RemainingBackgroundWork.ShouldBeGreaterThan(TimeSpan.FromMinutes(29));
    }

    [Test]
    public void Tracker_waits_without_a_limit_for_a_background_command_without_a_timeout_when_the_cap_is_infinite()
    {
        var tracker = new ClaudeStreamCompletionTracker(
            TimeSpan.FromMinutes(1),
            untimedBackgroundCommandTimeout: Timeout.InfiniteTimeSpan);

        ObserveUntimedBackgroundCommandTurn(tracker).ShouldBeFalse();

        tracker.CurrentWait.ShouldBe(ClaudeStreamCompletionTracker.PendingWait.MainAgentBackgroundWork);
        tracker.RemainingBackgroundWork.ShouldBe(Timeout.InfiniteTimeSpan);
    }

    [Test]
    public void Tracker_does_not_wait_for_a_background_command_without_a_timeout_by_default()
    {
        // A tracker built without the cap behaves as before: only a declared timeout is awaited.
        var tracker = new ClaudeStreamCompletionTracker(TimeSpan.FromMinutes(1));

        ObserveUntimedBackgroundCommandTurn(tracker).ShouldBeTrue();
        tracker.CurrentWait.ShouldBe(ClaudeStreamCompletionTracker.PendingWait.None);
    }

    [Test]
    public void Tracker_ends_the_wait_for_a_background_command_without_a_timeout_when_it_reports_back()
    {
        var tracker = new ClaudeStreamCompletionTracker(
            TimeSpan.FromMinutes(1),
            untimedBackgroundCommandTimeout: TimeSpan.FromMinutes(30));
        ObserveUntimedBackgroundCommandTurn(tracker);

        tracker.Observe(StreamMessage("system", new { subtype = "task_notification", task_id = "bash-1", status = "completed" }));
        tracker.Observe(StreamMessage("system", new { subtype = "init" }));

        tracker.CurrentWait.ShouldBe(ClaudeStreamCompletionTracker.PendingWait.None);
        tracker.Observe(StreamMessage("result", new { is_error = false })).ShouldBeTrue();
    }

    [TestCase(-5)]
    [TestCase(-60)]
    public async Task ExecuteAsync_rejects_a_negative_untimed_background_command_timeout(int seconds)
    {
        var provider = CreateProvider();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (var _ in provider.ExecuteAsync(
                               new ClaudeCodeOptions { UntimedBackgroundCommandTimeout = TimeSpan.FromSeconds(seconds) },
                               "hello"))
            {
            }
        });
    }

    [Test]
    public async Task ExecuteAsync_keeps_streaming_while_a_monitor_armed_by_the_main_agent_is_watching()
    {
        // Real stream order (Claude Code 2.1.296): the main agent arms a Monitor and ends its turn. A Monitor is reported as
        // a plain "local_bash" task, tied to its tool call only by the tool_use_id of "task_started", which follows the
        // task list. Every monitor event wakes the agent for another turn, and the monitor's own end is reported by a
        // "task_notification". Ending the run at the first "result" kills the monitor and loses all of its events.
        var provider = CreateProvider(messageBatches:
        [
            [
                StreamMessage("system", new { subtype = "init" }),
                MonitorToolUse(),
                StreamMessage("system", new { subtype = "background_tasks_changed", tasks = BackgroundCommandTasks("monitor-1") }),
                StreamMessage(
                    "system",
                    new { subtype = "task_started", task_id = "monitor-1", task_type = "local_bash", tool_use_id = "toolu-monitor" }),
                StreamMessage("result", new { is_error = false, result = "ARMED" }),
                StreamMessage("system", new { subtype = "init" }),
                StreamMessage("result", new { is_error = false, result = "GOT EVENT-ONE" }),
                StreamMessage("system", new { subtype = "task_updated", task_id = "monitor-1" }),
                StreamMessage("system", new { subtype = "task_notification", task_id = "monitor-1", status = "completed" }),
                StreamMessage("system", new { subtype = "background_tasks_changed", tasks = BackgroundCommandTasks() }),
                StreamMessage("system", new { subtype = "init" }),
                StreamMessage("result", new { is_error = false, result = "GOT EVENT-TWO" }),
                StreamMessage("assistant", new { marker = "after-final-result" })
            ]
        ]);

        var messages = await CollectAsync(provider);

        messages.Count(static message => message.Type == "result").ShouldBe(3);
        messages[^1].Content.GetProperty("result").GetString().ShouldBe("GOT EVENT-TWO");
        messages.Any(static message => message.Content.TryGetProperty("marker", out _)).ShouldBeFalse();
    }

    [Test]
    public async Task ExecuteAsync_keeps_streaming_while_a_background_command_with_a_timeout_is_running()
    {
        // The main agent starts a waiter in the background with an explicit timeout and ends its turn. The CLI wakes it
        // when the command exits (or kills it at the timeout), so the run must stay open until then.
        var provider = CreateProvider(messageBatches:
        [
            [
                StreamMessage("system", new { subtype = "init" }),
                BackgroundBashToolUse(timeoutMilliseconds: 7_200_000),
                StreamMessage("system", new { subtype = "background_tasks_changed", tasks = BackgroundCommandTasks("bash-1") }),
                StreamMessage(
                    "system",
                    new { subtype = "task_started", task_id = "bash-1", task_type = "local_bash", tool_use_id = "toolu-bash" }),
                StreamMessage("result", new { is_error = false, result = "waiting for the driver" }),
                StreamMessage("system", new { subtype = "task_notification", task_id = "bash-1", status = "completed" }),
                StreamMessage("system", new { subtype = "background_tasks_changed", tasks = BackgroundCommandTasks() }),
                StreamMessage("system", new { subtype = "init" }),
                StreamMessage("result", new { is_error = false, result = "driver finished" }),
                StreamMessage("assistant", new { marker = "after-final-result" })
            ]
        ]);

        var messages = await CollectAsync(provider);

        messages.Count(static message => message.Type == "result").ShouldBe(2);
        messages[^1].Content.GetProperty("result").GetString().ShouldBe("driver finished");
        messages.Any(static message => message.Content.TryGetProperty("marker", out _)).ShouldBeFalse();
    }

    [Test]
    public async Task ExecuteAsync_waits_for_a_background_command_until_the_cli_stops_it_at_its_timeout()
    {
        // The CLI enforces the declared timeout of a background command: it reports the task as stopped and wakes the
        // agent for a turn about it.
        var provider = CreateProvider(messageBatches:
        [
            [
                StreamMessage("system", new { subtype = "init" }),
                BackgroundBashToolUse(timeoutMilliseconds: 8_000),
                StreamMessage(
                    "system",
                    new { subtype = "task_started", task_id = "bash-1", task_type = "local_bash", tool_use_id = "toolu-bash" }),
                StreamMessage("result", new { is_error = false, result = "STARTED" }),
                StreamMessage("system", new { subtype = "task_notification", task_id = "bash-1", status = "stopped" }),
                StreamMessage("system", new { subtype = "init" }),
                StreamMessage("result", new { is_error = false, result = "NOTIFIED killed" })
            ]
        ]);

        var messages = await CollectAsync(provider);

        messages[^1].Content.GetProperty("result").GetString().ShouldBe("NOTIFIED killed");
    }

    [Test]
    public async Task ExecuteAsync_keeps_waiting_when_the_monitor_task_list_arrives_after_task_started()
    {
        var provider = CreateProvider(messageBatches:
        [
            [
                StreamMessage("system", new { subtype = "init" }),
                MonitorToolUse(),
                StreamMessage(
                    "system",
                    new { subtype = "task_started", task_id = "monitor-1", task_type = "local_bash", tool_use_id = "toolu-monitor" }),
                StreamMessage("system", new { subtype = "background_tasks_changed", tasks = BackgroundCommandTasks("monitor-1") }),
                StreamMessage("result", new { is_error = false, result = "ARMED" }),
                StreamMessage("system", new { subtype = "task_notification", task_id = "monitor-1", status = "completed" }),
                StreamMessage("system", new { subtype = "init" }),
                StreamMessage("result", new { is_error = false, result = "monitor ended" })
            ]
        ]);

        var messages = await CollectAsync(provider);

        messages.Count(static message => message.Type == "result").ShouldBe(2);
        messages[^1].Content.GetProperty("result").GetString().ShouldBe("monitor ended");
    }

    [Test]
    public async Task ExecuteAsync_stops_waiting_for_a_monitor_after_timeout()
    {
        var provider = CreateProvider(
            messageBatches:
            [
                [
                    StreamMessage("system", new { subtype = "init" }),
                    MonitorToolUse(timeoutMilliseconds: 150),
                    StreamMessage("system", new { subtype = "background_tasks_changed", tasks = BackgroundCommandTasks("monitor-1") }),
                    StreamMessage(
                        "system",
                        new { subtype = "task_started", task_id = "monitor-1", task_type = "local_bash", tool_use_id = "toolu-monitor" }),
                    StreamMessage("result", new { is_error = false, result = "ARMED" })
                ]
            ],
            transportFailures: [new TransportFailure(HangAfterBatch: true)]);
        var options = new ClaudeCodeOptions
        {
            SessionId = "session-1",
            WorkingDirectory = "/tmp/project",
            BackgroundWorkGracePeriod = TimeSpan.FromMilliseconds(100)
        };

        var messages = new List<CliMessage>();
        var stopwatch = Stopwatch.StartNew();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var message in provider.ExecuteAsync(options, "hello", guard.Token))
        {
            messages.Add(message);
        }

        guard.IsCancellationRequested.ShouldBeFalse();
        // Without the wait the run would end at the first "result", long before the declared timeout and grace pass.
        stopwatch.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(200));
        messages[^1].Type.ShouldBe("result");
        messages[^1].Content.GetProperty("result").GetString().ShouldBe("ARMED");
        provider.DisposedTransportCount.ShouldBe(1);
    }

    [Test]
    public async Task ExecuteAsync_stops_at_error_result_even_when_a_monitor_is_watching()
    {
        var provider = CreateProvider(messageBatches:
        [
            [
                StreamMessage("system", new { subtype = "init" }),
                MonitorToolUse(),
                StreamMessage(
                    "system",
                    new { subtype = "task_started", task_id = "monitor-1", task_type = "local_bash", tool_use_id = "toolu-monitor" }),
                StreamMessage("result", new { is_error = true, result = "boom" }),
                StreamMessage("assistant", new { marker = "after-final-result" })
            ]
        ]);

        var messages = await CollectAsync(provider);

        messages[^1].Type.ShouldBe("result");
        messages.Any(static message => message.Content.TryGetProperty("marker", out _)).ShouldBeFalse();
    }

    [TestCase(0)]
    [TestCase(-5)]
    public async Task ExecuteAsync_rejects_non_positive_background_work_grace_period(int seconds)
    {
        var provider = CreateProvider();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (var _ in provider.ExecuteAsync(
                               new ClaudeCodeOptions { BackgroundWorkGracePeriod = TimeSpan.FromSeconds(seconds) },
                               "hello"))
            {
            }
        });
    }

    [Test]
    public async Task ExecuteAsync_keeps_streaming_while_subagent_background_task_outlives_the_subagent()
    {
        // Real stream order (Claude Code 2.1.292): a background subagent starts its own background command and ends its
        // turn. The CLI reports the subagent as completed and lets the main agent take another turn (its own "result"),
        // while the command is still running. Only when the command finishes does the CLI resume the subagent, whose
        // real result reaches the main agent in a later turn. Ending the run at the interim "result" kills the command.
        var provider = CreateProvider(messageBatches:
        [
            [
                StreamMessage("system", new { subtype = "init" }),
                StreamMessage("system", new { subtype = "background_tasks_changed", tasks = SubagentTasks("agent-1") }),
                StreamMessage("system", new { subtype = "task_started", task_id = "agent-1", task_type = "local_agent" }),
                StreamMessage("result", new { is_error = false, result = "DONE" }),
                StreamMessage(
                    "system",
                    new
                    {
                        subtype = "task_started",
                        task_id = "bash-1",
                        task_type = "local_bash",
                        owned_by_subagent = true,
                        parent_task_id = "agent-1"
                    }),
                StreamMessage(
                    "system",
                    new
                    {
                        subtype = "background_tasks_changed",
                        tasks = new object[]
                        {
                            new { task_id = "agent-1", task_type = "local_agent" },
                            new { task_id = "bash-1", task_type = "local_bash", parent_task_id = "agent-1" }
                        }
                    }),
                StreamMessage("system", new { subtype = "task_notification", task_id = "agent-1", status = "completed" }),
                StreamMessage(
                    "system",
                    new
                    {
                        subtype = "background_tasks_changed",
                        tasks = new object[] { new { task_id = "bash-1", task_type = "local_bash", parent_task_id = "agent-1" } }
                    }),
                StreamMessage("system", new { subtype = "init" }),
                StreamMessage("result", new { is_error = false, result = "command is running, waiting" }),
                StreamMessage("system", new { subtype = "task_notification", task_id = "bash-1", status = "completed" }),
                StreamMessage("system", new { subtype = "background_tasks_changed", tasks = SubagentTasks() }),
                StreamMessage("system", new { subtype = "background_tasks_changed", tasks = SubagentTasks("agent-1") }),
                StreamMessage("system", new { subtype = "task_started", task_id = "agent-1", task_type = "local_agent" }),
                StreamMessage("system", new { subtype = "task_notification", task_id = "agent-1", status = "completed" }),
                StreamMessage("system", new { subtype = "background_tasks_changed", tasks = SubagentTasks() }),
                StreamMessage("system", new { subtype = "init" }),
                StreamMessage("result", new { is_error = false, result = "background test completed" }),
                StreamMessage("assistant", new { marker = "after-final-result" })
            ]
        ]);

        var messages = await CollectAsync(provider);

        messages.Count(static message => message.Type == "result").ShouldBe(3);
        messages[^1].Type.ShouldBe("result");
        messages[^1].Content.GetProperty("result").GetString().ShouldBe("background test completed");
        messages.Any(static message => message.Content.TryGetProperty("marker", out _)).ShouldBeFalse();
    }

    [Test]
    public async Task ExecuteAsync_waits_for_the_turn_that_handles_a_notification_queued_during_the_final_answer()
    {
        // Real stream (ClaudeAgentWorkCapture.NotificationQueuedDuringFinalAnswer): the subagent finishes while the main
        // agent writes its final answer. With no tool result left to attach it to, the CLI queues the notification and
        // starts a follow-up turn right after the "result". Ending the run at that "result" kills the CLI before the agent
        // sees what the subagent reported.
        var provider = CreateProvider(messageBatches:
        [
            [
                .. ClaudeAgentWorkCapture.NotificationQueuedDuringFinalAnswer,
                StreamMessage("assistant", new { marker = "after-final-result" })
            ]
        ]);

        var messages = await CollectAsync(provider);

        messages.Count(static message => message.Type == "result").ShouldBe(2);
        messages.Last(static message => message.Type == "result").Content.GetProperty("result").GetString()
            .ShouldStartWith("The background agent finished");
        messages.Any(static message => message.Content.TryGetProperty("marker", out _)).ShouldBeFalse();
    }

    [Test]
    public async Task ExecuteAsync_does_not_wait_for_a_notification_the_agent_received_at_a_tool_boundary()
    {
        // Real stream (ClaudeAgentWorkCapture.NotificationConsumedAtToolBoundary): the CLI attached the notification to the
        // request after a tool result and replayed it, so no follow-up turn starts and the first "result" is the last.
        var provider = CreateProvider(messageBatches:
        [
            [
                .. ClaudeAgentWorkCapture.NotificationConsumedAtToolBoundary,
                StreamMessage("assistant", new { marker = "after-final-result" })
            ]
        ]);

        var messages = await CollectAsync(provider);

        messages.Count(static message => message.Type == "result").ShouldBe(1);
        messages.Any(static message => message.Content.TryGetProperty("marker", out _)).ShouldBeFalse();
    }

    [TestCase("tool-result")]
    [TestCase("replay")]
    public async Task ExecuteAsync_treats_either_sign_of_delivery_as_the_end_of_a_queued_notification(string sign)
    {
        // The two signs are independent: a tool result of the main agent after the notification, or the CLI replaying it.
        var provider = CreateProvider(messageBatches:
        [
            [
                StreamMessage("system", new { subtype = "init" }),
                PromptEcho(),
                ToolUseMessage("Agent", "toolu-agent", new { description = "Review", prompt = "go" }),
                StreamMessage("system", new { subtype = "background_tasks_changed", tasks = SubagentTasks("agent-1") }),
                StreamMessage(
                    "system",
                    new { subtype = "task_started", task_id = "agent-1", task_type = "local_agent", tool_use_id = "toolu-agent" }),
                StreamMessage("system", new { subtype = "task_notification", task_id = "agent-1", status = "completed" }),
                StreamMessage("system", new { subtype = "background_tasks_changed", tasks = SubagentTasks() }),
                sign == "tool-result" ? ToolResultMessage() : NotificationReplay("agent-1"),
                StreamMessage("result", new { is_error = false, result = "answer" }),
                StreamMessage("assistant", new { marker = "after-final-result" })
            ]
        ]);

        var messages = await CollectAsync(provider);

        messages.Count(static message => message.Type == "result").ShouldBe(1);
        messages.Any(static message => message.Content.TryGetProperty("marker", out _)).ShouldBeFalse();
    }

    [Test]
    public async Task ExecuteAsync_keeps_waiting_when_only_a_subagent_gets_a_tool_result_after_the_notification()
    {
        // A subagent's own tool results (parent_tool_use_id) are boundaries of the subagent's turn, not of the main agent's.
        var provider = CreateProvider(messageBatches:
        [
            [
                StreamMessage("system", new { subtype = "init" }),
                PromptEcho(),
                ToolUseMessage("Agent", "toolu-agent", new { description = "Review", prompt = "go" }),
                StreamMessage("system", new { subtype = "background_tasks_changed", tasks = SubagentTasks("agent-1") }),
                StreamMessage(
                    "system",
                    new { subtype = "task_started", task_id = "agent-1", task_type = "local_agent", tool_use_id = "toolu-agent" }),
                StreamMessage("system", new { subtype = "task_notification", task_id = "agent-1", status = "completed" }),
                ToolResultMessage(parentToolUseId: "toolu-agent"),
                StreamMessage("system", new { subtype = "background_tasks_changed", tasks = SubagentTasks() }),
                StreamMessage("result", new { is_error = false, result = "answer" }),
                StreamMessage("system", new { subtype = "init" }),
                StreamMessage("result", new { is_error = false, result = "agent-1 handled" }),
                StreamMessage("assistant", new { marker = "after-final-result" })
            ]
        ]);

        var messages = await CollectAsync(provider);

        messages.Count(static message => message.Type == "result").ShouldBe(2);
        messages.Last(static message => message.Type == "result").Content.GetProperty("result").GetString().ShouldBe("agent-1 handled");
        messages.Any(static message => message.Content.TryGetProperty("marker", out _)).ShouldBeFalse();
    }

    [Test]
    public async Task ExecuteAsync_ends_when_a_queued_notification_never_starts_a_turn()
    {
        var provider = CreateProvider(
            messageBatches:
            [
                [
                    StreamMessage("system", new { subtype = "init" }),
                    PromptEcho(),
                    ToolUseMessage("Agent", "toolu-agent", new { description = "Review", prompt = "go" }),
                    StreamMessage(
                        "system",
                        new { subtype = "task_started", task_id = "agent-1", task_type = "local_agent", tool_use_id = "toolu-agent" }),
                    StreamMessage("system", new { subtype = "task_notification", task_id = "agent-1", status = "completed" }),
                    StreamMessage("result", new { is_error = false, result = "answer" })
                ]
            ],
            transportFailures: [new TransportFailure(HangAfterBatch: true)]);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var messages = await CollectAsync(
            provider,
            new ClaudeCodeOptions
            {
                SessionId = "session-1",
                WorkingDirectory = "/tmp/project",
                NotificationTurnTimeout = TimeSpan.FromMilliseconds(200)
            },
            guard.Token);

        guard.IsCancellationRequested.ShouldBeFalse();
        messages[^1].Type.ShouldBe("result");
        messages.Count(static message => message.Type == "result").ShouldBe(1);
        provider.DisposedTransportCount.ShouldBe(1);
    }

    [Test]
    public void Tracker_holds_the_run_open_for_a_notification_queued_before_the_result_until_its_turn_starts()
    {
        var tracker = new ClaudeStreamCompletionTracker(TimeSpan.FromMinutes(1));

        tracker.Observe(StreamMessage("system", new { subtype = "init" })).ShouldBeFalse();
        tracker.Observe(StreamMessage("system", new { subtype = "background_tasks_changed", tasks = SubagentTasks("agent-1") }));
        tracker.Observe(StreamMessage("system", new { subtype = "task_notification", task_id = "agent-1", status = "completed" }));
        tracker.CurrentWait.ShouldBe(ClaudeStreamCompletionTracker.PendingWait.None, "the turn is still running");

        tracker.Observe(StreamMessage("result", new { is_error = false })).ShouldBeFalse();
        tracker.CurrentWait.ShouldBe(ClaudeStreamCompletionTracker.PendingWait.NotificationTurn);

        tracker.Observe(StreamMessage("system", new { subtype = "init" }));
        tracker.CurrentWait.ShouldBe(ClaudeStreamCompletionTracker.PendingWait.None, "the follow-up turn is running");
        tracker.Observe(StreamMessage("result", new { is_error = false })).ShouldBeTrue();
        tracker.CurrentWait.ShouldBe(ClaudeStreamCompletionTracker.PendingWait.None);
    }

    [Test]
    public void Tracker_does_not_wait_for_a_notification_of_a_task_a_subagent_owns()
    {
        // That notification goes to the subagent, which the CLI resumes; only the subagent's own notification reaches the
        // main agent.
        var tracker = new ClaudeStreamCompletionTracker(TimeSpan.FromMinutes(1));

        tracker.Observe(StreamMessage("system", new { subtype = "init" }));
        tracker.Observe(
            StreamMessage(
                "system",
                new
                {
                    subtype = "task_started",
                    task_id = "bash-1",
                    task_type = "local_bash",
                    parent_task_id = "agent-1",
                    owned_by_subagent = true
                }));
        tracker.Observe(StreamMessage("system", new { subtype = "task_notification", task_id = "bash-1", status = "completed" }));
        tracker.Observe(StreamMessage("system", new { subtype = "background_tasks_changed", tasks = BackgroundCommandTasks() }));

        tracker.Observe(StreamMessage("result", new { is_error = false })).ShouldBeTrue();
    }

    [Test]
    public void Tracker_ignores_the_stopped_notifications_a_resumed_session_replays_for_tasks_it_never_started()
    {
        var tracker = new ClaudeStreamCompletionTracker(TimeSpan.FromMinutes(1));

        tracker.Observe(StreamMessage("system", new { subtype = "task_notification", task_id = "old-1", status = "stopped" }));
        tracker.Observe(StreamMessage("system", new { subtype = "task_notification", task_id = "old-2", status = "stopped" }));
        tracker.Observe(StreamMessage("system", new { subtype = "init" }));

        tracker.Observe(StreamMessage("result", new { is_error = false })).ShouldBeTrue();
    }

    [Test]
    public void The_replay_of_a_task_notification_is_not_the_replay_of_the_host_prompt()
    {
        var replay = NotificationReplay("agent-1");
        var tracker = new ClaudeStreamCompletionTracker(TimeSpan.FromMinutes(1));
        tracker.RecordHostPromptSent();

        ClaudeStreamCompletionTracker.IsTaskNotificationReplay(replay).ShouldBeTrue();
        ClaudeStreamCompletionTracker.IsPromptReplay(replay).ShouldBeFalse();
        ClaudeStreamCompletionTracker.IsPromptReplay(PromptEcho()).ShouldBeTrue();
        ClaudeStreamCompletionTracker.IsTaskNotificationReplay(PromptEcho()).ShouldBeFalse();

        tracker.Observe(replay).ShouldBeFalse();
        tracker.HostPromptAwaitingStart.ShouldBeTrue();
    }

    [TestCase(0)]
    [TestCase(-5)]
    public async Task ExecuteAsync_rejects_non_positive_notification_turn_timeout(int milliseconds)
    {
        var provider = CreateProvider();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (var _ in provider.ExecuteAsync(
                               new ClaudeCodeOptions { NotificationTurnTimeout = TimeSpan.FromMilliseconds(milliseconds) },
                               "hello"))
            {
            }
        });
    }

    [Test]
    public async Task ExecuteAsync_stops_waiting_for_subagent_background_task_after_timeout()
    {
        var provider = CreateProvider(
            messageBatches: [SubagentBackgroundTaskOutlivesSubagentBatch()],
            transportFailures: [new TransportFailure(HangAfterBatch: true)]);
        var options = new ClaudeCodeOptions
        {
            SessionId = "session-1",
            WorkingDirectory = "/tmp/project",
            SubagentBackgroundWorkTimeout = TimeSpan.FromMilliseconds(200)
        };

        var messages = new List<CliMessage>();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var message in provider.ExecuteAsync(options, "hello", guard.Token))
        {
            messages.Add(message);
        }

        guard.IsCancellationRequested.ShouldBeFalse();
        messages[^1].Type.ShouldBe("result");
        messages[^1].Content.GetProperty("result").GetString().ShouldBe("command is running, waiting");
        provider.DisposedTransportCount.ShouldBe(1);
    }

    [Test]
    public async Task ExecuteAsync_waits_for_host_prompt_turn_after_an_injected_turn_ends_first()
    {
        // On resume the CLI runs an injected turn for a stopped background-task notification before the host prompt.
        // Its "result" does not answer the prompt, which starts its own turn afterwards.
        var provider = CreateProvider(messageBatches:
        [
            [
                StreamMessage("system", new { subtype = "init" }),
                StreamMessage("result", new { is_error = false, result = "" }),
                StreamMessage("system", new { subtype = "init" }),
                StreamMessage("user", new { isReplay = true, message = new { role = "user", content = "hello" } }),
                StreamMessage("assistant", new { content = "final answer" }),
                StreamMessage("result", new { is_error = false, result = "final answer" })
            ]
        ]);

        var messages = await CollectAsync(provider);

        DescribeTypes(messages).ShouldBe(["system:init", "assistant", "result"]);
        messages[^1].Content.GetProperty("result").GetString().ShouldBe("final answer");
    }

    [Test]
    public async Task ExecuteAsync_resends_the_prompt_once_when_the_host_turn_does_not_start()
    {
        var provider = CreateProvider(
            messageBatches:
            [
                [
                    StreamMessage("system", new { subtype = "init" }),
                    StreamMessage("result", new { is_error = false, result = "" })
                ]
            ],
            transportFailures:
            [
                new TransportFailure(
                    EchoPrompt: false,
                    ReplyAfterResend:
                    [
                        StreamMessage("system", new { subtype = "init" }),
                        StreamMessage("user", new { isReplay = true, message = new { role = "user", content = "hello" } }),
                        StreamMessage("assistant", new { content = "resent answer" }),
                        StreamMessage("result", new { is_error = false, result = "resent answer" })
                    ])
            ]);

        var messages = await CollectAsync(
            provider,
            new ClaudeCodeOptions
            {
                SessionId = "session-1",
                WorkingDirectory = "/tmp/project",
                PromptStartTimeout = TimeSpan.FromMilliseconds(200)
            });

        DescribeTypes(messages).ShouldBe(["system:init", "assistant", "result"]);
        messages[^1].Content.GetProperty("result").GetString().ShouldBe("resent answer");
        provider.SentMessages.Count(static message => message.Type == "user").ShouldBe(2);
    }

    [Test]
    public async Task ExecuteAsync_ends_when_the_resent_prompt_does_not_start_either()
    {
        var provider = CreateProvider(
            messageBatches:
            [
                [
                    StreamMessage("system", new { subtype = "init" }),
                    StreamMessage("result", new { is_error = false, result = "" })
                ]
            ],
            transportFailures: [new TransportFailure(EchoPrompt: false, HangAfterBatch: true)]);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var messages = await CollectAsync(
            provider,
            new ClaudeCodeOptions
            {
                SessionId = "session-1",
                WorkingDirectory = "/tmp/project",
                PromptStartTimeout = TimeSpan.FromMilliseconds(200)
            },
            guard.Token);

        guard.IsCancellationRequested.ShouldBeFalse();
        messages.ShouldBeEmpty();
        provider.SentMessages.Count(static message => message.Type == "user").ShouldBe(2);
        provider.DisposedTransportCount.ShouldBe(1);
    }

    [Test]
    public async Task ExecuteAsync_still_delivers_a_turn_with_output_when_the_prompt_is_never_replayed()
    {
        var provider = CreateProvider(
            messageBatches:
            [
                [
                    StreamMessage("system", new { subtype = "init" }),
                    StreamMessage("assistant", new { content = "answer without replay" }),
                    StreamMessage("result", new { is_error = false, result = "answer without replay" })
                ]
            ],
            transportFailures: [new TransportFailure(EchoPrompt: false)]);

        var messages = await CollectAsync(provider);

        DescribeTypes(messages).ShouldBe(["system:init", "assistant", "result"]);
        messages[^1].Content.GetProperty("result").GetString().ShouldBe("answer without replay");
    }

    [TestCase(0)]
    [TestCase(-5)]
    public async Task ExecuteAsync_rejects_non_positive_prompt_start_timeout(int milliseconds)
    {
        var provider = CreateProvider();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (var _ in provider.ExecuteAsync(
                               new ClaudeCodeOptions { PromptStartTimeout = TimeSpan.FromMilliseconds(milliseconds) },
                               "hello"))
            {
            }
        });
    }

    [Test]
    public async Task ExecuteAsync_propagates_caller_cancellation_while_waiting_for_subagent_background_task()
    {
        var provider = CreateProvider(
            messageBatches: [SubagentBackgroundTaskOutlivesSubagentBatch()],
            transportFailures: [new TransportFailure(HangAfterBatch: true)]);
        using var cancellation = new CancellationTokenSource();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var message in provider.ExecuteAsync(
                               new ClaudeCodeOptions { SessionId = "session-1", WorkingDirectory = "/tmp/project" },
                               "hello",
                               cancellation.Token))
            {
                if (message.Type == "result")
                {
                    await cancellation.CancelAsync();
                }
            }
        });
    }

    [Test]
    public async Task ExecuteAsync_stop_interrupts_the_wait_for_background_work_armed_by_the_main_agent()
    {
        // The wait can last as long as the timeout the agent declared (two hours here), so a caller's stop must end it at once.
        var provider = CreateProvider(
            messageBatches:
            [
                [
                    StreamMessage("system", new { subtype = "init" }),
                    BackgroundBashToolUse(timeoutMilliseconds: 7_200_000),
                    StreamMessage("system", new { subtype = "background_tasks_changed", tasks = BackgroundCommandTasks("bash-1") }),
                    StreamMessage(
                        "system",
                        new { subtype = "task_started", task_id = "bash-1", task_type = "local_bash", tool_use_id = "toolu-bash" }),
                    StreamMessage("result", new { is_error = false, result = "waiting for the driver" })
                ]
            ],
            transportFailures: [new TransportFailure(HangAfterBatch: true)]);
        using var cancellation = new CancellationTokenSource();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stopwatch = Stopwatch.StartNew();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var message in provider.ExecuteAsync(
                               new ClaudeCodeOptions { SessionId = "session-1", WorkingDirectory = "/tmp/project" },
                               "hello",
                               cancellation.Token))
            {
                if (message.Type == "result")
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(100, guard.Token);
                        await cancellation.CancelAsync();
                    });
                }
            }
        });

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
        provider.DisposedTransportCount.ShouldBe(1);
    }

    [Test]
    public async Task ExecuteAsync_cancellation_mid_stream_throws_and_disposes_transport()
    {
        var provider = CreateProvider(
            messageBatches: [[StreamMessage("assistant", new { marker = "partial" })]],
            transportFailures: [new TransportFailure(HangAfterBatch: true)]);
        using var cancellation = new CancellationTokenSource();
        var messages = new List<CliMessage>();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var message in provider.ExecuteAsync(
                               new ClaudeCodeOptions { SessionId = "session-1", WorkingDirectory = "/tmp/project" },
                               "hello",
                               cancellation.Token))
            {
                messages.Add(message);
                await cancellation.CancelAsync();
            }
        });

        DescribeTypes(messages).ShouldBe(["assistant"]);
        provider.DisposedTransportCount.ShouldBe(1);
    }

    [TestCase(0)]
    [TestCase(-5)]
    public async Task ExecuteAsync_rejects_non_positive_subagent_background_work_timeout(int seconds)
    {
        var provider = CreateProvider();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (var _ in provider.ExecuteAsync(
                               new ClaudeCodeOptions { SubagentBackgroundWorkTimeout = TimeSpan.FromSeconds(seconds) },
                               "hello"))
            {
            }
        });
    }

    [Test]
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

    [Test]
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

    [Test]
    public async Task PingAsync_returns_failure_when_executable_is_missing()
    {
        var provider = CreateProvider(executableResolver: new MissingExecutableResolver());

        var result = await provider.PingAsync();

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
        result.ErrorMessage.ShouldContain("not found");
    }

    [Test]
    [Category("WindowsOnly")]
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

    [Test]
    [Category("RealCli")]
    [Category("RealCliInvocationContract")]
    [Category("External")]
    public async Task ExecuteAsync_real_cli_returns_actionable_authentication_failure_when_credentials_are_absent()
    {
        ExternalGate.Require(RealCliTestsEnvironmentVariable);

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

    [Test]
    [Category("RealCli")]
    [Category("RealCliInvocationContract")]
    [Category("External")]
    public async Task ExecuteAsync_real_cli_accepts_effort_flag_without_syntax_error()
    {
        ExternalGate.Require(RealCliTestsEnvironmentVariable);

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

    [Test]
    [Category("RealCli")]
    [Category("RealCliInvocationContract")]
    [Category("External")]
    public async Task ExecuteAsync_real_cli_can_resolve_from_hagicode_agent_cli_path_when_path_does_not_include_cli()
    {
        ExternalGate.Require(RealCliTestsEnvironmentVariable);

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

    [Test]
    [Category("RealCli")]
    [Category("RealCliInvocationContract")]
    [Category("RealCliWindowsZhCnWhitespaceShim")]
    [Category("External")]
    public async Task ExecuteAsync_real_cli_windows_zh_cn_whitespace_cmd_shim_surfaces_actionable_failure_without_mojibake()
    {
        ExternalGate.Require(RealCliTestsEnvironmentVariable);
        if (!OperatingSystem.IsWindows()
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

    [Test]
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

    [Test]
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

    private static async Task<List<CliMessage>> CollectAsync(
        ClaudeCodeProvider provider,
        ClaudeCodeOptions options,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<CliMessage>();
        await foreach (var message in provider.ExecuteAsync(options, "hello", cancellationToken))
        {
            messages.Add(message);
        }

        return messages;
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

    [Test]
    public async Task ExecuteAsync_logs_launch_shape_and_environment_names_without_secrets()
    {
        var logger = new CapturingClaudeLogger();
        var provider = CreateProvider(logger: logger);

        await CollectAsync(provider, new ClaudeCodeOptions
        {
            ApiKey = "sk-secret-api-key",
            SystemPrompt = "SECRET-SYSTEM-PROMPT",
            PluginDirectories = ["/plugins/hagi"],
            SessionId = "session-1",
            WorkingDirectory = "/tmp/project"
        });

        var arguments = logger.Entries.Single(static entry => entry.Message.StartsWith("Claude Code launch arguments:", StringComparison.Ordinal)).Message;
        arguments.ShouldContain("--plugin-dir /plugins/hagi");
        arguments.ShouldContain("--system-prompt");
        arguments.ShouldNotContain("SECRET-SYSTEM-PROMPT");

        var environment = logger.Entries.Single(static entry => entry.Message.StartsWith("Claude Code launch environment:", StringComparison.Ordinal)).Message;
        environment.ShouldContain("CLAUDE_CODE_ENTRYPOINT");
        environment.ShouldContain("ANTHROPIC_AUTH_TOKEN=present");
        AllLogText(logger).ShouldNotContain("sk-secret-api-key");
    }

    [Test]
    public async Task ExecuteAsync_launches_and_delivers_the_same_bytes_with_and_without_a_logger()
    {
        var options = new ClaudeCodeOptions
        {
            ApiKey = "sk-key",
            SystemPrompt = "system",
            PluginDirectories = ["/plugins/hagi"],
            SessionId = "session-1",
            WorkingDirectory = "/tmp/project"
        };
        var plain = CreateProvider();
        var logged = CreateProvider(logger: new CapturingClaudeLogger());

        var plainMessages = await CollectAsync(plain, options);
        var loggedMessages = await CollectAsync(logged, options);

        logged.LastStartContext!.Arguments.ShouldBe(plain.LastStartContext!.Arguments);
        EnvironmentPairs(logged.LastStartContext).ShouldBe(EnvironmentPairs(plain.LastStartContext));
        loggedMessages.Select(RawText).ShouldBe(plainMessages.Select(RawText));
        logged.SentMessages.Select(RawText).ShouldBe(plain.SentMessages.Select(RawText));
    }

    [Test]
    public async Task ExecuteAsync_logs_each_stdin_write_at_debug_without_its_content_and_sends_the_same_bytes()
    {
        var logger = new CapturingClaudeLogger();
        var logged = CreateProvider(logger: logger);
        var plain = CreateProvider();

        await CollectAsync(logged);
        await CollectAsync(plain);

        var promptEntry = logger.Entries.Single(static entry =>
            entry.Level == LogLevel.Debug && entry.Message.StartsWith("Claude Code stdin write 1:", StringComparison.Ordinal)).Message;
        promptEntry.ShouldContain("type=user");
        promptEntry.ShouldContain(" bytes=");
        promptEntry.ShouldNotContain("hello");
        logged.SentMessages.Select(RawText).ShouldBe(plain.SentMessages.Select(RawText));
    }

    [Test]
    public async Task ExecuteAsync_warns_when_a_registered_plugin_exposes_no_skills_and_still_yields_init()
    {
        var logger = new CapturingClaudeLogger();
        var init = InitBatch(
            [new { name = "hagi", source = "hagi@inline", path = "/plugins/hagi" }],
            ["debug"],
            ["Bash", "Artifact"]);
        var provider = CreateProvider(messageBatches: [init], logger: logger);

        var messages = await CollectAsync(provider, new ClaudeCodeOptions
        {
            PluginDirectories = ["/plugins/hagi"],
            SessionId = "session-1",
            WorkingDirectory = "/tmp/project"
        });

        var warning = logger.Entries.Single(static entry => entry.Level == LogLevel.Warning).Message;
        warning.ShouldContain("plugin registered but skills not exposed");
        warning.ShouldContain("hagi");
        warning.ShouldContain("/plugins/hagi");
        logger.Entries.Single(static entry => entry.Message.StartsWith("Claude Code init summary:", StringComparison.Ordinal))
            .Message.ShouldContain("hagi (hagi@inline)");
        ShouldKeepPayloadOf(messages.Single(static message => message.Type == "system"), init[0]);
    }

    [Test]
    public async Task ExecuteAsync_does_not_warn_when_the_registered_plugin_exposes_skills()
    {
        var logger = new CapturingClaudeLogger();
        var provider = CreateProvider(
            messageBatches: [InitBatch([new { name = "hagi", source = "hagi@inline", path = "/plugins/hagi" }], ["hagi:review"], ["Bash"])],
            logger: logger);

        await CollectAsync(provider, new ClaudeCodeOptions
        {
            PluginDirectories = ["/plugins/hagi"],
            SessionId = "session-1",
            WorkingDirectory = "/tmp/project"
        });

        logger.Entries.Where(static entry => entry.Level == LogLevel.Warning).ShouldBeEmpty();
    }

    [Test]
    public async Task ExecuteAsync_warns_when_a_passed_plugin_directory_was_not_registered()
    {
        var logger = new CapturingClaudeLogger();
        var provider = CreateProvider(messageBatches: [InitBatch([], ["debug"], ["Bash"])], logger: logger);

        await CollectAsync(provider, new ClaudeCodeOptions
        {
            PluginDirectories = ["/plugins/hagi"],
            SessionId = "session-1",
            WorkingDirectory = "/tmp/project"
        });

        var warning = logger.Entries.Single(static entry => entry.Level == LogLevel.Warning).Message;
        warning.ShouldContain("plugin directory passed but no plugin registered");
        warning.ShouldContain("/plugins/hagi");
    }

    [Test]
    public async Task ExecuteAsync_does_not_warn_about_plugins_when_no_plugin_directory_was_passed()
    {
        var logger = new CapturingClaudeLogger();
        var provider = CreateProvider(messageBatches: [InitBatch([], [], ["Bash"])], logger: logger);

        await CollectAsync(provider, new ClaudeCodeOptions { SessionId = "session-1", WorkingDirectory = "/tmp/project" });

        logger.Entries.Where(static entry => entry.Level == LogLevel.Warning).ShouldBeEmpty();
    }

    [Test]
    public async Task ExecuteAsync_delivers_a_malformed_init_unchanged_and_does_not_throw()
    {
        var logger = new CapturingClaudeLogger();
        var malformedInit = StreamMessage("system", new { subtype = "init", plugins = "not-an-array", skills = 5, tools = new { } });
        var provider = CreateProvider(messageBatches: [[malformedInit]], logger: logger);

        var messages = await CollectAsync(provider, new ClaudeCodeOptions
        {
            PluginDirectories = ["/plugins/hagi"],
            SessionId = "session-1",
            WorkingDirectory = "/tmp/project"
        });

        ShouldKeepPayloadOf(messages.Single(static message => message.Type == "system"), malformedInit);
    }

    private static IReadOnlyList<string> EnvironmentPairs(ProcessStartContext? startContext) =>
        (startContext!.EnvironmentVariables ?? new Dictionary<string, string?>())
        .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
        .Select(static pair => $"{pair.Key}={pair.Value}")
        .ToArray();

    private static string RawText(CliMessage message) => message.Content.GetRawText();

    // The delivered copy may carry debug metadata added by the provider, so each original field is compared, not the whole text.
    private static void ShouldKeepPayloadOf(CliMessage delivered, CliMessage original)
    {
        foreach (var property in original.Content.EnumerateObject())
        {
            delivered.Content.GetProperty(property.Name).GetRawText().ShouldBe(property.Value.GetRawText());
        }
    }

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

    private static object[] BackgroundCommandTasks(params string[] taskIds)
    {
        return taskIds.Select(static taskId => (object)new { task_id = taskId, task_type = "local_bash" }).ToArray();
    }

    private static CliMessage ToolUseMessage(string toolName, string toolUseId, object? input = null)
    {
        return StreamMessage(
            "assistant",
            new
            {
                message = new
                {
                    content = new object[]
                    {
                        new { type = "tool_use", id = toolUseId, name = toolName, input = input ?? new { } }
                    }
                }
            });
    }

    private static CliMessage MonitorToolUse(long timeoutMilliseconds = 120_000)
    {
        return ToolUseMessage("Monitor", "toolu-monitor", new { description = "wait test", timeout_ms = timeoutMilliseconds });
    }

    // The CLI replays the host's prompt (requested with --replay-user-messages) right after the init of the turn it starts.
    private static CliMessage PromptEcho()
    {
        return StreamMessage("user", new { isReplay = true, message = new { role = "user", content = "hello" } });
    }

    // The tool results that the CLI attaches queued notifications to: the main agent's own, or a subagent's when
    // parent_tool_use_id names the call that launched it.
    private static CliMessage ToolResultMessage(string toolUseId = "toolu-result", string? parentToolUseId = null)
    {
        var message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = toolUseId } } };
        return parentToolUseId is null
            ? StreamMessage("user", new { message })
            : StreamMessage("user", new { message, parent_tool_use_id = parentToolUseId });
    }

    // The CLI replays a task notification it hands to the agent, as the prompt text the agent receives.
    private static CliMessage NotificationReplay(params string[] taskIds)
    {
        var text = string.Concat(taskIds.Select(static taskId =>
            $"<task-notification>\n<task-id>{taskId}</task-id>\n<status>completed</status>\n</task-notification>\n"));
        return StreamMessage(
            "user",
            new { isReplay = true, origin = new { kind = "task-notification" }, message = new { role = "user", content = text } });
    }

    /// <summary>
    /// Feeds a turn in which the main agent starts a background command with no timeout and ends its turn; returns what
    /// the tracker says at that "result".
    /// </summary>
    private static bool ObserveUntimedBackgroundCommandTurn(ClaudeStreamCompletionTracker tracker)
    {
        tracker.Observe(StreamMessage("system", new { subtype = "init" }));
        tracker.Observe(BackgroundBashToolUse(timeoutMilliseconds: null));
        tracker.Observe(
            StreamMessage(
                "system",
                new { subtype = "task_started", task_id = "bash-1", task_type = "local_bash", tool_use_id = "toolu-bash" }));
        return tracker.Observe(StreamMessage("result", new { is_error = false }));
    }

    private static CliMessage BackgroundBashToolUse(long? timeoutMilliseconds)
    {
        return timeoutMilliseconds is { } timeout
            ? ToolUseMessage("Bash", "toolu-bash", new { command = "sleep 60", run_in_background = true, timeout })
            : ToolUseMessage("Bash", "toolu-bash", new { command = "sleep 60", run_in_background = true });
    }

    /// <summary>
    /// The stream up to the interim "result" of the main agent's follow-up turn: the subagent has reported back, but the
    /// background command it started is still running.
    /// </summary>
    private static IReadOnlyList<CliMessage> SubagentBackgroundTaskOutlivesSubagentBatch()
    {
        return
        [
            StreamMessage("system", new { subtype = "init" }),
            StreamMessage("system", new { subtype = "background_tasks_changed", tasks = SubagentTasks("agent-1") }),
            StreamMessage("result", new { is_error = false, result = "DONE" }),
            StreamMessage(
                "system",
                new
                {
                    subtype = "background_tasks_changed",
                    tasks = new object[]
                    {
                        new { task_id = "agent-1", task_type = "local_agent" },
                        new { task_id = "bash-1", task_type = "local_bash", parent_task_id = "agent-1" }
                    }
                }),
            StreamMessage("system", new { subtype = "task_notification", task_id = "agent-1", status = "completed" }),
            StreamMessage(
                "system",
                new
                {
                    subtype = "background_tasks_changed",
                    tasks = new object[] { new { task_id = "bash-1", task_type = "local_bash", parent_task_id = "agent-1" } }
                }),
            StreamMessage("system", new { subtype = "init" }),
            StreamMessage("result", new { is_error = false, result = "command is running, waiting" })
        ];
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

    [Test]
    public void BuildCommandArguments_sends_one_plugin_dir_per_plugin_directory_and_skips_blank_entries()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            PluginDirectories = ["/plugins/a", "  ", " /plugins/b "],
            ExtraArgs = new Dictionary<string, string?> { ["plugin-dir"] = "/plugins/raw" }
        });

        arguments.Where(static argument => argument == "--plugin-dir").Count().ShouldBe(3);
        arguments.ShouldContain("/plugins/a");
        arguments.ShouldContain("/plugins/b");
        arguments.ShouldContain("/plugins/raw");
    }

    private static TestClaudeCodeProvider CreateProviderWithRuntimeEnvironment(IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        return new TestClaudeCodeProvider(
            new StubExecutableResolver(),
            new StubCliProcessManager(),
            new StaticRuntimeEnvironmentResolver(runtimeEnvironment),
            messageBatches: null,
            transportFailures: null);
    }

    private static TestClaudeCodeProvider CreateProvider(
        CliExecutableResolver? executableResolver = null,
        CliProcessManager? processManager = null,
        IReadOnlyList<IReadOnlyList<CliMessage>>? messageBatches = null,
        IReadOnlyList<TransportFailure?>? transportFailures = null,
        ILogger<ClaudeCodeProvider>? logger = null)
    {
        return new TestClaudeCodeProvider(
            executableResolver ?? new StubExecutableResolver(),
            processManager ?? new StubCliProcessManager(),
            new StubRuntimeEnvironmentResolver(),
            messageBatches,
            transportFailures,
            logger);
    }

    private static IReadOnlyList<CliMessage> InitBatch(
        object[] plugins,
        string[] skills,
        string[] tools) =>
    [
        StreamMessage("system", new { subtype = "init", claude_code_version = "2.1.310", plugins, skills, tools }),
        StreamMessage("result", new { is_error = false })
    ];

    private static string AllLogText(CapturingClaudeLogger logger) =>
        string.Join('\n', logger.Entries.Select(static entry => entry.Message));

    /// <summary>
    /// Scripts how one transport attempt behaves. <see cref="OnSend" /> throws from SendAsync before the prompt is recorded;
    /// <see cref="OnReceive" /> throws from ReceiveAsync after the attempt's message batch has been yielded.
    /// <see cref="HangAfterBatch" /> keeps ReceiveAsync open after the batch until it is cancelled, like a CLI that is
    /// still running. <see cref="EchoPrompt" /> set to false models a CLI that never replays the prompt. When the prompt
    /// is sent again, <see cref="ReplyAfterResend" /> is yielded.
    /// </summary>
    private sealed record TransportFailure(
        Exception? OnSend = null,
        Exception? OnReceive = null,
        bool HangAfterBatch = false,
        bool EchoPrompt = true,
        IReadOnlyList<CliMessage>? ReplyAfterResend = null);

    private sealed class TestClaudeCodeProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver runtimeEnvironmentResolver,
        IReadOnlyList<IReadOnlyList<CliMessage>>? messageBatches,
        IReadOnlyList<TransportFailure?>? transportFailures,
        ILogger<ClaudeCodeProvider>? logger = null)
        : ClaudeCodeProvider(executableResolver, processManager, runtimeEnvironmentResolver, logger)
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
        private readonly TaskCompletionSource _resent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _sendCount;

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
            var batch = WithPromptEcho(getNextMessageBatch());
            for (var index = 0; index < batch.Count; index++)
            {
                yield return batch[index];
                if (index < batch.Count - 1)
                {
                    await Task.Yield();
                }
            }

            if (failure?.ReplyAfterResend is { } reply)
            {
                await _resent.Task.WaitAsync(cancellationToken);
                for (var index = 0; index < reply.Count; index++)
                {
                    yield return reply[index];
                }
            }

            if (failure?.HangAfterBatch == true)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
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
            if (++_sendCount > 1)
            {
                _resent.TrySetResult();
            }

            return Task.CompletedTask;
        }

        // The CLI replays a prompt right after the init of the turn it starts. Batches that script the replay themselves are left alone.
        private IReadOnlyList<CliMessage> WithPromptEcho(IReadOnlyList<CliMessage> batch)
        {
            if (failure?.EchoPrompt == false || batch.Any(static message => message.Type == "user"))
            {
                return batch;
            }

            var withEcho = batch.ToList();
            var initIndex = withEcho.FindIndex(static message =>
                message.Type == "system"
                && message.Content.TryGetProperty("subtype", out var subtype)
                && subtype.GetString() == "init");
            withEcho.Insert(
                initIndex + 1,
                StreamMessage("user", new { isReplay = true, message = new { role = "user", content = "hello" } }));
            return withEcho;
        }
    }

    private sealed record CapturedLogEntry(LogLevel Level, string Message);

    private sealed class CapturingClaudeLogger : ILogger<ClaudeCodeProvider>
    {
        private readonly object _sync = new();
        private readonly List<CapturedLogEntry> _entries = [];

        public IReadOnlyList<CapturedLogEntry> Entries
        {
            get
            {
                lock (_sync)
                {
                    return _entries.ToArray();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_sync)
            {
                _entries.Add(new CapturedLogEntry(logLevel, formatter(state, exception)));
            }
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
}