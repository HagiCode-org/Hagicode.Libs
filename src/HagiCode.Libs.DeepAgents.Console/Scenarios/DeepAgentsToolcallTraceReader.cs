using System.Text;
using System.Text.Json;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.DeepAgents;

namespace HagiCode.Libs.DeepAgents.Console.Scenarios;

internal static class DeepAgentsToolcallTraceReader
{
    private const int PreviewMaxLength = 240;

    public static async Task<DeepAgentsToolcallTrace> ReadTraceAsync(
        ICliProvider<DeepAgentsOptions> provider,
        DeepAgentsOptions options,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var rawMessageTypes = new List<string>();
        var lifecycleStages = new List<string>();
        var assistantMessages = new List<string>();
        var assistantTextBuilder = new StringBuilder();
        var toolEvents = new List<DeepAgentsToolcallEvent>();
        var knownToolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        string? sessionId = null;
        string? terminalMessageType = null;
        string? terminalMessage = null;

        await foreach (var message in provider.ExecuteAsync(options, prompt, cancellationToken))
        {
            rawMessageTypes.Add(message.Type);

            if (TryGetSessionId(message.Content, out var resolvedSessionId))
            {
                sessionId ??= resolvedSessionId;
            }

            if (string.Equals(message.Type, "assistant", StringComparison.OrdinalIgnoreCase) &&
                TryGetText(message.Content, out var assistantText) &&
                !string.IsNullOrWhiteSpace(assistantText))
            {
                assistantMessages.Add(assistantText);
                assistantTextBuilder.Append(assistantText);
            }

            var lifecycleStage = ResolveLifecycleStage(message);
            lifecycleStages.Add(lifecycleStage);

            if (TryBuildToolEvent(message, lifecycleStage, toolEvents.Count + 1, out var toolEvent))
            {
                var resolvedToolEvent = BackfillToolMetadata(toolEvent!, knownToolNames);
                toolEvents.Add(resolvedToolEvent);
            }

            if (string.Equals(lifecycleStage, "terminal.failed", StringComparison.OrdinalIgnoreCase))
            {
                terminalMessageType ??= lifecycleStage;
                terminalMessage ??= TryGetTerminalMessage(message.Content);
            }
            else if (string.Equals(lifecycleStage, "terminal.completed", StringComparison.OrdinalIgnoreCase))
            {
                terminalMessageType ??= lifecycleStage;
            }

            if (string.Equals(lifecycleStage, "terminal.completed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(lifecycleStage, "terminal.failed", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        return new DeepAgentsToolcallTrace(
            rawMessageTypes,
            lifecycleStages,
            assistantMessages,
            assistantTextBuilder.ToString().Trim(),
            sessionId,
            terminalMessageType,
            terminalMessage,
            toolEvents);
    }

    public static IReadOnlyList<string>? BuildDetailLines(
        DeepAgentsConsoleExecutionOptions executionOptions,
        DeepAgentsOptions options,
        string scenarioName,
        string prompt,
        DeepAgentsToolcallTrace trace,
        IReadOnlyList<string>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioName);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(trace);

        var includeSummary = executionOptions.Verbose || (diagnostics?.Count ?? 0) > 0;
        if (!includeSummary)
        {
            return null;
        }

        var lines = new List<string>();
        if (diagnostics is { Count: > 0 })
        {
            lines.AddRange(diagnostics.Where(static line => !string.IsNullOrWhiteSpace(line)));
        }

        if (executionOptions.Verbose)
        {
            lines.Add($"Scenario: {scenarioName}");
            lines.Add($"Prompt: {BuildPreview(prompt)}");
            lines.Add($"Model: {options.Model ?? "(default)"}");
            lines.Add($"Mode: {options.ModeId ?? "(default)"}");
            lines.Add($"Workspace: {options.WorkspaceRoot ?? options.WorkingDirectory ?? "(default)"}");
        }

        lines.Add($"SessionId: {trace.SessionId ?? "(none)"}");
        lines.Add($"RawMessageTypes: {trace.RawMessageTypeSummary}");
        lines.Add($"LifecycleTrace: {trace.LifecycleSummary}");

        if (!string.IsNullOrWhiteSpace(trace.TerminalMessage))
        {
            lines.Add($"TerminalMessage: {BuildPreview(trace.TerminalMessage!)}");
        }

        if (executionOptions.Verbose)
        {
            lines.Add($"AssistantSegments: {trace.AssistantMessages.Count}");
            lines.Add($"AssistantPreview: {BuildPreview(trace.AssistantText)}");
        }

        if (trace.ToolEvents.Count == 0)
        {
            lines.Add("ToolEvents: (none)");
        }
        else
        {
            foreach (var toolEvent in trace.ToolEvents)
            {
                lines.Add(
                    $"Tool[{toolEvent.Index}]: stage={toolEvent.LifecycleStage}, name={toolEvent.ToolName ?? "(unknown)"}, id={toolEvent.ToolCallId ?? "(none)"}, status={toolEvent.Status ?? "(none)"}, summary={BuildPreview(toolEvent.Summary)}");
            }
        }

        return lines;
    }

    public static string? FindFirstLifecycleMismatch(
        IReadOnlyList<string> expected,
        IReadOnlyList<string> actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        var actualIndex = 0;
        for (var expectedIndex = 0; expectedIndex < expected.Count; expectedIndex++)
        {
            while (actualIndex < actual.Count &&
                   !string.Equals(expected[expectedIndex], actual[actualIndex], StringComparison.OrdinalIgnoreCase))
            {
                actualIndex++;
            }

            if (actualIndex >= actual.Count)
            {
                return $"FirstMismatch: step {expectedIndex + 1} expected '{expected[expectedIndex]}' but it was not observed in order.";
            }

            actualIndex++;
        }

        return null;
    }

    private static string ResolveLifecycleStage(CliMessage message)
    {
        if (string.Equals(message.Type, "tool.update", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveToolUpdateLifecycle(message.Content);
        }

        if (string.Equals(message.Type, "tool.permission", StringComparison.OrdinalIgnoreCase))
        {
            return "tool.permission";
        }

        if (string.Equals(message.Type, "tool.failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.Type, "tool.completed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.Type, "tool.call", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.Type, "assistant", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.Type, "terminal.completed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.Type, "terminal.failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.Type, "session.started", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.Type, "session.resumed", StringComparison.OrdinalIgnoreCase))
        {
            return message.Type;
        }

        return message.Type;
    }

    private static string ResolveToolUpdateLifecycle(JsonElement content)
    {
        var updateElement = ResolveUpdateElement(content);
        var status = TryGetString(updateElement, "status")
                     ?? TryGetString(updateElement, "state")
                     ?? TryGetString(updateElement, "phase");
        if (status is null)
        {
            return "tool.update";
        }

        return status.Trim().ToLowerInvariant() switch
        {
            "completed" or "complete" or "done" or "success" or "succeeded" => "tool.completed",
            "failed" or "failure" or "error" => "tool.failed",
            _ => "tool.update"
        };
    }

    private static bool TryBuildToolEvent(
        CliMessage message,
        string lifecycleStage,
        int index,
        out DeepAgentsToolcallEvent? toolEvent)
    {
        toolEvent = null;
        if (!lifecycleStage.StartsWith("tool.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var updateElement = ResolveUpdateElement(message.Content);
        toolEvent = new DeepAgentsToolcallEvent(
            index,
            lifecycleStage,
            TryGetToolName(message.Content, updateElement),
            TryGetToolCallId(message.Content, updateElement),
            TryGetString(updateElement, "status") ?? TryGetString(updateElement, "state") ?? TryGetString(message.Content, "status"),
            TryGetSummary(message.Content, updateElement));
        return true;
    }

    private static DeepAgentsToolcallEvent BackfillToolMetadata(
        DeepAgentsToolcallEvent toolEvent,
        IDictionary<string, string> knownToolNames)
    {
        if (!string.IsNullOrWhiteSpace(toolEvent.ToolCallId))
        {
            if (!string.IsNullOrWhiteSpace(toolEvent.ToolName))
            {
                knownToolNames[toolEvent.ToolCallId] = toolEvent.ToolName;
                return toolEvent;
            }

            if (knownToolNames.TryGetValue(toolEvent.ToolCallId, out var knownToolName))
            {
                return toolEvent with { ToolName = knownToolName };
            }
        }

        return toolEvent;
    }

    private static JsonElement ResolveUpdateElement(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.Object &&
            content.TryGetProperty("update", out var updateElement) &&
            updateElement.ValueKind == JsonValueKind.Object)
        {
            return updateElement;
        }

        return content;
    }

    private static bool TryGetSessionId(JsonElement content, out string? sessionId)
    {
        sessionId = TryGetString(content, "session_id")
                    ?? TryGetString(content, "sessionId");
        return !string.IsNullOrWhiteSpace(sessionId);
    }

    private static bool TryGetText(JsonElement content, out string? text)
    {
        text = TryGetString(content, "text");
        return !string.IsNullOrWhiteSpace(text);
    }

    private static string? TryGetTerminalMessage(JsonElement content)
    {
        return TryGetString(content, "message")
               ?? TryGetString(content, "text")
               ?? TryGetString(ResolveUpdateElement(content), "message")
               ?? TryGetString(ResolveUpdateElement(content), "text");
    }

    private static string? TryGetToolName(JsonElement content, JsonElement updateElement)
    {
        return TryGetString(content, "tool_name")
               ?? TryGetString(content, "toolName")
               ?? TryGetString(updateElement, "tool_name")
               ?? TryGetString(updateElement, "toolName")
               ?? TryGetString(updateElement, "name")
               ?? TryGetNestedString(updateElement, "tool", "name")
               ?? TryGetNestedString(updateElement, "toolCall", "name");
    }

    private static string? TryGetToolCallId(JsonElement content, JsonElement updateElement)
    {
        return TryGetString(content, "tool_call_id")
               ?? TryGetString(content, "toolCallId")
               ?? TryGetString(updateElement, "tool_call_id")
               ?? TryGetString(updateElement, "toolCallId")
               ?? TryGetString(updateElement, "callId")
               ?? TryGetString(updateElement, "id")
               ?? TryGetNestedString(updateElement, "tool", "id")
               ?? TryGetNestedString(updateElement, "toolCall", "id");
    }

    private static string? TryGetSummary(JsonElement content, JsonElement updateElement)
    {
        return TryGetString(content, "text")
               ?? TryGetString(content, "title")
               ?? TryGetString(content, "message")
               ?? TryGetString(updateElement, "summary")
               ?? TryGetString(updateElement, "message")
               ?? TryGetString(updateElement, "text")
               ?? TryGetNestedString(updateElement, "content", "text")
               ?? TryGetNestedString(updateElement, "result", "text");
    }

    private static string? TryGetNestedString(JsonElement element, string propertyName, string nestedPropertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var propertyElement) ||
            propertyElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return TryGetString(propertyElement, nestedPropertyName);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var propertyElement))
        {
            return null;
        }

        return propertyElement.ValueKind switch
        {
            JsonValueKind.String => propertyElement.GetString(),
            JsonValueKind.Number => propertyElement.GetRawText(),
            _ => null
        };
    }

    private static string BuildPreview(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(empty)";
        }

        var normalized = value
            .Trim()
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

        return normalized.Length <= PreviewMaxLength
            ? normalized
            : $"{normalized[..PreviewMaxLength]}...";
    }
}

internal sealed record DeepAgentsToolcallTrace(
    IReadOnlyList<string> RawMessageTypes,
    IReadOnlyList<string> LifecycleStages,
    IReadOnlyList<string> AssistantMessages,
    string AssistantText,
    string? SessionId,
    string? TerminalMessageType,
    string? TerminalMessage,
    IReadOnlyList<DeepAgentsToolcallEvent> ToolEvents)
{
    public string RawMessageTypeSummary => RawMessageTypes.Count == 0 ? "(none)" : string.Join(" -> ", RawMessageTypes);

    public string LifecycleSummary => LifecycleStages.Count == 0 ? "(none)" : string.Join(" -> ", LifecycleStages);
}

internal sealed record DeepAgentsToolcallEvent(
    int Index,
    string LifecycleStage,
    string? ToolName,
    string? ToolCallId,
    string? Status,
    string? Summary);
