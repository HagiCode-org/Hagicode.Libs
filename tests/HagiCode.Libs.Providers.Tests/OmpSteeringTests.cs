using System.Text.Json;
using HagiCode.Libs.Providers.Omp;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

// The steering contract of OMP's rpc-ui mode, below the provider: the public channel, the wire commands and recognizers, and the
// steer tracker. The frames come from the probe captures (Fixtures/Omp/steer-*.jsonl, see OmpRpcCapture.cs). The replay of whole
// runs through the provider is in OmpProviderSteeringTests.
public sealed class OmpSteeringTests
{
    private const string Pineapple = "Also, end your answer with the single word PINEAPPLE.";
    private const string Prompt = "Count from 1 to 60, one number per line, with no other text.";

    // ---------------------------------------------------------------- the public channel (2.1)

    [Test]
    public async Task An_unattached_channel_answers_not_steerable_and_raises_no_outcome()
    {
        var steering = new OmpRunSteering();
        var observed = Observe(steering);

        (await steering.SteerAsync("s1", "hello")).ShouldBe(OmpSteerResult.NotSteerable);
        observed.ShouldBeEmpty();
    }

    [Test]
    public async Task A_blank_text_is_not_steerable_and_is_never_handed_to_the_attached_sink()
    {
        var steering = new OmpRunSteering();
        var handed = 0;
        steering.Attach((_, _, _) =>
        {
            handed++;
            return Task.FromResult(OmpSteerResult.Sent);
        });

        (await steering.SteerAsync("s1", "")).ShouldBe(OmpSteerResult.NotSteerable);
        (await steering.SteerAsync("s2", " \t\n")).ShouldBe(OmpSteerResult.NotSteerable);
        handed.ShouldBe(0);
    }

    [Test]
    public void A_steer_without_a_steer_id_is_an_argument_error()
    {
        var steering = new OmpRunSteering();

        Should.Throw<ArgumentException>(() => { steering.SteerAsync(" ", "hello"); });
    }

    [Test]
    public async Task An_attached_sink_receives_the_id_and_text_and_its_result_is_returned()
    {
        var steering = new OmpRunSteering();
        string? seenId = null;
        string? seenText = null;
        steering.Attach((id, text, _) =>
        {
            seenId = id;
            seenText = text;
            return Task.FromResult(OmpSteerResult.Sent);
        });

        (await steering.SteerAsync("s1", Pineapple)).ShouldBe(OmpSteerResult.Sent);
        seenId.ShouldBe("s1");
        seenText.ShouldBe(Pineapple);
    }

    [Test]
    public async Task A_detached_channel_is_not_steerable_again()
    {
        var steering = new OmpRunSteering();
        steering.Attach((_, _, _) => Task.FromResult(OmpSteerResult.Sent));
        steering.Detach();

        (await steering.SteerAsync("s1", "hello")).ShouldBe(OmpSteerResult.NotSteerable);
    }

    [Test]
    public void The_default_steer_delivery_timeout_is_thirty_seconds()
    {
        OmpOptions.DefaultSteerDeliveryTimeout.ShouldBe(TimeSpan.FromSeconds(30));
        new OmpOptions().SteerDeliveryTimeout.ShouldBe(TimeSpan.FromSeconds(30));
        new OmpOptions().Steering.ShouldBeNull();
    }

    // ---------------------------------------------------------------- wire commands and recognizers (3.1)

    [Test]
    public void BuildSteer_writes_one_line_with_the_probe_command_fields()
    {
        var line = OmpRpcProtocol.BuildSteer("hagicode-steer-1", Pineapple);

        line.Contains('\n').ShouldBeFalse();
        line.ShouldBe("{\"id\":\"hagicode-steer-1\",\"type\":\"steer\",\"message\":\"" + Pineapple + "\"}");
    }

    [Test]
    public void BuildSetInterruptMode_writes_the_wait_mode_command()
    {
        OmpRpcProtocol.BuildSetInterruptMode()
            .ShouldBe("{\"id\":\"hagicode-set-interrupt-mode\",\"type\":\"set_interrupt_mode\",\"mode\":\"wait\"}");
    }

    [Test]
    public void The_steer_reply_recognizer_accepts_every_steer_reply_in_the_fixtures_and_nothing_else()
    {
        var replies = new List<OmpSteerReply>();
        foreach (var frame in SteerFrames())
        {
            if (OmpRpcProtocol.TryReadSteerReply(frame, out var reply))
            {
                replies.Add(reply);
            }
        }

        replies.ShouldNotBeEmpty();
        replies.ShouldAllBe(static reply => reply.RequestId.StartsWith("hagicode-steer-", StringComparison.Ordinal));
        replies.Count(static reply => !reply.Success).ShouldBe(2, "steer-edge-cases answers a missing and a numeric message with success false");

        OmpRpcProtocol.TryReadSteerReply(
                Parse("{\"id\":\"hagicode-steer-1\",\"type\":\"response\",\"command\":\"steer\",\"success\":true}"),
                out var acceptedReply)
            .ShouldBeTrue();
        acceptedReply.ShouldBe(new OmpSteerReply("hagicode-steer-1", true));
    }

    [Test]
    public void The_steer_reply_recognizer_rejects_lookalikes()
    {
        // A reply to another command, a steer-prefixed id on another command, a missing success and a non-steer response.
        var lookalikes = new[]
        {
            "{\"id\":\"hagicode-steer-1\",\"type\":\"response\",\"command\":\"prompt\",\"success\":true}",
            "{\"id\":\"hagicode-prompt\",\"type\":\"response\",\"command\":\"steer\",\"success\":true}",
            "{\"id\":\"hagicode-steer-1\",\"type\":\"response\",\"command\":\"steer\"}",
            "{\"id\":\"hagicode-steer-1\",\"type\":\"event\",\"command\":\"steer\",\"success\":true}",
            "{\"id\":\"other-1\",\"type\":\"response\",\"command\":\"steer\",\"success\":true}"
        };

        foreach (var line in lookalikes)
        {
            OmpRpcProtocol.TryReadSteerReply(Parse(line), out _).ShouldBeFalse(line);
        }
    }

    [Test]
    public void The_steering_message_recognizer_reads_the_steered_message_start_only()
    {
        var texts = new List<string>();
        foreach (var frame in SteerFrames())
        {
            if (OmpRpcProtocol.ReadSteeringUserMessageText(frame) is { } text)
            {
                texts.Add(text);
            }
        }

        texts.ShouldContain(Pineapple);
        texts.ShouldContain("/help");
        texts.ShouldContain("Count from 1 to 60, one number per line, with no other text.", "a steer that equals the prompt is still flagged");

        // The prompt echo of the same run (no steering flag) and the message_end that repeats a steer are not steering messages.
        var promptEcho = Parse("{\"type\":\"message_start\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"Count from 1 to 60, one number per line, with no other text.\"}],\"attribution\":\"user\"},\"messageId\":\"msg-1\"}");
        OmpRpcProtocol.ReadSteeringUserMessageText(promptEcho).ShouldBeNull();

        var messageEnd = Parse("{\"type\":\"message_end\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"" + Pineapple + "\"}],\"steering\":true}}");
        OmpRpcProtocol.ReadSteeringUserMessageText(messageEnd).ShouldBeNull();
    }

    [Test]
    public void The_steering_message_recognizer_rejects_other_user_messages()
    {
        var assistant = Parse("{\"type\":\"message_start\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"x\"}],\"steering\":true}}");
        var twoBlocks = Parse("{\"type\":\"message_start\",\"message\":{\"role\":\"user\",\"steering\":true,\"content\":[{\"type\":\"text\",\"text\":\"a\"},{\"type\":\"text\",\"text\":\"b\"}]}}");
        var notText = Parse("{\"type\":\"message_start\",\"message\":{\"role\":\"user\",\"steering\":true,\"content\":[{\"type\":\"image\",\"data\":\"x\"}]}}");
        var notFlagged = Parse("{\"type\":\"message_start\",\"message\":{\"role\":\"user\",\"steering\":false,\"content\":[{\"type\":\"text\",\"text\":\"a\"}]}}");

        foreach (var frame in new[] { assistant, twoBlocks, notText, notFlagged })
        {
            OmpRpcProtocol.ReadSteeringUserMessageText(frame).ShouldBeNull();
        }
    }

    // ---------------------------------------------------------------- the steer tracker (3.2)

    [Test]
    public async Task A_steer_is_consumed_once_when_its_steering_message_arrives_after_the_reply()
    {
        var steering = new OmpRunSteering();
        var observed = Observe(steering);
        var tracker = new OmpSteerTracker(steering);
        tracker.TryOpen().ShouldBeTrue();

        tracker.TryRegister("s1", Pineapple, out var steer).ShouldBeTrue();
        tracker.OnReply(new OmpSteerReply(steer!.RequestId, true));
        (await steer.Reply.Task).ShouldBe(OmpSteerResult.Sent);
        observed.ShouldBeEmpty();

        tracker.OnSteeringMessage(Pineapple);
        tracker.Finish();
        tracker.OnSteeringMessage(Pineapple);

        observed.ShouldBe([("s1", OmpSteerOutcome.Consumed)]);
    }

    [Test]
    public async Task A_rejected_reply_refuses_the_steer_and_its_later_message_is_not_an_outcome()
    {
        var steering = new OmpRunSteering();
        var observed = Observe(steering);
        var tracker = new OmpSteerTracker(steering);
        tracker.TryOpen();
        tracker.TryRegister("s1", "hello", out var steer).ShouldBeTrue();

        tracker.OnReply(new OmpSteerReply(steer!.RequestId, false));
        (await steer.Reply.Task).ShouldBe(OmpSteerResult.NotSteerable);

        tracker.OnSteeringMessage("hello");
        tracker.Finish();
        observed.ShouldBeEmpty();
    }

    [Test]
    public async Task A_steering_message_read_before_its_reply_still_consumes_the_steer_once()
    {
        var steering = new OmpRunSteering();
        var observed = Observe(steering);
        var tracker = new OmpSteerTracker(steering);
        tracker.TryOpen();
        tracker.TryRegister("s1", "hello", out var steer).ShouldBeTrue();

        tracker.OnSteeringMessage("hello");
        observed.ShouldBeEmpty();

        tracker.OnReply(new OmpSteerReply(steer!.RequestId, true));
        (await steer.Reply.Task).ShouldBe(OmpSteerResult.Sent);
        tracker.Finish();

        observed.ShouldBe([("s1", OmpSteerOutcome.Consumed)]);
    }

    [Test]
    public async Task Two_steers_with_identical_text_are_consumed_one_per_message_in_send_order()
    {
        var steering = new OmpRunSteering();
        var observed = Observe(steering);
        var tracker = new OmpSteerTracker(steering);
        tracker.TryOpen();
        tracker.TryRegister("first", "SAME", out var first).ShouldBeTrue();
        tracker.TryRegister("second", "SAME", out var second).ShouldBeTrue();
        tracker.OnReply(new OmpSteerReply(first!.RequestId, true));
        tracker.OnReply(new OmpSteerReply(second!.RequestId, true));

        tracker.OnSteeringMessage("SAME");
        observed.ShouldBe([("first", OmpSteerOutcome.Consumed)]);

        tracker.OnSteeringMessage("SAME");
        observed.ShouldBe([("first", OmpSteerOutcome.Consumed), ("second", OmpSteerOutcome.Consumed)]);
        tracker.Finish();
        observed.Count.ShouldBe(2);
    }

    [Test]
    public async Task A_steer_whose_text_equals_the_prompt_is_consumed_only_by_its_own_flagged_message()
    {
        var steering = new OmpRunSteering();
        var observed = Observe(steering);
        var tracker = new OmpSteerTracker(steering);
        tracker.TryOpen();
        tracker.TryRegister("s1", Prompt, out var steer).ShouldBeTrue();
        tracker.OnReply(new OmpSteerReply(steer!.RequestId, true));
        (await steer.Reply.Task).ShouldBe(OmpSteerResult.Sent);

        // The prompt echo carries no steering flag, so the session never routes it to the tracker.
        var promptEcho = Parse("{\"type\":\"message_start\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"" + Prompt + "\"}]}}");
        OmpRpcProtocol.ReadSteeringUserMessageText(promptEcho).ShouldBeNull();
        observed.ShouldBeEmpty();

        var flagged = Parse("{\"type\":\"message_start\",\"message\":{\"role\":\"user\",\"steering\":true,\"content\":[{\"type\":\"text\",\"text\":\"" + Prompt + "\"}]}}");
        OmpRpcProtocol.ReadSteeringUserMessageText(flagged).ShouldBe(Prompt);
        tracker.OnSteeringMessage(Prompt);
        tracker.Finish();

        observed.ShouldBe([("s1", OmpSteerOutcome.Consumed)]);
    }

    [Test]
    public void A_steer_is_refused_before_the_window_opens_and_after_it_closes_without_being_tracked()
    {
        var steering = new OmpRunSteering();
        var tracker = new OmpSteerTracker(steering);

        tracker.TryRegister("early", "hello", out _).ShouldBeFalse();

        tracker.TryOpen().ShouldBeTrue();
        tracker.TryOpen().ShouldBeFalse("the window opens once");
        tracker.CloseWindow();

        tracker.TryRegister("late", "hello", out _).ShouldBeFalse();
        tracker.TryOpen().ShouldBeFalse("a closed window does not reopen for a background wake-up");
    }

    [Test]
    public void A_steer_with_an_id_already_used_in_the_execution_is_refused_and_the_first_stays_open()
    {
        var steering = new OmpRunSteering();
        var observed = Observe(steering);
        var tracker = new OmpSteerTracker(steering);
        tracker.TryOpen();

        tracker.TryRegister("dup", "first", out var first).ShouldBeTrue();
        tracker.TryRegister("dup", "second", out _).ShouldBeFalse();

        tracker.Finish();
        first!.Reply.Task.IsCompleted.ShouldBeTrue();
        observed.ShouldBeEmpty();
    }

    [Test]
    public async Task Finish_resolves_every_open_steer_once_and_closes_the_window()
    {
        var steering = new OmpRunSteering();
        var observed = Observe(steering);
        var tracker = new OmpSteerTracker(steering);
        tracker.TryOpen();

        tracker.TryRegister("accepted", "a", out var accepted).ShouldBeTrue();
        tracker.OnReply(new OmpSteerReply(accepted!.RequestId, true));

        tracker.TryRegister("injected", "b", out var injected).ShouldBeTrue();
        tracker.OnSteeringMessage("b");

        tracker.TryRegister("written", "c", out var written).ShouldBeTrue();

        tracker.Finish();
        tracker.Finish();

        observed.ShouldBe([("accepted", OmpSteerOutcome.NotConsumed), ("injected", OmpSteerOutcome.Consumed)]);
        (await accepted!.Reply.Task).ShouldBe(OmpSteerResult.Sent);
        (await injected!.Reply.Task).ShouldBe(OmpSteerResult.Sent);
        (await written!.Reply.Task).ShouldBe(OmpSteerResult.NotSteerable);
        tracker.TryRegister("after", "d", out _).ShouldBeFalse();
    }

    [Test]
    public void An_abandoned_steer_is_not_tracked_for_an_outcome()
    {
        var steering = new OmpRunSteering();
        var observed = Observe(steering);
        var tracker = new OmpSteerTracker(steering);
        tracker.TryOpen();
        tracker.TryRegister("s1", "hello", out var steer).ShouldBeTrue();

        tracker.Abandon(steer!);
        tracker.OnReply(new OmpSteerReply(steer!.RequestId, true));
        tracker.OnSteeringMessage("hello");
        tracker.Finish();

        observed.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- the fixtures (1.7)

    [Test]
    public void Every_steer_fixture_loads_and_each_of_its_frames_parses_as_one_json_object()
    {
        OmpRpcCapture.SteerFixtureNames.Count.ShouldBe(9);

        foreach (var name in OmpRpcCapture.SteerFixtureNames)
        {
            var lines = OmpRpcCapture.ReadFixture(name);
            lines.ShouldNotBeEmpty(name);
            foreach (var line in lines)
            {
                var frame = Parse(line);
                frame.ValueKind.ShouldBe(JsonValueKind.Object, name);
                frame.TryGetProperty("type", out _).ShouldBeTrue(name);
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private static List<(string SteerId, OmpSteerOutcome Outcome)> Observe(OmpRunSteering steering)
    {
        var observed = new List<(string SteerId, OmpSteerOutcome Outcome)>();
        steering.OutcomeObserved += (_, args) => observed.Add((args.SteerId, args.Outcome));
        return observed;
    }

    private static IEnumerable<JsonElement> SteerFrames()
    {
        foreach (var name in OmpRpcCapture.SteerFixtureNames)
        {
            foreach (var line in OmpRpcCapture.ReadFixture(name))
            {
                yield return Parse(line);
            }
        }
    }

    private static JsonElement Parse(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }
}
