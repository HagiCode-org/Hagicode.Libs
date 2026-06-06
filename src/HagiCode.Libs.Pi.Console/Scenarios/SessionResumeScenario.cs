using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Pi;

namespace HagiCode.Libs.Pi.Console.Scenarios;

public static class SessionResumeScenario
{
    public static ProviderConsoleScenario<ICliProvider<PiOptions>> Create(PiConsoleExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);

        return new ProviderConsoleScenario<ICliProvider<PiOptions>>(
            "Session Resume",
            "Verify that a follow-up request can resume the same Pi session.",
            (provider, cancellationToken) => ExecuteAsync(provider, executionOptions, cancellationToken));
    }

    private static async Task<ProviderConsoleScenarioResult> ExecuteAsync(
        ICliProvider<PiOptions> provider,
        PiConsoleExecutionOptions executionOptions,
        CancellationToken cancellationToken)
    {
        using var workspace = PiScenarioWorkspace.Create(executionOptions.WorkspacePath, executionOptions.SessionDirectory);
        var secret = $"BLUEPRINT-{Guid.NewGuid():N}";
        var initialPrompt = $"Remember the secret word: {secret}. Reply in visible final text with exactly the word ACK. Do not place the final answer only in reasoning.";
        var initialOptions = executionOptions.CreateBaseOptions() with
        {
            WorkingDirectory = workspace.WorkingDirectory,
            SessionDirectory = workspace.SessionDirectory,
            DisableAllTools = true,
        };

        var initialResult = await PiScenarioMessageReader.ReadExecutionResultAsync(
            provider,
            initialOptions,
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
                ErrorMessage: "Pi did not expose a session identifier, so the session could not be resumed.");
        }

        var resumedOptions = executionOptions.CreateBaseOptions() with
        {
            WorkingDirectory = workspace.WorkingDirectory,
            SessionDirectory = workspace.SessionDirectory,
            SessionId = initialResult.SessionId,
            DisableAllTools = true,
        };

        var followUpResult = await PiScenarioMessageReader.ReadExecutionResultAsync(
            provider,
            resumedOptions,
            "What was the secret word I told you earlier? Reply in visible final text with just the remembered word. Do not place the final answer only in reasoning.",
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
