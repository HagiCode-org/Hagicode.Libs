using System.Reflection;
using System.Text.Json;
using HagiCode.Libs.Core.AgentWork;
using HagiCode.Libs.Providers.Codex.AppServer;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Checks the Codex agent work tracker against the scenarios of the codex-agent-work-tracking spec, using raw notifications in the
/// shape the app-server and the exec stream send.
/// </summary>
public sealed class CodexAgentWorkTrackerTests
{
    private const string MainThread = "main-thread";

    [Test]
    public void Activity_started_creates_a_running_subagent_without_a_parent_or_blocking()
    {
        var tracker = new CodexAgentWorkTracker();

        Observe(tracker, "item/started", Activity(MainThread, "T1", "started", "/root/a"));

        var snapshot = tracker.TakeSnapshot().ShouldNotBeNull();
        var item = snapshot.Items.Single();
        item.Id.ShouldBe("codex:T1");
        item.Kind.ShouldBe(AgentWorkKinds.Subagent);
        item.Status.ShouldBe(AgentWorkStatuses.Running);
        item.ParentId.ShouldBeNull();
        item.BlocksCompletion.ShouldBeFalse();
    }

    [Test]
    public void Spawn_creates_one_item_per_receiver_titled_by_the_prompt_and_without_a_main_thread_parent()
    {
        var tracker = new CodexAgentWorkTracker();

        Observe(tracker, "item/started", Spawn(MainThread, "spawn-1", "Review the migration", "T1", "T2", "T3"));

        var items = tracker.TakeSnapshot().ShouldNotBeNull().Items;
        items.Select(item => item.Id).ShouldBe(["codex:T1", "codex:T2", "codex:T3"]);
        items.Select(item => item.Title).Distinct().ShouldBe(["Review the migration"]);
        items.All(item => item.ParentId is null).ShouldBeTrue();
        items.All(item => item.ToolUseId == "spawn-1").ShouldBeTrue();
    }

    [Test]
    public void Long_prompt_is_truncated_to_the_title_limit_with_an_ellipsis()
    {
        var tracker = new CodexAgentWorkTracker();
        var prompt = new string('p', 250);

        Observe(tracker, "item/started", Spawn(MainThread, "spawn-1", prompt, "T1"));

        var title = tracker.TakeSnapshot().ShouldNotBeNull().Items.Single().Title;
        title.Length.ShouldBe(CodexAgentWorkTracker.MaxTitleLength);
        title.ShouldEndWith("…");
    }

    [Test]
    public void Notifications_about_an_unknown_thread_create_no_item()
    {
        var tracker = new CodexAgentWorkTracker();

        Observe(tracker, "turn/completed", ChildTurnCompleted("T9", "completed"));
        Observe(tracker, "thread/status/changed", ThreadStatus("T9", "systemError"));
        Observe(tracker, "item/completed", CollabWithStates(MainThread, "wait", new Dictionary<string, string> { ["T9"] = "completed" }));

        tracker.TakeSnapshot().ShouldBeNull();
    }

    [Test]
    public void A_subagent_spawned_by_a_subagent_is_listed_under_the_top_level_ancestor()
    {
        var tracker = new CodexAgentWorkTracker();

        Observe(tracker, "item/started", Spawn(MainThread, "spawn-1", "Outer", "T1"));
        Observe(tracker, "item/started", Spawn("T1", "spawn-2", "Middle", "T2"));
        Observe(tracker, "item/started", Spawn("T2", "spawn-3", "Inner", "T3"));

        var items = tracker.TakeSnapshot().ShouldNotBeNull().Items.ToDictionary(item => item.Id);
        items["codex:T1"].ParentId.ShouldBeNull();
        items["codex:T2"].ParentId.ShouldBe("codex:T1");
        items["codex:T3"].ParentId.ShouldBe("codex:T1");
    }

    [Test]
    public void Provider_states_map_conservatively_and_unknown_words_become_stopped()
    {
        var tracker = new CodexAgentWorkTracker();
        Observe(tracker, "item/started", Spawn(MainThread, "spawn-1", "Work", "T1", "T2", "T3", "T4", "T5", "T6"));

        Observe(tracker, "item/completed", CollabWithStates(MainThread, "wait", new Dictionary<string, string>
        {
            ["T1"] = "pendingInit",
            ["T2"] = "running",
            ["T3"] = "completed",
            ["T4"] = "errored",
            ["T5"] = "shutdown",
            ["T6"] = "somethingNew"
        }, message: "Found 2 issues"));

        var items = tracker.TakeSnapshot().ShouldNotBeNull().Items.ToDictionary(item => item.Id);
        items["codex:T1"].Status.ShouldBe(AgentWorkStatuses.Starting);
        items["codex:T2"].Status.ShouldBe(AgentWorkStatuses.Running);
        items["codex:T3"].Status.ShouldBe(AgentWorkStatuses.Completed);
        items["codex:T3"].Summary.ShouldBe("Found 2 issues");
        items["codex:T3"].EndedAt.ShouldNotBeNull();
        items["codex:T4"].Status.ShouldBe(AgentWorkStatuses.Failed);
        items["codex:T5"].Status.ShouldBe(AgentWorkStatuses.Stopped);
        items["codex:T6"].Status.ShouldBe(AgentWorkStatuses.Stopped);
    }

    [Test]
    public void Exec_snake_case_states_are_read_the_same_way()
    {
        var tracker = new CodexAgentWorkTracker();
        tracker.ObserveExecLine(ExecCollab("item.started", "spawn_agent", "in_progress", receivers: ["T1"], states: """{"T1":{"status":"pending_init"}}"""))
            .ShouldNotBeNull().Items.Single().Status.ShouldBe(AgentWorkStatuses.Starting);

        var snapshot = tracker.ObserveExecLine(ExecCollab("item.completed", "wait", "completed", receivers: [], states: """{"T1":{"status":"not_found"}}"""));

        snapshot.ShouldNotBeNull().Items.Single().Status.ShouldBe(AgentWorkStatuses.Stopped);
    }

    [Test]
    public void A_close_after_completion_keeps_the_completed_result()
    {
        var tracker = new CodexAgentWorkTracker();
        Observe(tracker, "item/started", Activity(MainThread, "T1", "started"));
        Observe(tracker, "item/completed", Activity(MainThread, "T1", "completed"));
        tracker.TakeSnapshot();

        Observe(tracker, "item/completed", CollabWithStates(MainThread, "close_agent", new Dictionary<string, string> { ["T1"] = "shutdown" }));

        tracker.TakeSnapshot().ShouldBeNull();
        var item = Snapshot(tracker).Single();
        item.Status.ShouldBe(AgentWorkStatuses.Completed);
    }

    [Test]
    public void A_close_of_a_running_subagent_becomes_stopped()
    {
        var tracker = new CodexAgentWorkTracker();
        Observe(tracker, "item/started", Activity(MainThread, "T1", "started"));

        Observe(tracker, "item/started", Activity(MainThread, "T1", "interrupted"));

        Snapshot(tracker).Single().Status.ShouldBe(AgentWorkStatuses.Stopped);
    }

    [Test]
    public void A_resumed_subagent_reopens_the_same_item()
    {
        var tracker = new CodexAgentWorkTracker();
        Observe(tracker, "item/started", Activity(MainThread, "T1", "started"));
        Observe(tracker, "turn/completed", ChildTurnCompleted("T1", "completed", "Done"));
        Observe(tracker, "item/completed", Activity(MainThread, "T1", "completed"));
        Snapshot(tracker).Single().Summary.ShouldBe("Done");

        Observe(tracker, "turn/started", ChildTurnStarted("T1"));

        var items = Snapshot(tracker);
        items.Count.ShouldBe(1);
        items[0].Id.ShouldBe("codex:T1");
        items[0].Status.ShouldBe(AgentWorkStatuses.Running);
        items[0].EndedAt.ShouldBeNull();
        items[0].Summary.ShouldBeNull();
    }

    [Test]
    public void Interacted_activity_keeps_a_completed_status()
    {
        var tracker = new CodexAgentWorkTracker();
        Observe(tracker, "item/started", Activity(MainThread, "T1", "started"));
        Observe(tracker, "item/completed", Activity(MainThread, "T1", "completed"));

        Observe(tracker, "item/started", Activity(MainThread, "T1", "interacted"));

        Snapshot(tracker).Single().Status.ShouldBe(AgentWorkStatuses.Completed);
    }

    [Test]
    public void A_failed_child_turn_marks_the_subagent_failed()
    {
        var tracker = new CodexAgentWorkTracker();
        Observe(tracker, "item/started", Activity(MainThread, "T1", "started"));

        Observe(tracker, "turn/completed", ChildTurnCompleted("T1", "failed"));

        Snapshot(tracker).Single().Status.ShouldBe(AgentWorkStatuses.Failed);
    }

    [Test]
    public void The_final_message_of_a_completed_child_turn_is_the_summary_and_is_truncated()
    {
        var tracker = new CodexAgentWorkTracker();
        Observe(tracker, "item/started", Activity(MainThread, "T1", "started"));

        Observe(tracker, "turn/completed", ChildTurnCompleted("T1", "completed", new string('s', 5000)));

        var summary = Snapshot(tracker).Single().Summary.ShouldNotBeNull();
        summary.Length.ShouldBe(CodexAgentWorkTracker.MaxSummaryLength);
    }

    [Test]
    public void Usage_is_summed_from_the_child_thread_only()
    {
        var tracker = new CodexAgentWorkTracker();
        Observe(tracker, "item/started", Activity(MainThread, "T1", "started"));
        Observe(tracker, "thread/tokenUsage/updated", TokenUsage("T1", 1000));
        Observe(tracker, "thread/tokenUsage/updated", TokenUsage("T1", 200));
        Observe(tracker, "item/completed", ToolItem("T1", "commandExecution"));
        Observe(tracker, "item/completed", ToolItem("T1", "fileChange"));
        Observe(tracker, "item/completed", ToolItem("T1", "mcpToolCall"));
        Observe(tracker, "item/started", ToolItem("T1", "commandExecution"));
        Observe(tracker, "item/completed", ToolItem(MainThread, "commandExecution"));
        Observe(tracker, "thread/tokenUsage/updated", TokenUsage(MainThread, 5000));

        var usage = Snapshot(tracker).Single().Usage.ShouldNotBeNull();
        usage.TotalTokens.ShouldBe(1200);
        usage.ToolUses.ShouldBe(3);
    }

    [Test]
    public void A_subagent_with_no_reported_usage_has_no_usage_and_no_zero_fields()
    {
        var tracker = new CodexAgentWorkTracker();
        Observe(tracker, "item/started", Activity(MainThread, "T1", "started"));
        Observe(tracker, "item/completed", Activity(MainThread, "T1", "completed"));

        Snapshot(tracker).Single().Usage.ShouldBeNull();
    }

    [Test]
    public void Snapshots_are_delivered_only_when_the_content_changes_and_an_empty_first_list_is_suppressed()
    {
        var tracker = new CodexAgentWorkTracker();
        tracker.TakeSnapshot().ShouldBeNull();

        Observe(tracker, "item/started", Activity(MainThread, "T1", "started"));
        var first = tracker.TakeSnapshot().ShouldNotBeNull();
        first.Sequence.ShouldBe(1);

        tracker.TakeSnapshot().ShouldBeNull();
        Observe(tracker, "item/started", Activity(MainThread, "T1", "interacted"));
        tracker.TakeSnapshot().ShouldBeNull();

        Observe(tracker, "item/completed", Activity(MainThread, "T1", "completed"));
        tracker.TakeSnapshot().ShouldNotBeNull().Sequence.ShouldBe(2);
    }

    [Test]
    public void A_malformed_collaboration_notification_changes_nothing_and_keeps_the_previous_list()
    {
        var tracker = new CodexAgentWorkTracker();
        Observe(tracker, "item/started", Activity(MainThread, "T1", "started"));
        tracker.TakeSnapshot();

        Observe(tracker, "item/completed", Json("""{"item":{"type":"collabAgentToolCall","tool":"wait","agentsStates":"broken"}}"""));
        Observe(tracker, "item/completed", Json("""{"item":{"type":"subAgentActivity","agentThreadId":7,"kind":["completed"]}}"""));
        tracker.Observe("item/completed", default);

        tracker.TakeSnapshot().ShouldBeNull();
        Snapshot(tracker).Single().Status.ShouldBe(AgentWorkStatuses.Running);
    }

    [Test]
    public void Exec_spawn_then_completed_yields_running_then_completed_with_no_usage()
    {
        var tracker = new CodexAgentWorkTracker();

        var running = tracker.ObserveExecLine(ExecCollab(
            "item.started", "spawn_agent", "in_progress", receivers: ["T1"], states: """{"T1":{"status":"running"}}""", prompt: "Find issues"));
        var completed = tracker.ObserveExecLine(ExecCollab(
            "item.completed", "wait", "completed", receivers: [], states: """{"T1":{"status":"completed","message":"Found 2 issues"}}"""));

        running.ShouldNotBeNull().Items.Single().Status.ShouldBe(AgentWorkStatuses.Running);
        var item = completed.ShouldNotBeNull().Items.Single();
        item.Status.ShouldBe(AgentWorkStatuses.Completed);
        item.Summary.ShouldBe("Found 2 issues");
        item.Title.ShouldBe("Find issues");
        item.Usage.ShouldBeNull();
        completed.Sequence.ShouldBe(2);
    }

    [Test]
    public void Exec_collaboration_values_the_vendored_models_lack_are_read_without_failing()
    {
        var tracker = new CodexAgentWorkTracker();
        tracker.ObserveExecLine(ExecCollab("item.started", "spawn_agent", "in_progress", receivers: ["T1"], states: """{"T1":{"status":"running"}}"""));

        var snapshot = tracker.ObserveExecLine(ExecCollab(
            "item.completed", "interrupt_agent", "completed", receivers: ["T1"], states: """{"T1":{"status":"interrupted"}}"""));

        snapshot.ShouldNotBeNull().Items.Single().Status.ShouldBe(AgentWorkStatuses.Stopped);
    }

    [Test]
    public void Exec_lines_that_are_not_collaboration_items_return_nothing()
    {
        var tracker = new CodexAgentWorkTracker();

        tracker.ObserveExecLine("""{"type":"turn.completed","usage":{"input_tokens":1}}""").ShouldBeNull();
        tracker.ObserveExecLine("""{"type":"item.completed","item":{"id":"m1","type":"agent_message","text":"ok"}}""").ShouldBeNull();
    }

    [Test]
    public void The_snapshot_event_round_trips_its_snapshot_and_carries_no_raw_json()
    {
        var tracker = new CodexAgentWorkTracker();
        Observe(tracker, "item/started", Activity(MainThread, "T1", "started"));
        var snapshot = tracker.TakeSnapshot().ShouldNotBeNull();

        var threadEvent = new CodexAgentWorkSnapshotEvent(snapshot);

        threadEvent.Type.ShouldBe(CodexAgentWorkSnapshotEvent.EventType);
        threadEvent.Type.ShouldBe("agent_work_snapshot");
        threadEvent.Snapshot.ShouldBeSameAs(snapshot);
        typeof(CodexAgentWorkSnapshotEvent)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(property => property.PropertyType == typeof(JsonElement) || property.PropertyType == typeof(JsonDocument))
            .ShouldBeFalse();
    }

    private static IReadOnlyList<AgentWorkItem> Snapshot(CodexAgentWorkTracker tracker) => tracker.CurrentItems;

    private static void Observe(CodexAgentWorkTracker tracker, string method, JsonElement parameters)
    {
        tracker.Observe(method, parameters);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>
    /// Fills the <c>@name@</c> placeholders of a JSON template. Templates are plain raw strings, so their braces need no escaping.
    /// </summary>
    private static JsonElement Json(string template, params (string Name, string Value)[] values)
    {
        var json = template;
        foreach (var (name, value) in values)
        {
            json = json.Replace($"@{name}@", value);
        }

        return Json(json);
    }

    private static string Quoted(string value) => JsonSerializer.Serialize(value);

    private static JsonElement Activity(string reportingThread, string agentThread, string kind, string? agentPath = null)
    {
        return Json(
            """{"threadId":"@thread@","item":{"type":"subAgentActivity","id":"a-@agent@-@kind@","kind":"@kind@","agentThreadId":"@agent@","agentPath":@path@}}""",
            ("thread", reportingThread),
            ("agent", agentThread),
            ("kind", kind),
            ("path", agentPath is null ? "null" : Quoted(agentPath)));
    }

    private static JsonElement Spawn(string senderThread, string callId, string prompt, params string[] receivers)
    {
        var receiverJson = string.Join(",", receivers.Select(Quoted));
        return Json(
            """{"threadId":"@sender@","item":{"type":"collabAgentToolCall","id":"@call@","tool":"spawnAgent","status":"inProgress","senderThreadId":"@sender@","receiverThreadIds":[@receivers@],"prompt":@prompt@,"agentsStates":{}}}""",
            ("sender", senderThread),
            ("call", callId),
            ("receivers", receiverJson),
            ("prompt", Quoted(prompt)));
    }

    private static JsonElement CollabWithStates(string senderThread, string tool, IReadOnlyDictionary<string, string> states, string? message = null)
    {
        var entries = string.Join(",", states.Select(state =>
        {
            var messageJson = message is null ? string.Empty : $",\"message\":{Quoted(message)}";
            return $"{Quoted(state.Key)}:{{\"status\":{Quoted(state.Value)}{messageJson}}}";
        }));
        return Json(
            """{"threadId":"@sender@","item":{"type":"collabAgentToolCall","id":"c-@tool@-@count@","tool":"@tool@","status":"completed","senderThreadId":"@sender@","receiverThreadIds":[],"agentsStates":{@entries@}}}""",
            ("sender", senderThread),
            ("tool", tool),
            ("count", states.Count.ToString()),
            ("entries", entries));
    }

    private static JsonElement ChildTurnCompleted(string threadId, string status, string? finalMessage = null)
    {
        var items = finalMessage is null
            ? string.Empty
            : $"{{\"type\":\"agentMessage\",\"id\":\"m-{threadId}\",\"text\":{Quoted(finalMessage)}}}";
        return Json(
            """{"threadId":"@thread@","turn":{"id":"turn-@thread@","status":"@status@","items":[@items@],"error":null}}""",
            ("thread", threadId),
            ("status", status),
            ("items", items));
    }

    private static JsonElement ChildTurnStarted(string threadId)
    {
        return Json(
            """{"threadId":"@thread@","turn":{"id":"turn-@thread@-2","status":"inProgress","items":[]}}""",
            ("thread", threadId));
    }

    private static JsonElement ThreadStatus(string threadId, string type)
    {
        return Json(
            """{"threadId":"@thread@","status":{"type":"@type@"}}""",
            ("thread", threadId),
            ("type", type));
    }

    private static JsonElement TokenUsage(string threadId, long totalTokens)
    {
        return Json(
            """{"threadId":"@thread@","turnId":"turn-@thread@","tokenUsage":{"last":{"totalTokens":@tokens@,"inputTokens":0,"cachedInputTokens":0,"outputTokens":0,"reasoningOutputTokens":0},"total":{"totalTokens":@tokens@,"inputTokens":0,"cachedInputTokens":0,"outputTokens":0,"reasoningOutputTokens":0}}}""",
            ("thread", threadId),
            ("tokens", totalTokens.ToString()));
    }

    private static JsonElement ToolItem(string threadId, string type)
    {
        return Json(
            """{"threadId":"@thread@","item":{"type":"@type@","id":"t-@thread@-@type@","status":"completed"}}""",
            ("thread", threadId),
            ("type", type));
    }

    private static string ExecCollab(string eventType, string tool, string status, string[] receivers, string states, string? prompt = null)
    {
        var receiverJson = string.Join(",", receivers.Select(receiver => $"\"{receiver}\""));
        var promptJson = prompt is null ? "null" : JsonSerializer.Serialize(prompt);
        var item = "{\"id\":\"c-" + tool + "\",\"type\":\"collab_tool_call\",\"tool\":\"" + tool + "\",\"status\":\"" + status
                   + "\",\"sender_thread_id\":\"main-thread\",\"receiver_thread_ids\":[" + receiverJson + "],\"prompt\":" + promptJson
                   + ",\"agents_states\":" + states + "}";
        return "{\"type\":\"" + eventType + "\",\"item\":" + item + "}";
    }
}
