using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.DeepAgents;

namespace HagiCode.Libs.DeepAgents.Console.Scenarios;

public static class BypassBashPingScenario
{
    public static ProviderConsoleScenario<ICliProvider<DeepAgentsOptions>> Create(DeepAgentsConsoleExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);

        return new ProviderConsoleScenario<ICliProvider<DeepAgentsOptions>>(
            "Bypass Bash Ping",
            "Verify that bypass mode can execute a bash-based network ping without permission callbacks.",
            (provider, cancellationToken) => ExecuteAsync(provider, executionOptions, cancellationToken));
    }

    private static async Task<ProviderConsoleScenarioResult> ExecuteAsync(
        ICliProvider<DeepAgentsOptions> provider,
        DeepAgentsConsoleExecutionOptions executionOptions,
        CancellationToken cancellationToken)
    {
        var options = executionOptions.CreateBaseOptions();
        const string prompt = """
Use the bash tool to run exactly this command without asking for confirmation:
ping -c 1 1.1.1.1

If the command succeeds, reply with exactly PING_OK.
If the command fails, reply with exactly PING_FAIL.
Do not include any extra words.
""";

        var result = await DeepAgentsScenarioMessageReader.ReadExecutionResultAsync(
            provider,
            options,
            prompt,
            cancellationToken);
        var detailLines = DeepAgentsScenarioMessageReader.BuildDetailLines(executionOptions, options, prompt, result);

        if (result.Messages.Count == 0)
        {
            return new ProviderConsoleScenarioResult(
                provider.Name,
                "Bypass Bash Ping",
                false,
                0,
                ErrorMessage: "No assistant messages received from provider.",
                DetailLines: detailLines);
        }

        return string.Equals(result.AssistantText.Trim(), "PING_OK", StringComparison.Ordinal)
            ? new ProviderConsoleScenarioResult(provider.Name, "Bypass Bash Ping", true, 0, DetailLines: detailLines)
            : new ProviderConsoleScenarioResult(
                provider.Name,
                "Bypass Bash Ping",
                false,
                0,
                ErrorMessage: $"Expected response to be exactly 'PING_OK' but got: {result.AssistantText}",
                DetailLines: detailLines);
    }
}
