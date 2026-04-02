using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.DeepAgents;

namespace HagiCode.Libs.DeepAgents.Console.Scenarios;

public static class ToolcallFailureScenario
{
    private static readonly IReadOnlyList<string> ExpectedLifecycle =
    [
        "session.started",
        "assistant",
        "tool.call",
        "tool.failed",
        "terminal.failed"
    ];

    public static ProviderConsoleScenario<ICliProvider<DeepAgentsOptions>> Create(DeepAgentsConsoleExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);

        return new ProviderConsoleScenario<ICliProvider<DeepAgentsOptions>>(
            "Toolcall Failure",
            "Validate that failed toolcall transcripts surface actionable lifecycle diagnostics.",
            (provider, cancellationToken) => ExecuteAsync(provider, executionOptions, cancellationToken));
    }

    private static async Task<ProviderConsoleScenarioResult> ExecuteAsync(
        ICliProvider<DeepAgentsOptions> provider,
        DeepAgentsConsoleExecutionOptions executionOptions,
        CancellationToken cancellationToken)
    {
        var options = executionOptions.CreateBaseOptions();
        const string prompt = "DeepAgents toolcall diagnostics: failure lifecycle fixture.";
        var trace = await DeepAgentsToolcallTraceReader.ReadTraceAsync(provider, options, prompt, cancellationToken);

        var lifecycleMismatch = DeepAgentsToolcallTraceReader.FindFirstLifecycleMismatch(ExpectedLifecycle, trace.LifecycleStages);
        if (lifecycleMismatch is not null)
        {
            return CreateFailureResult(
                provider.Name,
                executionOptions,
                options,
                prompt,
                trace,
                "Expected the failure fixture to emit a failed tool lifecycle and terminal failure.",
                lifecycleMismatch);
        }

        var failedTool = trace.ToolEvents.LastOrDefault(static toolEvent => string.Equals(toolEvent.LifecycleStage, "tool.failed", StringComparison.OrdinalIgnoreCase));
        if (failedTool is null)
        {
            return CreateFailureResult(
                provider.Name,
                executionOptions,
                options,
                prompt,
                trace,
                "Expected a failed tool lifecycle node but none was observed.",
                "FirstMismatch: no tool.failed lifecycle node was emitted.");
        }

        if (string.IsNullOrWhiteSpace(trace.TerminalMessage) ||
            !trace.TerminalMessage.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
        {
            return CreateFailureResult(
                provider.Name,
                executionOptions,
                options,
                prompt,
                trace,
                "Expected the terminal failure to preserve the actionable failure message.",
                $"FirstMismatch: terminal failure message '{trace.TerminalMessage ?? "(null)"}' did not contain 'permission denied'.");
        }

        if (string.IsNullOrWhiteSpace(failedTool.ToolName) || string.IsNullOrWhiteSpace(failedTool.ToolCallId))
        {
            return CreateFailureResult(
                provider.Name,
                executionOptions,
                options,
                prompt,
                trace,
                "Tool failure diagnostics did not preserve the related tool metadata.",
                $"FirstMismatch: failed tool metadata incomplete. name='{failedTool.ToolName ?? "(null)"}', id='{failedTool.ToolCallId ?? "(null)"}'.");
        }

        var detailLines = DeepAgentsToolcallTraceReader.BuildDetailLines(
            executionOptions,
            options,
            "Toolcall Failure",
            prompt,
            trace);
        return new ProviderConsoleScenarioResult(provider.Name, "Toolcall Failure", true, 0, DetailLines: detailLines);
    }

    private static ProviderConsoleScenarioResult CreateFailureResult(
        string providerName,
        DeepAgentsConsoleExecutionOptions executionOptions,
        DeepAgentsOptions options,
        string prompt,
        DeepAgentsToolcallTrace trace,
        string errorMessage,
        string diagnosticLine)
    {
        var detailLines = DeepAgentsToolcallTraceReader.BuildDetailLines(
            executionOptions,
            options,
            "Toolcall Failure",
            prompt,
            trace,
            [diagnosticLine]);
        return new ProviderConsoleScenarioResult(providerName, "Toolcall Failure", false, 0, ErrorMessage: errorMessage, DetailLines: detailLines);
    }
}
