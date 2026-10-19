using System.Text.Json;
using System.Text.RegularExpressions;
using HagiCode.Libs.Core.AgentWork;
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
/// Background shell tasks owned by the main agent are awaited, each for a bounded time. A <c>Monitor</c>
/// (<c>timeout_ms</c>) or a <c>Bash</c> command run in the background with an explicit <c>timeout</c> is armed by the agent
/// to be woken by its events or its exit; it normally ends its turn right after, and the CLI ends the task, with a
/// <c>task_notification</c>, when that timeout passes. Closing the stream early kills the task and every later event is
/// lost, so the run waits for it until its declared timeout plus a grace period. Both are reported as a plain
/// <c>local_bash</c> task; they are recognized by the tool call whose <c>tool_use_id</c> the <c>task_started</c> message
/// carries.
/// A <c>Bash</c> command run in the background with no <c>timeout</c> may never finish on its own (a dev server), and the
/// CLI will not end it, but the agent cannot tell the host apart from a CLI that keeps waiting: a build started this way
/// is followed by a turn that ends with "I will continue when it finishes", and a host that closed the stream at that
/// <c>result</c> would kill the CLI and the build's notification with it. The run therefore waits for such a command up to
/// a host-side cap (<c>untimedBackgroundCommandTimeout</c>), counted from when the task started, and no grace period is
/// added because the CLI has no timeout of its own to report. A cap of zero restores the old behaviour of not waiting.
/// A <c>task_notification</c> that is still queued when a turn ends is a turn the CLI has yet to start. A subagent that
/// finishes while the main agent is still writing its final answer is reported mid-turn, but the CLI can only hand the
/// notification to the agent at a tool-result boundary; with none left it queues the notification and starts another
/// turn a few milliseconds after the <c>result</c>. Ending the run at that <c>result</c> kills the CLI before the agent
/// ever sees what the subagent reported. A notification counts as delivered once the main agent gets a tool result after
/// it (the CLI attaches the queue to the next request) or the CLI replays it (<c>origin.kind</c>
/// <c>task-notification</c>); otherwise the next <c>system/init</c> is the turn that handles it. Notifications for
/// tasks a subagent owns go to that subagent, and notifications for tasks this run never saw are the replays of an earlier
/// session's stopped tasks, which the prompt-start logic below already covers.
/// A prompt is recognized as started by the CLI replaying it (<c>isReplay</c>, requested with
/// <c>--replay-user-messages</c>). A turn that is not the prompt's own can end first: a stopped background-task
/// notification from an earlier session starts one on resume, and its <c>result</c> is not the answer to the prompt.
/// The tracker also keeps a record of every task it can show to a user (see <see cref="TakeSnapshot" />). That record is
/// purely observational: it is read from the same messages and never feeds back into the completion decision. Records are
/// created from <c>task_started</c> only, so status events for a task that never started in this run (a resumed session
/// replays stopped notifications of its old tasks) create nothing.
/// </remarks>
internal sealed partial class ClaudeStreamCompletionTracker
{
    private const string SubagentTaskType = "local_agent";
    private const string MonitorToolName = "Monitor";
    private const string BashToolName = "Bash";
    private const double DefaultMonitorTimeoutMilliseconds = 300_000;
    private const double MaxMonitorTimeoutMilliseconds = 1_800_000;
    private const string AgentToolName = "Agent";
    private const string LegacyAgentToolName = "Task";
    private const string ProviderKey = "claude";
    private const string TaskNotificationOrigin = "task-notification";
    private const string EmptyItemsSignature = "[]";
    private const int MaxDetailLength = 200;
    private const int MaxSummaryLength = 4_000;

    private readonly HashSet<string> _runningSubagentTaskIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _subagentOwnedTaskIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LaunchingToolCall> _launchingToolCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _boundedTaskDeadlines = new(StringComparer.Ordinal);
    private readonly HashSet<string> _runningBoundedTaskIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownTaskIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _taskIdsOwnedBySubagents = new(StringComparer.Ordinal);
    private readonly List<string> _undeliveredNotificationTaskIds = [];
    private readonly TimeSpan _backgroundWorkGracePeriod;
    private readonly TimeSpan _untimedBackgroundCommandTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, WorkRecord> _workRecords = new(StringComparer.Ordinal);
    private readonly List<WorkRecord> _workOrder = [];
    private HashSet<string> _lastListedTaskIds = new(StringComparer.Ordinal);
    private string _lastSnapshotSignature = EmptyItemsSignature;
    private long _snapshotSequence;
    private int _startedTurns;
    private int _finishedTurns;
    private int _hostPromptsAwaitingStart;
    private int _pendingQuestions;
    private string? _awaitingPromptText;

    // Steers are registered from the caller's thread and resolved on the receive loop. Every read or write of the fields
    // below happens under _steerGate, and _completed is set in the same critical section as the pending-steer check, so a
    // steer is either refused or tracked until it is resolved.
    private readonly object _steerGate = new();
    private readonly List<PendingSteer> _pendingSteers = [];
    private readonly List<ClaudeSteerResolution> _steerResolutions = [];
    private bool _completed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClaudeStreamCompletionTracker" /> class.
    /// </summary>
    /// <param name="backgroundWorkGracePeriod">
    /// How long past its declared timeout the run keeps waiting for a bounded background task of the main agent.
    /// </param>
    /// <param name="timeProvider">The clock that dates those tasks, or <see langword="null" /> for the system clock.</param>
    /// <param name="untimedBackgroundCommandTimeout">
    /// How long the run waits for a background <c>Bash</c> command of the main agent that declared no timeout, counted
    /// from its start. <see cref="Timeout.InfiniteTimeSpan" /> waits without a limit; zero (the default) does not wait.
    /// </param>
    public ClaudeStreamCompletionTracker(
        TimeSpan backgroundWorkGracePeriod,
        TimeProvider? timeProvider = null,
        TimeSpan untimedBackgroundCommandTimeout = default)
    {
        _backgroundWorkGracePeriod = backgroundWorkGracePeriod;
        _untimedBackgroundCommandTimeout = untimedBackgroundCommandTimeout;
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
        HostPromptStart,

        /// <summary>
        /// A task notification for the main agent was queued by the CLI and its turn has not started. The turn normally
        /// starts within milliseconds; the bound covers a notification the CLI handled without a visible sign.
        /// </summary>
        NotificationTurn,

        /// <summary>
        /// A steer the CLI accepted has not been echoed yet, and nothing else the run waits for is pending. The CLI can hold a
        /// steer written during a text-only turn until a follow-up turn starts, so the run waits for that echo, bounded by the
        /// steer delivery timeout.
        /// </summary>
        SteerDelivery,

        /// <summary>
        /// An interactive question waits for the user's answer. The other bounds are suspended while it does, because the
        /// agent is blocked on a person; the question timeout is the only bound that applies.
        /// </summary>
        UserQuestion
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
            if (Volatile.Read(ref _pendingQuestions) > 0)
            {
                return PendingWait.UserQuestion;
            }

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

            if (_hostPromptsAwaitingStart > 0)
            {
                return PendingWait.HostPromptStart;
            }

            if (_undeliveredNotificationTaskIds.Count > 0)
            {
                return PendingWait.NotificationTurn;
            }

            return HasPendingSteer() ? PendingWait.SteerDelivery : PendingWait.None;
        }
    }

    /// <summary>
    /// Gets how much longer the run waits for the bounded background work of the main agent: the latest declared
    /// timeout plus the grace period, counted from when each task started, or the host-side cap for a command that
    /// declared none. Never negative; <see cref="Timeout.InfiniteTimeSpan" /> when a task has no limit at all.
    /// </summary>
    public TimeSpan RemainingBackgroundWork
    {
        get
        {
            var now = _timeProvider.GetUtcNow();
            var remaining = TimeSpan.Zero;
            foreach (var taskId in _runningBoundedTaskIds)
            {
                if (!_boundedTaskDeadlines.TryGetValue(taskId, out var deadline))
                {
                    continue;
                }

                if (deadline == DateTimeOffset.MaxValue)
                {
                    return Timeout.InfiniteTimeSpan;
                }

                if (deadline - now > remaining)
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
    /// <param name="promptText">
    /// The text of the prompt, when known. A steer echo that has no <c>uuid</c> is attributed to the prompt instead of to a
    /// steer with the same text while the prompt is still awaiting its replay.
    /// </param>
    public void RecordHostPromptSent(string? promptText = null)
    {
        _hostPromptsAwaitingStart++;
        _awaitingPromptText = promptText;
    }

    /// <summary>
    /// Records that an interactive question now waits for the user. Safe to call from any thread: questions are answered
    /// by background tasks while the receive loop reads <see cref="CurrentWait" />.
    /// </summary>
    public void RecordQuestionPending()
    {
        Interlocked.Increment(ref _pendingQuestions);
    }

    /// <summary>
    /// Records that a pending question was resolved: answered, denied, timed out or cancelled. Safe to call from any thread.
    /// </summary>
    public void RecordQuestionResolved()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _pendingQuestions);
            if (current == 0)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _pendingQuestions, current - 1, current) != current);
    }

    /// <summary>
    /// Observes one CLI message and reports whether the run is complete after it.
    /// </summary>
    /// <param name="message">The message that was just received.</param>
    /// <returns><see langword="true" /> when no further messages are expected for this run.</returns>
    public bool Observe(CliMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        LastMessageWasSteerEcho = false;

        if (string.Equals(message.Type, "error", StringComparison.OrdinalIgnoreCase))
        {
            MarkCompleted();
            return true;
        }

        if (string.Equals(message.Type, "system", StringComparison.OrdinalIgnoreCase))
        {
            ObserveSystemMessage(message.Content);
            return false;
        }

        if (IsTaskNotificationReplay(message))
        {
            MarkNotificationsDelivered(message.Content);
            return false;
        }

        // A steer echo is the CLI replaying a follow-up the host wrote, not the prompt. It must not move the prompt's
        // start, or the prompt would be resent or considered started by a message that is not its own.
        if (TryMatchSteerEcho(message, out var steerId))
        {
            LastMessageWasSteerEcho = true;
            ResolveSteer(steerId, ClaudeSteerOutcome.Consumed);
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

        if (string.Equals(message.Type, "user", StringComparison.OrdinalIgnoreCase))
        {
            if (IsMainAgentToolResult(message.Content))
            {
                // The CLI attaches everything queued to the request that follows a tool result, so a notification seen
                // before this message reaches the agent within the running turn.
                _undeliveredNotificationTaskIds.Clear();
            }

            return false;
        }

        if (string.Equals(message.Type, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            RememberLaunchingToolCalls(message.Content);
            return false;
        }

        if (!string.Equals(message.Type, "result", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _finishedTurns++;
        if (IsErrorResult(message.Content))
        {
            MarkCompleted();
            return true;
        }

        // The completion conditions and the pending-steer check are decided together with the write of _completed, so a
        // steer registered after this decision is refused rather than written into a run that is ending.
        lock (_steerGate)
        {
            var complete = _runningSubagentTaskIds.Count == 0
                           && _subagentOwnedTaskIds.Count == 0
                           && _runningBoundedTaskIds.Count == 0
                           && _finishedTurns >= _startedTurns
                           && _hostPromptsAwaitingStart == 0
                           && _undeliveredNotificationTaskIds.Count == 0
                           && _pendingSteers.Count == 0;
            if (complete)
            {
                _completed = true;
            }

            return complete;
        }
    }

    /// <summary>
    /// Gets a value indicating whether the last message passed to <see cref="Observe" /> was the echo of a steer. Such an
    /// echo is part of the steer, not of the conversation, so turn delivery swallows it instead of showing it.
    /// </summary>
    internal bool LastMessageWasSteerEcho { get; private set; }

    /// <summary>
    /// Registers a steer before it is written to the CLI, so the CLI's echo can be matched and a result cannot complete the
    /// run while the steer is outstanding.
    /// </summary>
    /// <returns>
    /// <see langword="false" /> when the run is already complete or closed, or a steer with the same id is outstanding. A
    /// refused steer must not be written.
    /// </returns>
    internal bool TryRegisterSteer(string steerId, string text)
    {
        lock (_steerGate)
        {
            if (_completed || _pendingSteers.Exists(steer => steer.SteerId == steerId))
            {
                return false;
            }

            _pendingSteers.Add(new PendingSteer(steerId, text));
            return true;
        }
    }

    /// <summary>
    /// Forgets a steer that was registered but could not be written. It is not reported, because it was never accepted.
    /// </summary>
    internal void UnregisterSteer(string steerId)
    {
        lock (_steerGate)
        {
            _pendingSteers.RemoveAll(steer => steer.SteerId == steerId);
        }
    }

    /// <summary>
    /// Returns the outcomes of the steers resolved since the last call, in the order they were resolved.
    /// </summary>
    internal IReadOnlyList<ClaudeSteerResolution> TakeSteerResolutions()
    {
        lock (_steerGate)
        {
            return TakeResolutionsLocked();
        }
    }

    /// <summary>
    /// Closes steering for a run that is ending: no further steer is accepted, every steer still unresolved is reported as not
    /// consumed, and the outcomes not yet taken are returned.
    /// </summary>
    internal IReadOnlyList<ClaudeSteerResolution> CloseSteering()
    {
        lock (_steerGate)
        {
            _completed = true;
            foreach (var steer in _pendingSteers)
            {
                _steerResolutions.Add(new ClaudeSteerResolution(steer.SteerId, ClaudeSteerOutcome.NotConsumed));
            }

            _pendingSteers.Clear();
            return TakeResolutionsLocked();
        }
    }

    private void MarkCompleted()
    {
        lock (_steerGate)
        {
            _completed = true;
        }
    }

    private bool HasPendingSteer()
    {
        lock (_steerGate)
        {
            return _pendingSteers.Count > 0;
        }
    }

    private IReadOnlyList<ClaudeSteerResolution> TakeResolutionsLocked()
    {
        var resolutions = _steerResolutions.ToArray();
        _steerResolutions.Clear();
        return resolutions;
    }

    private void ResolveSteer(string steerId, ClaudeSteerOutcome outcome)
    {
        lock (_steerGate)
        {
            if (_pendingSteers.RemoveAll(steer => steer.SteerId == steerId) > 0)
            {
                _steerResolutions.Add(new ClaudeSteerResolution(steerId, outcome));
            }
        }
    }

    /// <summary>
    /// Reports whether the message is the CLI echoing an outstanding steer, and which one. The echo is matched by the
    /// <c>uuid</c> the steer was written with. Only when the echo has no <c>uuid</c> is its text compared with the steers,
    /// and then only if it cannot be the awaited prompt, whose text it may share.
    /// </summary>
    private bool TryMatchSteerEcho(CliMessage message, out string steerId)
    {
        steerId = string.Empty;
        if (!IsReplay(message) || IsTaskNotificationOrigin(message.Content))
        {
            return false;
        }

        lock (_steerGate)
        {
            if (_pendingSteers.Count == 0)
            {
                return false;
            }

            if (GetString(message.Content, "uuid") is { } uuid)
            {
                if (_pendingSteers.Find(steer => steer.SteerId == uuid) is not { } byId)
                {
                    return false;
                }

                steerId = byId.SteerId;
                return true;
            }

            var text = GetUserText(message.Content);
            if (text is null || (_hostPromptsAwaitingStart > 0 && string.Equals(text, _awaitingPromptText, StringComparison.Ordinal)))
            {
                return false;
            }

            if (_pendingSteers.Find(steer => string.Equals(steer.Text, text, StringComparison.Ordinal)) is not { } byText)
            {
                return false;
            }

            steerId = byText.SteerId;
            return true;
        }
    }

    private static string? GetUserText(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object
            || !content.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var body))
        {
            return null;
        }

        return body.ValueKind switch
        {
            JsonValueKind.String => body.GetString(),
            JsonValueKind.Array => string.Concat(body.EnumerateArray().Select(static block => GetString(block, "text"))),
            _ => null
        };
    }

    /// <summary>
    /// Returns the current agent work as a snapshot when it differs from the one returned last, otherwise
    /// <see langword="null" />. Call it after <see cref="Observe" /> to get at most one snapshot per observed message.
    /// </summary>
    /// <remarks>
    /// An empty list is never returned as the first snapshot: a run that waits on nothing has nothing to show.
    /// </remarks>
    public AgentWorkSnapshot? TakeSnapshot()
    {
        var items = BuildWorkItems();
        var signature = JsonSerializer.Serialize(items, AgentWorkSnapshot.JsonOptions);
        if (string.Equals(signature, _lastSnapshotSignature, StringComparison.Ordinal))
        {
            return null;
        }

        _lastSnapshotSignature = signature;
        _snapshotSequence++;
        return new AgentWorkSnapshot(_snapshotSequence, items);
    }

    /// <summary>
    /// Reports whether the message is the CLI replaying a prompt the host wrote to stdin.
    /// </summary>
    internal static bool IsPromptReplay(CliMessage message)
    {
        return IsReplay(message) && !IsTaskNotificationOrigin(message.Content);
    }

    /// <summary>
    /// Reports whether the message is the CLI replaying a task notification it handed to the main agent. It is replayed
    /// like a prompt, but it is not one the host wrote.
    /// </summary>
    internal static bool IsTaskNotificationReplay(CliMessage message)
    {
        return IsReplay(message) && IsTaskNotificationOrigin(message.Content);
    }

    private static bool IsReplay(CliMessage message)
    {
        return string.Equals(message.Type, "user", StringComparison.OrdinalIgnoreCase)
               && message.Content.ValueKind == JsonValueKind.Object
               && message.Content.TryGetProperty("isReplay", out var isReplay)
               && isReplay.ValueKind == JsonValueKind.True;
    }

    private static bool IsTaskNotificationOrigin(JsonElement content)
    {
        return content.ValueKind == JsonValueKind.Object
               && content.TryGetProperty("origin", out var origin)
               && string.Equals(GetString(origin, "kind"), TaskNotificationOrigin, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reports whether a <c>user</c> message carries tool results of the main agent. A subagent's own tool results are
    /// attributed to the tool call that launched it (<c>parent_tool_use_id</c>) and are boundaries of the subagent's turn.
    /// </summary>
    private static bool IsMainAgentToolResult(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object
            || GetString(content, "parent_tool_use_id") is not null
            || !content.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var blocks)
            || blocks.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var block in blocks.EnumerateArray())
        {
            if (string.Equals(GetString(block, "type"), "tool_result", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Forgets the notifications a replayed one names. The replay is the prompt text the agent receives, which carries the
    /// <c>task-id</c> of every notification it contains.
    /// </summary>
    private void MarkNotificationsDelivered(JsonElement content)
    {
        if (_undeliveredNotificationTaskIds.Count == 0
            || !content.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var body))
        {
            return;
        }

        var text = body.ValueKind switch
        {
            JsonValueKind.String => body.GetString(),
            JsonValueKind.Array => string.Concat(body.EnumerateArray().Select(static block => GetString(block, "text"))),
            _ => null
        };
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (Match match in TaskIdPattern().Matches(text))
        {
            _undeliveredNotificationTaskIds.Remove(match.Groups[1].Value);
        }
    }

    [GeneratedRegex("<task-id>([^<]+)</task-id>", RegexOptions.CultureInvariant)]
    private static partial Regex TaskIdPattern();

    private void ObserveSystemMessage(JsonElement content)
    {
        switch (GetString(content, "subtype"))
        {
            case "init":
                _startedTurns++;

                // The turn that starts after a queued notification is the one that handles it.
                if (_undeliveredNotificationTaskIds.Count > 0)
                {
                    _undeliveredNotificationTaskIds.RemoveAt(0);
                }

                break;
            case "background_tasks_changed":
                ReplaceRunningTasks(content);
                break;
            case "task_started":
                if (GetString(content, "task_id") is { } startedTaskId)
                {
                    TrackTask(content, startedTaskId);
                    StartWork(content, startedTaskId);
                }

                break;
            case "task_notification":
                if (GetString(content, "task_id") is { } finishedTaskId)
                {
                    if (_knownTaskIds.Contains(finishedTaskId) && !_taskIdsOwnedBySubagents.Contains(finishedTaskId))
                    {
                        _undeliveredNotificationTaskIds.Add(finishedTaskId);
                    }

                    _runningSubagentTaskIds.Remove(finishedTaskId);
                    _subagentOwnedTaskIds.Remove(finishedTaskId);
                    _runningBoundedTaskIds.Remove(finishedTaskId);
                    _boundedTaskDeadlines.Remove(finishedTaskId);
                    EndWork(content, finishedTaskId);
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
        var listedTaskIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in tasks.EnumerateArray())
        {
            if (GetString(task, "task_id") is { } taskId)
            {
                TrackTask(task, taskId);
                listedTaskIds.Add(taskId);
            }
        }

        ReconcileWorkWithList(listedTaskIds);
    }

    private void TrackTask(JsonElement task, string taskId)
    {
        _knownTaskIds.Add(taskId);
        if (IsSubagentTask(task))
        {
            _runningSubagentTaskIds.Add(taskId);
        }

        if (GetString(task, "parent_task_id") is not null)
        {
            _subagentOwnedTaskIds.Add(taskId);
            _taskIdsOwnedBySubagents.Add(taskId);
            return;
        }

        // The task list of background_tasks_changed has no tool_use_id and can precede the matching task_started, so a
        // bounded task is remembered by task id, with its deadline, once task_started has tied it to its tool call.
        if (GetString(task, "tool_use_id") is { } toolUseId
            && _launchingToolCalls.TryGetValue(toolUseId, out var launchingCall)
            && !_boundedTaskDeadlines.ContainsKey(taskId)
            && TryGetWaitDeadline(launchingCall, out var deadline))
        {
            _boundedTaskDeadlines[taskId] = deadline;
        }

        if (_boundedTaskDeadlines.ContainsKey(taskId))
        {
            _runningBoundedTaskIds.Add(taskId);
        }
    }

    /// <summary>
    /// Works out until when the run waits for a task its tool call launched. A call that declared a timeout is waited for
    /// until that timeout plus the grace period; a background command that declared none, until the host-side cap.
    /// </summary>
    private bool TryGetWaitDeadline(LaunchingToolCall call, out DateTimeOffset deadline)
    {
        var now = _timeProvider.GetUtcNow();
        if (call.DeclaredTimeout is { } declaredTimeout)
        {
            deadline = now + declaredTimeout + _backgroundWorkGracePeriod;
            return true;
        }

        if (call.IsUntimedBackgroundCommand)
        {
            if (_untimedBackgroundCommandTimeout == Timeout.InfiniteTimeSpan)
            {
                deadline = DateTimeOffset.MaxValue;
                return true;
            }

            if (_untimedBackgroundCommandTimeout > TimeSpan.Zero)
            {
                deadline = now + _untimedBackgroundCommandTimeout;
                return true;
            }
        }

        deadline = default;
        return false;
    }

    private void RememberLaunchingToolCalls(JsonElement content)
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
            if (!string.Equals(GetString(block, "type"), "tool_use", StringComparison.Ordinal)
                || GetString(block, "id") is not { } toolUseId
                || !block.TryGetProperty("input", out var input)
                || input.ValueKind != JsonValueKind.Object
                || GetString(block, "name") is not { } toolName)
            {
                continue;
            }

            if (toolName is AgentToolName or LegacyAgentToolName)
            {
                _launchingToolCalls[toolUseId] = new LaunchingToolCall(GetString(input, "description"), null, null);
            }
            else if (TryGetDeclaredTimeout(toolName, input, out var timeout))
            {
                _launchingToolCalls[toolUseId] = new LaunchingToolCall(
                    GetString(input, "description"),
                    GetString(input, "command"),
                    timeout);
            }
            else if (IsBackgroundCommand(toolName, input))
            {
                // A background command with no timeout is awaited only up to the host-side cap, and not at all when the
                // cap is zero; a subagent can also own one, and the task it becomes is shown with this command.
                _launchingToolCalls[toolUseId] = new LaunchingToolCall(
                    GetString(input, "description"),
                    GetString(input, "command"),
                    null,
                    IsUntimedBackgroundCommand: true);
            }
        }
    }

    private static bool IsBackgroundCommand(string toolName, JsonElement input)
    {
        return string.Equals(toolName, BashToolName, StringComparison.Ordinal)
               && input.TryGetProperty("run_in_background", out var background)
               && background.ValueKind == JsonValueKind.True;
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
            case BashToolName when IsBackgroundCommand(BashToolName, input)
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

    private void StartWork(JsonElement started, string taskId)
    {
        var launchingCall = GetString(started, "tool_use_id") is { } toolUseId
                            && _launchingToolCalls.TryGetValue(toolUseId, out var call)
            ? call
            : null;
        var parentTaskId = GetString(started, "parent_task_id");

        string kind;
        if (IsSubagentTask(started))
        {
            kind = AgentWorkKinds.Subagent;
        }
        else if (parentTaskId is not null || (launchingCall is not null && TryGetWaitDeadline(launchingCall, out _)))
        {
            // Work the host waits on: anything a subagent owns, a Monitor or timed background command of the main agent,
            // and a background command with no timeout while the host-side cap is on. With the cap off such a command is
            // not awaited and is not shown.
            kind = AgentWorkKinds.Monitor;
        }
        else
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var isSubagent = kind == AgentWorkKinds.Subagent;
        var title = isSubagent
            ? GetString(started, "description") ?? launchingCall?.Description
            : launchingCall?.Description ?? GetString(started, "description");

        if (_workRecords.TryGetValue(taskId, out var record))
        {
            // The CLI starts the same task id again when a subagent is resumed after the background command it was
            // waiting on finishes. The item reopens instead of becoming a second one.
            record.Status = AgentWorkStatuses.Running;
            record.EndedAt = null;
            record.Summary = null;
            record.Usage = null;
            record.ListedAsRunning = _lastListedTaskIds.Contains(taskId);
            record.Title = title ?? record.Title;
            return;
        }

        record = new WorkRecord(taskId, kind, now)
        {
            Title = title ?? string.Empty,
            Detail = isSubagent ? null : Truncate(launchingCall?.Command, MaxDetailLength),
            ParentTaskId = parentTaskId,
            ToolUseId = GetString(started, "tool_use_id"),
            DeadlineAt = !isSubagent && launchingCall?.DeclaredTimeout is { } timeout ? now + timeout : null,
            ListedAsRunning = _lastListedTaskIds.Contains(taskId)
        };
        _workRecords[taskId] = record;
        _workOrder.Add(record);
    }

    private void EndWork(JsonElement notification, string taskId)
    {
        // A notification for a task with no record is ignored: a resumed session replays stopped notifications for the
        // tasks of its earlier run, and those must not appear as items.
        if (!_workRecords.TryGetValue(taskId, out var record) || !AgentWorkStatuses.IsActive(record.Status))
        {
            return;
        }

        var providerStatus = GetString(notification, "status");
        record.Status = MapTerminalStatus(providerStatus);
        record.EndedAt = _timeProvider.GetUtcNow();
        var summary = GetString(notification, "summary");
        if (summary is null && record.Status == AgentWorkStatuses.Stopped
            && !string.Equals(providerStatus, "stopped", StringComparison.OrdinalIgnoreCase))
        {
            // An unrecognized status is reported as stopped; keep the provider's own word so nothing is lost.
            summary = providerStatus;
        }

        record.Summary = Truncate(summary, MaxSummaryLength);
        record.Usage = ReadUsage(notification);
        record.ListedAsRunning = false;
    }

    private void ReconcileWorkWithList(HashSet<string> listedTaskIds)
    {
        _lastListedTaskIds = listedTaskIds;
        foreach (var record in _workOrder)
        {
            if (!AgentWorkStatuses.IsActive(record.Status))
            {
                continue;
            }

            if (listedTaskIds.Contains(record.TaskId))
            {
                record.ListedAsRunning = true;
            }
            else if (record.ListedAsRunning)
            {
                // The list is authoritative: a task that was in it and is now gone ended without a notification. A task
                // that was never listed is left alone because the list can precede its task_started.
                record.Status = AgentWorkStatuses.Stopped;
                record.EndedAt = _timeProvider.GetUtcNow();
                record.ListedAsRunning = false;
            }
        }
    }

    private List<AgentWorkItem> BuildWorkItems()
    {
        var items = new List<AgentWorkItem>(_workOrder.Count);
        foreach (var record in _workOrder)
        {
            var blocksCompletion = AgentWorkStatuses.IsActive(record.Status)
                                   && (_runningSubagentTaskIds.Contains(record.TaskId)
                                       || _subagentOwnedTaskIds.Contains(record.TaskId)
                                       || _runningBoundedTaskIds.Contains(record.TaskId));
            items.Add(new AgentWorkItem(
                PrefixId(record.TaskId),
                record.Kind,
                record.ParentTaskId is null ? null : PrefixId(record.ParentTaskId),
                record.Status,
                record.Title,
                record.Detail,
                record.StartedAt,
                record.EndedAt,
                record.DeadlineAt,
                record.Summary,
                record.Usage,
                record.ToolUseId,
                blocksCompletion));
        }

        return items;
    }

    private static string PrefixId(string taskId) => $"{ProviderKey}:{taskId}";

    private static string MapTerminalStatus(string? providerStatus)
    {
        // Showing a wrong "completed" is worse than a conservative "stopped", so only known words map to the other two.
        return providerStatus?.ToLowerInvariant() switch
        {
            "completed" => AgentWorkStatuses.Completed,
            "failed" or "error" or "errored" => AgentWorkStatuses.Failed,
            _ => AgentWorkStatuses.Stopped
        };
    }

    private static AgentWorkUsage? ReadUsage(JsonElement notification)
    {
        if (notification.ValueKind != JsonValueKind.Object
            || !notification.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var totalTokens = GetNumber(usage, "total_tokens");
        var toolUses = GetNumber(usage, "tool_uses");
        var durationMs = GetNumber(usage, "duration_ms");
        return totalTokens is null && toolUses is null && durationMs is null
            ? null
            : new AgentWorkUsage(
                totalTokens is { } tokens ? (long)tokens : null,
                toolUses is { } uses ? (int)uses : null,
                durationMs is { } duration ? (long)duration : null);
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength)
        {
            return value;
        }

        return string.Concat(value.AsSpan(0, maxLength - 1), "\u2026");
    }

    /// <summary>The input of the tool call that launched a task, remembered by <c>tool_use_id</c>.</summary>
    /// <param name="Description">The call's description input.</param>
    /// <param name="Command">The call's command input, for a Monitor or background command.</param>
    /// <param name="DeclaredTimeout">The timeout the call declared, after the CLI default and cap; <see langword="null" /> when unbounded.</param>
    /// <param name="IsUntimedBackgroundCommand">Whether the call is a <c>Bash</c> command run in the background with no timeout.</param>
    private sealed record LaunchingToolCall(
        string? Description,
        string? Command,
        TimeSpan? DeclaredTimeout,
        bool IsUntimedBackgroundCommand = false);

    /// <summary>A steer the host wrote and the CLI has not echoed or closed yet.</summary>
    /// <param name="SteerId">The <c>uuid</c> the steer was written with.</param>
    /// <param name="Text">The text the steer carries, used to match an echo that has no <c>uuid</c>.</param>
    private sealed record PendingSteer(string SteerId, string Text);

    private sealed class WorkRecord(string taskId, string kind, DateTimeOffset startedAt)
    {
        public string TaskId { get; } = taskId;

        public string Kind { get; } = kind;

        public DateTimeOffset StartedAt { get; } = startedAt;

        public string Title { get; set; } = string.Empty;

        public string? Detail { get; init; }

        public string? ParentTaskId { get; init; }

        public string? ToolUseId { get; init; }

        public DateTimeOffset? DeadlineAt { get; init; }

        public string Status { get; set; } = AgentWorkStatuses.Running;

        public DateTimeOffset? EndedAt { get; set; }

        public string? Summary { get; set; }

        public AgentWorkUsage? Usage { get; set; }

        public bool ListedAsRunning { get; set; }
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
