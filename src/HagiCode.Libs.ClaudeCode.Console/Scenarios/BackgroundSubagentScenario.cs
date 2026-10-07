using System.Text.Json;
using System.Text.RegularExpressions;
using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.ClaudeCode;

namespace HagiCode.Libs.ClaudeCode.Console.Scenarios;

/// <summary>
/// Verifies that the stream keeps being tracked while Claude Code runs a background subagent.
/// </summary>
/// <remarks>
/// The turn that launches a background subagent ends with its own <c>result</c> while the subagent is still working.
/// The scenario only passes when the final <c>result</c> carries a token the subagent generates at run time, which is
/// only observable after the subagent reported back.
/// </remarks>
public static class BackgroundSubagentScenario
{
    private const string ScenarioName = "Background Subagent";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(4);
    private static readonly Regex TokenPattern = new(@"SUBAGENT-\d{4,}", RegexOptions.Compiled);

    public static ProviderConsoleScenario<ICliProvider<ClaudeCodeOptions>> Create(ClaudeConsoleExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);

        return new ProviderConsoleScenario<ICliProvider<ClaudeCodeOptions>>(
            ScenarioName,
            "Verify the stream is tracked through a background subagent until its final result.",
            (provider, cancellationToken) => ExecuteAsync(provider, executionOptions, cancellationToken));
    }

    private static async Task<ProviderConsoleScenarioResult> ExecuteAsync(
        ICliProvider<ClaudeCodeOptions> provider,
        ClaudeConsoleExecutionOptions executionOptions,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(Timeout);

        // The subagent runs a shell command, so permission prompts must not block the unattended run.
        var options = executionOptions.CreateBaseOptions() with
        {
            PermissionMode = "bypassPermissions",
        };

        const string prompt =
            "Use the Agent tool exactly once, with run_in_background set to true, to spawn a general-purpose subagent " +
            "that runs this Bash command and reports its output verbatim: `sleep 12; echo SUBAGENT-$RANDOM$RANDOM`. " +
            "Do not wait inside the same turn: end your turn right after launching it, and once the subagent reports " +
            "back reply with only the exact SUBAGENT-<digits> token it reported.";

        var results = new List<string>();
        var launchedBackgroundSubagent = false;
        var resultsBeforeSubagentFinished = 0;
        var subagentFinished = false;

        try
        {
            await foreach (var message in provider.ExecuteAsync(options, prompt, timeoutSource.Token))
            {
                if (string.Equals(message.Type, "result", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(ReadString(message.Content, "result") ?? string.Empty);
                    if (launchedBackgroundSubagent && !subagentFinished)
                    {
                        resultsBeforeSubagentFinished++;
                    }

                    continue;
                }

                if (!string.Equals(message.Type, "system", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var subtype = ReadString(message.Content, "subtype");
                if (subtype == "task_started"
                    && ReadString(message.Content, "task_type") == "local_agent"
                    && ReadBool(message.Content, "is_backgrounded"))
                {
                    launchedBackgroundSubagent = true;
                }
                else if (subtype == "task_notification" && launchedBackgroundSubagent)
                {
                    subagentFinished = true;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(provider, $"Timed out after {Timeout.TotalMinutes:0} minutes waiting for the subagent run to finish.");
        }

        if (results.Count == 0)
        {
            return Failure(provider, "No result message received from provider.");
        }

        if (!launchedBackgroundSubagent)
        {
            return Failure(
                provider,
                $"Claude did not launch a background subagent, so the scenario is inconclusive; run it again. Last result: {results[^1]}");
        }

        var finalResult = results[^1];
        if (!TokenPattern.IsMatch(finalResult))
        {
            return Failure(
                provider,
                $"The stream ended at result #{results.Count} before the subagent reported back. Last result: {finalResult}");
        }

        return new ProviderConsoleScenarioResult(
            provider.Name,
            ScenarioName,
            true,
            0,
            DetailLines:
            [
                $"Results: {results.Count} (before subagent finished: {resultsBeforeSubagentFinished})",
                $"Final result: {finalResult}"
            ]);
    }

    private static ProviderConsoleScenarioResult Failure(ICliProvider<ClaudeCodeOptions> provider, string errorMessage)
    {
        return new ProviderConsoleScenarioResult(provider.Name, ScenarioName, false, 0, ErrorMessage: errorMessage);
    }

    private static bool ReadBool(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.True;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }
}
