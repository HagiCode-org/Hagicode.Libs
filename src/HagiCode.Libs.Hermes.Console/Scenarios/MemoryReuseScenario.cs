using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Hermes;

namespace HagiCode.Libs.Hermes.Console.Scenarios;

public static class MemoryReuseScenario
{
    public static ProviderConsoleScenario<ICliProvider<HermesOptions>> Create(HermesConsoleExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);

        return new ProviderConsoleScenario<ICliProvider<HermesOptions>>(
            "Memory Reuse",
            "Verify that a follow-up request can reuse the in-memory Hermes conversation in the current run.",
            (provider, cancellationToken) => ExecuteAsync(provider, executionOptions, cancellationToken));
    }

    private static async Task<ProviderConsoleScenarioResult> ExecuteAsync(
        ICliProvider<HermesOptions> provider,
        HermesConsoleExecutionOptions executionOptions,
        CancellationToken cancellationToken)
    {
        var secret = $"BLUEPRINT-{Guid.NewGuid():N}";
        var initialPrompt = $"Remember the secret word: {secret}. Reply with exactly ACK.";
        var initialResult = await HermesScenarioMessageReader.ReadExecutionResultAsync(
            provider,
            executionOptions.CreateBaseOptions(),
            initialPrompt,
            cancellationToken);

        if (initialResult.Messages.Count == 0)
        {
            return new ProviderConsoleScenarioResult(
                provider.Name,
                "Memory Reuse",
                false,
                0,
                ErrorMessage: "Initial request returned no assistant messages.");
        }

        var initialCombined = initialResult.AssistantText;
        if (!initialCombined.Contains("ACK", StringComparison.OrdinalIgnoreCase))
        {
            return new ProviderConsoleScenarioResult(
                provider.Name,
                "Memory Reuse",
                false,
                0,
                ErrorMessage: $"Initial request did not acknowledge the setup prompt. Response: {initialCombined}");
        }

        if (string.IsNullOrWhiteSpace(initialResult.SessionId))
        {
            return new ProviderConsoleScenarioResult(
                provider.Name,
                "Memory Reuse",
                false,
                0,
                ErrorMessage: "Hermes did not expose an in-memory conversation key for reuse.");
        }

        var reusedOptions = executionOptions.CreateBaseOptions() with
        {
            SessionId = initialResult.SessionId,
        };

        var followUpResult = await HermesScenarioMessageReader.ReadExecutionResultAsync(
            provider,
            reusedOptions,
            "What was the secret word I told you earlier? Reply with just the word.",
            cancellationToken);

        if (followUpResult.Messages.Count == 0)
        {
            return new ProviderConsoleScenarioResult(
                provider.Name,
                "Memory Reuse",
                false,
                0,
                ErrorMessage: "Reuse request returned no assistant messages.");
        }

        var followUpCombined = followUpResult.AssistantText;
        var normalized = followUpCombined.Replace("`", string.Empty, StringComparison.Ordinal).Trim();
        var success = normalized.Contains(secret, StringComparison.OrdinalIgnoreCase);

        return success
            ? new ProviderConsoleScenarioResult(provider.Name, "Memory Reuse", true, 0)
            : new ProviderConsoleScenarioResult(
                provider.Name,
                "Memory Reuse",
                false,
                0,
                ErrorMessage: $"Reuse request did not return the remembered secret. Expected: {secret}. Response: {followUpCombined}");
    }
}
