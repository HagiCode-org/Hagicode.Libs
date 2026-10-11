using System.Text.Json;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Dsh;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class DshRealCliTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";
    private const string DshExecutableCandidate = "dsh";
    private const string TestModel = "claude-haiku-5-5";

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task PingAsync_real_cli_validates_dsh_identity_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        await using var provider = new DshProvider(new CliExecutableResolver(), new CliProcessManager());

        var result = await provider.PingAsync();

        result.Success.ShouldBeTrue(result.ErrorMessage);
        result.Version.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task ExecuteAsync_real_cli_prompt_returns_assistant_output_or_actionable_failure_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        var resolver = new CliExecutableResolver();
        if (resolver.ResolveFirstAvailablePath([DshExecutableCandidate, "dsh-cli"]) is null)
        {
            throw new InvalidOperationException("dsh CLI was not found on PATH even though the real CLI lane was enabled.");
        }

        var workspacePath = Path.Combine(Path.GetTempPath(), $"dsh-real-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);

        try
        {
            await using var provider = new DshProvider(resolver, new CliProcessManager());
            using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(120));

            var messages = new List<CliMessage>();
            await foreach (var message in provider.ExecuteAsync(
                               new DshOptions { WorkingDirectory = workspacePath, Model = TestModel },
                               "Reply with exactly the word 'pong'.",
                               cancellationTokenSource.Token))
            {
                messages.Add(message);
            }

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
                messages.Select(static message => $"{message.Type}: {message.Content.GetRawText()}"));

            failureMessage.ShouldNotBeNullOrWhiteSpace();
            RealCliInvocationTestHarness.AssertActionableFailure("dsh", failureMessage);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task ExecuteAsync_real_cli_resumed_session_recalls_prior_secret_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        var resolver = new CliExecutableResolver();
        RequireDshOnPath(resolver);

        var secret = $"DSH-SECRET-{Guid.NewGuid():N}";
        var workspacePath = CreateWorkspacePath();

        try
        {
            await using var provider = new DshProvider(resolver, new CliProcessManager());
            using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var options = new DshOptions { WorkingDirectory = workspacePath, Model = TestModel };

            var setup = await CollectMessagesAsync(
                provider,
                options,
                $"Remember the secret word: {secret}. Reply with exactly ACK.",
                cancellationTokenSource.Token);
            var sessionId = FindSessionId(setup);
            string.IsNullOrWhiteSpace(sessionId).ShouldBeFalse($"dsh did not expose a session id. Messages: {DescribeMessages(setup)}");

            var resumed = await CollectMessagesAsync(
                provider,
                options with { SessionId = sessionId },
                "What was the secret word I told you earlier? Reply with just the word.",
                cancellationTokenSource.Token);

            AssistantText(resumed).Contains(secret, StringComparison.Ordinal)
                .ShouldBeTrue($"Resumed dsh session did not recall the secret. Messages: {DescribeMessages(resumed)}");
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task ExecuteAsync_real_cli_fresh_session_does_not_recall_prior_secret_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        var resolver = new CliExecutableResolver();
        RequireDshOnPath(resolver);

        var secret = $"DSH-SECRET-{Guid.NewGuid():N}";
        var workspacePath = CreateWorkspacePath();

        try
        {
            await using var provider = new DshProvider(resolver, new CliProcessManager());
            using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var options = new DshOptions { WorkingDirectory = workspacePath, Model = TestModel };

            var setup = await CollectMessagesAsync(
                provider,
                options,
                $"Remember the secret word: {secret}. Reply with exactly ACK.",
                cancellationTokenSource.Token);
            FindSessionId(setup).ShouldNotBeNull();

            var fresh = await CollectMessagesAsync(
                provider,
                options,
                "What was the secret word I told you earlier? Reply with just the word.",
                cancellationTokenSource.Token);

            AssistantText(fresh).Contains(secret, StringComparison.Ordinal)
                .ShouldBeFalse($"A fresh dsh session without a session id recalled the secret. Messages: {DescribeMessages(fresh)}");
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    private static void RequireDshOnPath(CliExecutableResolver resolver)
    {
        if (resolver.ResolveFirstAvailablePath([DshExecutableCandidate, "dsh-cli"]) is null)
        {
            throw new InvalidOperationException("dsh CLI was not found on PATH even though the real CLI lane was enabled.");
        }
    }

    private static string CreateWorkspacePath()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), $"dsh-real-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);
        return workspacePath;
    }

    private static async Task<List<CliMessage>> CollectMessagesAsync(
        DshProvider provider,
        DshOptions options,
        string prompt,
        CancellationToken cancellationToken)
    {
        var messages = new List<CliMessage>();
        await foreach (var message in provider.ExecuteAsync(options, prompt, cancellationToken))
        {
            messages.Add(message);
        }

        return messages;
    }

    private static string AssistantText(IEnumerable<CliMessage> messages)
    {
        return string.Join(" ", messages
            .Where(static message => message.Type == "assistant")
            .Select(static message => message.Content.GetProperty("text").GetString()));
    }

    private static string? FindSessionId(IEnumerable<CliMessage> messages)
    {
        return messages
            .Select(static message => message.Content.ValueKind == JsonValueKind.Object &&
                                      message.Content.TryGetProperty("session_id", out var element) &&
                                      element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null)
            .FirstOrDefault(static sessionId => !string.IsNullOrWhiteSpace(sessionId));
    }

    private static string DescribeMessages(IEnumerable<CliMessage> messages)
    {
        return string.Join(Environment.NewLine, messages.Select(static message => $"{message.Type}: {message.Content.GetRawText()}"));
    }

    private static bool IsRealCliTestsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(RealCliTestsEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}
