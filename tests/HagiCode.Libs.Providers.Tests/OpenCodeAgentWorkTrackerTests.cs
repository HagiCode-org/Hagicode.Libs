using System.Text.Json;
using HagiCode.Libs.Core.AgentWork;
using HagiCode.Libs.Providers.OpenCode;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Checks the OpenCode agent work tracker against the scenarios of the opencode-agent-work-tracking spec, using event envelopes in the
/// shape OpenCode 2.0.32 sends on <c>GET /api/event</c>.
/// </summary>
public sealed class OpenCodeAgentWorkTrackerTests
{
    private const string Prompted = "ses_prompted";
    private const string Child = "ses_child_1";
    private const string Grandchild = "ses_grandchild_1";
    private const string GreatGrandchild = "ses_great_grandchild_1";

    [Test]
    public void A_child_created_by_the_prompted_session_becomes_a_running_subagent_without_a_parent()
    {
        var tracker = new OpenCodeAgentWorkTracker(Prompted);
        tracker.Observe(Created(Child, Prompted, "count files"));
        tracker.Observe(Event("session.execution.started", Child));

        var item = tracker.TakeSnapshot().ShouldNotBeNull().Items.Single();
        item.Id.ShouldBe($"opencode:{Child}");
        item.Kind.ShouldBe(AgentWorkKinds.Subagent);
        item.Status.ShouldBe(AgentWorkStatuses.Running);
        item.Title.ShouldBe("count files");
        item.ParentId.ShouldBeNull();
        item.BlocksCompletion.ShouldBeFalse();
    }

    [Test]
    public void A_grandchild_is_listed_under_the_child_and_a_deeper_session_under_the_top_level_child()
    {
        var tracker = new OpenCodeAgentWorkTracker(Prompted);
        tracker.Observe(Created(Child, Prompted, "outer"));
        tracker.Observe(Created(Grandchild, Child, "middle"));
        tracker.Observe(Created(GreatGrandchild, Grandchild, "inner"));

        var items = tracker.TakeSnapshot().ShouldNotBeNull().Items;
        items.Single(item => item.Id == $"opencode:{Child}").ParentId.ShouldBeNull();
        items.Single(item => item.Id == $"opencode:{Grandchild}").ParentId.ShouldBe($"opencode:{Child}");
        items.Single(item => item.Id == $"opencode:{GreatGrandchild}").ParentId.ShouldBe($"opencode:{Child}");
    }

    [Test]
    public void Sessions_of_another_client_on_the_same_stream_create_no_item_and_leave_the_snapshot_unchanged()
    {
        var tracker = new OpenCodeAgentWorkTracker(Prompted);
        tracker.Observe(Created("ses_other", null, "other client"));
        tracker.Observe(Event("session.execution.started", "ses_other"));
        tracker.Observe(Created("ses_other_child", "ses_other", "other child"));
        tracker.Observe(Event("session.text.ended", "ses_other", new { text = "FOREIGN" }));

        tracker.TakeSnapshot().ShouldBeNull();
        tracker.HasActiveWork.ShouldBeFalse();
    }

    [Test]
    public void An_event_for_a_session_of_unknown_ancestry_creates_no_item()
    {
        var tracker = new OpenCodeAgentWorkTracker(Prompted);
        tracker.Observe(Event("session.execution.started", "ses_never_seen"));
        tracker.Observe(Event("session.execution.succeeded", "ses_never_seen"));

        tracker.TakeSnapshot().ShouldBeNull();
    }

    [Test]
    public void A_long_task_description_is_truncated_to_the_title_limit()
    {
        var tracker = new OpenCodeAgentWorkTracker(Prompted);
        tracker.Observe(Created(Child, Prompted, new string('t', 250)));

        var title = tracker.TakeSnapshot().ShouldNotBeNull().Items.Single().Title;
        title.Length.ShouldBe(OpenCodeAgentWorkTracker.MaxTitleLength);
        title.ShouldEndWith("…");
    }

    [Test]
    public void Without_a_title_the_agent_name_is_the_title()
    {
        var tracker = new OpenCodeAgentWorkTracker(Prompted);
        tracker.Observe(Event("session.created", Child, new { parentID = Prompted, agent = "general" }));

        tracker.TakeSnapshot().ShouldNotBeNull().Items.Single().Title.ShouldBe("general");
    }

    [Test]
    public void Success_completes_the_child_with_its_final_text_as_summary_and_an_end_time()
    {
        var tracker = new OpenCodeAgentWorkTracker(Prompted);
        tracker.Observe(Created(Child, Prompted, "count files"));
        tracker.Observe(Event("session.execution.started", Child));
        tracker.Observe(Event("session.text.ended", Child, new { text = "There are 3 files." }));
        tracker.Observe(Event("session.execution.succeeded", Child));

        var item = tracker.TakeSnapshot().ShouldNotBeNull().Items.Single();
        item.Status.ShouldBe(AgentWorkStatuses.Completed);
        item.Summary.ShouldBe("There are 3 files.");
        item.EndedAt.ShouldNotBeNull();
    }

    [Test]
    public void Failure_marks_the_child_failed_with_the_error_message_as_summary()
    {
        var tracker = new OpenCodeAgentWorkTracker(Prompted);
        tracker.Observe(Created(Child, Prompted, "count files"));
        tracker.Observe(Event("session.execution.started", Child));
        tracker.Observe(Event("session.execution.failed", Child, new { error = new { type = "provider.no-route", message = "Model unavailable" } }));

        var item = tracker.TakeSnapshot().ShouldNotBeNull().Items.Single();
        item.Status.ShouldBe(AgentWorkStatuses.Failed);
        item.Summary.ShouldBe("Model unavailable");
    }

    [Test]
    public void An_interrupted_child_is_stopped()
    {
        var tracker = new OpenCodeAgentWorkTracker(Prompted);
        tracker.Observe(Created(Child, Prompted, "count files"));
        tracker.Observe(Event("session.execution.started", Child));
        tracker.Observe(Event("session.execution.interrupted", Child));

        tracker.TakeSnapshot().ShouldNotBeNull().Items.Single().Status.ShouldBe(AgentWorkStatuses.Stopped);
    }

    [Test]
    public void A_completed_child_that_later_reports_an_interrupt_stays_completed()
    {
        var tracker = new OpenCodeAgentWorkTracker(Prompted);
        tracker.Observe(Created(Child, Prompted, "count files"));
        tracker.Observe(Event("session.execution.started", Child));
        tracker.Observe(Event("session.execution.succeeded", Child));
        tracker.Observe(Event("session.execution.interrupted", Child));

        tracker.TakeSnapshot().ShouldNotBeNull().Items.Single().Status.ShouldBe(AgentWorkStatuses.Completed);
    }

    [Test]
    public void A_completed_child_that_starts_again_reopens_with_its_end_time_and_summary_cleared()
    {
        var tracker = new OpenCodeAgentWorkTracker(Prompted);
        tracker.Observe(Created(Child, Prompted, "count files"));
        tracker.Observe(Event("session.execution.started", Child));
        tracker.Observe(Event("session.text.ended", Child, new { text = "first answer" }));
        tracker.Observe(Event("session.execution.succeeded", Child));
        tracker.TakeSnapshot();

        tracker.Observe(Event("session.execution.started", Child));

        var item = tracker.TakeSnapshot().ShouldNotBeNull().Items.Single();
        item.Status.ShouldBe(AgentWorkStatuses.Running);
        item.EndedAt.ShouldBeNull();
        item.Summary.ShouldBeNull();
    }

    [Test]
    public void A_completion_notice_confirms_a_known_child_and_creates_none()
    {
        var tracker = new OpenCodeAgentWorkTracker(Prompted);
        tracker.Observe(Notice(Child, "completed"));
        tracker.TakeSnapshot().ShouldBeNull();

        tracker.Observe(Created(Child, Prompted, "count files"));
        tracker.Observe(Event("session.execution.started", Child));
        tracker.Observe(Notice(Child, "completed"));

        tracker.TakeSnapshot().ShouldNotBeNull().Items.Single().Status.ShouldBe(AgentWorkStatuses.Completed);
    }

    [Test]
    public void A_cancelled_notice_stops_the_child_and_an_unrecognized_state_never_completes_it()
    {
        var tracker = new OpenCodeAgentWorkTracker(Prompted);
        tracker.Observe(Created(Child, Prompted, "count files"));
        tracker.Observe(Event("session.execution.started", Child));
        tracker.Observe(Notice(Child, "cancelled"));

        tracker.TakeSnapshot().ShouldNotBeNull().Items.Single().Status.ShouldBe(AgentWorkStatuses.Stopped);

        tracker.Observe(Event("session.execution.started", Child));
        tracker.Observe(Notice(Child, "mystery"));

        tracker.TakeSnapshot().ShouldNotBeNull().Items.Single().Status.ShouldBe(AgentWorkStatuses.Stopped);
    }

    [Test]
    public void A_shell_completion_notice_is_not_a_child_status()
    {
        var tracker = new OpenCodeAgentWorkTracker(Prompted);
        tracker.Observe(Created(Child, Prompted, "count files"));
        tracker.Observe(Event("session.execution.started", Child));
        tracker.Observe(Event("session.inbox.enqueued", Prompted, new
        {
            item = new { type = "synthetic", payload = new { text = "<shell state=\"completed\">", metadata = new { source = "shell", shellID = Child, state = "completed" } } },
        }));

        tracker.TakeSnapshot().ShouldNotBeNull().Items.Single().Status.ShouldBe(AgentWorkStatuses.Running);
    }

    [Test]
    public void A_summary_longer_than_the_limit_is_truncated()
    {
        var tracker = new OpenCodeAgentWorkTracker(Prompted);
        tracker.Observe(Created(Child, Prompted, "count files"));
        tracker.Observe(Event("session.execution.started", Child));
        tracker.Observe(Event("session.text.ended", Child, new { text = new string('s', 5000) }));
        tracker.Observe(Event("session.execution.succeeded", Child));

        var summary = tracker.TakeSnapshot().ShouldNotBeNull().Items.Single().Summary;
        summary.ShouldNotBeNull();
        summary.Length.ShouldBe(OpenCodeAgentWorkTracker.MaxSummaryLength);
    }

    [Test]
    public void Tool_uses_count_every_finished_tool_call_and_no_token_field_is_invented()
    {
        var tracker = new OpenCodeAgentWorkTracker(Prompted);
        tracker.Observe(Created(Child, Prompted, "count files"));
        tracker.Observe(Event("session.execution.started", Child));
        tracker.Observe(Event("session.tool.success", Child, new { id = "call_1" }));
        tracker.Observe(Event("session.tool.success", Child, new { id = "call_2" }));
        tracker.Observe(Event("session.tool.failed", Child, new { id = "call_3" }));
        tracker.Observe(Event("session.execution.succeeded", Child));

        var usage = tracker.TakeSnapshot().ShouldNotBeNull().Items.Single().Usage.ShouldNotBeNull();
        usage.ToolUses.ShouldBe(3);
        usage.TotalTokens.ShouldBeNull();
    }

    [Test]
    public void A_child_that_ends_without_a_tool_call_reports_only_its_duration()
    {
        var tracker = new OpenCodeAgentWorkTracker(Prompted);
        tracker.Observe(Created(Child, Prompted, "count files"));
        tracker.Observe(Event("session.execution.started", Child));
        tracker.Observe(Event("session.execution.succeeded", Child));

        var usage = tracker.TakeSnapshot().ShouldNotBeNull().Items.Single().Usage.ShouldNotBeNull();
        usage.ToolUses.ShouldBeNull();
        usage.TotalTokens.ShouldBeNull();
        usage.DurationMs.ShouldNotBeNull();
    }

    [Test]
    public void A_child_still_running_reports_no_usage()
    {
        var tracker = new OpenCodeAgentWorkTracker(Prompted);
        tracker.Observe(Created(Child, Prompted, "count files"));
        tracker.Observe(Event("session.execution.started", Child));

        tracker.TakeSnapshot().ShouldNotBeNull().Items.Single().Usage.ShouldBeNull();
    }

    [Test]
    public void An_unchanged_list_is_not_delivered_twice_and_the_sequence_increases()
    {
        var tracker = new OpenCodeAgentWorkTracker(Prompted);
        tracker.Observe(Created(Child, Prompted, "count files"));
        tracker.Observe(Event("session.execution.started", Child));
        var first = tracker.TakeSnapshot().ShouldNotBeNull();

        tracker.Observe(Event("session.text.ended", Child, new { text = "still going" }));

        tracker.TakeSnapshot().ShouldBeNull();
        tracker.Observe(Event("session.execution.succeeded", Child));
        tracker.TakeSnapshot().ShouldNotBeNull().Sequence.ShouldBeGreaterThan(first.Sequence);
    }

    [Test]
    public void A_turn_with_no_child_has_no_first_snapshot()
    {
        var tracker = new OpenCodeAgentWorkTracker(Prompted);
        tracker.Observe(Event("session.text.ended", Prompted, new { text = "hello" }));
        tracker.Observe(Event("session.execution.succeeded", Prompted));

        tracker.TakeSnapshot().ShouldBeNull();
    }

    [Test]
    public void Holding_marks_the_active_children_as_blocking_and_releasing_clears_it()
    {
        var tracker = new OpenCodeAgentWorkTracker(Prompted);
        tracker.Observe(Created(Child, Prompted, "count files"));
        tracker.Observe(Event("session.execution.started", Child));
        tracker.TakeSnapshot();

        tracker.HoldActive();
        tracker.TakeSnapshot().ShouldNotBeNull().Items.Single().BlocksCompletion.ShouldBeTrue();

        tracker.ReleaseHold();
        tracker.TakeSnapshot().ShouldNotBeNull().Items.Single().BlocksCompletion.ShouldBeFalse();
    }

    [Test]
    public void Stopping_the_active_children_ends_them_and_the_hold_together()
    {
        var tracker = new OpenCodeAgentWorkTracker(Prompted);
        tracker.Observe(Created(Child, Prompted, "count files"));
        tracker.Observe(Event("session.execution.started", Child));
        tracker.HoldActive();
        tracker.TakeSnapshot();

        tracker.StopActive();

        var item = tracker.TakeSnapshot().ShouldNotBeNull().Items.Single();
        item.Status.ShouldBe(AgentWorkStatuses.Stopped);
        item.EndedAt.ShouldNotBeNull();
        item.BlocksCompletion.ShouldBeFalse();
        tracker.HasActiveWork.ShouldBeFalse();
    }

    [Test]
    public void A_background_command_is_not_listed_because_OpenCode_reports_it_without_a_timeout()
    {
        var tracker = new OpenCodeAgentWorkTracker(Prompted);
        tracker.Observe(Event("session.tool.called", Prompted, new
        {
            id = "call_shell",
            input = new { command = "sleep 20", background = true, workdir = "/work" },
            executed = false,
        }));
        tracker.Observe(Event("shell.created", Prompted));

        tracker.TakeSnapshot().ShouldBeNull();
    }

    [Test]
    public void A_malformed_event_changes_nothing()
    {
        var tracker = new OpenCodeAgentWorkTracker(Prompted);
        tracker.Observe(Created(Child, Prompted, "count files"));
        tracker.Observe(Event("session.execution.started", Child));
        var before = tracker.TakeSnapshot().ShouldNotBeNull();

        tracker.Observe(new OpenCodeEventEnvelope { Data = JsonSerializer.SerializeToElement("not an event") });
        tracker.Observe(new OpenCodeEventEnvelope { Data = JsonSerializer.SerializeToElement(new { type = "session.execution.succeeded", data = "broken" }) });

        tracker.TakeSnapshot().ShouldBeNull();
        tracker.HasActiveWork.ShouldBeTrue();
        before.Items.Single().Status.ShouldBe(AgentWorkStatuses.Running);
    }

    private static OpenCodeEventEnvelope Created(string sessionId, string? parentId, string title)
    {
        return Event("session.created", sessionId, new { parentID = parentId, title, agent = "general" });
    }

    /// <summary>
    /// The completion notice OpenCode enqueues on the prompted session when a subagent ends (an inbox item of type <c>synthetic</c>).
    /// </summary>
    private static OpenCodeEventEnvelope Notice(string childId, string state)
    {
        return Event("session.inbox.enqueued", Prompted, new
        {
            item = new
            {
                type = "synthetic",
                payload = new
                {
                    text = $"<subagent sessionID=\"{childId}\" state=\"{state}\">",
                    metadata = new { source = "subagent", childID = childId, agent = "General", state },
                },
            },
        });
    }

    /// <summary>
    /// Builds an envelope of the event <paramref name="type"/> for <paramref name="sessionId"/>, with the fields of <paramref name="extra"/>
    /// added to its payload (<c>data.data</c>).
    /// </summary>
    private static OpenCodeEventEnvelope Event(string type, string sessionId, object? extra = null)
    {
        var data = new Dictionary<string, JsonElement> { ["sessionID"] = JsonSerializer.SerializeToElement(sessionId) };
        if (extra is not null)
        {
            foreach (var (name, value) in JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(extra))!)
            {
                data[name] = value;
            }
        }

        return new OpenCodeEventEnvelope
        {
            Data = JsonSerializer.SerializeToElement(new { id = "evt_test", type, data }),
        };
    }
}
