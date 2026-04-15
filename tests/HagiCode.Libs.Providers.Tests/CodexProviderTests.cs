using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Shouldly;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Execution;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Codex;

namespace HagiCode.Libs.Providers.Tests;

public sealed class CodexProviderTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";
    private const string RetryableTransportDisconnectMessage =
        "Reconnecting... 1/5 (stream disconnected before completion: error sending request for url https://api.example.com/v1)";
    private const string RetryableContentFilterDisconnectMessage =
        "Reconnecting... 1/5 (stream disconnected before completion: Incomplete response returned, reason: content_filter)";
    private const string RetryableLaterAttemptDisconnectMessage =
        "Reconnecting... 3/5 (stream disconnected before completion: temporary upstream overload)";
    private const string RetryableServerErrorDisconnectMessage =
        "Reconnecting... 5/5 (stream disconnected before completion: The server had an error processing your request. Sorry about that! Please try again.)";
    private const string RetryableGenericReconnectMessage =
        "Reconnecting... 2/5 (stream disconnected before completion: authentication gateway reset by peer)";
    private const string NonRetryableNonNumericReconnectMessage =
        "Reconnecting... soon (stream disconnected before completion: temporary upstream overload)";
    private const string RetryableGenericRefusalMessage = "I'm sorry, but I cannot assist with that request.";
    private const string RetryableGenericRefusalWithSuffixMessage = "I'm sorry, but I cannot assist with that request. Please revise the prompt.";
    private const string RetryableModelCapacityMessage = "Selected model is at capacity. Please try a different model.";
    private const string RetryableModelCapacityWithSuffixMessage = "Selected model is at capacity. Please try a different model. request id=req_capacity_123";
    private const string RetryableRateLimitExceededMessage = "exceeded retry limit, last status: 429 Too Many Requests";
    private const string RetryableRateLimitExceededWithSuffixMessage = "exceeded retry limit, last status: 429 Too Many Requests. request id=req_123";
    private static readonly string[] CodexExecutableCandidates = ["codex", "codex-cli"];

    [Fact]
    public void BuildCommandArguments_includes_expected_switches()
    {
        var provider = CreateProvider();
        var arguments = provider.BuildCommandArguments(new CodexOptions
        {
            Model = "gpt-5-codex",
            SandboxMode = "workspace-write",
            WorkingDirectory = "/tmp/project",
            ApprovalPolicy = "never",
            Profile = "team-alpha",
            ThreadId = "thread-123",
            SkipGitRepositoryCheck = true,
            AddDirectories = ["/tmp/project", "/tmp/shared"],
            ConfigOverrides =
            [
                "model_reasoning_effort=\"high\"",
                "sandbox_workspace_write.network_access=true",
                "web_search=\"live\""
            ],
            ExtraArgs = new Dictionary<string, string?>
            {
                ["full-auto"] = null
            }
        });

        arguments.ShouldBe(
        [
            "exec",
            "--experimental-json",
            "--model",
            "gpt-5-codex",
            "--sandbox",
            "workspace-write",
            "--cd",
            "/tmp/project",
            "--add-dir",
            "/tmp/project",
            "--add-dir",
            "/tmp/shared",
            "--skip-git-repo-check",
            "--config",
            "approval_policy=\"never\"",
            "-p",
            "team-alpha",
            "resume",
            "thread-123",
            "--config",
            "model_reasoning_effort=\"high\"",
            "--config",
            "sandbox_workspace_write.network_access=true",
            "--config",
            "web_search=\"live\"",
            "--full-auto"
        ]);
    }

    [Fact]
    public void BuildCommandArguments_trims_optional_values_and_preserves_internal_spaces()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new CodexOptions
        {
            Model = "  gpt-5 codex  ",
            WorkingDirectory = "  /tmp/my repo  ",
            ApprovalPolicy = "  on-request  ",
            Profile = "  team alpha  ",
            ThreadId = "  thread-456  ",
            AddDirectories = ["  /tmp/shared repo  ", "   "],
            ConfigOverrides =
            [
                "  model_reasoning_effort=\"high\"  ",
                "  web_search=\"disabled\"  "
            ],
            ExtraArgs = new Dictionary<string, string?>
            {
                ["notes"] = "  keep internal  spaces  ",
                ["ignored"] = "   "
            }
        });

        arguments.ShouldBe(
        [
            "exec",
            "--experimental-json",
            "--model",
            "gpt-5 codex",
            "--cd",
            "/tmp/my repo",
            "--add-dir",
            "/tmp/shared repo",
            "--config",
            "approval_policy=\"on-request\"",
            "-p",
            "team alpha",
            "resume",
            "thread-456",
            "--config",
            "model_reasoning_effort=\"high\"",
            "--config",
            "web_search=\"disabled\"",
            "--notes",
            "keep internal  spaces"
        ]);
    }

    [Fact]
    public void BuildCommandArguments_splits_legacy_multiline_config_into_discrete_entries()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new CodexOptions
        {
            ExtraArgs = new Dictionary<string, string?>
            {
                ["config"] = "model_reasoning_effort=\"high\"\r\nsandbox_workspace_write.network_access=true\nweb_search=\"live\""
            }
        });

        arguments.ShouldBe(
        [
            "exec",
            "--experimental-json",
            "--config",
            "model_reasoning_effort=\"high\"",
            "--config",
            "sandbox_workspace_write.network_access=true",
            "--config",
            "web_search=\"live\""
        ]);
    }

    [Fact]
    public void BuildCommandArguments_rejects_legacy_config_without_value()
    {
        var provider = CreateProvider();

        var exception = Should.Throw<InvalidOperationException>(() => provider.BuildCommandArguments(new CodexOptions
        {
            ExtraArgs = new Dictionary<string, string?>
            {
                ["config"] = null
            }
        }));

        exception.Message.ShouldContain("ConfigOverrides");
        exception.Message.ShouldContain("config");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildCommandArguments_omits_profile_switch_for_null_or_blank_profile(string? profile)
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new CodexOptions
        {
            Profile = profile
        });

        arguments.ShouldBe(["exec", "--experimental-json"]);
        arguments.ShouldNotContain("-p");
        arguments.ShouldNotContain("--profile");
    }

    [Fact]
    public async Task ExecuteAsync_uses_custom_executable_and_streams_messages()
    {
        var provider = CreateProvider();
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new CodexOptions
                           {
                               ExecutablePath = "/custom/codex",
                               ApiKey = "token",
                               BaseUrl = "https://api.example.com",
                               EnvironmentVariables = new Dictionary<string, string?>
                               {
                                   ["CUSTOM_FLAG"] = "1"
                               }
                           },
                           "hello"))
        {
            messages.Add(message);
        }

        provider.LastStartContext!.ExecutablePath.ShouldBe("/custom/codex");
        provider.LastStartContext.EnvironmentVariables!["CODEX_API_KEY"].ShouldBe("token");
        provider.LastStartContext.EnvironmentVariables["OPENAI_BASE_URL"].ShouldBe("https://api.example.com");
        provider.LastStartContext.EnvironmentVariables["CODEX_INTERNAL_ORIGINATOR_OVERRIDE"].ShouldBe("codex_sdk_csharp");
        provider.LastStartContext.EnvironmentVariables["CUSTOM_FLAG"].ShouldBe("1");
        messages.Select(static message => message.Type).ShouldBe(["item.completed", "turn.completed"]);
        provider.SentMessages.ShouldHaveSingleItem();
        provider.SentMessages[0].Content.GetProperty("input").GetString().ShouldBe("hello");
    }

    [Fact]
    public async Task ExecuteAsync_delivers_chinese_prompt_with_default_utf8_input_encoding()
    {
        const string chinesePrompt = "请用中文解释 UTF-8 输入。";
        var provider = CreateProvider();

        await DrainCliMessagesAsync(provider.ExecuteAsync(
            new CodexOptions
            {
                WorkingDirectory = "/tmp/project"
            },
            chinesePrompt));

        provider.LastStartContext.ShouldNotBeNull();
        provider.LastStartContext.InputEncoding.WebName.ShouldBe(Encoding.UTF8.WebName);
        provider.SentMessages.ShouldHaveSingleItem();
        provider.SentMessages[0].Content.GetProperty("input").GetString().ShouldBe(chinesePrompt);
    }

    [Fact]
    public async Task ExecuteAsync_windows_cmd_shim_path_keeps_utf8_input_encoding_for_chinese_prompt()
    {
        const string chinesePrompt = "继续用中文回复。";
        var processManager = new WindowsBatchCliProcessManager(@"C:\tools\codex.cmd");
        var provider = CreateWindowsFocusedProvider(processManager);

        await DrainCliMessagesAsync(provider.ExecuteAsync(
            new CodexOptions
            {
                ExecutablePath = "codex",
                WorkingDirectory = @"C:\workspace\project"
            },
            chinesePrompt));

        provider.LastProcessStartInfo.ShouldNotBeNull();
        provider.LastProcessStartInfo.FileName.ShouldBe("cmd.exe");
        provider.LastProcessStartInfo.StandardInputEncoding.ShouldNotBeNull();
        provider.LastProcessStartInfo.StandardInputEncoding.WebName.ShouldBe(Encoding.UTF8.WebName);
        provider.SentMessages.ShouldHaveSingleItem();
        provider.SentMessages[0].Content.GetProperty("input").GetString().ShouldBe(chinesePrompt);
    }

    [Fact]
    public async Task ExecuteAsync_reuses_pooled_thread_id_for_follow_up_requests()
    {
        var provider = CreateProvider(messageBatches:
        [
            [
                new CliMessage("thread.started", JsonSerializer.SerializeToElement(new { type = "thread.started", thread_id = "thread-xyz" })),
                new CliMessage("turn.completed", JsonSerializer.SerializeToElement(new { type = "turn.completed" }))
            ],
            [
                new CliMessage("turn.completed", JsonSerializer.SerializeToElement(new { type = "turn.completed" }))
            ]
        ]);

        await foreach (var _ in provider.ExecuteAsync(
                           new CodexOptions
                           {
                               WorkingDirectory = "/tmp/project",
                               LogicalSessionKey = "session-1"
                           },
                           "first"))
        {
        }

        await foreach (var _ in provider.ExecuteAsync(
                           new CodexOptions
                           {
                               WorkingDirectory = "/tmp/project",
                               LogicalSessionKey = "session-1"
                           },
                           "second"))
        {
        }

        provider.StartContexts.Count.ShouldBe(2);
        provider.StartContexts[1].Arguments.ShouldContain("resume");
        provider.StartContexts[1].Arguments.ShouldContain("thread-xyz");
    }

    [Fact]
    public async Task ExecuteAsync_restarts_process_but_keeps_thread_id_when_same_logical_session_changes_runtime_fingerprint()
    {
        var provider = CreateProvider(messageBatches:
        [
            [
                new CliMessage("thread.started", JsonSerializer.SerializeToElement(new { type = "thread.started", thread_id = "thread-old" })),
                new CliMessage("turn.completed", JsonSerializer.SerializeToElement(new { type = "turn.completed" }))
            ],
            [
                new CliMessage("thread.started", JsonSerializer.SerializeToElement(new { type = "thread.started", thread_id = "thread-old" })),
                new CliMessage("turn.completed", JsonSerializer.SerializeToElement(new { type = "turn.completed" }))
            ]
        ]);

        await DrainCliMessagesAsync(provider.ExecuteAsync(
            new CodexOptions
            {
                WorkingDirectory = "/tmp/project",
                LogicalSessionKey = "session-1",
                ApprovalPolicy = "never"
            },
            "first"));

        await DrainCliMessagesAsync(provider.ExecuteAsync(
            new CodexOptions
            {
                WorkingDirectory = "/tmp/project",
                LogicalSessionKey = "session-1",
                ApprovalPolicy = "on-request"
            },
            "second"));

        provider.StartContexts.Count.ShouldBe(2);
        provider.StartContexts[1].Arguments.ShouldContain("resume");
        provider.StartContexts[1].Arguments.ShouldContain("thread-old");
    }

    [Fact]
    public void TryExtractTerminalMessage_recognizes_retryable_samples()
    {
        CodexProvider.TryExtractTerminalMessage(
                JsonSerializer.SerializeToElement(new { type = "turn.failed", error = new { message = RetryableTransportDisconnectMessage } }),
                out var transportMessage)
            .ShouldBeTrue();
        transportMessage.ShouldBe(RetryableTransportDisconnectMessage);

        CodexProvider.TryExtractTerminalMessage(
                JsonSerializer.SerializeToElement(new { type = "turn.failed", error = new { message = RetryableContentFilterDisconnectMessage } }),
                out var contentFilterMessage)
            .ShouldBeTrue();
        contentFilterMessage.ShouldBe(RetryableContentFilterDisconnectMessage);

        CodexProvider.TryExtractTerminalMessage(
                JsonSerializer.SerializeToElement(new { type = "turn.failed", error = new { message = RetryableServerErrorDisconnectMessage } }),
                out var serverErrorMessage)
            .ShouldBeTrue();
        serverErrorMessage.ShouldBe(RetryableServerErrorDisconnectMessage);

        CodexProvider.TryExtractTerminalMessage(
                JsonSerializer.SerializeToElement(new { type = "turn.completed", result = RetryableGenericRefusalMessage }),
                out var refusalMessage)
            .ShouldBeTrue();
        refusalMessage.ShouldBe(RetryableGenericRefusalMessage);
    }

    [Fact]
    public void TryExtractRetryableTerminalSummary_recognizes_new_server_error_reconnect_prefix()
    {
        CodexProvider.TryExtractRetryableTerminalSummary(RetryableServerErrorDisconnectMessage, out var retrySummary)
            .ShouldBeTrue();

        retrySummary.ShouldBe(RetryableServerErrorDisconnectMessage);
    }

    [Fact]
    public void TryExtractRetryableTerminalSummary_recognizes_reconnect_prefix_without_known_suffix()
    {
        CodexProvider.TryExtractRetryableTerminalSummary(RetryableGenericReconnectMessage, out var retrySummary)
            .ShouldBeTrue();

        retrySummary.ShouldBe(RetryableGenericReconnectMessage);
    }

    [Fact]
    public void TryExtractRetryableTerminalSummary_recognizes_later_reconnect_attempts()
    {
        CodexProvider.TryExtractRetryableTerminalSummary(RetryableLaterAttemptDisconnectMessage, out var retrySummary)
            .ShouldBeTrue();

        retrySummary.ShouldBe(RetryableLaterAttemptDisconnectMessage);
    }

    [Fact]
    public void TryExtractRetryableTerminalSummary_rejects_non_numeric_reconnect_prefix()
    {
        CodexProvider.TryExtractRetryableTerminalSummary(NonRetryableNonNumericReconnectMessage, out var retrySummary)
            .ShouldBeFalse();

        retrySummary.ShouldBeEmpty();
    }

    [Fact]
    public void TryExtractRetryableTerminalSummary_recognizes_rate_limit_retry_cap_message()
    {
        CodexProvider.TryExtractRetryableTerminalSummary(RetryableRateLimitExceededMessage, out var retrySummary)
            .ShouldBeTrue();

        retrySummary.ShouldBe(RetryableRateLimitExceededMessage);
    }

    [Fact]
    public void TryExtractRetryableTerminalSummary_recognizes_generic_refusal_prefix_with_suffix()
    {
        CodexProvider.TryExtractRetryableTerminalSummary(RetryableGenericRefusalWithSuffixMessage, out var retrySummary)
            .ShouldBeTrue();

        retrySummary.ShouldBe(RetryableGenericRefusalWithSuffixMessage);
    }

    [Fact]
    public void TryExtractRetryableTerminalSummary_recognizes_model_capacity_prefix()
    {
        CodexProvider.TryExtractRetryableTerminalSummary(RetryableModelCapacityMessage, out var retrySummary)
            .ShouldBeTrue();

        retrySummary.ShouldBe(RetryableModelCapacityMessage);
    }

    [Fact]
    public void TryExtractRetryableTerminalSummary_recognizes_model_capacity_prefix_with_suffix()
    {
        CodexProvider.TryExtractRetryableTerminalSummary(RetryableModelCapacityWithSuffixMessage, out var retrySummary)
            .ShouldBeTrue();

        retrySummary.ShouldBe(RetryableModelCapacityWithSuffixMessage);
    }

    [Fact]
    public void TryExtractRetryableTerminalSummary_recognizes_rate_limit_prefix_with_suffix()
    {
        CodexProvider.TryExtractRetryableTerminalSummary(RetryableRateLimitExceededWithSuffixMessage, out var retrySummary)
            .ShouldBeTrue();

        retrySummary.ShouldBe(RetryableRateLimitExceededWithSuffixMessage);
    }

    [Fact]
    public async Task ExecuteAsync_reuses_pooled_thread_id_after_retryable_failure_replay()
    {
        var provider = CreateProvider(messageBatches:
        [
            [
                new CliMessage("thread.started", JsonSerializer.SerializeToElement(new { type = "thread.started", thread_id = "thread-retry" })),
                new CliMessage("turn.failed", JsonSerializer.SerializeToElement(new { type = "turn.failed", error = new { message = RetryableServerErrorDisconnectMessage } }))
            ],
            [
                new CliMessage("turn.completed", JsonSerializer.SerializeToElement(new { type = "turn.completed" }))
            ]
        ]);

        await DrainCliMessagesAsync(provider.ExecuteAsync(
            new CodexOptions
            {
                WorkingDirectory = "/tmp/project",
                LogicalSessionKey = "session-retry"
            },
            "first"));

        await DrainCliMessagesAsync(provider.ExecuteAsync(
            new CodexOptions
            {
                WorkingDirectory = "/tmp/project",
                LogicalSessionKey = "session-retry",
                ThreadId = "thread-retry"
            },
            "replay"));

        provider.StartContexts.Count.ShouldBe(2);
        provider.StartContexts[1].Arguments.ShouldContain("resume");
        provider.StartContexts[1].Arguments.ShouldContain("thread-retry");
    }

    [Fact]
    public async Task ExecuteAsync_keeps_anonymous_retry_replays_unbound()
    {
        var provider = CreateProvider(messageBatches:
        [
            [
                new CliMessage("thread.started", JsonSerializer.SerializeToElement(new { type = "thread.started", thread_id = "thread-anon-retry" })),
                new CliMessage("turn.failed", JsonSerializer.SerializeToElement(new { type = "turn.failed", error = new { message = RetryableTransportDisconnectMessage } }))
            ],
            [
                new CliMessage("turn.completed", JsonSerializer.SerializeToElement(new { type = "turn.completed" }))
            ]
        ]);

        await DrainCliMessagesAsync(provider.ExecuteAsync(
            new CodexOptions
            {
                WorkingDirectory = "/tmp/project"
            },
            "first"));

        await DrainCliMessagesAsync(provider.ExecuteAsync(
            new CodexOptions
            {
                WorkingDirectory = "/tmp/project"
            },
            "replay"));

        provider.StartContexts.Count.ShouldBe(2);
        provider.StartContexts[1].Arguments.ShouldNotContain("resume");
    }

    [Fact]
    public async Task ExecuteAsync_does_not_reuse_anonymous_requests_for_same_working_directory()
    {
        var provider = CreateProvider(messageBatches:
        [
            [
                new CliMessage("thread.started", JsonSerializer.SerializeToElement(new { type = "thread.started", thread_id = "thread-anon-1" })),
                new CliMessage("turn.completed", JsonSerializer.SerializeToElement(new { type = "turn.completed" }))
            ],
            [
                new CliMessage("turn.completed", JsonSerializer.SerializeToElement(new { type = "turn.completed" }))
            ]
        ]);

        await foreach (var _ in provider.ExecuteAsync(
                           new CodexOptions
                           {
                               WorkingDirectory = "/tmp/project"
                           },
                           "first"))
        {
        }

        await foreach (var _ in provider.ExecuteAsync(
                           new CodexOptions
                           {
                               WorkingDirectory = "/tmp/project"
                           },
                           "second"))
        {
        }

        provider.StartContexts.Count.ShouldBe(2);
        provider.StartContexts[1].Arguments.ShouldNotContain("resume");
    }

    [Fact]
    public async Task ExecuteAsync_allows_concurrent_execution_for_different_logical_sessions_in_same_directory()
    {
        var firstScript = new CoordinatedTransportScript(threadId: "thread-a");
        var secondScript = new CoordinatedTransportScript(threadId: "thread-b");
        var provider = CreateCoordinatedProvider(firstScript, secondScript);

        var firstTask = DrainCliMessagesAsync(provider.ExecuteAsync(
            new CodexOptions
            {
                WorkingDirectory = "/tmp/project",
                LogicalSessionKey = "session-a"
            },
            "first"));

        await firstScript.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var secondTask = DrainCliMessagesAsync(provider.ExecuteAsync(
            new CodexOptions
            {
                WorkingDirectory = "/tmp/project",
                LogicalSessionKey = "session-b"
            },
            "second"));

        await secondScript.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        firstScript.Release.TrySetResult();
        secondScript.Release.TrySetResult();

        await Task.WhenAll(firstTask, secondTask);

        provider.StartContexts.Count.ShouldBe(2);
        provider.StartContexts[0].Arguments.ShouldNotContain("resume");
        provider.StartContexts[1].Arguments.ShouldNotContain("resume");
    }

    [Fact]
    public async Task ExecuteAsync_serializes_same_logical_session_and_reuses_thread_after_reindex()
    {
        var firstScript = new CoordinatedTransportScript(threadId: "thread-serial");
        var secondScript = new CoordinatedTransportScript();
        var provider = CreateCoordinatedProvider(firstScript, secondScript);

        var firstTask = DrainCliMessagesAsync(provider.ExecuteAsync(
            new CodexOptions
            {
                WorkingDirectory = "/tmp/project",
                LogicalSessionKey = "session-serial"
            },
            "first"));

        await firstScript.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var secondTask = DrainCliMessagesAsync(provider.ExecuteAsync(
            new CodexOptions
            {
                WorkingDirectory = "/tmp/project",
                LogicalSessionKey = "session-serial"
            },
            "second"));

        await Task.Delay(150);
        secondScript.Started.Task.IsCompleted.ShouldBeFalse();

        firstScript.Release.TrySetResult();
        await firstTask.WaitAsync(TimeSpan.FromSeconds(1));
        await secondScript.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        secondScript.Release.TrySetResult();
        await secondTask.WaitAsync(TimeSpan.FromSeconds(1));

        provider.StartContexts.Count.ShouldBe(2);
        provider.StartContexts[1].Arguments.ShouldContain("resume");
        provider.StartContexts[1].Arguments.ShouldContain("thread-serial");
    }

    [Fact]
    public async Task ExecuteAsync_uses_one_shot_codex_path_when_pooling_is_disabled()
    {
        var provider = CreateProvider(messageBatches:
        [
            [
                new CliMessage("thread.started", JsonSerializer.SerializeToElement(new { type = "thread.started", thread_id = "thread-xyz" })),
                new CliMessage("turn.completed", JsonSerializer.SerializeToElement(new { type = "turn.completed" }))
            ],
            [
                new CliMessage("turn.completed", JsonSerializer.SerializeToElement(new { type = "turn.completed" }))
            ]
        ]);

        await foreach (var _ in provider.ExecuteAsync(
                           new CodexOptions
                           {
                               WorkingDirectory = "/tmp/project",
                               PoolSettings = new HagiCode.Libs.Core.Acp.CliPoolSettings { Enabled = false }
                           },
                           "first"))
        {
        }

        await foreach (var _ in provider.ExecuteAsync(
                           new CodexOptions
                           {
                               WorkingDirectory = "/tmp/project",
                               PoolSettings = new HagiCode.Libs.Core.Acp.CliPoolSettings { Enabled = false }
                           },
                           "second"))
        {
        }

        provider.StartContexts.Count.ShouldBe(2);
        provider.StartContexts[1].Arguments.ShouldNotContain("resume");
    }

    [Theory]
    [InlineData("npm")]
    [InlineData("npx")]
    public async Task ExecuteAsync_on_windows_resolves_npm_style_short_names_to_cmd(string executableName)
    {
        using var sandbox = new DirectorySandbox();
        var resolvedExecutable = sandbox.CreateFile($"{executableName}.cmd");
        var provider = CreateProvider(
            executableResolver: new CliExecutableResolver(static () => true),
            runtimeEnvironmentResolver: new StubRuntimeEnvironmentResolver(new Dictionary<string, string?>
            {
                ["PATH"] = sandbox.RootPath,
                ["PATHEXT"] = ".EXE;.CMD;.BAT"
            }));
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new CodexOptions
                           {
                               ExecutablePath = executableName
                           },
                           "hello"))
        {
            messages.Add(message);
        }

        provider.LastStartContext.ShouldNotBeNull();
        provider.LastStartContext.ExecutablePath.ShouldBe(resolvedExecutable);
        messages.Select(static message => message.Type).ShouldBe(["item.completed", "turn.completed"]);
    }

    [Fact]
    public async Task PingAsync_reports_version_when_process_succeeds()
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResult = new ProcessResult(0, "codex 1.2.3", string.Empty)
        };
        var provider = CreateProvider(processManager: processManager);

        var result = await provider.PingAsync();

        result.Success.ShouldBeTrue();
        result.Version.ShouldBe("codex 1.2.3");
    }

    [Fact]
    public async Task PingAsync_prefers_injected_execution_facade()
    {
        var executionFacade = new StubExecutionFacade
        {
            Result = new CliExecutionResult
            {
                Status = CliExecutionStatus.Success,
                ExitCode = 0,
                CommandPreview = "codex --version",
                StandardOutput = "codex 9.9.9",
                StartedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow
            }
        };
        var provider = CreateProvider(executionFacade: executionFacade);

        var result = await provider.PingAsync();

        executionFacade.Requests.ShouldHaveSingleItem();
        executionFacade.Requests[0].ExecutablePath.ShouldBe("codex");
        executionFacade.Requests[0].Arguments.ShouldBe(["--version"]);
        result.Success.ShouldBeTrue();
        result.Version.ShouldBe("codex 9.9.9");
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
    [Trait("Category", "RealCli")]
    [Trait("Category", "RealCliInvocationContract")]
    public async Task ExecuteAsync_real_cli_returns_actionable_authentication_failure_when_credentials_are_absent()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        using var sandbox = new RealCliInvocationSandbox();
        await using var provider = new CodexProvider(new CliExecutableResolver(), new CliProcessManager(), sandbox);

        var failureMessage = await RealCliInvocationTestHarness.CaptureFailureMessageAsync(
            provider,
            new CodexOptions
            {
                WorkingDirectory = sandbox.WorkingDirectory,
                AddDirectories = [sandbox.WorkingDirectory],
                SandboxMode = "workspace-write",
                ApprovalPolicy = "never",
                SkipGitRepositoryCheck = true,
                PoolSettings = new CliPoolSettings
                {
                    Enabled = false
                }
            },
            "Reply with exactly the word 'pong'.",
            TimeSpan.FromSeconds(45));

        RealCliInvocationTestHarness.AssertActionableFailure("codex", failureMessage);
    }

    private static TestCodexProvider CreateProvider(
        CliExecutableResolver? executableResolver = null,
        CliProcessManager? processManager = null,
        ICliExecutionFacade? executionFacade = null,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null,
        IReadOnlyList<IReadOnlyList<CliMessage>>? messageBatches = null)
    {
        return new TestCodexProvider(
            executableResolver ?? new StubExecutableResolver(),
            processManager ?? new StubCliProcessManager(),
            runtimeEnvironmentResolver ?? new StubRuntimeEnvironmentResolver(),
            executionFacade,
            messageBatches);
    }

    private static CoordinatedCodexProvider CreateCoordinatedProvider(params CoordinatedTransportScript[] scripts)
    {
        return new CoordinatedCodexProvider(
            new StubExecutableResolver(),
            new StubCliProcessManager(),
            new StubRuntimeEnvironmentResolver(),
            null,
            scripts);
    }

    private static WindowsFocusedCodexProvider CreateWindowsFocusedProvider(
        CliProcessManager processManager,
        IReadOnlyList<IReadOnlyList<CliMessage>>? messageBatches = null)
    {
        return new WindowsFocusedCodexProvider(
            new StubExecutableResolver(),
            processManager,
            new StubRuntimeEnvironmentResolver(),
            null,
            messageBatches);
    }

    private static async Task DrainCliMessagesAsync(IAsyncEnumerable<CliMessage> stream)
    {
        await foreach (var _ in stream)
        {
        }
    }

    private sealed class TestCodexProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver runtimeEnvironmentResolver,
        ICliExecutionFacade? executionFacade,
        IReadOnlyList<IReadOnlyList<CliMessage>>? messageBatches)
        : CodexProvider(executableResolver, processManager, runtimeEnvironmentResolver, executionFacade)
    {
        public ProcessStartContext? LastStartContext { get; private set; }
        public List<ProcessStartContext> StartContexts { get; } = [];
        public List<CliMessage> SentMessages { get; } = [];
        private readonly Queue<IReadOnlyList<CliMessage>> _messageBatches = new(messageBatches ?? [DefaultBatch]);

        internal static IReadOnlyList<CliMessage> DefaultBatch =>
        [
            new CliMessage(
                "item.completed",
                JsonSerializer.SerializeToElement(new
                {
                    type = "item.completed",
                    item = new
                    {
                        type = "agent_message",
                        text = "pong"
                    }
                })),
            new CliMessage(
                "turn.completed",
                JsonSerializer.SerializeToElement(new
                {
                    type = "turn.completed",
                    usage = new
                    {
                        input_tokens = 1,
                        cached_input_tokens = 0,
                        output_tokens = 1
                    }
                }))
        ];

        protected override ICliTransport CreateTransport(ProcessStartContext startContext)
        {
            LastStartContext = startContext;
            StartContexts.Add(startContext);
            var batch = _messageBatches.Count > 0 ? _messageBatches.Dequeue() : DefaultBatch;
            return new StubTransport(SentMessages, batch);
        }
    }

    private sealed class StubTransport(List<CliMessage> sentMessages, IReadOnlyList<CliMessage> messages) : ICliTransport
    {
        public bool IsConnected { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public Task InterruptAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async IAsyncEnumerable<CliMessage> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var message in messages)
            {
                yield return message;
                await Task.Yield();
            }
        }

        public Task SendAsync(CliMessage message, CancellationToken cancellationToken = default)
        {
            sentMessages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class CoordinatedCodexProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver runtimeEnvironmentResolver,
        ICliExecutionFacade? executionFacade,
        IReadOnlyList<CoordinatedTransportScript> scripts)
        : CodexProvider(executableResolver, processManager, runtimeEnvironmentResolver, executionFacade)
    {
        private readonly Queue<CoordinatedTransportScript> _scripts = new(scripts);

        public List<ProcessStartContext> StartContexts { get; } = [];

        protected override ICliTransport CreateTransport(ProcessStartContext startContext)
        {
            StartContexts.Add(startContext);
            return new CoordinatedTransport(_scripts.Dequeue());
        }
    }

    private sealed class WindowsFocusedCodexProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver runtimeEnvironmentResolver,
        ICliExecutionFacade? executionFacade,
        IReadOnlyList<IReadOnlyList<CliMessage>>? messageBatches)
        : CodexProvider(executableResolver, processManager, runtimeEnvironmentResolver, executionFacade)
    {
        private readonly CliProcessManager _processManager = processManager;
        private readonly Queue<IReadOnlyList<CliMessage>> _messageBatches = new(messageBatches ?? [TestCodexProvider.DefaultBatch]);

        public ProcessStartInfo? LastProcessStartInfo { get; private set; }

        public List<CliMessage> SentMessages { get; } = [];

        protected override ICliTransport CreateTransport(ProcessStartContext startContext)
        {
            LastProcessStartInfo = _processManager.CreateStartInfo(startContext);
            var batch = _messageBatches.Count > 0 ? _messageBatches.Dequeue() : TestCodexProvider.DefaultBatch;
            return new StubTransport(SentMessages, batch);
        }
    }

    private sealed class CoordinatedTransportScript(string? threadId = null)
    {
        public string? ThreadId { get; } = threadId;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class CoordinatedTransport(CoordinatedTransportScript script) : ICliTransport
    {
        public bool IsConnected { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public Task InterruptAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async IAsyncEnumerable<CliMessage> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            script.Started.TrySetResult();
            await script.Release.Task.WaitAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(script.ThreadId))
            {
                yield return new CliMessage(
                    "thread.started",
                    JsonSerializer.SerializeToElement(new { type = "thread.started", thread_id = script.ThreadId }));
            }

            yield return new CliMessage(
                "turn.completed",
                JsonSerializer.SerializeToElement(new { type = "turn.completed" }));

            script.Completed.TrySetResult();
        }

        public Task SendAsync(CliMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
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
        private readonly IReadOnlyDictionary<string, string?> _environment;

        public StubRuntimeEnvironmentResolver(IReadOnlyDictionary<string, string?>? environment = null)
        {
            _environment = environment ?? new Dictionary<string, string?>
            {
                ["PATH"] = "/tmp/bin"
            };
        }

        public Task<IReadOnlyDictionary<string, string?>> ResolveAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_environment);
        }
    }

    private sealed class DirectorySandbox : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"hagicode-libs-provider-{Guid.NewGuid():N}");

        public DirectorySandbox()
        {
            Directory.CreateDirectory(_root);
        }

        public string RootPath => _root;

        public string CreateFile(string relativePath)
        {
            var fullPath = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, string.Empty);
            return fullPath;
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
    }

    private sealed class StubExecutionFacade : ICliExecutionFacade
    {
        public List<CliExecutionRequest> Requests { get; } = [];

        public CliExecutionResult Result { get; init; } = new()
        {
            Status = CliExecutionStatus.Success,
            ExitCode = 0,
            CommandPreview = "codex --version",
            StandardOutput = "codex 1.0.0",
            StartedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow
        };

        public Task<CliExecutionResult> ExecuteAsync(CliExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Result);
        }

        public async IAsyncEnumerable<CliExecutionEvent> ExecuteStreamingAsync(CliExecutionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            yield return new CliExecutionEvent
            {
                Kind = CliExecutionEventKind.Completed,
                Result = Result,
                TimestampUtc = DateTimeOffset.UtcNow
            };

            await Task.CompletedTask;
        }
    }

    private sealed class StubCliProcessManager : CliProcessManager
    {
        public ProcessResult ExecuteResult { get; init; } = new(0, "codex 1.0.0", string.Empty);

        public override Task<ProcessResult> ExecuteAsync(ProcessStartContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ExecuteResult);
        }
    }

    private sealed class WindowsBatchCliProcessManager(string resolvedExecutablePath) : CliProcessManager
    {
        protected override bool IsWindows() => true;

        protected override string ResolveExecutablePath(string executablePath, IReadOnlyDictionary<string, string?>? environmentVariables)
            => resolvedExecutablePath;
    }

    private static bool IsRealCliTestsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(RealCliTestsEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}
