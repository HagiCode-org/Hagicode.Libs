using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.IFlow;

namespace HagiCode.Libs.IFlow.Console.Scenarios;

public static class SessionResumeScenario
{
    public static ProviderConsoleScenario<ICliProvider<IFlowOptions>> Create(IFlowConsoleExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);

        return new ProviderConsoleScenario<ICliProvider<IFlowOptions>>(
            "Session Resume",
            "Verify that a follow-up request can resume the same IFlow session.",
            (provider, cancellationToken) => ExecuteAsync(provider, executionOptions, cancellationToken));
    }

    private static async Task<ProviderConsoleScenarioResult> ExecuteAsync(
        ICliProvider<IFlowOptions> provider,
        IFlowConsoleExecutionOptions executionOptions,
        CancellationToken cancellationToken)
    {
        var secret = $"BLUEPRINT-{Guid.NewGuid():N}";
        var initialPrompt = $"Remember the secret word: {secret}. Reply with exactly ACK.";
        var initialResult = await IFlowScenarioMessageReader.ReadExecutionResultAsync(
            provider,
            executionOptions.CreateBaseOptions(),
            initialPrompt,
            cancellationToken);

        if (initialResult.Messages.Count == 0)
        {
            return new ProviderConsoleScenarioResult(
                provider.Name,
                "Session Resume",
                false,
                0,
                ErrorMessage: "Initial request returned no assistant messages.");
        }

        var initialCombined = initialResult.AssistantText;
        if (!initialCombined.Contains("ACK", StringComparison.OrdinalIgnoreCase))
        {
            return new ProviderConsoleScenarioResult(
                provider.Name,
                "Session Resume",
                false,
                0,
                ErrorMessage: $"Initial request did not acknowledge the setup prompt. Response: {initialCombined}");
        }

        if (string.IsNullOrWhiteSpace(initialResult.SessionId))
        {
            return new ProviderConsoleScenarioResult(
                provider.Name,
                "Session Resume",
                false,
                0,
                ErrorMessage: "IFlow did not expose a session identifier, so the session could not be resumed.");
        }

        var resumedOptions = executionOptions.CreateBaseOptions() with
        {
            SessionId = initialResult.SessionId,
        };

        var followUpResult = await IFlowScenarioMessageReader.ReadExecutionResultAsync(
            provider,
            resumedOptions,
            "What was the secret word I told you earlier? Reply with just the word.",
            cancellationToken);

        if (followUpResult.Messages.Count == 0)
        {
            return new ProviderConsoleScenarioResult(
                provider.Name,
                "Session Resume",
                false,
                0,
                ErrorMessage: "Resume request returned no assistant messages.");
        }

        var followUpCombined = followUpResult.AssistantText;
        var normalized = followUpCombined.Replace("`", string.Empty, StringComparison.Ordinal).Trim();
        var success = normalized.Contains(secret, StringComparison.OrdinalIgnoreCase);

        return success
            ? new ProviderConsoleScenarioResult(provider.Name, "Session Resume", true, 0)
            : new ProviderConsoleScenarioResult(
                provider.Name,
                "Session Resume",
                false,
                0,
                ErrorMessage: $"Resume request did not return the remembered secret. Expected: {secret}. Response: {followUpCombined}");
    }
}
