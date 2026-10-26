using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Pi;

namespace HagiCode.Libs.Providers.Tests;

// Steering on Pi (pi-run-steering). The conversations come from Fixtures/Pi/steer-*.jsonl, which hold real pi 1.2.0 output
// recorded on 2026-10-26 (see PiRpcCapture.cs). ReplayPiProcess plays pi from those lines: it answers a command only once the
// host has written it, so a steer reply waits for the steer to be written, the way pi answers it. None of these tests needs pi,
// credentials or a model.
public sealed class PiSteeringTests
{
    private const string ToolCallSteer = "Also append the word STEERED-A to your final reply.";
    private const string DuplicateSteer = "Append the exact word DUP-OK to your final reply.";
    private const string TextStreamSteer = "Finish your answer with the exact word STEERED-B.";
    private const string ToolCallPrompt = "Use the bash tool exactly once to run: sleep 10; echo TOOL-DONE . Then reply with only the word FINISHED.";
    private const string SimplePrompt = "Reply with only the word OK.";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    private static readonly Func<PiUserInputRequest, CancellationToken, Task<PiUserInputResponse?>> NoAnswer =
        static (_, _) => Task.FromResult<PiUserInputResponse?>(null);

    // ---------------------------------------------------------------- PiRunSteering (2.1)

    [Test]
    public async Task SteerAsync_on_an_unattached_handle_is_not_steerable_and_raises_no_event()
    {
        var steering = new PiRunSteering();
        var events = new ConcurrentQueue<PiSteerOutcomeEventArgs>();
        steering.OutcomeObserved += (_, args) => events.Enqueue(args);

        var result = await steering.SteerAsync("steer-1", "hello");

        result.ShouldBe(PiSteerResult.NotSteerable);
        events.ShouldBeEmpty();
    }

    [Test]
    public async Task SteerAsync_with_an_empty_text_is_not_steerable_and_never_reaches_the_sink()
    {
        var steering = new PiRunSteering();
        var sinkCalls = 0;
        steering.Attach((_, _, _) =>
        {
            sinkCalls++;
            return Task.FromResult(PiSteerResult.Sent);
        });

        (await steering.SteerAsync("steer-1", "")).ShouldBe(PiSteerResult.NotSteerable);
        (await steering.SteerAsync("steer-2", "   ")).ShouldBe(PiSteerResult.NotSteerable);
        sinkCalls.ShouldBe(0);
    }

    [Test]
    public async Task SteerAsync_after_detach_is_not_steerable()
    {
        var steering = new PiRunSteering();
        steering.Attach((_, _, _) => Task.FromResult(PiSteerResult.Sent));
        steering.Detach();

        (await steering.SteerAsync("steer-1", "hello")).ShouldBe(PiSteerResult.NotSteerable);
    }

    // ---------------------------------------------------------------- options (2.2)

    [Test]
    public void SteerDeliveryTimeout_defaults_to_thirty_seconds()
    {
        new PiOptions().SteerDeliveryTimeout.ShouldBe(TimeSpan.FromSeconds(30));
        PiOptions.DefaultSteerDeliveryTimeout.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task ExecuteAsync_rejects_a_zero_steer_timeout_on_an_rpc_run_before_any_process_starts()
    {
        var provider = new ReplayProvider(new ReplayPiProcess(SimpleRunScript()));

        var exception = await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            CollectAsync(provider, RpcOptions(new PiRunSteering(), TimeSpan.Zero), SimplePrompt));

        exception.ParamName.ShouldBe("options");
        provider.RpcStarts.ShouldBe(0);
    }

    [Test]
    public async Task ExecuteAsync_rejects_a_negative_steer_timeout_on_an_rpc_run_before_any_process_starts()
    {
        var provider = new ReplayProvider(new ReplayPiProcess(SimpleRunScript()));

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            CollectAsync(provider, RpcOptions(new PiRunSteering(), TimeSpan.FromSeconds(-1)), SimplePrompt));

        provider.RpcStarts.ShouldBe(0);
    }

    [Test]
    public async Task ExecuteAsync_does_not_validate_the_steer_timeout_of_a_print_run()
    {
        var provider = new ReplayProvider(new ReplayPiProcess(SimpleRunScript()));
        var options = new PiOptions { Model = "mock-model", NoSession = true, SteerDeliveryTimeout = TimeSpan.Zero };

        var messages = await CollectAsync(provider, options, SimplePrompt);

        messages.Select(static message => message.Type).ShouldBe(["terminal.completed"]);
        provider.RpcStarts.ShouldBe(0);
    }

    // ---------------------------------------------------------------- protocol (2.3)

    [Test]
    public void BuildSteer_writes_one_line_of_json_with_the_steer_command_and_text()
    {
        var line = PiRpcProtocol.BuildSteer("hagicode-steer-1", "first line\nsecond line \u2028 with a separator");

        line.ShouldNotContain('\n');
        using var document = JsonDocument.Parse(line);
        document.RootElement.GetProperty("type").GetString().ShouldBe("steer");
        document.RootElement.GetProperty("id").GetString().ShouldBe("hagicode-steer-1");
        document.RootElement.GetProperty("message").GetString().ShouldBe("first line\nsecond line \u2028 with a separator");
    }

    [Test]
    public void TryReadSteerReply_accepts_the_recorded_replies()
    {
        var queued = FixtureLines("steer-tool-call").Single(static line => line.Contains("\"command\":\"steer\""));
        PiRpcProtocol.TryReadSteerReply(Parse(queued), out var accepted).ShouldBeTrue();
        accepted.RequestId.ShouldBe("hagicode-steer-1");
        accepted.Success.ShouldBeTrue();
        accepted.Disposition.ShouldBe("queued");

        var rejected = FixtureLines("steer-rejected-invalid").Where(static line => line.Contains("\"command\":\"steer\"")).First();
        PiRpcProtocol.TryReadSteerReply(Parse(rejected), out var refused).ShouldBeTrue();
        refused.Success.ShouldBeFalse();
        refused.Disposition.ShouldBeNull();
    }

    [Test]
    public void TryReadSteerReply_rejects_lookalike_records()
    {
        PiRpcProtocol.TryReadSteerReply(Parse("""{"id":"hagicode-steer-1","type":"response","command":"prompt","success":true,"data":{"disposition":"queued"}}"""), out _).ShouldBeFalse();
        PiRpcProtocol.TryReadSteerReply(Parse("""{"id":"steer-1","type":"response","command":"steer","success":true,"data":{"disposition":"queued"}}"""), out _).ShouldBeFalse();
        PiRpcProtocol.TryReadSteerReply(Parse("""{"id":"hagicode-steer-1","type":"response","command":"steer","success":"true","data":{"disposition":"queued"}}"""), out _).ShouldBeFalse();
        PiRpcProtocol.TryReadSteerReply(Parse("""{"id":"hagicode-steer-1","type":"event","command":"steer","success":true}"""), out _).ShouldBeFalse();
        PiRpcProtocol.TryReadSteerReply(Parse("""{"id":"hagicode-steer-1","type":"response","command":"steer"}"""), out _).ShouldBeFalse();
    }

    [Test]
    public void ReadUserMessageText_reads_the_injected_steer_from_the_recording()
    {
        var injection = FixtureLines("steer-tool-call")
            .Where(static line => line.Contains("\"message_start\"") && line.Contains("\"role\":\"user\""))
            .Select(Parse)
            .Select(PiRpcProtocol.ReadUserMessageText)
            .ToArray();

        injection.ShouldContain(ToolCallSteer);
        injection.ShouldContain(ToolCallPrompt);
    }

    [Test]
    public void ReadUserMessageText_rejects_lookalike_records()
    {
        PiRpcProtocol.ReadUserMessageText(Parse("""{"type":"message_start","message":{"role":"assistant","content":[{"type":"text","text":"hi"}]}}""")).ShouldBeNull();
        PiRpcProtocol.ReadUserMessageText(Parse("""{"type":"message_start","message":{"role":"user","content":[{"type":"text","text":"a"},{"type":"text","text":"b"}]}}""")).ShouldBeNull();
        PiRpcProtocol.ReadUserMessageText(Parse("""{"type":"message_start","message":{"role":"user","content":[{"type":"image","data":"x"}]}}""")).ShouldBeNull();
        PiRpcProtocol.ReadUserMessageText(Parse("""{"type":"turn_start"}""")).ShouldBeNull();
        PiRpcProtocol.ReadUserMessageText(Parse("""{"type":"message_end","message":{"role":"user","content":[{"type":"text","text":"hi"}]}}""")).ShouldBeNull();
    }

    // ---------------------------------------------------------------- tracker (3.2, 3.3, 3.4)

    [Test]
    public async Task Tracker_resolves_two_identical_steers_one_injection_at_a_time_in_order()
    {
        var steering = new PiRunSteering();
        var events = Observe(steering);
        var tracker = new PiSteerTracker(steering);
        tracker.Open("prompt");
        var first = Register(tracker, "s1", "same");
        var second = Register(tracker, "s2", "same");
        Answer(tracker, first, "queued");
        Answer(tracker, second, "queued");

        (await first.Reply.Task).ShouldBe(PiSteerResult.Sent);
        (await second.Reply.Task).ShouldBe(PiSteerResult.Sent);

        tracker.OnUserMessage("prompt");
        tracker.OnUserMessage("same");
        tracker.OnUserMessage("same");
        tracker.Finish();

        events.ShouldBe([("s1", PiSteerOutcome.Consumed), ("s2", PiSteerOutcome.Consumed)]);
    }

    [Test]
    public void Tracker_refuses_a_steer_after_agent_end_and_never_tracks_it()
    {
        var steering = new PiRunSteering();
        var events = Observe(steering);
        var tracker = new PiSteerTracker(steering);
        tracker.Open("prompt");
        tracker.CloseWindow();

        tracker.TryRegister("late", "too late", out var late).ShouldBeFalse();
        late.ShouldBeNull();

        tracker.Finish();
        events.ShouldBeEmpty();
    }

    [Test]
    public void Tracker_refuses_a_steer_id_that_an_earlier_steer_already_used()
    {
        var steering = new PiRunSteering();
        var tracker = new PiSteerTracker(steering);
        tracker.Open("prompt");

        tracker.TryRegister("s1", "first", out _).ShouldBeTrue();
        tracker.TryRegister("s1", "second", out _).ShouldBeFalse();
    }

    [Test]
    public async Task Tracker_finish_resolves_an_accepted_steer_not_consumed_and_an_unanswered_steer_not_steerable()
    {
        var steering = new PiRunSteering();
        var events = Observe(steering);
        var tracker = new PiSteerTracker(steering);
        tracker.Open("prompt");
        var accepted = Register(tracker, "accepted", "later");
        var unanswered = Register(tracker, "unanswered", "never answered");
        Answer(tracker, accepted, "queued");

        (await accepted.Reply.Task).ShouldBe(PiSteerResult.Sent);
        tracker.Finish();

        (await unanswered.Reply.Task).ShouldBe(PiSteerResult.NotSteerable);
        events.ShouldBe([("accepted", PiSteerOutcome.NotConsumed)]);
    }

    [Test]
    public async Task Tracker_reports_a_handled_steer_as_consumed_once_and_refuses_a_failed_one()
    {
        var steering = new PiRunSteering();
        var events = Observe(steering);
        var tracker = new PiSteerTracker(steering);
        tracker.Open("prompt");
        var handled = Register(tracker, "handled", "by a handler");
        var failed = Register(tracker, "failed", "refused");

        Answer(tracker, handled, "handled");
        Answer(tracker, failed, null, success: false);
        tracker.Finish();

        (await handled.Reply.Task).ShouldBe(PiSteerResult.Sent);
        (await failed.Reply.Task).ShouldBe(PiSteerResult.NotSteerable);
        events.ShouldBe([("handled", PiSteerOutcome.Consumed)]);
    }

    [Test]
    public async Task Tracker_reports_a_steer_injected_before_its_reply_as_consumed_once_when_the_reply_arrives()
    {
        var steering = new PiRunSteering();
        var events = Observe(steering);
        var tracker = new PiSteerTracker(steering);
        tracker.Open("prompt");
        var early = Register(tracker, "early", "fast");

        tracker.OnUserMessage("fast");
        events.ShouldBeEmpty();
        Answer(tracker, early, "queued");
        tracker.Finish();

        (await early.Reply.Task).ShouldBe(PiSteerResult.Sent);
        events.ShouldBe([("early", PiSteerOutcome.Consumed)]);
    }

    [Test]
    public void Tracker_never_matches_the_first_user_message_of_the_run_when_it_is_the_prompt()
    {
        var steering = new PiRunSteering();
        var events = Observe(steering);
        var tracker = new PiSteerTracker(steering);
        tracker.Open("same as the prompt");
        Register(tracker, "s1", "same as the prompt");

        tracker.OnUserMessage("same as the prompt");
        tracker.Finish();

        events.ShouldBe([]);
    }

    // ---------------------------------------------------------------- session on the recorded conversations (3.1, 3.2, 3.3, 3.4)

    [Test]
    public async Task A_steer_during_a_tool_call_is_sent_and_consumed_once()
    {
        var process = new ReplayPiProcess(FixtureLines("steer-tool-call"));
        var provider = new ReplayProvider(process);
        var steering = new PiRunSteering();
        var events = Observe(steering);
        var run = new ObservedRun(provider, RpcOptions(steering), ToolCallPrompt);

        await run.FirstToolCall.WaitAsync(TestTimeout);
        var result = await steering.SteerAsync("steer-1", ToolCallSteer).WaitAsync(TestTimeout);
        await run.Completion.WaitAsync(TestTimeout);

        result.ShouldBe(PiSteerResult.Sent);
        events.ShouldBe([("steer-1", PiSteerOutcome.Consumed)]);
        process.Written.Count(static line => line.Contains("\"type\":\"steer\"")).ShouldBe(1);
        process.Written.Single(static line => line.Contains("\"type\":\"steer\"")).ShouldContain("\"id\":\"hagicode-steer-1\"");
        run.Messages.Select(static message => message.Type).ShouldContain("tool.call");
    }

    [Test]
    public async Task A_steer_during_text_streaming_is_consumed_at_the_turn_boundary()
    {
        var process = new ReplayPiProcess(FixtureLines("steer-text-stream"));
        var provider = new ReplayProvider(process);
        var steering = new PiRunSteering();
        var events = Observe(steering);
        var run = new ObservedRun(provider, RpcOptions(steering), "Write about 250 words about rivers. Do not use any tools.");

        await run.FirstTextDelta.WaitAsync(TestTimeout);
        var result = await steering.SteerAsync("steer-1", TextStreamSteer).WaitAsync(TestTimeout);
        await run.Completion.WaitAsync(TestTimeout);

        result.ShouldBe(PiSteerResult.Sent);
        events.ShouldBe([("steer-1", PiSteerOutcome.Consumed)]);
    }

    [Test]
    public async Task Two_steers_with_identical_text_are_each_consumed_in_submission_order()
    {
        var process = new ReplayPiProcess(FixtureLines("steer-duplicate-text"));
        var provider = new ReplayProvider(process);
        var steering = new PiRunSteering();
        var events = Observe(steering);
        var run = new ObservedRun(provider, RpcOptions(steering), ToolCallPrompt);

        await run.FirstToolCall.WaitAsync(TestTimeout);
        (await steering.SteerAsync("steer-1", DuplicateSteer).WaitAsync(TestTimeout)).ShouldBe(PiSteerResult.Sent);
        (await steering.SteerAsync("steer-2", DuplicateSteer).WaitAsync(TestTimeout)).ShouldBe(PiSteerResult.Sent);
        await run.Completion.WaitAsync(TestTimeout);

        events.ShouldBe([("steer-1", PiSteerOutcome.Consumed), ("steer-2", PiSteerOutcome.Consumed)]);
    }

    [Test]
    public async Task A_steer_before_the_run_is_attached_is_not_steerable_and_writes_nothing()
    {
        var process = new ReplayPiProcess(FixtureLines("steer-tool-call"));
        var provider = new ReplayProvider(process);
        var steering = new PiRunSteering();

        (await steering.SteerAsync("steer-1", ToolCallSteer)).ShouldBe(PiSteerResult.NotSteerable);
        process.Written.ShouldBeEmpty();
        provider.RpcStarts.ShouldBe(0);
    }

    [Test]
    public async Task A_steer_after_the_run_ended_is_not_steerable_and_writes_nothing()
    {
        // A run that ends without waiting for a steer, so it has ended before the steer is attempted.
        var process = new ReplayPiProcess(SimpleRunScript());
        var provider = new ReplayProvider(process);
        var steering = new PiRunSteering();
        var run = new ObservedRun(provider, RpcOptions(steering), SimplePrompt);
        await run.Completion.WaitAsync(TestTimeout);
        var writtenBefore = process.Written.Count;

        (await steering.SteerAsync("steer-1", ToolCallSteer)).ShouldBe(PiSteerResult.NotSteerable);
        process.Written.Count.ShouldBe(writtenBefore);
    }

    [Test]
    public async Task A_steer_after_the_enumeration_was_abandoned_is_not_steerable()
    {
        var process = new ReplayPiProcess(FixtureLines("steer-tool-call"));
        var provider = new ReplayProvider(process);
        var steering = new PiRunSteering();
        var enumerator = provider.ExecuteAsync(RpcOptions(steering), ToolCallPrompt).GetAsyncEnumerator();
        (await enumerator.MoveNextAsync().AsTask().WaitAsync(TestTimeout)).ShouldBeTrue();

        await enumerator.DisposeAsync().AsTask().WaitAsync(TestTimeout);

        (await steering.SteerAsync("steer-1", ToolCallSteer)).ShouldBe(PiSteerResult.NotSteerable);
        process.Written.Count(static line => line.Contains("\"type\":\"steer\"")).ShouldBe(0);
    }

    [Test]
    public async Task A_prompt_that_was_handled_never_attaches_the_steering_channel()
    {
        var handled = new[]
        {
            """{"id":"hagicode-get-state","type":"response","command":"get_state","success":true,"data":{"sessionId":"fixture-session"}}""",
            """{"id":"hagicode-prompt","type":"response","command":"prompt","success":true,"data":{"disposition":"handled"}}"""
        };
        var process = new ReplayPiProcess(handled);
        var provider = new ReplayProvider(process);
        var steering = new PiRunSteering();
        await CollectAsync(provider, RpcOptions(steering), SimplePrompt);

        (await steering.SteerAsync("steer-1", "hello")).ShouldBe(PiSteerResult.NotSteerable);
        process.Written.Count(static line => line.Contains("\"type\":\"steer\"")).ShouldBe(0);
    }

    [Test]
    public async Task A_steer_that_pi_refuses_is_not_steerable_and_reports_no_outcome()
    {
        var script = FixtureLines("steer-tool-call")
            .Select(static line => line.Contains("\"command\":\"steer\"")
                ? """{"id":"hagicode-steer-1","type":"response","command":"steer","success":false,"error":"text.startsWith is not a function"}"""
                : line)
            .ToArray();
        var provider = new ReplayProvider(new ReplayPiProcess(script));
        var steering = new PiRunSteering();
        var events = Observe(steering);
        var run = new ObservedRun(provider, RpcOptions(steering), ToolCallPrompt);

        await run.FirstToolCall.WaitAsync(TestTimeout);
        (await steering.SteerAsync("steer-1", ToolCallSteer).WaitAsync(TestTimeout)).ShouldBe(PiSteerResult.NotSteerable);
        await run.Completion.WaitAsync(TestTimeout);

        events.ShouldBeEmpty();
    }

    [Test]
    public async Task A_prompt_that_was_rejected_never_attaches_the_steering_channel()
    {
        var rejected = new[]
        {
            """{"id":"hagicode-get-state","type":"response","command":"get_state","success":true,"data":{"sessionId":"fixture-session"}}""",
            """{"id":"hagicode-prompt","type":"response","command":"prompt","success":false,"error":"No API key found for mock."}"""
        };
        var process = new ReplayPiProcess(rejected);
        var provider = new ReplayProvider(process);
        var steering = new PiRunSteering();
        var messages = await CollectAsync(provider, RpcOptions(steering), SimplePrompt);

        messages.Select(static message => message.Type).ShouldContain("terminal.failed");
        (await steering.SteerAsync("steer-1", "hello")).ShouldBe(PiSteerResult.NotSteerable);
        process.Written.Count(static line => line.Contains("\"type\":\"steer\"")).ShouldBe(0);
    }

    [Test]
    public async Task A_steer_still_pending_when_pi_settles_is_not_consumed_at_once()
    {
        var script = FixtureLines("steer-tool-call")
            .Where(static line => PiRpcProtocol.ReadUserMessageText(Parse(line)) != ToolCallSteer)
            .ToArray();
        var provider = new ReplayProvider(new ReplayPiProcess(script));
        var steering = new PiRunSteering();
        var events = Observe(steering);
        var run = new ObservedRun(provider, RpcOptions(steering), ToolCallPrompt);

        await run.FirstToolCall.WaitAsync(TestTimeout);
        (await steering.SteerAsync("steer-1", ToolCallSteer).WaitAsync(TestTimeout)).ShouldBe(PiSteerResult.Sent);
        await run.Completion.WaitAsync(TestTimeout);

        events.ShouldBe([("steer-1", PiSteerOutcome.NotConsumed)]);
    }

    [Test]
    public async Task A_steer_whose_reply_does_not_arrive_in_time_is_not_steerable_and_not_tracked()
    {
        var script = FixtureLines("steer-tool-call")
            .Select(static line => line.Contains("\"command\":\"steer\"") ? ReplayPiProcess.Gate : line)
            .ToArray();
        var process = new ReplayPiProcess(script);
        var provider = new ReplayProvider(process);
        var steering = new PiRunSteering();
        var events = Observe(steering);
        var options = RpcOptions(steering) with { SteerDeliveryTimeout = TimeSpan.FromMilliseconds(200) };
        var run = new ObservedRun(provider, options, ToolCallPrompt);

        await run.FirstToolCall.WaitAsync(TestTimeout);
        (await steering.SteerAsync("steer-1", ToolCallSteer).WaitAsync(TestTimeout)).ShouldBe(PiSteerResult.NotSteerable);
        process.Release();
        await run.Completion.WaitAsync(TestTimeout);

        events.ShouldBeEmpty();
    }

    [Test]
    public async Task Pi_exiting_with_an_accepted_steer_pending_reports_not_consumed()
    {
        var lines = FixtureLines("steer-tool-call");
        var script = lines.Take(Array.FindIndex(lines, static line => line.Contains("\"tool_execution_end\""))).ToArray();
        var provider = new ReplayProvider(new ReplayPiProcess(script));
        var steering = new PiRunSteering();
        var events = Observe(steering);
        var run = new ObservedRun(provider, RpcOptions(steering), ToolCallPrompt);

        await run.FirstToolCall.WaitAsync(TestTimeout);
        (await steering.SteerAsync("steer-1", ToolCallSteer).WaitAsync(TestTimeout)).ShouldBe(PiSteerResult.Sent);
        await run.Completion.WaitAsync(TestTimeout);

        events.ShouldBe([("steer-1", PiSteerOutcome.NotConsumed)]);
    }

    [Test]
    public async Task Cancelling_the_run_with_an_accepted_steer_pending_reports_not_consumed()
    {
        var lines = FixtureLines("steer-tool-call");
        var script = lines.Take(Array.FindIndex(lines, static line => line.Contains("\"tool_execution_end\""))).Append(ReplayPiProcess.Gate).ToArray();
        var provider = new ReplayProvider(new ReplayPiProcess(script));
        var steering = new PiRunSteering();
        var events = Observe(steering);
        using var cancellation = new CancellationTokenSource();
        var run = new ObservedRun(provider, RpcOptions(steering), ToolCallPrompt, cancellation.Token);

        await run.FirstToolCall.WaitAsync(TestTimeout);
        (await steering.SteerAsync("steer-1", ToolCallSteer).WaitAsync(TestTimeout)).ShouldBe(PiSteerResult.Sent);
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => run.Completion.WaitAsync(TestTimeout));
        events.ShouldBe([("steer-1", PiSteerOutcome.NotConsumed)]);
    }

    [Test]
    public async Task Without_steer_traffic_a_run_with_a_channel_matches_a_run_without_one()
    {
        var script = FixtureLines("steer-tool-call")
            .Where(static line => !line.Contains("\"command\":\"steer\"") && PiRpcProtocol.ReadUserMessageText(Parse(line)) != ToolCallSteer)
            .ToArray();

        var withoutChannel = new ReplayPiProcess(script);
        var plain = await CollectAsync(new ReplayProvider(withoutChannel), RpcOptions(null), ToolCallPrompt);
        var withChannel = new ReplayPiProcess(script);
        var channelled = await CollectAsync(new ReplayProvider(withChannel), RpcOptions(new PiRunSteering()), ToolCallPrompt);

        channelled.Select(static message => message.Type).ShouldBe(plain.Select(static message => message.Type));
        withChannel.Written.ShouldBe(withoutChannel.Written);
    }

    [Test]
    public async Task An_rpc_command_line_is_identical_with_and_without_the_channel()
    {
        var withoutChannel = new ReplayProvider(new ReplayPiProcess(SimpleRunScript()));
        await CollectAsync(withoutChannel, RpcOptions(null), SimplePrompt);

        var withChannel = new ReplayProvider(new ReplayPiProcess(SimpleRunScript()));
        await CollectAsync(withChannel, RpcOptions(new PiRunSteering()), SimplePrompt);

        withChannel.RpcContext.ShouldNotBeNull();
        withChannel.RpcContext!.Arguments.ShouldBe(withoutChannel.RpcContext!.Arguments);
    }

    [Test]
    public async Task A_print_run_with_a_channel_stays_in_print_mode_and_is_not_steerable()
    {
        var withChannel = new ReplayProvider(new ReplayPiProcess([]));
        var steering = new PiRunSteering();
        var options = new PiOptions { Model = "mock-model", NoSession = true, Steering = steering };

        var messages = await CollectAsync(withChannel, options, SimplePrompt);

        messages.Select(static message => message.Type).ShouldBe(["terminal.completed"]);
        withChannel.RpcStarts.ShouldBe(0);
        withChannel.PrintContext.ShouldNotBeNull();
        (await steering.SteerAsync("steer-1", "hello")).ShouldBe(PiSteerResult.NotSteerable);

        var withoutChannel = new ReplayProvider(new ReplayPiProcess([]));
        await CollectAsync(withoutChannel, new PiOptions { Model = "mock-model", NoSession = true }, SimplePrompt);
        withChannel.PrintContext!.Arguments.ShouldBe(withoutChannel.PrintContext!.Arguments);
    }

    [Test]
    public async Task Steer_writes_and_the_question_answers_never_interleave_on_the_wire()
    {
        var process = new ReplayPiProcess([]);
        var writer = new PiRpcWriter(process);

        await Task.WhenAll(Enumerable.Range(0, 60).Select(index => writer.TrySendAsync(
            index % 2 == 0
                ? PiRpcProtocol.BuildSteer($"hagicode-steer-{index}", $"steer number {index} with a long body {new string('x', 200)}")
                : PiRpcProtocol.BuildDialogValue($"dialog-{index}", $"answer {index} with a long body {new string('y', 200)}"),
            CancellationToken.None)));

        process.Written.Count.ShouldBe(60);
        foreach (var line in process.Written)
        {
            line.ShouldNotContain('\n');
            using var document = JsonDocument.Parse(line);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static PiOptions RpcOptions(PiRunSteering? steering, TimeSpan? steerTimeout = null) => new()
    {
        Model = "mock-model",
        NoSession = true,
        UserInputHandler = NoAnswer,
        Steering = steering,
        SteerDeliveryTimeout = steerTimeout ?? PiOptions.DefaultSteerDeliveryTimeout
    };

    private static ConcurrentQueue<(string SteerId, PiSteerOutcome Outcome)> Observe(PiRunSteering steering)
    {
        var events = new ConcurrentQueue<(string SteerId, PiSteerOutcome Outcome)>();
        steering.OutcomeObserved += (_, args) => events.Enqueue((args.SteerId, args.Outcome));
        return events;
    }

    private static PiSteerTracker.PendingSteer Register(PiSteerTracker tracker, string steerId, string text)
    {
        tracker.TryRegister(steerId, text, out var steer).ShouldBeTrue();
        return steer!;
    }

    private static void Answer(PiSteerTracker tracker, PiSteerTracker.PendingSteer steer, string? disposition, bool success = true)
    {
        var reply = new SteerReply(steer.RequestId, success, disposition);
        tracker.OnReply(reply);
    }

    private static string[] FixtureLines(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Pi", name + ".jsonl");
        return File.ReadAllLines(path).Where(static line => !string.IsNullOrWhiteSpace(line)).ToArray();
    }

    private static JsonElement Parse(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }

    /// <summary>A run that ends on its own: get_state, the prompt started, one turn, agent_end and agent_settled.</summary>
    private static string[] SimpleRunScript() =>
    [
        .. PiRpcCapture.GetStateReply,
        """{"id":"hagicode-prompt","type":"response","command":"prompt","success":true,"data":{"disposition":"started"}}""",
        """{"type":"agent_start"}""",
        """{"type":"turn_start"}""",
        """{"type":"turn_end","message":{"role":"assistant","content":[{"type":"text","text":"OK"}],"provider":"mock","model":"mock-model","stopReason":"stop"},"toolResults":[]}""",
        """{"type":"agent_end","messages":[],"willRetry":false}""",
        """{"type":"agent_settled","aborted":false}"""
    ];

    private static async Task<List<CliMessage>> CollectAsync(
        PiProvider provider,
        PiOptions options,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<CliMessage>();
        await foreach (var message in provider.ExecuteAsync(options, prompt, cancellationToken))
        {
            messages.Add(message);
        }

        return messages;
    }

    /// <summary>
    /// Runs the provider on a background task, so the read loop keeps pulling while the test steers from its own thread. The first
    /// tool call and the first text delta are signalled as they arrive.
    /// </summary>
    private sealed class ObservedRun
    {
        private readonly List<CliMessage> _messages = [];
        private readonly TaskCompletionSource<CliMessage> _firstToolCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<CliMessage> _firstTextDelta = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ObservedRun(PiProvider provider, PiOptions options, string prompt, CancellationToken cancellationToken = default)
        {
            Completion = Task.Run(async () =>
            {
                await foreach (var message in provider.ExecuteAsync(options, prompt, cancellationToken))
                {
                    lock (_messages)
                    {
                        _messages.Add(message);
                    }

                    if (message.Type == "tool.call")
                    {
                        _firstToolCall.TrySetResult(message);
                    }

                    if (message.Type == "assistant")
                    {
                        _firstTextDelta.TrySetResult(message);
                    }
                }
            }, CancellationToken.None);
        }

        public Task Completion { get; }

        public Task FirstToolCall => _firstToolCall.Task;

        public Task FirstTextDelta => _firstTextDelta.Task;

        public IReadOnlyList<CliMessage> Messages
        {
            get
            {
                lock (_messages)
                {
                    return [.. _messages];
                }
            }
        }
    }

    private sealed class ReplayProvider(ReplayPiProcess process) : PiProvider(new StubExecutableResolver(), new CliProcessManager(), null, null)
    {
        public ProcessStartContext? RpcContext { get; private set; }

        public ProcessStartContext? PrintContext { get; private set; }

        public int RpcStarts { get; private set; }

        internal override ValueTask<IPiRpcProcess> StartRpcProcessAsync(ProcessStartContext startContext, CancellationToken cancellationToken)
        {
            RpcContext = startContext;
            RpcStarts++;
            return ValueTask.FromResult<IPiRpcProcess>(process);
        }

        internal override async IAsyncEnumerable<CliMessage> ExecuteProcessAsync(
            ProcessStartContext startContext,
            string? requestedSessionId,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            PrintContext = startContext;
            await Task.CompletedTask;
            yield return new CliMessage(
                "terminal.completed",
                JsonSerializer.SerializeToElement(new { type = "terminal.completed", text = "print path" }));
        }
    }

    /// <summary>
    /// Plays pi from recorded lines. A line that answers a command (an id starting with <c>hagicode-</c>) is held back until the host
    /// has written that command, and <see cref="Gate" /> holds the script until <see cref="Release" />. The script's end ends the
    /// stream, as pi's exit does.
    /// </summary>
    private sealed class ReplayPiProcess(IEnumerable<string> script) : IPiRpcProcess
    {
        public const string Gate = "<gate>";

        private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _standardError = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _script = [.. script];
        private readonly List<string> _written = [];
        private readonly HashSet<string> _writtenIds = [];
        private readonly object _gate = new();
        private int _cursor;
        private bool _released;
        private bool _inputClosed;

        public IReadOnlyList<string> Written
        {
            get
            {
                lock (_gate)
                {
                    return [.. _written];
                }
            }
        }

        public Task<string> StandardError => _standardError.Task;

        public void Release()
        {
            lock (_gate)
            {
                _released = true;
            }

            Pump();
        }

        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _output.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_inputClosed)
                {
                    throw new IOException("Broken pipe");
                }

                _written.Add(line);
                if (Parse(line).TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    _writtenIds.Add(id.GetString()!);
                }
            }

            Pump();
            return ValueTask.CompletedTask;
        }

        public void CloseInput()
        {
            lock (_gate)
            {
                _inputClosed = true;
            }

            _output.Writer.TryComplete();
            _standardError.TrySetResult(string.Empty);
            _exit.TrySetResult(0);
        }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);

        public Task StopAsync()
        {
            _output.Writer.TryComplete();
            _standardError.TrySetResult(string.Empty);
            _exit.TrySetResult(137);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void Pump()
        {
            lock (_gate)
            {
                while (_cursor < _script.Count)
                {
                    var line = _script[_cursor];
                    if (line == Gate)
                    {
                        if (!_released)
                        {
                            return;
                        }

                        _cursor++;
                        continue;
                    }

                    if (AwaitsWrite(line))
                    {
                        return;
                    }

                    _output.Writer.TryWrite(line);
                    _cursor++;
                }

                _output.Writer.TryComplete();
            }
        }

        private bool AwaitsWrite(string line)
        {
            var record = Parse(line);
            return PiRpcProtocol.ReadString(record, "type") == "response"
                   && PiRpcProtocol.ReadString(record, "id") is { } id
                   && id.StartsWith("hagicode-", StringComparison.Ordinal)
                   && !_writtenIds.Contains(id);
        }
    }

    private sealed class StubExecutableResolver : CliExecutableResolver
    {
        public override string? ResolveExecutablePath(string? executableName, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => executableName;

        public override string? ResolveFirstAvailablePath(IEnumerable<string> executableNames, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => executableNames.FirstOrDefault();
    }
}
