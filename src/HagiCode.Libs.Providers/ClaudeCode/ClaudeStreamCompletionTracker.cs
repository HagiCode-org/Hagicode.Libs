using System.Text.Json;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.ClaudeCode;

/// <summary>
/// Decides when a Claude Code <c>stream-json</c> run is really finished.
/// </summary>
/// <remarks>
/// A <c>result</c> message only closes one turn. When the model launches a background subagent the CLI emits a
/// <c>result</c> for the launching turn, keeps the subagent running, and starts follow-up turns
/// (<c>system/init</c> ... <c>result</c>) once the subagent reports back through <c>task_notification</c>.
/// Ending the stream at the first <c>result</c> would therefore drop everything the subagent produces and kill the CLI
/// mid-task. A run is complete at a <c>result</c> only when no subagent is still running and every turn that
/// started has produced its own <c>result</c>.
/// Background shell tasks are deliberately not awaited: a long-lived background command (for example a dev server)
/// never finishes on its own and would keep the run open forever.
/// </remarks>
internal sealed class ClaudeStreamCompletionTracker
{
    private const string SubagentTaskType = "local_agent";

    private readonly HashSet<string> _runningSubagentTaskIds = new(StringComparer.Ordinal);
    private int _startedTurns;
    private int _finishedTurns;

    /// <summary>
    /// Observes one CLI message and reports whether the run is complete after it.
    /// </summary>
    /// <param name="message">The message that was just received.</param>
    /// <returns><see langword="true" /> when no further messages are expected for this run.</returns>
    public bool Observe(CliMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (string.Equals(message.Type, "error", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(message.Type, "system", StringComparison.OrdinalIgnoreCase))
        {
            ObserveSystemMessage(message.Content);
            return false;
        }

        if (!string.Equals(message.Type, "result", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _finishedTurns++;
        return IsErrorResult(message.Content)
               || (_runningSubagentTaskIds.Count == 0 && _finishedTurns >= _startedTurns);
    }

    private void ObserveSystemMessage(JsonElement content)
    {
        switch (GetString(content, "subtype"))
        {
            case "init":
                _startedTurns++;
                break;
            case "background_tasks_changed":
                ReplaceRunningSubagents(content);
                break;
            case "task_started":
                if (IsSubagentTask(content) && GetString(content, "task_id") is { } startedTaskId)
                {
                    _runningSubagentTaskIds.Add(startedTaskId);
                }

                break;
            case "task_notification":
                if (GetString(content, "task_id") is { } finishedTaskId)
                {
                    _runningSubagentTaskIds.Remove(finishedTaskId);
                }

                break;
        }
    }

    private void ReplaceRunningSubagents(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object
            || !content.TryGetProperty("tasks", out var tasks)
            || tasks.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        _runningSubagentTaskIds.Clear();
        foreach (var task in tasks.EnumerateArray())
        {
            if (IsSubagentTask(task) && GetString(task, "task_id") is { } taskId)
            {
                _runningSubagentTaskIds.Add(taskId);
            }
        }
    }

    private static bool IsSubagentTask(JsonElement element)
    {
        return string.Equals(GetString(element, "task_type"), SubagentTaskType, StringComparison.Ordinal);
    }

    private static bool IsErrorResult(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (content.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        return GetString(content, "subtype") is { } subtype
               && subtype.StartsWith("error", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
