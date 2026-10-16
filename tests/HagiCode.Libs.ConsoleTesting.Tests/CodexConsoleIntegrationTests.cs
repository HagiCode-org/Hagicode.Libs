using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Codex.Console;
using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Codex;
using ManagedCode.CodexSharpSDK.Client;
using ManagedCode.CodexSharpSDK.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace HagiCode.Libs.ConsoleTesting.Tests;

public sealed class CodexConsoleIntegrationTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task ProgramMain_can_ping_the_real_codex_cli_when_opted_in()
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

    /// <summary>The shared dispatch contract row for the Codex console (see ProviderConsoleDispatchContractTests).</summary>
    internal static ProviderConsoleDispatchRow CreateDispatchRow() => new CodexDispatchRow();

    private sealed class CodexDispatchRow : ProviderConsoleDispatchRow
    {
        private FakeCodexProvider _provider = new();

        public override ProviderConsoleDefinition Definition => CodexConsoleDefinition.Instance;

        public override IReadOnlyList<string> DefaultSuiteScenarioNames { get; } = ["Ping", "Simple Prompt", "Complex Prompt", "Session Resume"];

        public override IReadOnlyList<string> ExpectedHelpText { get; } =
        [
            "--sandbox <mode>",
            "--approval-policy <mode>"
        ];

        public override IReadOnlyList<string> InvalidOptionArguments => ["--test-provider-full", "--sandbox", "unsupported"];

        public override string InvalidOptionMessage => "Unsupported sandbox mode";

        public override IProviderConsoleRunner CreateRunner(TextWriter output)
        {
            _provider = new FakeCodexProvider();
            return new CodexConsoleRunner(Definition, _provider, new ProviderConsoleOutputFormatter(output));
        }

        public override IReadOnlyList<string> RuntimeOptionArguments(string repositoryPath)
        {
            return ["--test-provider-full", "--model", "gpt-5-codex", "--sandbox", "workspace-write", "--approval-policy", "never", "--repo", repositoryPath];
        }

        public override void AssertRuntimeOptions(string repositoryPath, string renderedOutput)
        {
            _provider.ReceivedOptions.Count.ShouldBe(5);
            _provider.ReceivedOptions[0].ThreadOptions.Model.ShouldBe("gpt-5-codex");
            _provider.ReceivedOptions[0].ThreadOptions.SandboxMode.ShouldBe(SandboxMode.WorkspaceWrite);
            _provider.ReceivedOptions[0].ThreadOptions.ApprovalPolicy.ShouldBe(ApprovalMode.Never);
            _provider.ReceivedOptions[3].ThreadId.ShouldNotBeNullOrWhiteSpace();
            _provider.ReceivedOptions[4].ThreadOptions.WorkingDirectory.ShouldBe(repositoryPath);
            _provider.ReceivedOptions[4].ThreadOptions.AdditionalDirectories.ShouldBe([repositoryPath]);
            renderedOutput.ShouldContain("[PASS] codex / Repository Analysis");
        }

        public override IReadOnlyList<string> ExplicitOptionArguments { get; } = ["--test-provider-full", "--model", "gpt-5-codex-mini"];

        public override void AssertExplicitOptions()
        {
            _provider.ReceivedOptions[0].ThreadOptions.Model.ShouldBe("gpt-5-codex-mini");
        }
    }

    private sealed class FakeCodexProvider : ICodexProvider
    {
        private readonly Dictionary<string, string> _threadSecrets = [];

        public string Name => "codex";

        public bool IsAvailable => true;

        public List<CodexSessionOptions> ReceivedOptions { get; } = [];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<CliProviderTestResult> PingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CliProviderTestResult
            {
                ProviderName = Name,
                Success = true,
                Version = "codex-test-1.0.0"
            });
        }

        public Task<CodexSessionHandle> CreateSessionAsync(CodexSessionOptions options, CancellationToken cancellationToken = default)
        {
            ReceivedOptions.Add(options);
            return Task.FromResult(CodexSessionFactory.Create(options, invocation => BuildLines(options, invocation)));
        }

        private IReadOnlyList<string> BuildLines(CodexSessionOptions options, CodexProcessInvocation invocation)
        {
            var threadId = options.ThreadId ?? $"thread-{ReceivedOptions.Count}";
            var response = BuildResponse(invocation.Input, options.ThreadOptions.WorkingDirectory, threadId);
            var lines = new List<string>();

            if (string.IsNullOrWhiteSpace(options.ThreadId))
            {
                lines.Add(CreateEvent(new
                {
                    type = "thread.started",
                    thread_id = threadId
                }));
            }

            lines.Add(CreateEvent(new
            {
                type = "item.completed",
                item = new
                {
                    id = "msg-1",
                    type = "agent_message",
                    text = response
                }
            }));
            lines.Add(CreateEvent(new
            {
                type = "turn.completed",
                usage = new
                {
                    input_tokens = 1,
                    cached_input_tokens = 0,
                    output_tokens = 1
                }
            }));

            return lines;
        }

        private string BuildResponse(string prompt, string? workingDirectory, string threadId)
        {
            if (prompt.Contains("Remember the secret word:", StringComparison.OrdinalIgnoreCase))
            {
                var secret = ExtractSecret(prompt);
                if (secret is not null)
                {
                    _threadSecrets[threadId] = secret;
                }

                return "ACK";
            }

            if (prompt.Contains("What was the secret word I told you earlier", StringComparison.OrdinalIgnoreCase))
            {
                return _threadSecrets.TryGetValue(threadId, out var secret)
                    ? secret
                    : "UNKNOWN";
            }

            if (prompt.Contains("exactly the word 'pong'", StringComparison.OrdinalIgnoreCase))
            {
                return "pong";
            }

            if (prompt.Contains("software architecture", StringComparison.OrdinalIgnoreCase))
            {
                return "- Advantage: modular scaling and clearer ownership.\n- Trade-off: extra coordination, deployment overhead, and operational complexity.";
            }

            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                var repositoryName = new DirectoryInfo(workingDirectory).Name;
                return $"Repository {repositoryName} contains src, tests, docs, and app folders with .cs and .json files.";
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
    }

    private static class CodexSessionFactory
    {
        public static CodexSessionHandle Create(
            CodexSessionOptions options,
            Func<CodexProcessInvocation, IReadOnlyList<string>> buildLines)
        {
            var sdkOptions = new ManagedCode.CodexSharpSDK.Configuration.CodexOptions
            {
                CodexExecutablePath = options.ExecutablePath ?? "codex",
                ApiKey = options.ApiKey,
                BaseUrl = options.BaseUrl,
                Config = options.Config,
                EnvironmentVariables = options.EnvironmentVariables
                    .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value!, StringComparer.Ordinal),
                Logger = NullLogger.Instance
            };

            var client = new CodexClient(sdkOptions);
            var exec = new CodexExec(
                sdkOptions.CodexExecutablePath,
                sdkOptions.EnvironmentVariables,
                sdkOptions.Config,
                new FakeCodexProcessRunner(buildLines),
                NullLogger.Instance);
            var thread = new CodexThread(exec, sdkOptions, options.ThreadOptions, options.ThreadId);
            return new CodexSessionHandle(client, thread);
        }
    }

    private sealed class FakeCodexProcessRunner(
        Func<CodexProcessInvocation, IReadOnlyList<string>> buildLines) : ICodexProcessRunner
    {
        public async IAsyncEnumerable<string> RunAsync(
            CodexProcessInvocation invocation,
            ILogger logger,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var line in buildLines(invocation))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return line;
            }

            await Task.Yield();
        }
    }

    private static string CreateEvent(object payload)
    {
        return JsonSerializer.Serialize(payload);
    }
}
