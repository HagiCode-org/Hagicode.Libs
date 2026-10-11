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
/// mid-task. A run is complete at a <c>result</c> only when no subagent is still running, no background task owned by
/// a subagent is still running, and every turn that started has produced its own <c>result</c>.
/// A subagent can end its turn while a background command or monitor it started is still running. The CLI then reports
/// the subagent as completed, lets the main agent take another turn, and resumes the same subagent once that task
/// finishes; closing the stream in between kills the task and loses the subagent's real result. Such tasks are
/// recognizable by their <c>parent_task_id</c>.
/// Background shell tasks owned by the main agent are deliberately not awaited: a long-lived background command
/// (for example a dev server) never finishes on its own and would keep the run open forever.
/// </remarks>
internal sealed class ClaudeStreamCompletionTracker
{
    private const string SubagentTaskType = "local_agent";

    private readonly HashSet<string> _runningSubagentTaskIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _subagentOwnedTaskIds = new(StringComparer.Ordinal);
    private int _startedTurns;
    private int _finishedTurns;

    /// <summary>
    /// Gets a value indicating whether a <c>result</c> has been seen and the only thing holding the run open is
    /// background work started by a subagent that has already reported back.
    /// </summary>
    /// <remarks>
    /// This work is awaited, but unlike a running subagent it can last forever (a dev server started by a subagent),
    /// so callers should bound the wait.
    /// </remarks>
    public bool IsWaitingOnSubagentBackgroundWork =>
        _finishedTurns > 0
        && _finishedTurns >= _startedTurns
        && _runningSubagentTaskIds.Count == 0
        && _subagentOwnedTaskIds.Count > 0;

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
               || (_runningSubagentTaskIds.Count == 0
                   && _subagentOwnedTaskIds.Count == 0
                   && _finishedTurns >= _startedTurns);
    }

    private void ObserveSystemMessage(JsonElement content)
    {
        switch (GetString(content, "subtype"))
        {
            case "init":
                _startedTurns++;
                break;
            case "background_tasks_changed":
                ReplaceRunningTasks(content);
                break;
            case "task_started":
                if (GetString(content, "task_id") is { } startedTaskId)
                {
                    TrackTask(content, startedTaskId);
                }

                break;
            case "task_notification":
                if (GetString(content, "task_id") is { } finishedTaskId)
                {
                    _runningSubagentTaskIds.Remove(finishedTaskId);
                    _subagentOwnedTaskIds.Remove(finishedTaskId);
                }

                break;
        }
    }

    private void ReplaceRunningTasks(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object
            || !content.TryGetProperty("tasks", out var tasks)
            || tasks.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        _runningSubagentTaskIds.Clear();
        _subagentOwnedTaskIds.Clear();
        foreach (var task in tasks.EnumerateArray())
        {
            if (GetString(task, "task_id") is { } taskId)
            {
                TrackTask(task, taskId);
            }
        }
    }

    private void TrackTask(JsonElement task, string taskId)
    {
        if (IsSubagentTask(task))
        {
            _runningSubagentTaskIds.Add(taskId);
        }

        if (GetString(task, "parent_task_id") is not null)
        {
            _subagentOwnedTaskIds.Add(taskId);
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
