using System.Text.Json;
using HagiCode.Libs.Core.AgentWork;
using HagiCode.Libs.Providers.OpenCode;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Replays the event stream recorded from OpenCode 2.0.32 (a parent that spawns two background subagents, interrupts one, and
/// re-spawns it) through the agent work tracker, so the tracker is checked against the real wire shapes and not only hand-built ones.
/// </summary>
public sealed class OpenCodeAgentWorkRecordedFixtureTests
{
    private const string FixtureName = "events-subagent-background.jsonl";

    [Test]
    public void The_recorded_run_lists_each_subagent_with_the_status_OpenCode_reported()
    {
        var (parentSessionId, events) = LoadRecording();
        var tracker = new OpenCodeAgentWorkTracker(parentSessionId);

        var snapshots = new List<AgentWorkSnapshot>();
        foreach (var envelope in events)
        {
            tracker.Observe(envelope);
            if (tracker.TakeSnapshot() is { } snapshot)
            {
                snapshots.Add(snapshot);
            }
        }

        var items = snapshots[^1].Items;
        items.Count.ShouldBe(3);
        items.ShouldAllBe(static item => item.Id.StartsWith("opencode:ses_", StringComparison.Ordinal));
        items.ShouldAllBe(static item => item.Kind == AgentWorkKinds.Subagent && item.ParentId == null);
        items.Select(static item => item.Title).ShouldBe(["count files", "slow long job", "slow long job"]);
        items.Select(static item => item.Status).ShouldBe([AgentWorkStatuses.Completed, AgentWorkStatuses.Stopped, AgentWorkStatuses.Completed]);
        items.ShouldAllBe(static item => !item.BlocksCompletion);
        items.ShouldAllBe(static item => item.EndedAt != null);
    }

    [Test]
    public void The_recorded_run_marks_the_parent_success_as_happening_while_a_subagent_still_runs()
    {
        var (parentSessionId, events) = LoadRecording();
        var tracker = new OpenCodeAgentWorkTracker(parentSessionId);
        var activeAtParentSuccess = new List<bool>();

        foreach (var envelope in events)
        {
            tracker.Observe(envelope);
            if (envelope.EventType == "session.execution.succeeded"
                && envelope.SessionId == parentSessionId)
            {
                activeAtParentSuccess.Add(tracker.HasActiveWork);
            }
        }

        // The recording has the parent report success while a background subagent still runs, which is why the turn is held open.
        activeAtParentSuccess.ShouldContain(true);
    }

    private static (string ParentSessionId, List<OpenCodeEventEnvelope> Events) LoadRecording()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "OpenCode", "v2.0.32", FixtureName);
        string? parentSessionId = null;
        var events = new List<OpenCodeEventEnvelope>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement.Clone();
            if (root.TryGetProperty("_header", out _))
            {
                parentSessionId = root.GetProperty("parentSessionID").GetString();
                continue;
            }

            events.Add(new OpenCodeEventEnvelope { Data = root });
        }

        return (parentSessionId ?? throw new InvalidOperationException("The recording has no header."), events);
    }
}
