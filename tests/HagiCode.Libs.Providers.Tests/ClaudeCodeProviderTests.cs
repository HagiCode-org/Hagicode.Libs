using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.ClaudeCode;

namespace HagiCode.Libs.Providers.Tests;

public sealed class ClaudeCodeProviderTests
{
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
            ContinueConversation = true,
            Resume = "resume-id",
            SessionId = "session-id",
            AddDirectories = ["/tmp/project"],
            ExtraArgs = new Dictionary<string, string?> { ["dangerously-skip-permissions"] = null }
        });

        arguments.Should().ContainInOrder("--output-format", "stream-json");
        arguments.Should().Contain(["--model", "claude-sonnet"]);
        arguments.Should().Contain(["--system-prompt", "system"]);
        arguments.Should().Contain(["--max-turns", "3"]);
        arguments.Should().Contain("--continue");
        arguments.Should().Contain(["--resume", "resume-id"]);
        arguments.Should().Contain(["--session-id", "session-id"]);
        arguments.Should().Contain(["--add-dir", "/tmp/project"]);
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

        provider.LastStartContext!.ExecutablePath.Should().Be("/custom/claude");
        provider.LastStartContext.EnvironmentVariables!["ANTHROPIC_AUTH_TOKEN"].Should().Be("token");
        provider.LastStartContext.EnvironmentVariables["CLAUDE_CODE_ENTRYPOINT"].Should().Be("sdk-csharp");
        messages.Select(static message => message.Type).Should().Equal("assistant", "result");
        provider.SentMessages.Should().ContainSingle();
        provider.SentMessages[0].Content.GetProperty("message").GetProperty("content").GetString().Should().Be("hello");
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

        result.Success.Should().BeTrue();
        result.Version.Should().Be("1.2.3");
    }

    [Fact]
    public async Task PingAsync_returns_failure_when_executable_is_missing()
    {
        var provider = CreateProvider(executableResolver: new MissingExecutableResolver());

        var result = await provider.PingAsync();

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
    }

    [Fact(Skip = "Requires Claude Code CLI installation.")]
    public async Task Integration_executeAsync_can_run_against_real_claude_cli()
    {
        var resolver = new CliExecutableResolver();
        if (!resolver.IsExecutableAvailable("claude") && !resolver.IsExecutableAvailable("claude-code"))
        {
            return;
        }

        var provider = new ClaudeCodeProvider(resolver, new CliProcessManager(), null);
        await foreach (var _ in provider.ExecuteAsync(new ClaudeCodeOptions(), "ping"))
        {
            break;
        }
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

        protected override ICliTransport CreateTransport(ProcessStartContext startContext)
        {
            LastStartContext = startContext;
            return new StubTransport(SentMessages);
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

    private sealed class StubCliProcessManager : CliProcessManager
    {
        public ProcessResult ExecuteResult { get; init; } = new(0, "1.0.0", string.Empty);

        public override Task<ProcessResult> ExecuteAsync(ProcessStartContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ExecuteResult);
        }
    }
}
