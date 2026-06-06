using System.Text.Json;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Pi;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class PiProviderTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";
    private static readonly string[] PiExecutableCandidates = ["pi", "pi-cli"];

    [Fact]
    public async Task ExecuteAsync_builds_expected_non_interactive_json_command()
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResult = CreateSuccessfulExecutionResult("Trip outline")
        };
        var provider = CreateProvider(processManager: processManager);

        await CollectMessagesAsync(
            provider,
            new PiOptions
            {
                ExecutablePath = "/custom/pi",
                WorkingDirectory = "/tmp/pi-project",
                Provider = "omniroute",
                Model = "glm/glm-4.7",
                SystemPrompt = "You are a trip planner.",
                AppendSystemPrompts = ["Keep answers terse."],
                SessionId = "pi-session-1",
                DisableBuiltinTools = true,
                AllowedTools = ["read", "grep"],
                ExcludedTools = ["bash"],
                Thinking = "minimal",
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["PI_OFFLINE"] = "1"
                },
                ExtraArguments = ["--verbose", "   ", "--offline"]
            },
            "Plan a two-day trip.");

        processManager.LastContext.ShouldNotBeNull();
        processManager.LastContext.ExecutablePath.ShouldBe("/custom/pi");
        processManager.LastContext.WorkingDirectory.ShouldBe("/tmp/pi-project");
        processManager.LastContext.Arguments.ShouldBe(
        [
            "--mode",
            "json",
            "--print",
            "--provider",
            "omniroute",
            "--model",
            "glm/glm-4.7",
            "--system-prompt",
            "You are a trip planner.",
            "--append-system-prompt",
            "Keep answers terse.",
            "--thinking",
            "minimal",
            "--session-id",
            "pi-session-1",
            "--no-builtin-tools",
            "--tools",
            "read,grep",
            "--exclude-tools",
            "bash",
            "--verbose",
            "--offline",
            "Plan a two-day trip."
        ]);
        processManager.LastContext.EnvironmentVariables!.ShouldContainKeyAndValue("PI_OFFLINE", "1");
        processManager.LastContext.EnvironmentVariables.ShouldContainKeyAndValue("PATH", "/tmp/bin");
    }

    [Fact]
    public async Task ExecuteAsync_normalizes_pi_json_events_into_shared_cli_messages()
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResult = CreateSuccessfulExecutionResult("Trip outline")
        };
        var provider = CreateProvider(processManager: processManager);

        var messages = await CollectMessagesAsync(
            provider,
            new PiOptions
            {
                WorkingDirectory = "/tmp/pi-project",
                Model = "glm/glm-4.7",
                DisableAllTools = true,
                NoSession = true
            },
            "Plan a trip.");

        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
        messages[0].Content.GetProperty("session_id").GetString().ShouldBe("session-1");
        messages[1].Content.GetProperty("text").GetString().ShouldBe("Trip outline");
        messages[1].Content.GetProperty("provider").GetString().ShouldBe("omniroute");
        messages[2].Content.GetProperty("text").GetString().ShouldBe("Trip outline");
        messages[2].Content.GetProperty("stop_reason").GetString().ShouldBe("stop");
    }

    [Fact]
    public async Task ExecuteAsync_marks_matching_requested_session_id_as_resumed()
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResult = CreateSuccessfulExecutionResult("Trip outline", "pi-session-1")
        };
        var provider = CreateProvider(processManager: processManager);

        var messages = await CollectMessagesAsync(
            provider,
            new PiOptions
            {
                SessionId = "pi-session-1",
                Model = "glm/glm-4.7"
            },
            "Plan a trip.");

        messages[0].Type.ShouldBe("session.resumed");
        messages[0].Content.GetProperty("session_id").GetString().ShouldBe("pi-session-1");
        messages[0].Content.GetProperty("requested_session_id").GetString().ShouldBe("pi-session-1");
        messages[0].Content.GetProperty("resumeMode").GetString().ShouldBe("resumed");
        messages[0].Content.GetProperty("resumed").GetBoolean().ShouldBeTrue();
        messages[0].Content.GetProperty("restarted").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_marks_mismatched_requested_session_id_as_restarted()
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResult = CreateSuccessfulExecutionResult("Trip outline", "session-new")
        };
        var provider = CreateProvider(processManager: processManager);

        var messages = await CollectMessagesAsync(
            provider,
            new PiOptions
            {
                SessionId = "pi-session-1",
                Model = "glm/glm-4.7"
            },
            "Plan a trip.");

        messages[0].Type.ShouldBe("session.started");
        messages[0].Content.GetProperty("session_id").GetString().ShouldBe("session-new");
        messages[0].Content.GetProperty("requested_session_id").GetString().ShouldBe("pi-session-1");
        messages[0].Content.GetProperty("resumeMode").GetString().ShouldBe("restarted");
        messages[0].Content.GetProperty("resumed").GetBoolean().ShouldBeFalse();
        messages[0].Content.GetProperty("restarted").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_surfaces_non_zero_exit_as_terminal_failure_with_diagnostics()
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResult = CreateFailedExecutionResult("model rejected", "provider stderr")
        };
        var provider = CreateProvider(processManager: processManager);

        var messages = await CollectMessagesAsync(provider, new PiOptions { Model = "glm/glm-4.7" }, "Plan a trip.");

        messages.Select(static message => message.Type).ShouldBe(["session.started", "terminal.failed"]);
        var failureText = messages[^1].Content.GetProperty("text").GetString();
        failureText.ShouldNotBeNullOrWhiteSpace();
        failureText.ShouldContain("model rejected");
        failureText.ShouldContain("provider stderr");
        messages[^1].Content.GetProperty("exit_code").GetInt32().ShouldBe(1);
        messages[^1].Content.GetProperty("invalid_output_lines").EnumerateArray().Select(static value => value.GetString()).ShouldContain("Warning: Model alias fallback");
    }

    [Fact]
    public async Task ExecuteAsync_reports_invalid_json_output_as_terminal_failure()
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResult = new ProcessResult(0, "plain text preamble\nthis is not json\n", string.Empty)
        };
        var provider = CreateProvider(processManager: processManager);

        var messages = await CollectMessagesAsync(provider, new PiOptions { NoSession = true }, "Plan a trip.");

        messages.ShouldHaveSingleItem();
        messages[0].Type.ShouldBe("terminal.failed");
        var failureText = messages[0].Content.GetProperty("text").GetString();
        failureText.ShouldNotBeNullOrWhiteSpace();
        failureText.ShouldContain("non-JSON output");
        messages[0].Content.GetProperty("invalid_output_lines").EnumerateArray().Select(static value => value.GetString()).ShouldContain("plain text preamble");
    }

    [Fact]
    public async Task PingAsync_reports_version_when_process_succeeds()
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResult = new ProcessResult(0, "0.78.1", string.Empty)
        };
        var provider = CreateProvider(processManager: processManager);

        var result = await provider.PingAsync();

        result.ProviderName.ShouldBe("pi");
        result.Success.ShouldBeTrue();
        result.Version.ShouldBe("0.78.1");
        processManager.LastContext.ShouldNotBeNull();
        processManager.LastContext.Arguments.ShouldBe(["--version"]);
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
    public async Task ExecuteAsync_real_cli_trip_prompt_returns_assistant_output_or_actionable_failure_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        var resolver = new CliExecutableResolver();
        var executablePath = resolver.ResolveFirstAvailablePath(PiExecutableCandidates);
        if (executablePath is null)
        {
            throw new InvalidOperationException("Pi CLI was not found on PATH even though the real CLI lane was enabled.");
        }

        using var workspace = new TemporaryPiWorkspace();
        await using var provider = new PiProvider(resolver, new CliProcessManager());
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var messages = await CollectMessagesAsync(
            provider,
            new PiOptions
            {
                WorkingDirectory = workspace.WorkingDirectory,
                Provider = "omniroute",
                Model = "glm/glm-4.7",
                DisableAllTools = true,
                NoSession = true
            },
            "Plan a two-day Chongqing trip and reply with exactly three short bullet points.",
            cancellationTokenSource.Token);

        var assistantTexts = messages
            .Where(static message => message.Type == "assistant")
            .Select(static message => message.Content.GetProperty("text").GetString())
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .ToArray();

        if (assistantTexts.Length > 0)
        {
            assistantTexts[0].ShouldNotBeNullOrWhiteSpace();
            messages.ShouldContain(static message => message.Type == "terminal.completed");
            return;
        }

        var failureMessage = string.Join(
            Environment.NewLine,
            messages
                .SelectMany(static message => EnumerateStringValues(message.Content))
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.Ordinal));

        failureMessage.ShouldNotBeNullOrWhiteSpace();
        RealCliInvocationTestHarness.AssertActionableFailure("pi", failureMessage);
    }

    private static PiProvider CreateProvider(
        CliExecutableResolver? executableResolver = null,
        CliProcessManager? processManager = null,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null)
    {
        return new PiProvider(
            executableResolver ?? new StubExecutableResolver(),
            processManager ?? new StubCliProcessManager(),
            runtimeEnvironmentResolver ?? new StubRuntimeEnvironmentResolver());
    }

    private static async Task<List<CliMessage>> CollectMessagesAsync(
        ICliProvider<PiOptions> provider,
        PiOptions options,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<CliMessage>();
        await foreach (var message in provider.ExecuteAsync(options, prompt, cancellationToken))
        {
            messages.Add(message);
        }

        return messages;
    }

    private static ProcessResult CreateSuccessfulExecutionResult(string assistantText, string sessionId = "session-1")
    {
        var assistantMessage = new
        {
            role = "assistant",
            content = new object[]
            {
                new { type = "thinking", thinking = "internal" },
                new { type = "text", text = assistantText }
            },
            api = "openai-completions",
            provider = "omniroute",
            model = "glm/glm-4.7",
            usage = new
            {
                input = 12,
                output = 3,
                cacheRead = 0,
                cacheWrite = 0,
                totalTokens = 15,
                cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0, total = 0 }
            },
            stopReason = "stop",
            timestamp = 1780750996887L,
            responseId = "response-1",
            responseModel = "glm-4.7"
        };

        var lines = new[]
        {
            JsonSerializer.Serialize(new { type = "session", version = 3, id = sessionId, timestamp = "2026-06-06T13:03:16.799Z", cwd = "/tmp/pi-project" }),
            JsonSerializer.Serialize(new { type = "message_end", message = assistantMessage }),
            JsonSerializer.Serialize(new { type = "turn_end", message = assistantMessage, toolResults = Array.Empty<object>() }),
            JsonSerializer.Serialize(new
            {
                type = "agent_end",
                messages = new object[]
                {
                    new { role = "user", content = new object[] { new { type = "text", text = "Plan a trip." } }, timestamp = 1780750996867L },
                    assistantMessage
                },
                willRetry = false
            })
        };

        return new ProcessResult(0, string.Join(Environment.NewLine, lines) + Environment.NewLine, string.Empty);
    }

    private static ProcessResult CreateFailedExecutionResult(string errorMessage, string standardError)
    {
        var assistantMessage = new
        {
            role = "assistant",
            content = Array.Empty<object>(),
            api = "openai-completions",
            provider = "omniroute",
            model = "glm/glm-4.7",
            usage = new
            {
                input = 0,
                output = 0,
                cacheRead = 0,
                cacheWrite = 0,
                totalTokens = 0,
                cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0, total = 0 }
            },
            stopReason = "error",
            timestamp = 1780751012306L,
            errorMessage
        };

        var lines = new[]
        {
            "Warning: Model alias fallback",
            JsonSerializer.Serialize(new { type = "session", version = 3, id = "session-1", timestamp = "2026-06-06T13:03:32.219Z", cwd = "/tmp/pi-project" }),
            JsonSerializer.Serialize(new { type = "message_end", message = assistantMessage }),
            JsonSerializer.Serialize(new { type = "turn_end", message = assistantMessage, toolResults = Array.Empty<object>() }),
            JsonSerializer.Serialize(new { type = "agent_end", messages = new object[] { assistantMessage }, willRetry = false })
        };

        return new ProcessResult(1, string.Join(Environment.NewLine, lines) + Environment.NewLine, standardError);
    }

    private static IEnumerable<string> EnumerateStringValues(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    yield return value;
                }

                yield break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var propertyValue in EnumerateStringValues(property.Value))
                    {
                        yield return propertyValue;
                    }
                }

                yield break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var itemValue in EnumerateStringValues(item))
                    {
                        yield return itemValue;
                    }
                }

                yield break;
            default:
                yield break;
        }
    }

    private static bool IsRealCliTestsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(RealCliTestsEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
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
        public ProcessStartContext? LastContext { get; private set; }
        public ProcessResult ExecuteResult { get; init; } = new(0, string.Empty, string.Empty);

        public override Task<ProcessResult> ExecuteAsync(ProcessStartContext context, CancellationToken cancellationToken = default)
        {
            LastContext = context;
            return Task.FromResult(ExecuteResult);
        }
    }

    private sealed class TemporaryPiWorkspace : IDisposable
    {
        private bool _disposed;

        public TemporaryPiWorkspace()
        {
            RootDirectory = Path.Combine(Path.GetTempPath(), $"hagicode-libs-pi-{Guid.NewGuid():N}");
            WorkingDirectory = Path.Combine(RootDirectory, "workspace");

            Directory.CreateDirectory(WorkingDirectory);
            File.WriteAllText(Path.Combine(WorkingDirectory, "README.md"), "# pi real cli workspace");
        }

        public string RootDirectory { get; }

        public string WorkingDirectory { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, recursive: true);
            }
        }
    }
}
