using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Providers.Codebuddy;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

// The steer rules of CodebuddySteerTracker (codebuddy-run-steering, tasks 3A.3 and 3A.6), with a scripted reply. No CLI is involved.
public sealed class CodebuddySteerTrackerTests
{
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task An_acknowledged_steer_that_is_injected_is_consumed_once()
    {
        var sent = new List<string>();
        var tracker = new CodebuddySteerTracker(Acknowledge(sent), AckTimeout);

        (await tracker.SteerAsync("steer-1", "text")).ShouldBe(CodebuddySteerResult.Sent);
        tracker.Observe(Echo("steer-1", "text")).ShouldBeTrue();

        var outcomes = tracker.Finish();
        outcomes.ShouldBe([("steer-1", CodebuddySteerOutcome.Consumed)]);
        sent.ShouldBe(["steer-1"]);
    }

    [Test]
    public async Task An_acknowledged_steer_that_is_never_injected_is_not_consumed_at_finish()
    {
        var tracker = new CodebuddySteerTracker(Acknowledge(), AckTimeout);

        (await tracker.SteerAsync("steer-1", "text")).ShouldBe(CodebuddySteerResult.Sent);

        tracker.Finish().ShouldBe([("steer-1", CodebuddySteerOutcome.NotConsumed)]);
    }

    [Test]
    public async Task A_refused_reply_is_not_steerable_and_gets_no_outcome()
    {
        var tracker = new CodebuddySteerTracker(Reply("""{"steered":false,"reason":"idle"}"""), AckTimeout);

        (await tracker.SteerAsync("steer-1", "text")).ShouldBe(CodebuddySteerResult.NotSteerable);

        tracker.Finish().ShouldBeEmpty();
    }

    [Test]
    public async Task An_injection_read_before_its_reply_is_consumed_once_the_steer_is_accepted()
    {
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tracker = new CodebuddySteerTracker((_, _, _) => reply.Task, AckTimeout);

        var steer = tracker.SteerAsync("steer-1", "text");
        tracker.Observe(Echo("steer-1", "text")).ShouldBeTrue();
        reply.SetResult(Steered(true));

        (await steer).ShouldBe(CodebuddySteerResult.Sent);
        tracker.Finish().ShouldBe([("steer-1", CodebuddySteerOutcome.Consumed)]);
    }

    [Test]
    public async Task Two_steers_with_identical_text_are_matched_by_their_own_ids()
    {
        var tracker = new CodebuddySteerTracker(Acknowledge(), AckTimeout);
        (await tracker.SteerAsync("steer-a", "same")).ShouldBe(CodebuddySteerResult.Sent);
        (await tracker.SteerAsync("steer-b", "same")).ShouldBe(CodebuddySteerResult.Sent);

        tracker.Observe(Echo("steer-b", "same"));

        tracker.Finish().ShouldBe(
        [
            ("steer-a", CodebuddySteerOutcome.NotConsumed),
            ("steer-b", CodebuddySteerOutcome.Consumed)
        ]);
    }

    [Test]
    public async Task A_steer_equal_to_the_prompt_is_not_matched_to_the_prompt_echo()
    {
        var tracker = new CodebuddySteerTracker(Acknowledge(), AckTimeout);
        (await tracker.SteerAsync("steer-1", "Reply with OK.")).ShouldBe(CodebuddySteerResult.Sent);

        // The prompt's own user message has a different id, so it stays conversation output and is not a steer echo.
        tracker.Observe(Echo("prompt-message", "Reply with OK.")).ShouldBeFalse();

        tracker.Finish().ShouldBe([("steer-1", CodebuddySteerOutcome.NotConsumed)]);
    }

    [Test]
    public async Task A_steer_after_the_window_closed_is_refused_and_never_written()
    {
        var sent = new List<string>();
        var tracker = new CodebuddySteerTracker(Acknowledge(sent), AckTimeout);
        tracker.CloseWindow();

        (await tracker.SteerAsync("steer-1", "text")).ShouldBe(CodebuddySteerResult.NotSteerable);

        sent.ShouldBeEmpty();
        tracker.Finish().ShouldBeEmpty();
    }

    [Test]
    public async Task A_steer_in_flight_when_the_window_closes_stays_tracked_once_acknowledged()
    {
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tracker = new CodebuddySteerTracker((_, _, _) => reply.Task, AckTimeout);

        var steer = tracker.SteerAsync("steer-1", "text");
        tracker.CloseWindow();
        reply.SetResult(Steered(true));

        (await steer).ShouldBe(CodebuddySteerResult.Sent);
        tracker.Finish().ShouldBe([("steer-1", CodebuddySteerOutcome.NotConsumed)]);
    }

    [Test]
    public async Task A_reply_that_never_arrives_is_abandoned_after_the_timeout_and_gets_no_outcome()
    {
        var tracker = new CodebuddySteerTracker(NeverReplies, TimeSpan.FromMilliseconds(50));

        (await tracker.SteerAsync("steer-1", "text")).ShouldBe(CodebuddySteerResult.NotSteerable);

        tracker.Finish().ShouldBeEmpty();
    }

    [Test]
    public async Task A_failed_write_is_not_steerable()
    {
        var tracker = new CodebuddySteerTracker((_, _, _) => throw new InvalidOperationException("write failed"), AckTimeout);

        (await tracker.SteerAsync("steer-1", "text")).ShouldBe(CodebuddySteerResult.NotSteerable);

        tracker.Finish().ShouldBeEmpty();
    }

    [Test]
    public async Task A_duplicate_steer_id_is_refused_without_a_second_write()
    {
        var sent = new List<string>();
        var tracker = new CodebuddySteerTracker(Acknowledge(sent), AckTimeout);
        (await tracker.SteerAsync("steer-1", "text")).ShouldBe(CodebuddySteerResult.Sent);

        (await tracker.SteerAsync("steer-1", "text")).ShouldBe(CodebuddySteerResult.NotSteerable);

        sent.ShouldBe(["steer-1"]);
    }

    [Test]
    public async Task An_acknowledged_steer_that_is_injected_after_the_timeout_stays_consumed()
    {
        var tracker = new CodebuddySteerTracker(Acknowledge(), TimeSpan.FromMilliseconds(30));
        (await tracker.SteerAsync("steer-1", "text")).ShouldBe(CodebuddySteerResult.Sent);

        await Task.Delay(TimeSpan.FromMilliseconds(120));
        tracker.Observe(Echo("steer-1", "text"));

        tracker.Finish().ShouldBe([("steer-1", CodebuddySteerOutcome.Consumed)]);
    }

    [Test]
    public async Task Finish_resolves_an_accepted_steer_not_consumed_when_the_run_is_cancelled()
    {
        var tracker = new CodebuddySteerTracker(Acknowledge(), AckTimeout);
        (await tracker.SteerAsync("steer-1", "text")).ShouldBe(CodebuddySteerResult.Sent);

        tracker.CloseWindow();

        tracker.Finish().ShouldBe([("steer-1", CodebuddySteerOutcome.NotConsumed)]);
    }

    [Test]
    public void Observe_ignores_a_user_message_whose_id_no_steer_registered()
    {
        var tracker = new CodebuddySteerTracker(Acknowledge(), AckTimeout);

        tracker.Observe(Echo("somebody-else", "text")).ShouldBeFalse();
        tracker.Observe(new AcpNotification("session/update", JsonSerializer.SerializeToElement(new { update = new { sessionUpdate = "agent_message_chunk" } }))).ShouldBeFalse();
    }

    private static Func<string, string, CancellationToken, Task<JsonElement>> Acknowledge(List<string>? sent = null)
    {
        return (steerId, _, _) =>
        {
            sent?.Add(steerId);
            return Task.FromResult(Steered(true));
        };
    }

    private static async Task<JsonElement> NeverReplies(string steerId, string text, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return default;
    }

    private static Func<string, string, CancellationToken, Task<JsonElement>> Reply(string json)
    {
        var element = JsonDocument.Parse(json).RootElement.Clone();
        return (_, _, _) => Task.FromResult(element);
    }

    private static JsonElement Steered(bool steered)
    {
        return JsonSerializer.SerializeToElement(new { steered });
    }

    private static AcpNotification Echo(string messageId, string text)
    {
        return new AcpNotification("session/update", JsonSerializer.SerializeToElement(new
        {
            sessionId = "session-1",
            update = new
            {
                sessionUpdate = "user_message_chunk",
                content = new { type = "text", text },
                _meta = new Dictionary<string, object?> { ["codebuddy.ai/messageId"] = messageId }
            }
        }));
    }
}
