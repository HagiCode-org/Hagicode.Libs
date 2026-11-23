using System.Text.Json;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Providers.Copilot;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// A real Copilot run for a session starts its own headless CLI, which is registered under that session while the run is in
/// progress and removed when it ends.
/// </summary>
[TestFixture]
public sealed class CopilotOwnedCliRealCliTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";
    private const string RealCopilotSessionTestsEnvironmentVariable = "HAGICODE_REAL_CLI_COPILOT_SESSION_TESTS";
    private const string OwnerSessionId = "real-cli-owner-session";

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task Session_run_is_attributed_to_its_headless_cli_while_it_runs_and_the_entry_is_removed_afterwards()
    {
        ExternalGate.Require(RealCliTestsEnvironmentVariable);
        ExternalGate.Require(RealCopilotSessionTestsEnvironmentVariable);

        var resolver = new CliExecutableResolver();
        var executablePath = resolver.ResolveFirstAvailablePath(["copilot"])
            ?? throw new InvalidOperationException("Copilot CLI was not found on PATH even though the real CLI validation path was enabled.");

        var manager = new CliProcessManager();
        var runtimeEnvironment = new FixedRuntimeEnvironmentResolver(
            new Dictionary<string, string?> { ["HAGI_SESSION_ID"] = OwnerSessionId });
        await using var provider = new CopilotProvider(resolver, manager, runtimeEnvironment);

        var query = new CliManagedProcessQuery { ProviderName = OwnedCopilotCli.ProviderName, SessionId = OwnerSessionId };
        var options = new CopilotOptions
        {
            ExecutablePath = executablePath,
            WorkingDirectory = Directory.GetCurrentDirectory(),
            Timeout = TimeSpan.FromSeconds(90),
            StartupTimeout = TimeSpan.FromSeconds(30),
        };

        var run = Task.Run(() => ReadAssistantTextAsync(provider, options, "Reply with exactly the word 'pong'."));
        IReadOnlyList<CliManagedProcessInfo> seen = [];
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (!run.IsCompleted && DateTime.UtcNow < deadline)
        {
            var alive = manager.GetAliveManagedProcesses(query);
            if (alive.Count > 0)
            {
                seen = alive;
            }

            await Task.Delay(50);
        }

        var (assistantText, transcript) = await run.WaitAsync(TimeSpan.FromSeconds(30));

        assistantText.ShouldContain("pong", Case.Insensitive, customMessage: $"Transcript: {transcript}");
        seen.Count.ShouldBe(1, "the headless CLI should be registered under the session while the run is in progress");
        seen[0].ProviderName.ShouldBe("copilot");
        seen[0].SessionId.ShouldBe(OwnerSessionId);
        manager.GetAliveManagedProcesses(query).ShouldBeEmpty();
    }

    private static async Task<(string AssistantText, string Transcript)> ReadAssistantTextAsync(
        ICliProvider<CopilotOptions> provider,
        CopilotOptions options,
        string prompt)
    {
        var assistantText = new List<string>();
        var transcript = new List<string>();
        await foreach (var message in provider.ExecuteAsync(options, prompt))
        {
            transcript.Add($"{message.Type}: {message.Content.GetRawText()}");
            if (message.Type == "assistant" &&
                message.Content.ValueKind == JsonValueKind.Object &&
                message.Content.TryGetProperty("text", out var textElement) &&
                textElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(textElement.GetString()))
            {
                assistantText.Add(textElement.GetString()!);
            }
        }

        return (string.Join(Environment.NewLine, assistantText), string.Join(Environment.NewLine, transcript));
    }

    private sealed class FixedRuntimeEnvironmentResolver(IReadOnlyDictionary<string, string?> environment) : IRuntimeEnvironmentResolver
    {
        public Task<IReadOnlyDictionary<string, string?>> ResolveAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(environment);
        }
    }
}
