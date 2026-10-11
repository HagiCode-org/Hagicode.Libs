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
/// a subagent is still running, every turn that started has produced its own <c>result</c>, and every prompt written
/// to the CLI has started its own turn.
/// A subagent can end its turn while a background command or monitor it started is still running. The CLI then reports
/// the subagent as completed, lets the main agent take another turn, and resumes the same subagent once that task
/// finishes; closing the stream in between kills the task and loses the subagent's real result. Such tasks are
/// recognizable by their <c>parent_task_id</c>.
/// Background shell tasks owned by the main agent are awaited only when they are bounded. A long-lived background
/// command with no timeout (for example a dev server) never finishes on its own and would keep the run open forever.
/// A <c>Monitor</c> (<c>timeout_ms</c>) or a <c>Bash</c> command run in the background with an explicit <c>timeout</c> is
/// different: the agent arms it to be woken by its events or its exit and normally ends its turn right after, and the
/// CLI ends the task, with a <c>task_notification</c>, when that timeout passes. Closing the stream early kills the task
/// and every later event is lost, so the run waits for it until its declared timeout plus a grace period. Both are
/// reported as a plain <c>local_bash</c> task; they are recognized by the tool call whose <c>tool_use_id</c> the
/// <c>task_started</c> message carries.
/// A prompt is recognized as started by the CLI replaying it (<c>isReplay</c>, requested with
/// <c>--replay-user-messages</c>). A turn that is not the prompt's own can end first: a stopped background-task
/// notification from an earlier session starts one on resume, and its <c>result</c> is not the answer to the prompt.
/// </remarks>
internal sealed class ClaudeStreamCompletionTracker
{
    private const string SubagentTaskType = "local_agent";
    private const string MonitorToolName = "Monitor";
    private const string BashToolName = "Bash";
    private const double DefaultMonitorTimeoutMilliseconds = 300_000;
    private const double MaxMonitorTimeoutMilliseconds = 1_800_000;

    private readonly HashSet<string> _runningSubagentTaskIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _subagentOwnedTaskIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimeSpan> _boundedToolCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _boundedTaskDeadlines = new(StringComparer.Ordinal);
    private readonly HashSet<string> _runningBoundedTaskIds = new(StringComparer.Ordinal);
    private readonly TimeSpan _backgroundWorkGracePeriod;
    private readonly TimeProvider _timeProvider;
    private int _startedTurns;
    private int _finishedTurns;
    private int _hostPromptsAwaitingStart;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClaudeStreamCompletionTracker" /> class.
    /// </summary>
    /// <param name="backgroundWorkGracePeriod">
    /// How long past its declared timeout the run keeps waiting for a bounded background task of the main agent.
    /// </param>
    /// <param name="timeProvider">The clock that dates those tasks, or <see langword="null" /> for the system clock.</param>
    public ClaudeStreamCompletionTracker(TimeSpan backgroundWorkGracePeriod, TimeProvider? timeProvider = null)
    {
        _backgroundWorkGracePeriod = backgroundWorkGracePeriod;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Describes what the run is idle on when no turn is running, so the caller knows which bounded wait to apply.
    /// </summary>
    internal enum PendingWait
    {
        /// <summary>No bounded wait applies: keep reading until the stream ends or completes.</summary>
        None,

        /// <summary>Background work owned by a subagent is still running after its turn ended.</summary>
        SubagentBackgroundWork,

        /// <summary>A monitor or a timed background command armed by the main agent is still running.</summary>
        MainAgentBackgroundWork,

        /// <summary>A prompt written to the CLI has not started its own turn.</summary>
        HostPromptStart
    }

    /// <summary>
    /// Gets the bounded wait the run is currently in.
    /// </summary>
    /// <remarks>
    /// These waits are awaited, but they may never end on their own (a dev server started by a subagent, or a CLI that
    /// dropped the prompt), so callers should bound them.
    /// </remarks>
    public PendingWait CurrentWait
    {
        get
        {
            if (_finishedTurns == 0 || _finishedTurns < _startedTurns || _runningSubagentTaskIds.Count > 0)
            {
                return PendingWait.None;
            }

            if (_subagentOwnedTaskIds.Count > 0)
            {
                return PendingWait.SubagentBackgroundWork;
            }

            if (_runningBoundedTaskIds.Count > 0)
            {
                return PendingWait.MainAgentBackgroundWork;
            }

            return _hostPromptsAwaitingStart > 0 ? PendingWait.HostPromptStart : PendingWait.None;
        }
    }

    /// <summary>
    /// Gets how much longer the run waits for the bounded background work of the main agent: the latest declared
    /// timeout plus the grace period, counted from when each task started. Never negative.
    /// </summary>
    public TimeSpan RemainingBackgroundWork
    {
        get
        {
            var now = _timeProvider.GetUtcNow();
            var remaining = TimeSpan.Zero;
            foreach (var taskId in _runningBoundedTaskIds)
            {
                if (_boundedTaskDeadlines.TryGetValue(taskId, out var deadline) && deadline - now > remaining)
                {
                    remaining = deadline - now;
                }
            }

            return remaining;
        }
    }

    /// <summary>
    /// Gets a value indicating whether a prompt written to the CLI has not been replayed yet.
    /// </summary>
    public bool HostPromptAwaitingStart => _hostPromptsAwaitingStart > 0;

    /// <summary>
    /// Records that a prompt was written to the CLI and has not started its turn yet.
    /// </summary>
    public void RecordHostPromptSent()
    {
        _hostPromptsAwaitingStart++;
    }

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

        if (IsPromptReplay(message))
        {
            if (_hostPromptsAwaitingStart > 0)
            {
                _hostPromptsAwaitingStart--;
            }

            return false;
        }

        if (string.Equals(message.Type, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            RememberBoundedToolCalls(message.Content);
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
                   && _runningBoundedTaskIds.Count == 0
                   && _finishedTurns >= _startedTurns
                   && _hostPromptsAwaitingStart == 0);
    }

    /// <summary>
    /// Reports whether the message is the CLI replaying a prompt the host wrote to stdin.
    /// </summary>
    internal static bool IsPromptReplay(CliMessage message)
    {
        return string.Equals(message.Type, "user", StringComparison.OrdinalIgnoreCase)
               && message.Content.ValueKind == JsonValueKind.Object
               && message.Content.TryGetProperty("isReplay", out var isReplay)
               && isReplay.ValueKind == JsonValueKind.True;
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
                    _runningBoundedTaskIds.Remove(finishedTaskId);
                    _boundedTaskDeadlines.Remove(finishedTaskId);
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
        _runningBoundedTaskIds.Clear();
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
            return;
        }

        // The task list of background_tasks_changed has no tool_use_id and can precede the matching task_started, so a
        // bounded task is remembered by task id, with its deadline, once task_started has tied it to its tool call.
        if (GetString(task, "tool_use_id") is { } toolUseId
            && _boundedToolCalls.TryGetValue(toolUseId, out var declaredTimeout)
            && !_boundedTaskDeadlines.ContainsKey(taskId))
        {
            _boundedTaskDeadlines[taskId] = _timeProvider.GetUtcNow() + declaredTimeout + _backgroundWorkGracePeriod;
        }

        if (_boundedTaskDeadlines.ContainsKey(taskId))
        {
            _runningBoundedTaskIds.Add(taskId);
        }
    }

    private void RememberBoundedToolCalls(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object
            || !content.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var blocks)
            || blocks.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var block in blocks.EnumerateArray())
        {
            if (string.Equals(GetString(block, "type"), "tool_use", StringComparison.Ordinal)
                && GetString(block, "id") is { } toolUseId
                && block.TryGetProperty("input", out var input)
                && TryGetDeclaredTimeout(GetString(block, "name"), input, out var timeout))
            {
                _boundedToolCalls[toolUseId] = timeout;
            }
        }
    }

    private static bool TryGetDeclaredTimeout(string? toolName, JsonElement input, out TimeSpan timeout)
    {
        timeout = default;
        if (input.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        double milliseconds;
        switch (toolName)
        {
            case MonitorToolName:
                // timeout_ms is required, but a missing value means the CLI default; the CLI also caps it.
                milliseconds = GetNumber(input, "timeout_ms") ?? DefaultMonitorTimeoutMilliseconds;
                milliseconds = Math.Min(milliseconds, MaxMonitorTimeoutMilliseconds);
                break;
            case BashToolName when input.TryGetProperty("run_in_background", out var background)
                                   && background.ValueKind == JsonValueKind.True
                                   && GetNumber(input, "timeout") is { } bashTimeout:
                // Without an explicit timeout the background command is unbounded, like a dev server.
                milliseconds = bashTimeout;
                break;
            default:
                return false;
        }

        if (milliseconds <= 0)
        {
            return false;
        }

        timeout = TimeSpan.FromMilliseconds(milliseconds);
        return true;
    }

    private static double? GetNumber(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetDouble(out var value)
            ? value
            : null;
    }

    private static bool IsSubagentTask(JsonElement element)
    {
        return string.Equals(GetString(element, "task_type"), SubagentTaskType, StringComparison.Ordinal);
    }

    internal static bool IsErrorResult(JsonElement content)
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
