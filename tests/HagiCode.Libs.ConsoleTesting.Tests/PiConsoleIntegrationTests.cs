using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Pi.Console;
using HagiCode.Libs.Pi.Console.Scenarios;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Pi;
using Shouldly;

namespace HagiCode.Libs.ConsoleTesting.Tests;

public sealed class PiConsoleIntegrationTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";

    [Test]
    public async Task DispatchAsync_accepts_terminal_completed_text_when_no_assistant_chunks_are_emitted()
    {
        var provider = new FakePiProvider(emitAssistantMessages: false, includeTerminalText: true);
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new PiConsoleRunner(PiConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync([], PiConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(0);
        output.ToString().ShouldContain("Summary: 4/4 passed");
    }

    [Test]
    public async Task PiScenarioMessageReader_deduplicates_cumulative_snapshots_and_reconciles_terminal_completion()
    {
        var provider = new ScriptedPiProvider(
        [
            CreateMessage("session.started", new { type = "session.started", session_id = "pi-session-1" }),
            CreateMessage("assistant", new { type = "assistant", session_id = "pi-session-1", text = "1" }),
            CreateMessage("assistant", new { type = "assistant", session_id = "pi-session-1", text = "12" }),
            CreateMessage("assistant", new { type = "assistant", session_id = "pi-session-1", text = "123" }),
            CreateMessage("terminal.completed", new { type = "terminal.completed", session_id = "pi-session-1", text = "1234" })
        ]);

        var result = await PiScenarioMessageReader.ReadExecutionResultAsync(
            provider,
            new PiOptions { NoSession = true },
            "分析当前项目",
            CancellationToken.None);

        result.SessionId.ShouldBe("pi-session-1");
        result.Messages.ShouldBe(["1", "2", "3", "4"]);
        result.AssistantText.ShouldBe("1234");
    }

    [Test]
    public async Task PiScenarioMessageReader_appends_delta_marked_fragments_exactly_as_they_arrive()
    {
        var provider = new ScriptedPiProvider(
        [
            CreateMessage("session.started", new { type = "session.started", session_id = "pi-session-1" }),
            CreateMessage("assistant", new { type = "assistant", session_id = "pi-session-1", text = "The cat.", delta = true }),
            CreateMessage("assistant", new { type = "assistant", session_id = "pi-session-1", text = "\n", delta = true }),
            // A fragment equal to the start of the text so far is real content, not a replayed snapshot.
            CreateMessage("assistant", new { type = "assistant", session_id = "pi-session-1", text = "The", delta = true }),
            CreateMessage("assistant", new { type = "assistant", session_id = "pi-session-1", text = " dog.", delta = true }),
            CreateMessage("terminal.completed", new { type = "terminal.completed", session_id = "pi-session-1", text = "The cat.\nThe dog." })
        ]);

        var result = await PiScenarioMessageReader.ReadExecutionResultAsync(
            provider,
            new PiOptions { NoSession = true },
            "分析当前项目",
            CancellationToken.None);

        result.Messages.ShouldBe(["The cat.", "\n", "The", " dog."]);
        result.AssistantText.ShouldBe("The cat.\nThe dog.");
    }

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task ProgramMain_can_ping_the_real_pi_cli_when_opted_in()
    {
        ExternalGate.Require(RealCliTestsEnvironmentVariable);

        using var output = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(output);
            var exitCode = await Program.Main(["--test-provider"]);
            exitCode.ShouldBe(0);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task ProgramMain_can_run_the_full_default_suite_when_opted_in()
    {
        ExternalGate.Require(RealCliTestsEnvironmentVariable);

        using var output = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(output);
            var exitCode = await Program.Main([]);
            exitCode.ShouldBe(0);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static CliMessage CreateMessage(string type, object payload)
    {
        return new CliMessage(type, JsonSerializer.SerializeToElement(payload));
    }

    /// <summary>The shared dispatch contract row for the Pi console (see ProviderConsoleDispatchContractTests).</summary>
    internal static ProviderConsoleDispatchRow CreateDispatchRow() => new PiDispatchRow();

    private sealed class PiDispatchRow : ProviderConsoleDispatchRow
    {
        private FakePiProvider _provider = new();

        public override ProviderConsoleDefinition Definition => PiConsoleDefinition.Instance;

        public override IReadOnlyList<string> DefaultSuiteScenarioNames { get; } = ["Ping", "Simple Prompt", "Complex Prompt", "Session Resume"];

        public override IReadOnlyList<string> ExpectedHelpText { get; } =
        [
            "--provider <name>",
            "--model <model>",
            "--workspace <path>",
            "--session-dir <path>",
            "--arg <value>"
        ];

        public override IProviderConsoleRunner CreateRunner(TextWriter output)
        {
            _provider = new FakePiProvider();
            return new PiConsoleRunner(Definition, _provider, new ProviderConsoleOutputFormatter(output));
        }

        private string _workspacePath = string.Empty;
        private string _sessionDirectory = string.Empty;

        public override IReadOnlyList<string> RuntimeOptionArguments(string repositoryPath)
        {
            _workspacePath = Path.Combine(repositoryPath, "workspace");
            _sessionDirectory = Path.Combine(repositoryPath, "sessions");
            Directory.CreateDirectory(_workspacePath);
            Directory.CreateDirectory(_sessionDirectory);
            return ["--test-provider-full", "--repo", repositoryPath, "--workspace", _workspacePath, "--session-dir", _sessionDirectory, "--executable", "/tmp/pi", "--thinking", "minimal", "--arg", "--profile=smoke"];
        }

        public override void AssertRuntimeOptions(string repositoryPath, string renderedOutput)
        {
            _provider.ReceivedOptions.Count.ShouldBe(5);
            _provider.ReceivedOptions[0].Provider.ShouldBe(PiConsoleExecutionOptions.DefaultProvider);
            _provider.ReceivedOptions[0].ExecutablePath.ShouldBe("/tmp/pi");
            _provider.ReceivedOptions[0].Thinking.ShouldBe("minimal");
            _provider.ReceivedOptions[0].ExtraArguments.ShouldBe(["--profile=smoke"]);
            _provider.ReceivedOptions[0].WorkingDirectory.ShouldBe(_workspacePath);
            _provider.ReceivedOptions[0].DisableAllTools.ShouldBeTrue();
            _provider.ReceivedOptions[3].SessionDirectory.ShouldBe(_sessionDirectory);
            _provider.ReceivedOptions[4].WorkingDirectory.ShouldBe(repositoryPath);
            _provider.ReceivedOptions[4].DisableAllTools.ShouldBeFalse();
            renderedOutput.ShouldContain("[PASS] pi / Repository Summary");
        }

        public override IReadOnlyList<string> ExplicitOptionArguments { get; } = ["--test-provider-full", "--provider", "another-provider", "--model", "another-model"];

        public override void AssertExplicitOptions()
        {
            _provider.ReceivedOptions[0].Provider.ShouldBe("another-provider");
            _provider.ReceivedOptions[0].Model.ShouldBe("another-model");
        }
    }

    private sealed class FakePiProvider : ICliProvider<PiOptions>
    {
        private readonly Dictionary<string, string> _sessionSecrets = [];
        private readonly bool _emitAssistantMessages;
        private readonly bool _includeTerminalText;

        public FakePiProvider(bool emitAssistantMessages = true, bool includeTerminalText = false)
        {
            _emitAssistantMessages = emitAssistantMessages;
            _includeTerminalText = includeTerminalText;
        }

        public string Name => "pi";

        public bool IsAvailable => true;

        public List<PiOptions> ReceivedOptions { get; } = [];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<CliProviderTestResult> PingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CliProviderTestResult
            {
                ProviderName = Name,
                Success = true,
                Version = "pi-test-1.0.0"
            });
        }

        public async IAsyncEnumerable<CliMessage> ExecuteAsync(
            PiOptions options,
            string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReceivedOptions.Add(options);
            var sessionId = options.SessionId ?? $"session-{ReceivedOptions.Count}";
            var response = BuildResponse(prompt, options, sessionId);
            var lifecycleType = options.SessionId is null ? "session.started" : "session.resumed";

            yield return new CliMessage(
                lifecycleType,
                JsonSerializer.SerializeToElement(new
                {
                    type = lifecycleType,
                    session_id = sessionId,
                    requested_session_id = options.SessionId,
                    resumeMode = options.SessionId is null ? "started" : "resumed"
                }));

            if (_emitAssistantMessages)
            {
                foreach (var chunk in SplitResponse(response))
                {
                    yield return new CliMessage(
                        "assistant",
                        JsonSerializer.SerializeToElement(new
                        {
                            type = "assistant",
                            session_id = sessionId,
                            text = chunk
                        }));
                }
            }

            yield return new CliMessage(
                "terminal.completed",
                JsonSerializer.SerializeToElement(new
                {
                    type = "terminal.completed",
                    session_id = sessionId,
                    text = _includeTerminalText ? response : null,
                    stop_reason = "stop"
                }));
            await Task.Yield();
        }

        private string BuildResponse(string prompt, PiOptions options, string sessionId)
        {
            if (prompt.Contains("Remember the secret word:", StringComparison.OrdinalIgnoreCase))
            {
                var secret = ExtractSecret(prompt);
                if (secret is not null)
                {
                    _sessionSecrets[sessionId] = secret;
                }

                return "ACK";
            }

            if (prompt.Contains("What was the secret word I told you earlier", StringComparison.OrdinalIgnoreCase))
            {
                return _sessionSecrets.TryGetValue(sessionId, out var secret)
                    ? secret
                    : "UNKNOWN";
            }

            if (prompt.Contains("exactly the word 'pong'", StringComparison.OrdinalIgnoreCase)
                || prompt.Contains("exactly one word: pong", StringComparison.OrdinalIgnoreCase))
            {
                return "pong";
            }

            if (prompt.Contains("software testing", StringComparison.OrdinalIgnoreCase))
            {
                return "- Advantage: faster feedback and safer refactors.\n- Trade-off: extra maintenance, flaky cases, and longer pipelines.";
            }

            if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
            {
                var repositoryName = new DirectoryInfo(options.WorkingDirectory).Name;
                return $"Repository {repositoryName} contains src, tests, docs, and packages with .cs and .json files.";
            }

            return "pong";
        }

        private static string? ExtractSecret(string prompt)
        {
            const string marker = "Remember the secret word:";
            var markerIndex = prompt.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return null;
            }

            var secretSegment = prompt[(markerIndex + marker.Length)..];
            var stopIndex = secretSegment.IndexOf('.', StringComparison.Ordinal);
            var value = stopIndex >= 0 ? secretSegment[..stopIndex] : secretSegment;
            return value.Trim();
        }

        private static IEnumerable<string> SplitResponse(string response)
        {
            const int chunkSize = 6;
            for (var index = 0; index < response.Length; index += chunkSize)
            {
                yield return response.Substring(index, Math.Min(chunkSize, response.Length - index));
            }
        }
    }

    private sealed class ScriptedPiProvider(IReadOnlyList<CliMessage> messages) : ICliProvider<PiOptions>
    {
        public string Name => "pi";

        public bool IsAvailable => true;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<CliProviderTestResult> PingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CliProviderTestResult
            {
                ProviderName = Name,
                Success = true,
                Version = "pi-test-1.0.0"
            });
        }

        public async IAsyncEnumerable<CliMessage> ExecuteAsync(
            PiOptions options,
            string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var message in messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return message;
            }

            await Task.Yield();
        }
    }
}
