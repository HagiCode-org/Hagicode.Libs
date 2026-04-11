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
    public void BuildCommandArguments_omits_session_id_when_continue_is_enabled()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            ContinueConversation = true,
            SessionId = "session-id"
        });

        arguments.ShouldContain("--continue");
        arguments.ShouldNotContain("--session-id");
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
    public async Task ExecuteAsync_reuses_warm_transport_for_same_session_key_when_pooling_is_enabled()
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

        provider.CreatedTransportCount.ShouldBe(1);
        provider.SentMessages.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ExecuteAsync_restarts_transport_when_runtime_inputs_change_but_session_key_matches()
    {
        var provider = CreateProvider();
        var restartedMessages = new List<CliMessage>();

        await foreach (var _ in provider.ExecuteAsync(
                           new ClaudeCodeOptions
                           {
                               SessionId = "session-1",
                               WorkingDirectory = "/tmp/project-a",
                               Model = "claude-sonnet"
                           },
                           "hello"))
        {
        }

        await foreach (var message in provider.ExecuteAsync(
                           new ClaudeCodeOptions
                           {
                               SessionId = "session-1",
                               WorkingDirectory = "/tmp/project-b",
                               Model = "claude-opus"
                           },
                           "follow up"))
        {
            restartedMessages.Add(message);
        }

        provider.CreatedTransportCount.ShouldBe(2);
        provider.SentMessages.Count.ShouldBe(2);
        restartedMessages[0].Content.GetProperty("resume_mode").GetString().ShouldBe("restarted");
    }

    [Fact]
    public async Task ExecuteAsync_emits_debug_metadata_for_pooled_messages()
    {
        var provider = CreateProvider();

        var initialMessages = new List<CliMessage>();
        await foreach (var message in provider.ExecuteAsync(
                           new ClaudeCodeOptions
                           {
                               SessionId = "session-1",
                               WorkingDirectory = "/tmp/project"
                           },
                           "hello"))
        {
            initialMessages.Add(message);
        }

        var resumedMessages = new List<CliMessage>();
        await foreach (var message in provider.ExecuteAsync(
                           new ClaudeCodeOptions
                           {
                               SessionId = "session-1",
                               WorkingDirectory = "/tmp/project"
                           },
                           "follow up"))
        {
            resumedMessages.Add(message);
        }

        initialMessages[0].Content.GetProperty("requested_session_id").GetString().ShouldBe("session-1");
        initialMessages[0].Content.GetProperty("binding_key").GetString().ShouldBe("session-1");
        initialMessages[0].Content.GetProperty("runtime_fingerprint").GetString().ShouldNotBeNullOrWhiteSpace();
        initialMessages[0].Content.GetProperty("pool_fingerprint").GetString().ShouldBe("session-1");
        initialMessages[0].Content.GetProperty("resume_mode").GetString().ShouldBe("started");
        initialMessages[0].Content.GetProperty("event_timestamp").GetString().ShouldNotBeNullOrWhiteSpace();

        resumedMessages[0].Content.GetProperty("resume_mode").GetString().ShouldBe("resumed");
        resumedMessages[1].Content.GetProperty("pool_fingerprint").GetString().ShouldBe("session-1");
    }

    [Fact]
    public async Task ExecuteAsync_does_not_release_lock_when_wait_is_cancelled_before_acquire()
    {
        using var executionLock = new SemaphoreSlim(1, 1);
        await executionLock.WaitAsync();
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        var provider = CreateProvider();
        provider.OverrideExecutionLock = executionLock;

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in provider.ExecuteAsync(
                               new ClaudeCodeOptions
                               {
                                   SessionId = "session-1",
                                   WorkingDirectory = "/tmp/project"
                               },
                               "hello",
                               cancellationTokenSource.Token))
            {
            }
        });

        executionLock.Wait(0).ShouldBeFalse();
        provider.ReleaseExecutionLockCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_marks_lease_faulted_when_lock_release_hits_disposed_lock()
    {
        var provider = CreateProvider();
        provider.ThrowOnReleaseCount = 1;

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
        provider.ReleaseExecutionLockCallCount.ShouldBe(2);
    }

    [Fact]
    public async Task ExecuteAsync_uses_one_shot_transport_when_pooling_is_disabled()
    {
        var provider = CreateProvider();

        await foreach (var _ in provider.ExecuteAsync(
                           new ClaudeCodeOptions
                           {
                               SessionId = "session-1",
                               WorkingDirectory = "/tmp/project",
                               PoolSettings = new HagiCode.Libs.Core.Acp.CliPoolSettings { Enabled = false }
                           },
                           "hello"))
        {
        }

        await foreach (var _ in provider.ExecuteAsync(
                           new ClaudeCodeOptions
                           {
                               SessionId = "session-1",
                               WorkingDirectory = "/tmp/project",
                               PoolSettings = new HagiCode.Libs.Core.Acp.CliPoolSettings { Enabled = false }
                           },
                           "follow up"))
        {
        }

        provider.CreatedTransportCount.ShouldBe(2);
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
                               SessionId = "windows-session",
                               PoolSettings = new CliPoolSettings
                               {
                                   Enabled = false
                               }
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
                PermissionMode = "plan",
                PoolSettings = new CliPoolSettings
                {
                    Enabled = false
                }
            },
            "Reply with exactly the word 'pong'.",
            TimeSpan.FromSeconds(45));

        RealCliInvocationTestHarness.AssertActionableFailure("claude-code", failureMessage);
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
                PermissionMode = "plan",
                PoolSettings = new CliPoolSettings
                {
                    Enabled = false
                }
            },
            "请只回复 pong。",
            TimeSpan.FromSeconds(45));

        RealCliInvocationTestHarness.AssertActionableFailure("claude-code/windows-zh-cn-whitespace-shim", failureMessage);
    }

    private static TestClaudeCodeProvider CreateProvider(
        CliExecutableResolver? executableResolver = null,
        CliProcessManager? processManager = null)
    {
        return new TestClaudeCodeProvider(
            executableResolver ?? new StubExecutableResolver(),
            processManager ?? new StubCliProcessManager(),
            new StubRuntimeEnvironmentResolver());
    }

    private sealed class TestClaudeCodeProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver runtimeEnvironmentResolver)
        : ClaudeCodeProvider(executableResolver, processManager, runtimeEnvironmentResolver)
    {
        public ProcessStartContext? LastStartContext { get; private set; }
        public List<CliMessage> SentMessages { get; } = [];
        public int CreatedTransportCount { get; private set; }
        public SemaphoreSlim? OverrideExecutionLock { get; set; }
        public int ReleaseExecutionLockCallCount { get; private set; }
        public int ThrowOnReleaseCount { get; set; }

        protected override ICliTransport CreateTransport(ProcessStartContext startContext)
        {
            LastStartContext = startContext;
            CreatedTransportCount++;
            return new StubTransport(SentMessages);
        }

        protected override Task AcquireExecutionLockAsync(
            SemaphoreSlim executionLock,
            CancellationToken cancellationToken)
        {
            return (OverrideExecutionLock ?? executionLock).WaitAsync(cancellationToken);
        }

        protected override void ReleaseExecutionLock(SemaphoreSlim executionLock)
        {
            ReleaseExecutionLockCallCount++;
            if (ThrowOnReleaseCount > 0)
            {
                ThrowOnReleaseCount--;
                throw new ObjectDisposedException(nameof(SemaphoreSlim));
            }

            (OverrideExecutionLock ?? executionLock).Release();
        }
    }

    private sealed class StubTransport(List<CliMessage> sentMessages) : ICliTransport
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
            yield return new CliMessage("assistant", JsonSerializer.SerializeToElement(new { type = "assistant", content = "hi" }));
            await Task.Yield();
            yield return new CliMessage("result", JsonSerializer.SerializeToElement(new { type = "result", done = true }));
        }

        public Task SendAsync(CliMessage message, CancellationToken cancellationToken = default)
        {
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

            WrapperPath = Path.Combine(_rootDirectory, "claude.cmd");
            File.WriteAllText(
                WrapperPath,
                $$"""
                @echo off
                setlocal
                call "{{realExecutablePath}}" %*
                exit /b %ERRORLEVEL%
                """,
                new UTF8Encoding(false));

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

        public Task<IReadOnlyDictionary<string, string?>> ResolveAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_environment);
        }

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
