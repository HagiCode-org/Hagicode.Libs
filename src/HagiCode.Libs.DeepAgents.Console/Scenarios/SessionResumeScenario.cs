using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.DeepAgents;

namespace HagiCode.Libs.DeepAgents.Console.Scenarios;

public static class SessionResumeScenario
{
    public static ProviderConsoleScenario<ICliProvider<DeepAgentsOptions>> Create(DeepAgentsConsoleExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);

        return new ProviderConsoleScenario<ICliProvider<DeepAgentsOptions>>(
            "Session Resume",
            "Verify that a follow-up request can resume the same DeepAgents session.",
            (provider, cancellationToken) => ExecuteAsync(provider, executionOptions, cancellationToken));
    }

    private static async Task<ProviderConsoleScenarioResult> ExecuteAsync(
        ICliProvider<DeepAgentsOptions> provider,
        DeepAgentsConsoleExecutionOptions executionOptions,
        CancellationToken cancellationToken)
    {
        var secret = $"BLUEPRINT-{Guid.NewGuid():N}";
        var initialPrompt = $"Remember the secret word: {secret}. Reply with exactly ACK.";
        var initialOptions = executionOptions.CreateBaseOptions();
        var initialResult = await DeepAgentsScenarioMessageReader.ReadExecutionResultAsync(
            provider,
            initialOptions,
            initialPrompt,
            cancellationToken);
        var initialDetailLines = DeepAgentsScenarioMessageReader.BuildDetailLines(
            executionOptions,
            initialOptions,
            initialPrompt,
            initialResult,
            "Initial");

        if (initialResult.Messages.Count == 0)
        {
            return new ProviderConsoleScenarioResult(
                provider.Name,
                "Session Resume",
                false,
                0,
                ErrorMessage: "Initial request returned no assistant messages.",
                DetailLines: initialDetailLines);
        }

        if (!initialResult.AssistantText.Contains("ACK", StringComparison.OrdinalIgnoreCase))
        {
            return new ProviderConsoleScenarioResult(
                provider.Name,
                "Session Resume",
                false,
                0,
                ErrorMessage: $"Initial request did not acknowledge the setup prompt. Response: {initialResult.AssistantText}",
                DetailLines: initialDetailLines);
        }

        if (string.IsNullOrWhiteSpace(initialResult.SessionId))
        {
            return new ProviderConsoleScenarioResult(
                provider.Name,
                "Session Resume",
                false,
                0,
                ErrorMessage: "DeepAgents did not expose a session identifier, so the session could not be resumed.",
                DetailLines: initialDetailLines);
        }

        var resumedOptions = executionOptions.CreateBaseOptions() with
        {
            SessionId = initialResult.SessionId
        };

        var followUpResult = await DeepAgentsScenarioMessageReader.ReadExecutionResultAsync(
            provider,
            resumedOptions,
            "What was the secret word I told you earlier? Reply with just the word.",
            cancellationToken);
        var followUpDetailLines = DeepAgentsScenarioMessageReader.BuildDetailLines(
            executionOptions,
            resumedOptions,
            "What was the secret word I told you earlier? Reply with just the word.",
            followUpResult,
            "Resume");
        var combinedDetailLines = DeepAgentsScenarioMessageReader.CombineDetailLines(initialDetailLines, followUpDetailLines);

        if (followUpResult.Messages.Count == 0)
        {
            return new ProviderConsoleScenarioResult(
                provider.Name,
                "Session Resume",
                false,
                0,
                ErrorMessage: "Resume request returned no assistant messages.",
                DetailLines: combinedDetailLines);
        }

        var normalized = followUpResult.AssistantText.Replace("`", string.Empty, StringComparison.Ordinal).Trim();
        return normalized.Contains(secret, StringComparison.OrdinalIgnoreCase)
            ? new ProviderConsoleScenarioResult(provider.Name, "Session Resume", true, 0, DetailLines: combinedDetailLines)
            : new ProviderConsoleScenarioResult(
                provider.Name,
                "Session Resume",
                false,
                0,
                ErrorMessage: $"Resume request did not return the remembered secret. Expected: {secret}. Response: {followUpResult.AssistantText}",
                DetailLines: combinedDetailLines);
    }
}
