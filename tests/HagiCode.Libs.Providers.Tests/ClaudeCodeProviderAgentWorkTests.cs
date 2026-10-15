using System.Text.Json;
using Shouldly;
using HagiCode.Libs.Core.AgentWork;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.ClaudeCode;

namespace HagiCode.Libs.Providers.Tests;

// Agent work snapshots: the synthetic messages the Claude provider yields for what a run waits on. The captured streams
// in ClaudeAgentWorkCapture are real CLI output; the synthetic ones cover shapes that are hard to provoke on demand.
public sealed partial class ClaudeCodeProviderTests
{
    [Test]
    public async Task ExecuteAsync_snapshots_a_subagent_from_running_to_completed_with_its_summary()
    {
        var messages = await CollectAsync(CreateProvider(messageBatches: [ClaudeAgentWorkCapture.ForegroundRequestedSubagent]));

        var snapshots = Snapshots(messages);
        snapshots.Count.ShouldBe(2);

        var running = snapshots[0].Items.ShouldHaveSingleItem();
        running.Id.ShouldBe("claude:ac8077dcfa89e58b2");
        running.Kind.ShouldBe(AgentWorkKinds.Subagent);
        running.Status.ShouldBe(AgentWorkStatuses.Running);
        running.Title.ShouldBe("Say hello");
        running.ToolUseId.ShouldBe("toolu_01UvNk4BGk53ScNoeMie9tZ3");
        running.ParentId.ShouldBeNull();
        running.BlocksCompletion.ShouldBeTrue();
        running.EndedAt.ShouldBeNull();
        running.Detail.ShouldBeNull();
        running.DeadlineAt.ShouldBeNull();

        var completed = snapshots[1].Items.ShouldHaveSingleItem();
        completed.Id.ShouldBe(running.Id);
        completed.Status.ShouldBe(AgentWorkStatuses.Completed);
        completed.Summary.ShouldBe("HELLO");
        completed.EndedAt.ShouldNotBeNull();
        completed.StartedAt.ShouldBe(running.StartedAt);
        completed.BlocksCompletion.ShouldBeFalse();
        completed.Usage.ShouldBe(new AgentWorkUsage(TotalTokens: 12473, ToolUses: 0, DurationMs: 1091));
        snapshots[1].Sequence.ShouldBeGreaterThan(snapshots[0].Sequence);
    }

    [Test]
    public async Task ExecuteAsync_snapshots_a_monitor_with_its_description_command_and_deadline()
    {
        var messages = await CollectAsync(CreateProvider(messageBatches: [ClaudeAgentWorkCapture.MonitorToolCall]));

        var monitor = Snapshots(messages)[0].Items.ShouldHaveSingleItem();
        monitor.Kind.ShouldBe(AgentWorkKinds.Monitor);
        monitor.Status.ShouldBe(AgentWorkStatuses.Running);
        monitor.Title.ShouldBe("wait test");
        monitor.Detail.ShouldBe("sleep 20; echo EVENT-ONE; sleep 10; echo EVENT-TWO");
        monitor.DeadlineAt.ShouldBe(monitor.StartedAt + TimeSpan.FromMilliseconds(120_000));
        monitor.BlocksCompletion.ShouldBeTrue();
        monitor.ToolUseId.ShouldBe("toolu_01PkELACvXbijRydYU75Gx1y");

        var last = Snapshots(messages)[^1].Items.ShouldHaveSingleItem();
        last.Status.ShouldBe(AgentWorkStatuses.Completed);
        last.Summary.ShouldBe("Monitor \"wait test\" stream ended");
        last.BlocksCompletion.ShouldBeFalse();
    }

    [Test]
    public async Task ExecuteAsync_truncates_a_long_monitor_command_and_applies_the_cli_default_timeout()
    {
        var longCommand = new string('x', 500);
        var provider = CreateProvider(messageBatches:
        [
            [
                StreamMessage("system", new { subtype = "init" }),
                ToolUseMessage("Monitor", "toolu-monitor", new { description = "long", command = longCommand }),
                StreamMessage(
                    "system",
                    new { subtype = "task_started", task_id = "monitor-1", task_type = "local_bash", tool_use_id = "toolu-monitor" }),
                StreamMessage("result", new { is_error = false }),
                StreamMessage("system", new { subtype = "task_notification", task_id = "monitor-1", status = "completed" }),
                StreamMessage("system", new { subtype = "init" }),
                StreamMessage("result", new { is_error = false })
            ]
        ]);

        var monitor = Snapshots(await CollectAsync(provider))[0].Items.ShouldHaveSingleItem();

        monitor.Detail!.Length.ShouldBe(200);
        monitor.Detail.ShouldEndWith("…");
        monitor.DeadlineAt.ShouldBe(monitor.StartedAt + TimeSpan.FromMinutes(5));
    }

    [Test]
    public async Task ExecuteAsync_lists_a_timed_background_bash_as_a_monitor_and_keeps_the_provider_stop_reason()
    {
        var messages = await CollectAsync(CreateProvider(messageBatches: [ClaudeAgentWorkCapture.TimedBackgroundBashStopped]));

        var snapshots = Snapshots(messages);
        var running = snapshots[0].Items.ShouldHaveSingleItem();
        running.Kind.ShouldBe(AgentWorkKinds.Monitor);
        running.Title.ShouldBe("Sleep 40 seconds then echo marker in background");
        running.Detail.ShouldBe("sleep 40; echo DONE-BG");
        running.DeadlineAt.ShouldBe(running.StartedAt + TimeSpan.FromMilliseconds(8_000));

        var stopped = snapshots[^1].Items.ShouldHaveSingleItem();
        stopped.Status.ShouldBe(AgentWorkStatuses.Stopped);
        stopped.Summary.ShouldBe(
            "Background command \"Sleep 40 seconds then echo marker in background\" was stopped after reaching its background time limit");
        stopped.BlocksCompletion.ShouldBeFalse();
    }

    [Test]
    public async Task ExecuteAsync_lists_a_background_bash_without_a_timeout_that_the_host_waits_for()
    {
        var messages = await CollectAsync(CreateProvider(messageBatches: [ClaudeAgentWorkCapture.UnboundedBackgroundBash]));

        var command = Snapshots(messages)[0].Items.ShouldHaveSingleItem();
        command.Kind.ShouldBe(AgentWorkKinds.Monitor);
        command.Status.ShouldBe(AgentWorkStatuses.Running);
        command.Title.ShouldBe("Sleep 300 seconds then echo DONE-BG");
        command.Detail.ShouldBe("sleep 300; echo DONE-BG");
        // The command declared no timeout and the CLI will not end it, so there is no deadline to show.
        command.DeadlineAt.ShouldBeNull();
        command.BlocksCompletion.ShouldBeTrue();
    }

    [Test]
    public async Task ExecuteAsync_does_not_list_a_background_bash_without_a_timeout_when_the_cap_is_zero()
    {
        var messages = await CollectAsync(
            CreateProvider(messageBatches: [ClaudeAgentWorkCapture.UnboundedBackgroundBash]),
            new ClaudeCodeOptions
            {
                SessionId = "session-1",
                WorkingDirectory = "/tmp/project",
                UntimedBackgroundCommandTimeout = TimeSpan.Zero
            });

        Snapshots(messages).ShouldBeEmpty();
    }

    [Test]
    public async Task ExecuteAsync_reports_a_failed_background_command_as_failed()
    {
        var messages = await CollectAsync(CreateProvider(messageBatches: [ClaudeAgentWorkCapture.FailedBackgroundBash]));

        Snapshots(messages)[^1].Items.ShouldHaveSingleItem().Status.ShouldBe(AgentWorkStatuses.Failed);
    }

    [Test]
    public async Task ExecuteAsync_nests_work_owned_by_a_subagent_under_it_and_reopens_a_resumed_subagent()
    {
        var messages = await CollectAsync(CreateProvider(messageBatches: [ClaudeAgentWorkCapture.BackgroundSubagentWithOwnedBash]));

        var snapshots = Snapshots(messages);
        var withChild = snapshots.First(static snapshot => snapshot.Items.Count == 2);
        var agent = withChild.Items[0];
        var child = withChild.Items[1];
        agent.Id.ShouldBe("claude:a94d8c6601245aedc");
        agent.Kind.ShouldBe(AgentWorkKinds.Subagent);
        child.Id.ShouldBe("claude:bmn7ig1yk");
        child.Kind.ShouldBe(AgentWorkKinds.Monitor);
        child.ParentId.ShouldBe(agent.Id);
        child.Title.ShouldBe("Sleep 12 seconds then echo SUB-DONE");
        child.Detail.ShouldBe("sleep 12; echo SUB-DONE");
        child.DeadlineAt.ShouldBe(child.StartedAt + TimeSpan.FromMilliseconds(30_000));
        child.BlocksCompletion.ShouldBeTrue();

        // The subagent ends its turn while its command runs; the host still waits on the command.
        var interim = snapshots.First(static snapshot =>
            snapshot.Items.Count == 2 && snapshot.Items[0].Status == AgentWorkStatuses.Completed);
        interim.Items[0].BlocksCompletion.ShouldBeFalse();
        interim.Items[1].Status.ShouldBe(AgentWorkStatuses.Running);
        interim.Items[1].BlocksCompletion.ShouldBeTrue();

        // The CLI starts the same subagent task again when the command finishes: the item reopens, it is not a third one.
        var reopened = snapshots.First(snapshot =>
            snapshot.Items.Count == 2
            && snapshot.Items[0].Status == AgentWorkStatuses.Running
            && snapshot.Items[1].Status == AgentWorkStatuses.Completed);
        reopened.Items[0].EndedAt.ShouldBeNull();
        reopened.Items[0].Summary.ShouldBeNull();
        reopened.Items[0].StartedAt.ShouldBe(agent.StartedAt);

        var final = snapshots[^1];
        final.Items.Count.ShouldBe(2);
        final.Items.ShouldAllBe(static item => item.Status == AgentWorkStatuses.Completed && !item.BlocksCompletion);
        final.Items[0].Summary.ShouldBe("SUB-DONE");
        final.Items[0].Usage!.TotalTokens.ShouldBe(16031);
    }

    [Test]
    public async Task ExecuteAsync_titles_a_subagent_from_the_launching_tool_call_when_the_start_event_has_no_description()
    {
        var provider = CreateProvider(messageBatches:
        [
            [
                StreamMessage("system", new { subtype = "init" }),
                ToolUseMessage("Agent", "toolu-agent", new { description = "Review the migration plan", prompt = "go" }),
                StreamMessage(
                    "system",
                    new { subtype = "task_started", task_id = "agent-1", task_type = "local_agent", tool_use_id = "toolu-agent" }),
                StreamMessage("result", new { is_error = false }),
                StreamMessage("system", new { subtype = "task_notification", task_id = "agent-1", status = "completed" }),
                StreamMessage("system", new { subtype = "init" }),
                StreamMessage("result", new { is_error = false })
            ]
        ]);

        var item = Snapshots(await CollectAsync(provider))[0].Items.ShouldHaveSingleItem();

        item.Title.ShouldBe("Review the migration plan");
    }

    [Test]
    public async Task ExecuteAsync_prefers_the_start_event_description_for_a_subagent_title()
    {
        var provider = CreateProvider(messageBatches:
        [
            [
                StreamMessage("system", new { subtype = "init" }),
                ToolUseMessage("Agent", "toolu-agent", new { description = "from the tool call" }),
                StreamMessage(
                    "system",
                    new
                    {
                        subtype = "task_started",
                        task_id = "agent-1",
                        task_type = "local_agent",
                        tool_use_id = "toolu-agent",
                        description = "from the start event"
                    }),
                StreamMessage("result", new { is_error = false }),
                StreamMessage("system", new { subtype = "task_notification", task_id = "agent-1", status = "completed" }),
                StreamMessage("system", new { subtype = "init" }),
                StreamMessage("result", new { is_error = false })
            ]
        ]);

        Snapshots(await CollectAsync(provider))[0].Items.ShouldHaveSingleItem().Title.ShouldBe("from the start event");
    }

    [Test]
    public async Task ExecuteAsync_yields_a_snapshot_directly_after_the_message_that_caused_it()
    {
        var messages = await CollectAsync(CreateProvider(messageBatches: [ClaudeAgentWorkCapture.ForegroundRequestedSubagent]));

        var types = DescribeTypes(messages);
        for (var index = 0; index < types.Count; index++)
        {
            if (types[index] != AgentWorkSnapshot.MessageType)
            {
                continue;
            }

            types[index - 1].ShouldBeOneOf("system:task_started", "system:task_notification", "system:background_tasks_changed");
        }

        types.IndexOf(AgentWorkSnapshot.MessageType).ShouldBe(types.IndexOf("system:task_started") + 1);
    }

    [Test]
    public async Task ExecuteAsync_yields_no_snapshot_for_messages_that_change_nothing()
    {
        var provider = CreateProvider(messageBatches:
        [
            [
                StreamMessage("system", new { subtype = "init" }),
                StreamMessage("assistant", new { message = new { content = new object[] { new { type = "text", text = "hi" } } } }),
                StreamMessage("system", new { subtype = "task_progress", task_id = "x", description = "Running y" }),
                StreamMessage("result", new { is_error = false, result = "DONE" })
            ]
        ]);

        var messages = await CollectAsync(provider);

        Snapshots(messages).ShouldBeEmpty();
        DescribeTypes(messages).ShouldBe(["system:init", "assistant", "system:task_progress", "result"]);
    }

    [Test]
    public async Task ExecuteAsync_yields_at_most_one_snapshot_for_one_message()
    {
        // One task list drops two items at once: that is a single observed message and a single snapshot.
        var provider = CreateProvider(messageBatches:
        [
            [
                StreamMessage("system", new { subtype = "init" }),
                ToolUseMessage("Monitor", "toolu-a", new { description = "a", timeout_ms = 60_000 }),
                ToolUseMessage("Monitor", "toolu-b", new { description = "b", timeout_ms = 60_000 }),
                StreamMessage(
                    "system",
                    new { subtype = "background_tasks_changed", tasks = BackgroundCommandTasks("monitor-a", "monitor-b") }),
                StreamMessage(
                    "system",
                    new { subtype = "task_started", task_id = "monitor-a", task_type = "local_bash", tool_use_id = "toolu-a" }),
                StreamMessage(
                    "system",
                    new { subtype = "task_started", task_id = "monitor-b", task_type = "local_bash", tool_use_id = "toolu-b" }),
                StreamMessage("system", new { subtype = "background_tasks_changed", tasks = BackgroundCommandTasks() }),
                StreamMessage("result", new { is_error = false })
            ]
        ]);

        var messages = await CollectAsync(provider);

        DescribeTypes(messages).ShouldBe(
        [
            "system:init",
            "assistant",
            "assistant",
            "system:background_tasks_changed",
            "system:task_started",
            AgentWorkSnapshot.MessageType,
            "system:task_started",
            AgentWorkSnapshot.MessageType,
            "system:background_tasks_changed",
            AgentWorkSnapshot.MessageType,
            "result"
        ]);
        var final = Snapshots(messages)[^1];
        final.Items.Count.ShouldBe(2);
        final.Items.ShouldAllBe(static item => item.Status == AgentWorkStatuses.Stopped && !item.BlocksCompletion);
    }

    [Test]
    public async Task ExecuteAsync_stops_an_item_that_vanishes_from_the_task_list_only_when_it_was_listed_before()
    {
        var provider = CreateProvider(messageBatches:
        [
            [
                StreamMessage("system", new { subtype = "init" }),
                ToolUseMessage("Monitor", "toolu-a", new { description = "listed", timeout_ms = 60_000 }),
                ToolUseMessage("Monitor", "toolu-b", new { description = "never listed", timeout_ms = 60_000 }),
                StreamMessage("system", new { subtype = "background_tasks_changed", tasks = BackgroundCommandTasks("monitor-a") }),
                StreamMessage(
                    "system",
                    new { subtype = "task_started", task_id = "monitor-a", task_type = "local_bash", tool_use_id = "toolu-a" }),
                StreamMessage(
                    "system",
                    new { subtype = "task_started", task_id = "monitor-b", task_type = "local_bash", tool_use_id = "toolu-b" }),
                StreamMessage("system", new { subtype = "background_tasks_changed", tasks = BackgroundCommandTasks("monitor-a") }),
                StreamMessage("system", new { subtype = "background_tasks_changed", tasks = BackgroundCommandTasks() }),
                StreamMessage("result", new { is_error = false })
            ]
        ]);

        var messages = await CollectAsync(provider);

        var final = Snapshots(messages)[^1];
        final.Items.Select(static item => (item.Title, item.Status)).ShouldBe(
        [
            ("listed", AgentWorkStatuses.Stopped),
            ("never listed", AgentWorkStatuses.Running)
        ]);
        final.Items[0].EndedAt.ShouldNotBeNull();
        final.Items[0].BlocksCompletion.ShouldBeFalse();
    }

    [Test]
    public async Task ExecuteAsync_creates_no_item_for_the_notifications_a_resumed_session_replays()
    {
        // Real resume order: a stopped notification for a task of the old session, an injected empty turn, then the
        // host prompt's own turn (replayed user message, assistant, result).
        var capture = ClaudeAgentWorkCapture.ResumedSessionReplay.ToList();
        var secondInit = capture.FindLastIndex(static message => message.Type == "system"
                                                                 && message.Content.GetProperty("subtype").GetString() == "init");
        capture.Insert(
            secondInit + 1,
            StreamMessage("user", new { isReplay = true, message = new { role = "user", content = "hello" } }));
        var provider = CreateProvider(messageBatches: [capture]);

        var messages = await CollectAsync(provider);

        Snapshots(messages).ShouldBeEmpty();
        DescribeTypes(messages)[0].ShouldBe("system:task_notification");
    }

    [Test]
    public async Task ExecuteAsync_creates_no_item_for_a_notification_of_a_task_that_never_started()
    {
        var provider = CreateProvider(messageBatches:
        [
            [
                StreamMessage("system", new { subtype = "init" }),
                StreamMessage(
                    "system",
                    new { subtype = "task_notification", task_id = "ghost-1", status = "stopped", summary = "old session" }),
                StreamMessage("system", new { subtype = "background_tasks_changed", tasks = SubagentTasks("ghost-2") }),
                StreamMessage("system", new { subtype = "background_tasks_changed", tasks = SubagentTasks() }),
                StreamMessage("result", new { is_error = false })
            ]
        ]);

        Snapshots(await CollectAsync(provider)).ShouldBeEmpty();
    }

    [Test]
    public async Task ExecuteAsync_reports_an_unrecognized_terminal_status_as_stopped_with_the_provider_text()
    {
        var provider = CreateProvider(messageBatches:
        [
            [
                StreamMessage("system", new { subtype = "init" }),
                StreamMessage("system", new { subtype = "task_started", task_id = "agent-1", task_type = "local_agent", description = "x" }),
                StreamMessage("result", new { is_error = false }),
                StreamMessage("system", new { subtype = "task_notification", task_id = "agent-1", status = "exploded" }),
                StreamMessage("system", new { subtype = "init" }),
                StreamMessage("result", new { is_error = false })
            ]
        ]);

        var item = Snapshots(await CollectAsync(provider))[^1].Items.ShouldHaveSingleItem();

        item.Status.ShouldBe(AgentWorkStatuses.Stopped);
        item.Summary.ShouldBe("exploded");
    }

    [Test]
    public async Task ExecuteAsync_caps_a_very_long_summary()
    {
        var provider = CreateProvider(messageBatches:
        [
            [
                StreamMessage("system", new { subtype = "init" }),
                StreamMessage("system", new { subtype = "task_started", task_id = "agent-1", task_type = "local_agent", description = "x" }),
                StreamMessage("result", new { is_error = false }),
                StreamMessage(
                    "system",
                    new { subtype = "task_notification", task_id = "agent-1", status = "completed", summary = new string('s', 10_000) }),
                StreamMessage("system", new { subtype = "init" }),
                StreamMessage("result", new { is_error = false })
            ]
        ]);

        var item = Snapshots(await CollectAsync(provider))[^1].Items.ShouldHaveSingleItem();

        item.Summary!.Length.ShouldBe(4_000);
        item.Summary.ShouldEndWith("…");
    }

    [Test]
    public async Task ExecuteAsync_snapshot_messages_carry_only_the_contract_members_and_no_raw_cli_json()
    {
        var messages = await CollectAsync(CreateProvider(messageBatches: [ClaudeAgentWorkCapture.BackgroundSubagentWithOwnedBash]));

        foreach (var message in messages.Where(static message => message.Type == AgentWorkSnapshot.MessageType))
        {
            message.Content.EnumerateObject().Select(static property => property.Name).ShouldBe(["sequence", "items"]);
            var itemMembers = new HashSet<string>(
            [
                "id", "kind", "parentId", "status", "title", "detail", "startedAt", "endedAt", "deadlineAt", "summary", "usage",
                "toolUseId", "blocksCompletion"
            ]);
            foreach (var item in message.Content.GetProperty("items").EnumerateArray())
            {
                item.EnumerateObject().Select(static property => property.Name).ShouldBeSubsetOf(itemMembers);
            }
        }
    }

    [Test]
    public async Task ExecuteAsync_ends_after_the_same_message_and_yields_the_same_other_messages_as_without_snapshots()
    {
        var capture = ClaudeAgentWorkCapture.BackgroundSubagentWithOwnedBash.ToList();
        var finalResult = capture.FindLastIndex(static message => message.Type == "result");
        var withTrailingMessage = capture.Take(finalResult + 1)
            .Append(StreamMessage("assistant", new { marker = "after-final-result" }))
            .ToList();
        var provider = CreateProvider(messageBatches: [withTrailingMessage]);

        var messages = await CollectAsync(provider);

        var others = messages.Where(static message => message.Type != AgentWorkSnapshot.MessageType).ToList();
        // The prompt replay is consumed by the host and never delivered; every other message of the stream is.
        var expected = capture.Take(finalResult + 1).Where(static message => message.Type != "user").ToList();
        DescribeTypes(others).ShouldBe(DescribeTypes(expected));
        others[^1].Type.ShouldBe("result");
        messages.Any(static message => message.Content.TryGetProperty("marker", out _)).ShouldBeFalse();
        messages[^1].Type.ShouldNotBe(AgentWorkSnapshot.MessageType);
        provider.DisposedTransportCount.ShouldBe(1);
    }

    private static List<AgentWorkSnapshot> Snapshots(IEnumerable<CliMessage> messages)
    {
        var snapshots = new List<AgentWorkSnapshot>();
        foreach (var message in messages.Where(static message => message.Type == AgentWorkSnapshot.MessageType))
        {
            AgentWorkSnapshot.TryFromCliMessage(message, out var snapshot).ShouldBeTrue();
            snapshots.Add(snapshot);
        }

        return snapshots;
    }
}
