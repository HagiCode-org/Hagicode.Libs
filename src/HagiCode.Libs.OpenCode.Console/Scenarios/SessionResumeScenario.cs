using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.OpenCode;

namespace HagiCode.Libs.OpenCode.Console.Scenarios;

public static class SessionResumeScenario
{
    public static ProviderConsoleScenario<ICliProvider<OpenCodeOptions>> Create(OpenCodeConsoleExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);

        return new ProviderConsoleScenario<ICliProvider<OpenCodeOptions>>(
            "Session Resume",
            "Verify that a follow-up request can resume the same OpenCode session.",
            (provider, cancellationToken) => ExecuteAsync(provider, executionOptions, cancellationToken));
    }

    private static async Task<ProviderConsoleScenarioResult> ExecuteAsync(
        ICliProvider<OpenCodeOptions> provider,
        OpenCodeConsoleExecutionOptions executionOptions,
        CancellationToken cancellationToken)
    {
        var secret = $"BLUEPRINT-{Guid.NewGuid():N}";
        var initialPrompt = $"Remember the secret word: {secret}. Reply with exactly ACK.";
        var initialResult = await OpenCodeScenarioMessageReader.ReadExecutionResultAsync(
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
                ErrorMessage: "OpenCode did not expose a session identifier, so the session could not be resumed.");
        }

        var resumedOptions = executionOptions.CreateBaseOptions() with
        {
            SessionId = initialResult.SessionId,
        };

        var followUpResult = await OpenCodeScenarioMessageReader.ReadExecutionResultAsync(
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
