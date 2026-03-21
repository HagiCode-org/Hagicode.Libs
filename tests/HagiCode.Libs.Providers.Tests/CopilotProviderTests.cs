using System.Runtime.CompilerServices;
using System.Text.Json;
using Shouldly;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Copilot;

namespace HagiCode.Libs.Providers.Tests;

public sealed class CopilotProviderTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";
    private static readonly string[] CopilotExecutableCandidates = ["copilot"];

    [Fact]
    public void BuildSdkRequest_includes_typed_runtime_fields_and_filtered_args()
    {
        var provider = CreateProvider();
        var request = provider.BuildSdkRequest(
            new CopilotOptions
            {
                ExecutablePath = "/custom/copilot",
                Model = "claude-sonnet-4.5",
                WorkingDirectory = "/tmp/project",
                Timeout = TimeSpan.FromMinutes(3),
                StartupTimeout = TimeSpan.FromSeconds(15),
                AuthSource = CopilotAuthSource.GitHubToken,
                GitHubToken = "ghu_test",
                Permissions = new CopilotPermissionOptions
                {
                    AllowAllTools = true,
                    AllowedPaths = ["/tmp/project"],
                    AllowedTools = ["grep"],
                    DeniedTools = ["rm"],
                    DeniedUrls = ["example.com"]
                },
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["CUSTOM_FLAG"] = "1"
                },
                AdditionalArgs = ["--config-dir", "/tmp/copilot", "--headless"]
            },
            "hello",
            "/custom/copilot",
            new Dictionary<string, string?>
            {
                ["PATH"] = "/tmp/bin"
            });

        request.CliPath.ShouldBe("/custom/copilot");
        request.Model.ShouldBe("claude-sonnet-4.5");
        request.WorkingDirectory.ShouldBe("/tmp/project");
        request.Timeout.ShouldBe(TimeSpan.FromMinutes(3));
        request.StartupTimeout.ShouldBe(TimeSpan.FromSeconds(15));
        request.GitHubToken.ShouldBe("ghu_test");
        request.UseLoggedInUser.ShouldBeFalse();
        request.CliArgs.ShouldBe(
        [
            "--allow-all-tools",
            "--no-ask-user",
            "--add-dir",
            "/tmp/project",
            "--available-tools",
            "grep",
            "--deny-tool",
            "rm",
            "--deny-url",
            "example.com",
            "--config-dir",
            "/tmp/copilot"
        ]);
        request.EnvironmentVariables["CUSTOM_FLAG"].ShouldBe("1");
        request.EnvironmentVariables["COPILOT_INTERNAL_ORIGINATOR_OVERRIDE"].ShouldBe("hagicode_libs_csharp");
    }

    [Fact]
    public void BuildCliArgs_filters_unsupported_flags_and_records_diagnostics()
    {
        var result = CopilotCliCompatibility.BuildCliArgs(new CopilotOptions
        {
            NoAskUser = false,
            AdditionalArgs = ["--experimental", "--config-dir", "/tmp/copilot", "--headless", "--prompt", "hello", "stray-value"]
        });

        result.CliArgs.ShouldBe(
        [
            "--experimental",
            "--config-dir",
            "/tmp/copilot"
        ]);
        result.Diagnostics.Count.ShouldBe(4);
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.Contains("--headless", StringComparison.Ordinal));
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.Contains("--prompt", StringComparison.Ordinal));
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.Contains("stray-value", StringComparison.Ordinal));
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.Contains("hello", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_normalizes_sdk_stream_events_into_cli_messages()
    {
        var provider = CreateProvider(gateway: new StubCopilotSdkGateway(
        [
            new CopilotSdkStreamEvent(CopilotSdkStreamEventType.TextDelta, Content: "pong"),
            new CopilotSdkStreamEvent(CopilotSdkStreamEventType.ReasoningDelta, Content: "thinking"),
            new CopilotSdkStreamEvent(CopilotSdkStreamEventType.ToolExecutionStart, ToolName: "grep", ToolCallId: "tool-1"),
            new CopilotSdkStreamEvent(CopilotSdkStreamEventType.ToolExecutionEnd, Content: "completed successfully", ToolName: "grep", ToolCallId: "tool-1"),
            new CopilotSdkStreamEvent(CopilotSdkStreamEventType.Completed)
        ]));
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new CopilotOptions
                           {
                               ExecutablePath = "/custom/copilot",
                               AdditionalArgs = ["--headless"]
                           },
                           "hello"))
        {
            messages.Add(message);
        }

        messages.Select(static message => message.Type).ShouldBe(["diagnostic", "assistant", "reasoning", "tool.started", "tool.completed", "result"]);
        messages[0].Content.GetProperty("message").GetString()!.ShouldContain("--headless");
        messages[1].Content.GetProperty("text").GetString().ShouldBe("pong");
        messages[2].Content.GetProperty("text").GetString().ShouldBe("thinking");
        messages[3].Content.GetProperty("tool_name").GetString().ShouldBe("grep");
        messages[4].Content.GetProperty("failed").GetBoolean().ShouldBeFalse();
        messages[5].Content.GetProperty("status").GetString().ShouldBe("completed");
    }

    [Fact]
    public async Task ExecuteAsync_emits_error_terminal_message_when_gateway_fails()
    {
        var provider = CreateProvider(gateway: new StubCopilotSdkGateway(
        [
            new CopilotSdkStreamEvent(CopilotSdkStreamEventType.Error, ErrorMessage: "startup failed")
        ]));
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(new CopilotOptions { ExecutablePath = "/custom/copilot" }, "hello"))
        {
            messages.Add(message);
        }

        messages.Select(static message => message.Type).ShouldBe(["error"]);
        messages[0].Content.GetProperty("message").GetString().ShouldBe("startup failed");
    }

    [Fact]
    public async Task PingAsync_reports_version_when_process_succeeds()
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResults = new Queue<ProcessResult>(
            [
                new ProcessResult(0, "copilot 1.0.10", string.Empty)
            ])
        };
        var provider = CreateProvider(processManager: processManager);

        var result = await provider.PingAsync();

        result.ProviderName.ShouldBe("copilot");
        result.Success.ShouldBeTrue();
        result.Version.ShouldBe("copilot 1.0.10");
    }

    [Fact]
    public async Task PingAsync_returns_failure_when_executable_is_missing()
    {
        var provider = CreateProvider(executableResolver: new MissingExecutableResolver());

        var result = await provider.PingAsync();

        result.Success.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("not found", Case.Insensitive);
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task PingAsync_can_validate_installed_copilot_cli_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        var resolver = new CliExecutableResolver();
        var executablePath = resolver.ResolveFirstAvailablePath(CopilotExecutableCandidates);
        if (executablePath is null)
        {
            throw new InvalidOperationException("Copilot CLI was not found on PATH even though the real CLI validation path was enabled.");
        }

        Path.GetFileNameWithoutExtension(executablePath).ShouldBe("copilot");

        var provider = new CopilotProvider(resolver, new CliProcessManager(), new StubCopilotSdkGateway([]), null);

        provider.IsAvailable.ShouldBeTrue();

        var result = await provider.PingAsync();

        result.ProviderName.ShouldBe("copilot");
        result.Success.ShouldBeTrue();
        result.Version.ShouldNotBeNullOrWhiteSpace();
        result.ErrorMessage.ShouldBeNullOrWhiteSpace();
    }

    private static TestCopilotProvider CreateProvider(
        CliExecutableResolver? executableResolver = null,
        CliProcessManager? processManager = null,
        ICopilotSdkGateway? gateway = null)
    {
        return new TestCopilotProvider(
            executableResolver ?? new StubExecutableResolver(),
            processManager ?? new StubCliProcessManager(),
            gateway ?? new StubCopilotSdkGateway([]),
            new StubRuntimeEnvironmentResolver());
    }

    private sealed class TestCopilotProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        ICopilotSdkGateway gateway,
        IRuntimeEnvironmentResolver runtimeEnvironmentResolver)
        : CopilotProvider(executableResolver, processManager, gateway, runtimeEnvironmentResolver)
    {
    }

    private sealed class StubCopilotSdkGateway(IReadOnlyList<CopilotSdkStreamEvent> events) : ICopilotSdkGateway
    {
        public async IAsyncEnumerable<CopilotSdkStreamEvent> SendPromptAsync(
            CopilotSdkRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var eventData in events)
            {
                yield return eventData;
                await Task.Yield();
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

    private sealed class StubCliProcessManager : CliProcessManager
    {
        public Queue<ProcessResult> ExecuteResults { get; init; } = new([
            new ProcessResult(0, "copilot 1.0.10", string.Empty)
        ]);

        public override Task<ProcessResult> ExecuteAsync(ProcessStartContext context, CancellationToken cancellationToken = default)
        {
            if (ExecuteResults.Count == 0)
            {
                return Task.FromResult(new ProcessResult(1, string.Empty, "missing result"));
            }

            return Task.FromResult(ExecuteResults.Dequeue());
        }
    }

    private static bool IsRealCliTestsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(RealCliTestsEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}
