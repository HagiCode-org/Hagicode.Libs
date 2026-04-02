using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.DeepAgents;

namespace HagiCode.Libs.DeepAgents.Console.Scenarios;

public static class ToolcallParsingScenario
{
    private static readonly IReadOnlyList<string> ExpectedLifecycle =
    [
        "session.started",
        "tool.call",
        "tool.permission",
        "tool.completed",
        "assistant",
        "terminal.completed"
    ];

    public static ProviderConsoleScenario<ICliProvider<DeepAgentsOptions>> Create(DeepAgentsConsoleExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);

        return new ProviderConsoleScenario<ICliProvider<DeepAgentsOptions>>(
            "Toolcall Parsing",
            "Validate the normalized DeepAgents toolcall lifecycle for a successful tool execution.",
            (provider, cancellationToken) => ExecuteAsync(provider, executionOptions, cancellationToken));
    }

    private static async Task<ProviderConsoleScenarioResult> ExecuteAsync(
        ICliProvider<DeepAgentsOptions> provider,
        DeepAgentsConsoleExecutionOptions executionOptions,
        CancellationToken cancellationToken)
    {
        var options = executionOptions.CreateBaseOptions();
        const string prompt = """
DeepAgents toolcall diagnostics: successful lifecycle validation.

Use the execute tool to run exactly this command:
dotnet --version

Do not answer from memory.
After the command completes, reply with exactly:
tool completed successfully
""";
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
                "Expected the DeepAgents toolcall lifecycle to reach tool completion in order.",
                lifecycleMismatch);
        }

        var completedTool = trace.ToolEvents.LastOrDefault(static toolEvent => string.Equals(toolEvent.LifecycleStage, "tool.completed", StringComparison.OrdinalIgnoreCase));
        if (completedTool is null)
        {
            return CreateFailureResult(
                provider.Name,
                executionOptions,
                options,
                prompt,
                trace,
                "Expected a completed tool event but none was observed.",
                "FirstMismatch: no tool.completed lifecycle node was emitted.");
        }

        if (string.IsNullOrWhiteSpace(completedTool.ToolName) || string.IsNullOrWhiteSpace(completedTool.ToolCallId))
        {
            return CreateFailureResult(
                provider.Name,
                executionOptions,
                options,
                prompt,
                trace,
                "Tool metadata was not preserved across normalization.",
                $"FirstMismatch: tool metadata incomplete. name='{completedTool.ToolName ?? "(null)"}', id='{completedTool.ToolCallId ?? "(null)"}'.");
        }

        if (!trace.AssistantText.Contains("tool completed successfully", StringComparison.OrdinalIgnoreCase))
        {
            return CreateFailureResult(
                provider.Name,
                executionOptions,
                options,
                prompt,
                trace,
                "Expected assistant output to preserve the post-tool completion summary.",
                $"FirstMismatch: assistant output '{trace.AssistantText}' did not contain the expected completion summary.");
        }

        var detailLines = DeepAgentsToolcallTraceReader.BuildDetailLines(
            executionOptions,
            options,
            "Toolcall Parsing",
            prompt,
            trace);
        return new ProviderConsoleScenarioResult(provider.Name, "Toolcall Parsing", true, 0, DetailLines: detailLines);
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
            "Toolcall Parsing",
            prompt,
            trace,
            [diagnosticLine]);
        return new ProviderConsoleScenarioResult(providerName, "Toolcall Parsing", false, 0, ErrorMessage: errorMessage, DetailLines: detailLines);
    }
}
