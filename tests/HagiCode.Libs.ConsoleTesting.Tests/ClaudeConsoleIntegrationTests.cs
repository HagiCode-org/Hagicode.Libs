using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using HagiCode.Libs.ClaudeCode.Console;
using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.ClaudeCode;
using Shouldly;

namespace HagiCode.Libs.ConsoleTesting.Tests;

public sealed class ClaudeConsoleIntegrationTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";

    [Fact]
    public async Task DispatchAsync_runs_claude_default_suite_with_fake_provider()
    {
        var provider = new FakeClaudeProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new ClaudeConsoleRunner(ClaudeConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync([], ClaudeConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(0);
        var rendered = output.ToString();
        rendered.ShouldContain("[PASS] claude-code / Ping");
        rendered.ShouldContain("[PASS] claude-code / Simple Prompt");
        rendered.ShouldContain("[PASS] claude-code / Complex Prompt");
        rendered.ShouldContain("[PASS] claude-code / Session Restore (Session Id)");
        rendered.ShouldContain("[PASS] claude-code / Session Restore (Resume Id)");
        rendered.ShouldContain("Summary: 6/6 passed");
    }

    [Fact]
    public async Task DispatchAsync_restores_conversations_by_explicit_session_id_and_by_reported_resume_id()
    {
        var provider = new FakeClaudeProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new ClaudeConsoleRunner(ClaudeConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync([], ClaudeConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(0);

        // --session-id <id> on the first request, then --resume <same id> on the follow-up.
        var explicitIdSetup = provider.Calls.Single(call =>
            call.Prompt.Contains("Remember the secret word:", StringComparison.Ordinal) && call.Options.SessionId is not null);
        var explicitIdFollowUp = provider.Calls.Single(call => call.Options.Resume == explicitIdSetup.Options.SessionId);
        explicitIdFollowUp.Options.ContinueConversation.ShouldBeFalse();
        explicitIdFollowUp.Options.SessionId.ShouldBeNull();

        // No id on the first request, then --resume <session_id reported by the first stream>.
        var reportedIdSetups = provider.Calls
            .Where(call => call.Prompt.Contains("Remember the secret word:", StringComparison.Ordinal)
                           && call.Options.SessionId is null
                           && !call.Options.ContinueConversation)
            .ToArray();
        var reportedIdFollowUps = provider.Calls
            .Where(call => call.Options.Resume is not null && call.Options.Resume != explicitIdSetup.Options.SessionId)
            .ToArray();
        reportedIdSetups.Length.ShouldBe(2);
        reportedIdFollowUps.Length.ShouldBe(1);
        provider.ReportedSessionIds.ShouldContain(reportedIdFollowUps[0].Options.Resume!);
    }

    [Fact]
    public async Task DispatchAsync_fails_resume_id_scenarios_when_the_provider_does_not_restore_the_conversation()
    {
        var provider = new FakeClaudeProvider { RestoreByResumeId = false };
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new ClaudeConsoleRunner(ClaudeConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync([], ClaudeConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(1);
        var rendered = output.ToString();
        rendered.ShouldContain("[FAIL] claude-code / Session Restore (Session Id)");
        rendered.ShouldContain("[FAIL] claude-code / Session Restore (Resume Id)");
        rendered.ShouldContain("did not return the remembered secret");
        // --continue still works, so only the by-id scenarios regress.
        Regex.IsMatch(rendered, @"^\[PASS\] claude-code / Session Restore( \(\d+ms\))?\r?$", RegexOptions.Multiline).ShouldBeTrue();
    }

    [Fact]
    public async Task DispatchAsync_fails_session_id_scenario_when_the_provider_ignores_the_requested_session_id()
    {
        var provider = new FakeClaudeProvider { HonorRequestedSessionId = false };
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new ClaudeConsoleRunner(ClaudeConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync([], ClaudeConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(1);
        var rendered = output.ToString();
        rendered.ShouldContain("[FAIL] claude-code / Session Restore (Session Id)");
        rendered.ShouldContain("did not honor --session-id");
        rendered.ShouldContain("[PASS] claude-code / Session Restore (Resume Id)");
    }

    [Fact]
    public async Task DispatchAsync_shows_provider_specific_help_text()
    {
        var provider = new FakeClaudeProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new ClaudeConsoleRunner(ClaudeConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(["--help"], ClaudeConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(0);
        var rendered = output.ToString();
        rendered.ShouldContain("--test-provider");
        rendered.ShouldContain("--test-provider-full");
        rendered.ShouldContain("--test-all");
        rendered.ShouldContain("--repo <path>");
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task ProgramMain_can_ping_the_real_claude_cli_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

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

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task ProgramMain_can_run_the_full_default_suite_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

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

    private static bool IsRealCliTestsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(RealCliTestsEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeClaudeProvider : ICliProvider<ClaudeCodeOptions>
    {
        private readonly Dictionary<string, string> _secretsBySessionId = new(StringComparer.OrdinalIgnoreCase);
        private string? _lastSessionId;

        /// <summary>
        /// When <see langword="false" />, <c>--resume &lt;id&gt;</c> is ignored and the follow-up starts a blank conversation.
        /// </summary>
        public bool RestoreByResumeId { get; init; } = true;

        /// <summary>
        /// When <see langword="false" />, a requested <c>--session-id</c> is ignored and a fresh id is reported instead.
        /// </summary>
        public bool HonorRequestedSessionId { get; init; } = true;

        public List<(ClaudeCodeOptions Options, string Prompt)> Calls { get; } = [];

        public List<string> ReportedSessionIds { get; } = [];

        public string Name => "claude-code";

        public bool IsAvailable => true;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<CliProviderTestResult> PingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CliProviderTestResult
            {
                ProviderName = Name,
                Success = true,
                Version = "test-1.0.0"
            });
        }

        public async IAsyncEnumerable<CliMessage> ExecuteAsync(
            ClaudeCodeOptions options,
            string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls.Add((options, prompt));

            var response = BuildResponse(prompt, options.WorkingDirectory);
            var sessionId = ResolveSessionId(options);
            if (prompt.Contains("Remember the secret word:", StringComparison.OrdinalIgnoreCase))
            {
                var secret = ExtractSecret(prompt);
                if (secret is not null)
                {
                    _secretsBySessionId[sessionId] = secret;
                }

                response = "ACK";
            }
            else if (prompt.Contains("What was the secret word I told you earlier", StringComparison.OrdinalIgnoreCase))
            {
                response = LookUpSecret(options, sessionId) ?? "UNKNOWN";
            }

            _lastSessionId = sessionId;
            ReportedSessionIds.Add(sessionId);

            yield return new CliMessage(
                "assistant",
                JsonSerializer.SerializeToElement(new
                {
                    session_id = sessionId,
                    message = new
                    {
                        content = new[]
                        {
                            new
                            {
                                type = "text",
                                text = response
                            }
                        }
                    }
                }));
            yield return new CliMessage(
                "result",
                JsonSerializer.SerializeToElement(new { type = "result", subtype = "success", session_id = sessionId }));
            await Task.Yield();
        }

        private string ResolveSessionId(ClaudeCodeOptions options)
        {
            if (options.Resume is not null && RestoreByResumeId)
            {
                return options.Resume;
            }

            if (options.ContinueConversation && _lastSessionId is not null)
            {
                return _lastSessionId;
            }

            if (options.SessionId is not null && HonorRequestedSessionId)
            {
                return options.SessionId;
            }

            return Guid.NewGuid().ToString();
        }

        private string? LookUpSecret(ClaudeCodeOptions options, string sessionId)
        {
            // Only a request that asks to restore a conversation (--continue or --resume <id>) can see earlier secrets.
            if (!options.ContinueConversation && options.Resume is null)
            {
                return null;
            }

            return _secretsBySessionId.TryGetValue(sessionId, out var secret) ? secret : null;
        }

        private static string BuildResponse(string prompt, string? workingDirectory)
        {
            if (prompt.Contains("exactly the word 'pong'", StringComparison.OrdinalIgnoreCase))
            {
                return "pong";
            }

            if (prompt.Contains("microservices architecture", StringComparison.OrdinalIgnoreCase))
            {
                return "- Advantage: scaling and team ownership.\n- Trade-off: operational overhead and distributed tracing complexity.";
            }

            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                var repositoryName = new DirectoryInfo(workingDirectory).Name;
                return $"Repository {repositoryName} contains src, tests, and app directories with .cs and .json files.";
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
}
