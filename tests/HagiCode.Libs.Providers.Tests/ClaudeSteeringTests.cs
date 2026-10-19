using System.Collections.Concurrent;
using System.Text.Json;
using Shouldly;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.ClaudeCode;

namespace HagiCode.Libs.Providers.Tests;

public sealed class ClaudeSteeringTests
{
    private const string FollowUpText = "switch to lakes";

    [Test]
    public async Task SteerAsync_is_not_steerable_before_attach_and_after_detach()
    {
        var steering = new ClaudeRunSteering();
        var sinkCalls = 0;

        (await steering.SteerAsync("s1", "text")).ShouldBe(ClaudeSteerResult.NotSteerable);

        steering.Attach((_, _, _) =>
        {
            sinkCalls++;
            return Task.FromResult(ClaudeSteerResult.Sent);
        });
        (await steering.SteerAsync("s1", "text")).ShouldBe(ClaudeSteerResult.Sent);

        steering.Detach();
        (await steering.SteerAsync("s2", "text")).ShouldBe(ClaudeSteerResult.NotSteerable);
        sinkCalls.ShouldBe(1);
    }

    [Test]
    public void Report_raises_the_outcome_event_once_per_call()
    {
        var steering = new ClaudeRunSteering();
        var observed = new List<ClaudeSteerOutcomeEventArgs>();
        steering.OutcomeObserved += (_, args) => observed.Add(args);

        steering.Report(new ClaudeSteerResolution("s1", ClaudeSteerOutcome.Consumed));
        steering.Report(new ClaudeSteerResolution("s2", ClaudeSteerOutcome.NotConsumed));

        observed.Select(args => (args.SteerId, args.Outcome))
            .ShouldBe([("s1", ClaudeSteerOutcome.Consumed), ("s2", ClaudeSteerOutcome.NotConsumed)]);
    }

    [Test]
    public async Task SteerAsync_rejects_an_empty_id_or_text()
    {
        var steering = new ClaudeRunSteering();

        await Should.ThrowAsync<ArgumentException>(async () => await steering.SteerAsync(" ", "text"));
        await Should.ThrowAsync<ArgumentException>(async () => await steering.SteerAsync("s1", ""));
    }

    [Test]
    public void SteerDeliveryTimeout_defaults_to_thirty_seconds()
    {
        new ClaudeCodeOptions().SteerDeliveryTimeout.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Test]
    public void Tracker_keeps_a_result_open_while_a_steer_is_unconsumed()
    {
        var tracker = new ClaudeStreamCompletionTracker(TimeSpan.FromMinutes(1));
        tracker.RecordHostPromptSent("hello");
        tracker.Observe(Message("system", new { subtype = "init" })).ShouldBeFalse();
        tracker.Observe(PromptReplay("hello")).ShouldBeFalse();

        tracker.TryRegisterSteer("s1", FollowUpText).ShouldBeTrue();

        tracker.Observe(Message("result", new { result = "answer" })).ShouldBeFalse();
        tracker.CurrentWait.ShouldBe(ClaudeStreamCompletionTracker.PendingWait.SteerDelivery);
    }

    [Test]
    public void Tracker_refuses_a_steer_once_the_run_is_complete()
    {
        var tracker = new ClaudeStreamCompletionTracker(TimeSpan.FromMinutes(1));
        tracker.Observe(Message("system", new { subtype = "init" }));
        tracker.Observe(Message("result", new { result = "done" })).ShouldBeTrue();

        tracker.TryRegisterSteer("late", FollowUpText).ShouldBeFalse();
        tracker.CurrentWait.ShouldBe(ClaudeStreamCompletionTracker.PendingWait.None);
    }

    [Test]
    public void Tracker_refuses_a_steer_with_an_id_that_is_already_outstanding()
    {
        var tracker = new ClaudeStreamCompletionTracker(TimeSpan.FromMinutes(1));

        tracker.TryRegisterSteer("s1", "first").ShouldBeTrue();
        tracker.TryRegisterSteer("s1", "second").ShouldBeFalse();
    }

    [Test]
    public void Tracker_reports_a_steer_consumed_when_its_echo_arrives_and_completes_on_the_next_result()
    {
        var tracker = new ClaudeStreamCompletionTracker(TimeSpan.FromMinutes(1));
        tracker.RecordHostPromptSent("hello");
        tracker.Observe(Message("system", new { subtype = "init" }));
        tracker.Observe(PromptReplay("hello"));
        tracker.TryRegisterSteer("s1", FollowUpText);
        tracker.Observe(Message("result", new { result = "first" })).ShouldBeFalse();

        tracker.Observe(Message("system", new { subtype = "init" })).ShouldBeFalse();
        tracker.Observe(SteerEcho("s1", FollowUpText)).ShouldBeFalse();
        tracker.LastMessageWasSteerEcho.ShouldBeTrue();
        tracker.TakeSteerResolutions().ShouldBe([new ClaudeSteerResolution("s1", ClaudeSteerOutcome.Consumed)]);

        tracker.Observe(Message("result", new { result = "second" })).ShouldBeTrue();
        tracker.TakeSteerResolutions().ShouldBeEmpty();
    }

    [Test]
    public void Tracker_reports_a_consumed_steer_exactly_once_even_if_the_echo_repeats()
    {
        var tracker = new ClaudeStreamCompletionTracker(TimeSpan.FromMinutes(1));
        tracker.TryRegisterSteer("s1", "text");

        tracker.Observe(SteerEcho("s1", "text"));
        tracker.Observe(SteerEcho("s1", "text"));

        tracker.TakeSteerResolutions().ShouldBe([new ClaudeSteerResolution("s1", ClaudeSteerOutcome.Consumed)]);
    }

    [Test]
    public void Steer_echo_does_not_end_the_prompt_start_wait()
    {
        var tracker = new ClaudeStreamCompletionTracker(TimeSpan.FromMinutes(1));
        tracker.RecordHostPromptSent("hello");
        tracker.Observe(Message("system", new { subtype = "init" }));
        tracker.TryRegisterSteer("s1", "steer");

        tracker.Observe(SteerEcho("s1", "steer"));
        tracker.HostPromptAwaitingStart.ShouldBeTrue();

        tracker.Observe(PromptReplay("hello"));
        tracker.HostPromptAwaitingStart.ShouldBeFalse();
        tracker.LastMessageWasSteerEcho.ShouldBeFalse();
    }

    [Test]
    public void Steer_whose_text_equals_the_prompt_is_attributed_by_its_uuid()
    {
        var tracker = new ClaudeStreamCompletionTracker(TimeSpan.FromMinutes(1));
        tracker.RecordHostPromptSent("hello");
        tracker.Observe(Message("system", new { subtype = "init" }));
        tracker.TryRegisterSteer("s1", "hello");

        tracker.Observe(SteerEcho("s1", "hello"));
        tracker.TakeSteerResolutions().ShouldBe([new ClaudeSteerResolution("s1", ClaudeSteerOutcome.Consumed)]);
        tracker.HostPromptAwaitingStart.ShouldBeTrue();

        tracker.Observe(PromptReplay("hello"));
        tracker.HostPromptAwaitingStart.ShouldBeFalse();
        tracker.TakeSteerResolutions().ShouldBeEmpty();
    }

    [Test]
    public void Echo_without_uuid_matches_a_steer_by_text_when_no_prompt_is_awaited()
    {
        var tracker = new ClaudeStreamCompletionTracker(TimeSpan.FromMinutes(1));
        tracker.TryRegisterSteer("s1", "go");

        tracker.Observe(ReplayWithoutUuid("go"));

        tracker.LastMessageWasSteerEcho.ShouldBeTrue();
        tracker.TakeSteerResolutions().ShouldBe([new ClaudeSteerResolution("s1", ClaudeSteerOutcome.Consumed)]);
    }

    [Test]
    public void Echo_without_uuid_stays_the_prompt_echo_while_the_prompt_with_that_text_is_awaited()
    {
        var tracker = new ClaudeStreamCompletionTracker(TimeSpan.FromMinutes(1));
        tracker.RecordHostPromptSent("go");
        tracker.TryRegisterSteer("s1", "go");

        tracker.Observe(ReplayWithoutUuid("go"));

        tracker.LastMessageWasSteerEcho.ShouldBeFalse();
        tracker.HostPromptAwaitingStart.ShouldBeFalse();
        tracker.TakeSteerResolutions().ShouldBeEmpty();
    }

    [Test]
    public void CurrentWait_reports_steer_delivery_only_when_nothing_else_is_pending()
    {
        var tracker = new ClaudeStreamCompletionTracker(TimeSpan.FromMinutes(1));
        tracker.Observe(Message("system", new { subtype = "init" }));
        tracker.TryRegisterSteer("s1", "text");

        tracker.CurrentWait.ShouldBe(ClaudeStreamCompletionTracker.PendingWait.None, "a turn is still running");

        tracker.Observe(Message("result", new { result = "done" }));
        tracker.CurrentWait.ShouldBe(ClaudeStreamCompletionTracker.PendingWait.SteerDelivery);
    }

    [Test]
    public void CurrentWait_gives_the_other_pending_waits_precedence_over_a_steer()
    {
        var awaitingPrompt = new ClaudeStreamCompletionTracker(TimeSpan.FromMinutes(1));
        awaitingPrompt.RecordHostPromptSent("hello");
        awaitingPrompt.Observe(Message("system", new { subtype = "init" }));
        awaitingPrompt.Observe(Message("result", new { result = "first" }));
        awaitingPrompt.TryRegisterSteer("s1", "text");
        awaitingPrompt.CurrentWait.ShouldBe(ClaudeStreamCompletionTracker.PendingWait.HostPromptStart);

        var notification = new ClaudeStreamCompletionTracker(TimeSpan.FromMinutes(1));
        notification.Observe(Message("system", new { subtype = "init" }));
        notification.Observe(Message("system", new { subtype = "task_started", task_id = "t1", task_type = "local_bash" }));
        notification.Observe(Message("system", new { subtype = "task_notification", task_id = "t1", status = "completed" }));
        notification.Observe(Message("result", new { result = "first" }));
        notification.TryRegisterSteer("s1", "text");
        notification.CurrentWait.ShouldBe(ClaudeStreamCompletionTracker.PendingWait.NotificationTurn);
    }

    [Test]
    public void CloseSteering_reports_every_unresolved_steer_as_not_consumed_and_refuses_later_steers()
    {
        var tracker = new ClaudeStreamCompletionTracker(TimeSpan.FromMinutes(1));
        tracker.TryRegisterSteer("s1", "one");
        tracker.TryRegisterSteer("s2", "two");

        tracker.CloseSteering().ShouldBe(
        [
            new ClaudeSteerResolution("s1", ClaudeSteerOutcome.NotConsumed),
            new ClaudeSteerResolution("s2", ClaudeSteerOutcome.NotConsumed)
        ]);
        tracker.TryRegisterSteer("s3", "three").ShouldBeFalse();
        tracker.CloseSteering().ShouldBeEmpty();
    }

    [Test]
    public void UnregisterSteer_forgets_a_steer_that_was_never_written_without_reporting_it()
    {
        var tracker = new ClaudeStreamCompletionTracker(TimeSpan.FromMinutes(1));
        tracker.TryRegisterSteer("s1", "text");

        tracker.UnregisterSteer("s1");

        tracker.TakeSteerResolutions().ShouldBeEmpty();
        tracker.CloseSteering().ShouldBeEmpty();
    }

    [Test]
    public async Task A_steer_racing_the_final_result_is_either_tracked_until_resolved_or_refused()
    {
        for (var round = 0; round < 200; round++)
        {
            var tracker = new ClaudeStreamCompletionTracker(TimeSpan.FromMinutes(1));
            tracker.Observe(Message("system", new { subtype = "init" }));
            var accepted = new ConcurrentBag<string>();

            var steering = Task.Run(() =>
            {
                for (var index = 0; index < 20; index++)
                {
                    var steerId = $"s{index}";
                    if (tracker.TryRegisterSteer(steerId, "text"))
                    {
                        accepted.Add(steerId);
                    }
                }
            });
            var completion = Task.Run(() => tracker.Observe(Message("result", new { result = "done" })));
            await Task.WhenAll(steering, completion);

            // A run that completed had no outstanding steer at that moment, and nothing was consumed, so every steer must
            // have been refused after the completion. An accepted steer is never dropped: closing reports all of them.
            if (completion.Result)
            {
                accepted.ShouldBeEmpty();
            }

            tracker.CloseSteering().Select(resolution => resolution.SteerId)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ShouldBe(accepted.OrderBy(id => id, StringComparer.Ordinal));
        }
    }

    [Test]
    public void Consumed_echo_of_a_steer_from_an_earlier_text_turn_is_matched_when_the_turn_order_is_result_then_echo()
    {
        // Mirrors the captured text-generation run: the steer is held by the CLI until a follow-up turn echoes it.
        var tracker = new ClaudeStreamCompletionTracker(TimeSpan.FromMinutes(1));
        tracker.RecordHostPromptSent("essay");
        tracker.Observe(Message("system", new { subtype = "init" }));
        tracker.TryRegisterSteer("s1", "lakes");
        tracker.Observe(PromptReplay("essay"));
        tracker.Observe(Message("result", new { result = "essay" })).ShouldBeFalse();

        tracker.Observe(Message("system", new { subtype = "init" })).ShouldBeFalse();
        tracker.Observe(SteerEcho("s1", "lakes"));
        tracker.Observe(Message("result", new { result = "lakes" })).ShouldBeTrue();
        tracker.TakeSteerResolutions().ShouldBe([new ClaudeSteerResolution("s1", ClaudeSteerOutcome.Consumed)]);
    }

    // The captured CLI 2.1.303 transcripts in Fixtures/ClaudeCode/steer back the assumptions above with real output.

    [Test]
    public void Captured_bash_steer_echo_carries_the_steer_uuid_and_is_consumed_before_the_turn_result()
    {
        var replay = ReplayFixture("bash-steer-during-tool");
        var stdin = ReadSteerWrite("bash-steer-during-tool");
        var tracker = new ClaudeStreamCompletionTracker(TimeSpan.FromMinutes(1), untimedBackgroundCommandTimeout: TimeSpan.FromMinutes(30));
        tracker.RecordHostPromptSent(stdin.PromptText);
        tracker.TryRegisterSteer(stdin.SteerUuid, stdin.SteerText);

        var echoes = new List<string>();
        var consumedAt = -1;
        for (var index = 0; index < replay.Count; index++)
        {
            var message = replay[index];
            var completed = tracker.Observe(message);
            if (tracker.LastMessageWasSteerEcho)
            {
                echoes.Add(message.Content.GetProperty("uuid").GetString()!);
                consumedAt = index;
            }

            if (completed)
            {
                index.ShouldBeGreaterThan(consumedAt, "the steer must be consumed before the run completes");
                break;
            }
        }

        echoes.ShouldBe([stdin.SteerUuid]);
        tracker.TakeSteerResolutions().ShouldBe([new ClaudeSteerResolution(stdin.SteerUuid, ClaudeSteerOutcome.Consumed)]);
    }

    [Test]
    public void Captured_text_steer_is_held_past_the_first_result_and_consumed_in_the_follow_up_turn()
    {
        var replay = ReplayFixture("text-steer-during-generation");
        var stdin = ReadSteerWrite("text-steer-during-generation");
        var tracker = new ClaudeStreamCompletionTracker(TimeSpan.FromMinutes(1));
        tracker.RecordHostPromptSent(stdin.PromptText);
        tracker.TryRegisterSteer(stdin.SteerUuid, stdin.SteerText);

        var completedAt = -1;
        var resultsSeen = 0;
        var steerEchoSeen = false;
        for (var index = 0; index < replay.Count; index++)
        {
            var message = replay[index];
            var isResult = string.Equals(message.Type, "result", StringComparison.Ordinal);
            if (isResult)
            {
                resultsSeen++;
            }

            var completed = tracker.Observe(message);
            steerEchoSeen |= tracker.LastMessageWasSteerEcho;
            if (isResult && resultsSeen == 1)
            {
                completed.ShouldBeFalse("the first result must not complete the run while the steer is unconsumed");
                tracker.CurrentWait.ShouldBe(ClaudeStreamCompletionTracker.PendingWait.SteerDelivery);
            }

            if (completed)
            {
                completedAt = index;
                break;
            }
        }

        steerEchoSeen.ShouldBeTrue();
        completedAt.ShouldBeGreaterThan(0);
        resultsSeen.ShouldBe(2);
        tracker.TakeSteerResolutions().ShouldBe([new ClaudeSteerResolution(stdin.SteerUuid, ClaudeSteerOutcome.Consumed)]);
    }

    [Test]
    public void Captured_steer_after_result_starts_a_turn_that_echoes_it_and_completes()
    {
        var replay = ReplayFixture("steer-after-result");
        var steer = ReadSteerWrite("steer-after-result");
        var tracker = new ClaudeStreamCompletionTracker(TimeSpan.FromMinutes(1));
        tracker.RecordHostPromptSent(steer.PromptText);
        var steerRegistered = false;

        var completed = false;
        foreach (var message in replay)
        {
            if (!steerRegistered && string.Equals(message.Type, "result", StringComparison.Ordinal))
            {
                // The host writes the steer as the first result arrives. Registering it before the tracker decides on that
                // result is what keeps the run open for the steer.
                tracker.TryRegisterSteer(steer.SteerUuid, steer.SteerText).ShouldBeTrue();
                steerRegistered = true;
                tracker.Observe(message).ShouldBeFalse();
                continue;
            }

            completed = tracker.Observe(message);
            if (completed)
            {
                break;
            }
        }

        completed.ShouldBeTrue();
        tracker.TakeSteerResolutions().ShouldBe([new ClaudeSteerResolution(steer.SteerUuid, ClaudeSteerOutcome.Consumed)]);
    }

    [Test]
    public void Steer_message_carries_the_uuid_priority_and_text_the_captured_cli_accepted()
    {
        var captured = ReadSteerWrite("bash-steer-during-tool");

        var wire = ClaudeCodeProvider.CreateSteerMessage(captured.SteerUuid, captured.SteerText, new ClaudeCodeOptions());

        wire.Type.ShouldBe("user");
        wire.Content.GetProperty("uuid").GetString().ShouldBe(captured.SteerUuid);
        wire.Content.GetProperty("priority").GetString().ShouldBe(captured.Priority);
        wire.Content.GetProperty("message").GetProperty("role").GetString().ShouldBe("user");
        wire.Content.GetProperty("message").GetProperty("content").GetString().ShouldBe(captured.SteerText);
    }

    private static CliMessage Message(string type, object payload)
    {
        return new CliMessage(type, JsonSerializer.SerializeToElement(payload));
    }

    private static CliMessage PromptReplay(string text)
    {
        return Replay(text, uuid: "cli-generated-prompt-uuid");
    }

    private static CliMessage SteerEcho(string uuid, string text)
    {
        return Replay(text, uuid);
    }

    private static CliMessage ReplayWithoutUuid(string text)
    {
        return new CliMessage("user", JsonSerializer.SerializeToElement(new
        {
            type = "user",
            isReplay = true,
            message = new { role = "user", content = text }
        }));
    }

    private static CliMessage Replay(string text, string uuid)
    {
        return new CliMessage("user", JsonSerializer.SerializeToElement(new
        {
            type = "user",
            isReplay = true,
            uuid,
            message = new { role = "user", content = text }
        }));
    }

    private static List<CliMessage> ReplayFixture(string name)
    {
        return File.ReadAllLines(FixturePath($"{name}.jsonl"))
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Select(static line =>
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement.Clone();
                return new CliMessage(root.GetProperty("type").GetString()!, root);
            })
            .ToList();
    }

    // The first host write of a capture is the prompt (no uuid); the write that carries a uuid is the steer.
    private static CapturedSteerWrite ReadSteerWrite(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath($"{name}.stdin.json")));
        var writes = document.RootElement.EnumerateArray().Select(static entry => entry.GetProperty("msg")).ToList();
        var prompt = writes.First(static msg => !msg.TryGetProperty("uuid", out _));
        var steer = writes.Single(static msg => msg.TryGetProperty("uuid", out _));
        return new CapturedSteerWrite(
            prompt.GetProperty("message").GetProperty("content").GetString()!,
            steer.GetProperty("uuid").GetString()!,
            steer.GetProperty("message").GetProperty("content").GetString()!,
            steer.GetProperty("priority").GetString()!);
    }

    private static string FixturePath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", "ClaudeCode", "steer", fileName);
    }

    private sealed record CapturedSteerWrite(string PromptText, string SteerUuid, string SteerText, string Priority);
}
