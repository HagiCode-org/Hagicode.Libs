using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Pi;

namespace HagiCode.Libs.Pi.Console.Scenarios;

public static class SimplePromptScenario
{
    public static ProviderConsoleScenario<ICliProvider<PiOptions>> Create(PiConsoleExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);

        return new ProviderConsoleScenario<ICliProvider<PiOptions>>(
            "Simple Prompt",
            "Send a basic prompt and validate the expected pong response.",
            (provider, cancellationToken) => ExecuteAsync(provider, executionOptions, cancellationToken));
    }

    private static async Task<ProviderConsoleScenarioResult> ExecuteAsync(
        ICliProvider<PiOptions> provider,
        PiConsoleExecutionOptions executionOptions,
        CancellationToken cancellationToken)
    {
        using var workspace = PiScenarioWorkspace.Create(executionOptions.WorkspacePath, executionOptions.SessionDirectory);
        var options = executionOptions.CreateBaseOptions() with
        {
            WorkingDirectory = workspace.WorkingDirectory,
            NoSession = true,
            DisableAllTools = true,
        };

        var result = await PiScenarioMessageReader.ReadExecutionResultAsync(
            provider,
            options,
            "Reply in visible final text with exactly one word: pong. Do not place the final answer only in reasoning.",
            cancellationToken);

        if (result.Messages.Count == 0)
        {
            return new ProviderConsoleScenarioResult(provider.Name, "Simple Prompt", false, 0, ErrorMessage: "No assistant messages received from provider.");
        }

        var combined = result.AssistantText;
        var success = combined.Contains("pong", StringComparison.OrdinalIgnoreCase);

        return success
            ? new ProviderConsoleScenarioResult(provider.Name, "Simple Prompt", true, 0)
            : new ProviderConsoleScenarioResult(
                provider.Name,
                "Simple Prompt",
                false,
                0,
                ErrorMessage: $"Expected response to contain 'pong' but got: {combined}");
    }
}
