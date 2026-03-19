using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.ClaudeCode;

namespace HagiCode.Libs.ClaudeCode.Console.Scenarios;

public static class SessionResumeScenario
{
    public static ProviderConsoleScenario<ICliProvider<ClaudeCodeOptions>> Create(ClaudeConsoleExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);

        return new ProviderConsoleScenario<ICliProvider<ClaudeCodeOptions>>(
            "Session Restore",
            "Verify that a follow-up request can continue the most recent Claude conversation.",
            (provider, cancellationToken) => ExecuteAsync(provider, executionOptions, cancellationToken));
    }

    private static async Task<ProviderConsoleScenarioResult> ExecuteAsync(
        ICliProvider<ClaudeCodeOptions> provider,
        ClaudeConsoleExecutionOptions executionOptions,
        CancellationToken cancellationToken)
    {
        var secret = $"BLUEPRINT-{Guid.NewGuid():N}";

        var firstOptions = executionOptions.CreateBaseOptions() with
        {
            MaxTurns = 1,
        };

        var firstPrompt = $"Remember the secret word: {secret}. Reply with exactly ACK.";
        var (firstMessages, _) = await ClaudeScenarioMessageReader.ReadAssistantMessagesAsync(
            provider,
            firstOptions,
            firstPrompt,
            cancellationToken);

        if (firstMessages.Count == 0)
        {
            return new ProviderConsoleScenarioResult(
                provider.Name,
                "Session Restore",
                false,
                0,
                ErrorMessage: "Initial request returned no assistant messages.");
        }

        var firstCombined = string.Join(" ", firstMessages);
        if (!firstCombined.Contains("ACK", StringComparison.OrdinalIgnoreCase))
        {
            return new ProviderConsoleScenarioResult(
                provider.Name,
                "Session Restore",
                false,
                0,
                ErrorMessage: $"Initial request did not acknowledge the setup prompt. Response: {firstCombined}");
        }

        var secondOptions = executionOptions.CreateBaseOptions() with
        {
            ContinueConversation = true,
            MaxTurns = 1,
        };

        var secondPrompt = "What was the secret word I told you earlier? Reply with just the word.";
        var (secondMessages, _) = await ClaudeScenarioMessageReader.ReadAssistantMessagesAsync(
            provider,
            secondOptions,
            secondPrompt,
            cancellationToken);

        if (secondMessages.Count == 0)
        {
            return new ProviderConsoleScenarioResult(
                provider.Name,
                "Session Restore",
                false,
                0,
                ErrorMessage: "Resume request returned no assistant messages.");
        }

        var secondCombined = string.Join(" ", secondMessages);
        var normalized = secondCombined.Replace("`", string.Empty, StringComparison.Ordinal).Trim();
        var success = normalized.Contains(secret, StringComparison.OrdinalIgnoreCase);

        return success
            ? new ProviderConsoleScenarioResult(provider.Name, "Session Restore", true, 0)
            : new ProviderConsoleScenarioResult(
                provider.Name,
                "Session Restore",
                false,
                0,
                ErrorMessage: $"Resume request did not return the remembered secret. Expected: {secret}. Response: {secondCombined}");
    }
}
