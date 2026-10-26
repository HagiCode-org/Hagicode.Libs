using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Omp;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

// Steering on a whole OMP rpc-ui run, replayed from the probe captures (Fixtures/Omp/steer-*.jsonl, see OmpRpcCapture.cs). The
// scripted process plays the frames in the recorded order and holds the stream at chosen frames, so a steer is written at the same
// point of the run as in the capture. The steer's own reply and consumption come from the recording, and the replayed run must not
// need omp, credentials or a model.
public sealed class OmpProviderSteeringTests
{
    private const string Prompt = "Count from 1 to 60, one number per line, with no other text.";
    private const string Pineapple = "Also, end your answer with the single word PINEAPPLE.";
    private const string Late = "Also, end your answer with the single word LATE.";
    private const string Colour = "Which colour do you prefer?";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    private static readonly Func<OmpUserInputRequest, CancellationToken, Task<OmpUserInputResponse?>> NoAnswer =
        static (_, _) => Task.FromResult<OmpUserInputResponse?>(null);

    // ---------------------------------------------------------------- steering in a streamed text run (3.6, 3.8)

    [Test]
    public async Task A_steer_while_the_agent_streams_is_sent_consumed_once_and_leaves_the_message_stream_as_without_it()
    {
        var frames = Fixture("steer-text-stream");
        var gate = IndexOfLine(frames, IsQueueUpdateWithSteers);
        var process = new ReplayProcess(frames).HoldBefore(gate, "steer");
        var provider = new ReplayProvider(process);
        var steering = new OmpRunSteering();

        var run = Start(provider, Options(steering), Prompt);
        await process.WaitUntilHeldAsync(gate);
        (await steering.SteerAsync("s1", Pineapple)).ShouldBe(OmpSteerResult.Sent);
        await run.Completed;

        run.Failure.ShouldBeNull();
        run.Log.Where(static entry => entry.StartsWith("outcome:", StringComparison.Ordinal)).ToArray()
            .ShouldBe(["outcome:s1:Consumed"]);
        process.Written.Count(static line => line.Contains("\"type\":\"steer\"", StringComparison.Ordinal)).ShouldBe(1);

        var baselineProcess = new ReplayProcess(WithoutSteerFrames(frames));
        var baseline = Start(new ReplayProvider(baselineProcess), Options(), Prompt);
        await baseline.Completed;

        Describe(run.Messages).ShouldBe(Describe(baseline.Messages), "the steering message is not conversation output");
    }

    [Test]
    public async Task A_steer_during_a_tool_call_keeps_the_real_tool_result_because_the_wait_mode_is_set_before_the_prompt()
    {
        var frames = Fixture("steer-tool-call-wait");
        var gate = IndexOfLine(frames, IsQueueUpdateWithSteers);
        var process = new ReplayProcess(frames).HoldBefore(gate, "steer");
        var provider = new ReplayProvider(process);
        var steering = new OmpRunSteering();

        var run = Start(provider, Options(steering), "Use the bash tool to run exactly: sleep 8; echo TOOLDONE.");
        await process.WaitUntilHeldAsync(gate);
        (await steering.SteerAsync("s1", Pineapple)).ShouldBe(OmpSteerResult.Sent);
        await run.Completed;

        run.Failure.ShouldBeNull();
        run.Log.ShouldContain("outcome:s1:Consumed");
        Commands(process.Written).ShouldBe(["set_ask_dialog", "get_state", "set_interrupt_mode", "prompt", "steer"]);
        process.Written.Any(line => line.Contains("\"mode\":\"wait\"", StringComparison.Ordinal)).ShouldBeTrue();
        run.Messages.Any(message => message.Content.GetRawText().Contains("Wall time: 8.37 seconds", StringComparison.Ordinal)).ShouldBeTrue();
        run.Messages.Any(message => message.Content.GetRawText().Contains("Backgrounded early", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Test]
    public async Task A_steer_during_an_open_ask_leaves_the_dialog_answerable_and_is_consumed_after_the_answer()
    {
        var frames = Fixture("steer-ask-open");
        var gate = IndexOfLine(frames, IsQueueUpdateWithSteers);
        var process = new ReplayProcess(frames).HoldBefore(gate, "steer");
        var provider = new ReplayProvider(process);
        var steering = new OmpRunSteering();
        var answer = new OmpUserInputResponse(new Dictionary<string, IReadOnlyList<string>> { [Colour] = ["Blue"] });

        var run = Start(provider, Options(steering, handler: (_, _) => Task.FromResult<OmpUserInputResponse?>(answer)), "Ask me the colour.");
        await process.WaitForWriteAsync(line => line.Contains("\"extension_ui_response\"", StringComparison.Ordinal));
        await process.WaitUntilHeldAsync(gate);
        (await steering.SteerAsync("s1", Pineapple)).ShouldBe(OmpSteerResult.Sent);
        await run.Completed;

        run.Failure.ShouldBeNull();
        run.Log.ShouldContain("outcome:s1:Consumed");
        var dialogAnswers = process.Written.Where(static line => line.Contains("\"dialog-2\"", StringComparison.Ordinal)).ToArray();
        dialogAnswers.Length.ShouldBe(1);
        dialogAnswers[0].ShouldContain("Blue");
        process.Written.Any(static line => line.Contains("\"cancelled\"", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Test]
    public async Task A_steer_accepted_before_the_run_ends_but_never_taken_into_it_is_reported_not_consumed_before_the_terminal_message()
    {
        var frames = WithoutSteeringMessages(Fixture("steer-text-stream"));
        var gate = IndexOfLine(frames, IsQueueUpdateWithSteers);
        var process = new ReplayProcess(frames).HoldBefore(gate, "steer");
        var provider = new ReplayProvider(process);
        var steering = new OmpRunSteering();

        var run = Start(provider, Options(steering), Prompt);
        await process.WaitUntilHeldAsync(gate);
        (await steering.SteerAsync("s1", Pineapple)).ShouldBe(OmpSteerResult.Sent);
        await run.Completed;

        run.Failure.ShouldBeNull();
        var outcome = run.Log.IndexOf("outcome:s1:NotConsumed");
        outcome.ShouldBeGreaterThanOrEqualTo(0);
        outcome.ShouldBeLessThan(run.Log.FindLastIndex(static entry => entry.StartsWith("message:", StringComparison.Ordinal)));
    }

    // ---------------------------------------------------------------- refusals (3.3, 3.8)

    [Test]
    public async Task A_steer_before_the_run_is_connected_is_not_steerable_and_nothing_is_written()
    {
        var process = new ReplayProcess(Fixture("steer-text-stream"));
        var provider = new ReplayProvider(process);
        var steering = new OmpRunSteering();

        (await steering.SteerAsync("s1", Pineapple)).ShouldBe(OmpSteerResult.NotSteerable);
        process.Written.ShouldBeEmpty();

        await Start(provider, Options(steering), Prompt).Completed;
    }

    [Test]
    public async Task A_steer_after_the_yielding_agent_end_is_refused_and_never_written_even_with_background_work_pending()
    {
        var frames = Fixture("steer-after-agent-end-background");
        var gate = IndexOfFrame(frames, static frame => ReadType(frame) == "agent_end") + 1;
        var process = new ReplayProcess(frames).HoldBefore(gate, null);
        var provider = new ReplayProvider(process);
        var steering = new OmpRunSteering();

        var run = Start(provider, Options(steering), "Start the shell command in the background.");
        await process.WaitUntilHeldAsync(gate);
        (await steering.SteerAsync("s1", Late)).ShouldBe(OmpSteerResult.NotSteerable);
        process.Release(gate);
        await run.Completed;

        run.Failure.ShouldBeNull();
        process.Written.Any(static line => line.Contains("\"type\":\"steer\"", StringComparison.Ordinal)).ShouldBeFalse();
        run.Log.Any(static entry => entry.StartsWith("outcome:", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Test]
    public async Task A_steer_after_the_execution_settled_is_not_steerable_and_nothing_is_written()
    {
        var process = new ReplayProcess(Fixture("steer-after-settled"));
        var provider = new ReplayProvider(process);
        var steering = new OmpRunSteering();

        var run = Start(provider, Options(steering), Prompt);
        await run.Completed;

        (await steering.SteerAsync("s1", Pineapple)).ShouldBe(OmpSteerResult.NotSteerable);
        process.Written.Any(static line => line.Contains("\"type\":\"steer\"", StringComparison.Ordinal)).ShouldBeFalse();
        run.Log.Any(static entry => entry.StartsWith("outcome:", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Test]
    public async Task A_steer_after_the_enumeration_was_abandoned_is_not_steerable_and_nothing_is_written()
    {
        var process = new ReplayProcess(Fixture("steer-text-stream"));
        var provider = new ReplayProvider(process);
        var steering = new OmpRunSteering();

        await using (var enumerator = provider.ExecuteAsync(Options(steering), Prompt).GetAsyncEnumerator())
        {
            var seen = 0;
            while (seen < 2 && await enumerator.MoveNextAsync())
            {
                seen++;
            }
        }

        (await steering.SteerAsync("s1", Pineapple)).ShouldBe(OmpSteerResult.NotSteerable);
        process.Written.Any(static line => line.Contains("\"type\":\"steer\"", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Test]
    public async Task A_steer_whose_reply_never_arrives_is_not_steerable_after_the_timeout_and_its_later_consumption_is_no_outcome()
    {
        var frames = WithoutSteerReplies(Fixture("steer-text-stream"));
        var gate = IndexOfLine(frames, IsQueueUpdateWithSteers);
        var idleGate = IndexOfFrameAfter(frames, gate + 1, static frame => ReadType(frame) == "queue_update" && !HasSteers(frame));
        var process = new ReplayProcess(frames).HoldBefore(gate, "steer").HoldBefore(idleGate, null);
        var provider = new ReplayProvider(process);
        var steering = new OmpRunSteering();

        var run = Start(provider, Options(steering, timeout: TimeSpan.FromMilliseconds(300)), Prompt);
        await process.WaitUntilHeldAsync(gate);
        (await steering.SteerAsync("s1", Pineapple)).ShouldBe(OmpSteerResult.NotSteerable);
        process.Release(idleGate);
        await run.Completed;

        run.Failure.ShouldBeNull();
        run.Log.Any(static entry => entry.StartsWith("outcome:", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Test]
    public async Task A_steer_that_omp_refuses_is_not_steerable_and_its_later_message_is_no_outcome()
    {
        var frames = WithRefusedSteerReplies(Fixture("steer-text-stream"));
        var gate = IndexOfLine(frames, IsQueueUpdateWithSteers);
        var process = new ReplayProcess(frames).HoldBefore(gate, "steer");
        var provider = new ReplayProvider(process);
        var steering = new OmpRunSteering();

        var run = Start(provider, Options(steering), Prompt);
        await process.WaitUntilHeldAsync(gate);
        (await steering.SteerAsync("s1", Pineapple)).ShouldBe(OmpSteerResult.NotSteerable);
        await run.Completed;

        run.Failure.ShouldBeNull();
        run.Log.Any(static entry => entry.StartsWith("outcome:", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Test]
    public async Task A_prompt_that_is_rejected_or_finished_locally_never_attaches_the_steering_channel()
    {
        const string ready = "{\"type\":\"ready\",\"protocolVersion\":1,\"supportedProtocolVersions\":[1,2]}";
        const string askDialog = "{\"id\":\"hagicode-set-ask-dialog\",\"type\":\"response\",\"command\":\"set_ask_dialog\",\"success\":true,\"data\":{\"enabled\":true}}";
        const string state = "{\"id\":\"hagicode-get-state\",\"type\":\"response\",\"command\":\"get_state\",\"success\":true,\"data\":{\"sessionId\":\"s-1\"}}";
        var rejected = new[] { ready, askDialog, state, "{\"id\":\"hagicode-prompt\",\"type\":\"response\",\"command\":\"prompt\",\"success\":false,\"error\":\"no model\"}" };
        var finishedLocally = new[] { ready, askDialog, state, "{\"id\":\"hagicode-prompt\",\"type\":\"response\",\"command\":\"prompt\",\"success\":true,\"data\":{\"agentInvoked\":false}}" };

        foreach (var frames in new[] { rejected, finishedLocally })
        {
            var process = new ReplayProcess(frames);
            var provider = new ReplayProvider(process);
            var steering = new OmpRunSteering();

            var run = Start(provider, Options(steering), Prompt);
            await run.Completed;

            run.Failure.ShouldBeNull();
            (await steering.SteerAsync("s1", Pineapple)).ShouldBe(OmpSteerResult.NotSteerable);
            process.Written.Any(static line => line.Contains("\"type\":\"steer\"", StringComparison.Ordinal)).ShouldBeFalse();
        }
    }

    [Test]
    public async Task A_steer_still_waiting_for_its_reply_resolves_not_steerable_when_the_run_settles()
    {
        var frames = WithoutSteerReplies(WithoutSteeringMessages(Fixture("steer-text-stream")));
        var gate = IndexOfLine(frames, IsQueueUpdateWithSteers);
        var process = new ReplayProcess(frames).HoldBefore(gate, "steer");
        var provider = new ReplayProvider(process);
        var steering = new OmpRunSteering();

        var run = Start(provider, Options(steering, timeout: TimeSpan.FromSeconds(10)), Prompt);
        await process.WaitUntilHeldAsync(gate);
        var pending = steering.SteerAsync("s1", Pineapple);
        await run.Completed;

        (await pending.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(OmpSteerResult.NotSteerable);
        run.Log.Any(static entry => entry.StartsWith("outcome:", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Test]
    public async Task A_long_tool_call_longer_than_the_delivery_timeout_keeps_an_accepted_steer_open_until_it_is_taken()
    {
        var frames = Fixture("steer-tool-call-wait");
        var gate = IndexOfLine(frames, IsQueueUpdateWithSteers);
        var process = new ReplayProcess(frames)
            .HoldBefore(gate, "steer")
            .DelayBefore(IndexOfFrame(frames, static frame => ReadType(frame) == "tool_execution_end"), TimeSpan.FromMilliseconds(800));
        var provider = new ReplayProvider(process);
        var steering = new OmpRunSteering();

        var run = Start(provider, Options(steering, timeout: TimeSpan.FromMilliseconds(300)), "Use the bash tool to run exactly: sleep 8; echo TOOLDONE.");
        await process.WaitUntilHeldAsync(gate);
        (await steering.SteerAsync("s1", Pineapple)).ShouldBe(OmpSteerResult.Sent);
        await run.Completed;

        run.Log.ShouldContain("outcome:s1:Consumed");
        run.Log.ShouldNotContain("outcome:s1:NotConsumed");
    }

    [Test]
    public async Task Cancelling_the_run_with_an_accepted_steer_pending_reports_it_not_consumed()
    {
        var frames = Fixture("steer-text-stream");
        var steerGate = IndexOfLine(frames, IsQueueUpdateWithSteers);
        var idleGate = IndexOfFrameAfter(frames, steerGate + 1, static frame => ReadType(frame) == "queue_update" && !HasSteers(frame));
        var process = new ReplayProcess(frames)
            .HoldBefore(steerGate, "steer")
            .HoldBefore(idleGate, "abort");
        var provider = new ReplayProvider(process);
        var steering = new OmpRunSteering();
        using var cancellation = new CancellationTokenSource();

        var run = Start(provider, Options(steering), Prompt, cancellation.Token);
        await process.WaitUntilHeldAsync(steerGate);
        (await steering.SteerAsync("s1", Pineapple)).ShouldBe(OmpSteerResult.Sent);
        await process.WaitUntilHeldAsync(idleGate);
        await cancellation.CancelAsync();
        await run.Completed;

        (run.Failure is OperationCanceledException).ShouldBeTrue();
        run.Log.ShouldContain("outcome:s1:NotConsumed");
        process.Written.Any(static line => line.Contains("\"type\":\"abort\"", StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Test]
    public async Task A_rejected_write_makes_the_steer_not_steerable_and_no_outcome_is_reported()
    {
        var frames = Fixture("steer-text-stream");
        var gate = IndexOfLine(frames, IsQueueUpdateWithSteers);
        var process = new ReplayProcess(frames)
        {
            FailWrite = static line => line.Contains("\"type\":\"steer\"", StringComparison.Ordinal)
        }.HoldBefore(gate, "steer");
        var provider = new ReplayProvider(process);
        var steering = new OmpRunSteering();

        var run = Start(provider, Options(steering), Prompt);
        await process.WaitUntilHeldAsync(gate);
        (await steering.SteerAsync("s1", Pineapple)).ShouldBe(OmpSteerResult.NotSteerable);
        await run.Completed;

        run.Failure.ShouldBeNull();
        run.Log.Any(static entry => entry.StartsWith("outcome:", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Test]
    public async Task A_blank_steer_is_not_steerable_and_nothing_is_written()
    {
        var frames = Fixture("steer-text-stream");
        var gate = IndexOfLine(frames, IsQueueUpdateWithSteers);
        var process = new ReplayProcess(frames).HoldBefore(gate, null);
        var provider = new ReplayProvider(process);
        var steering = new OmpRunSteering();

        var run = Start(provider, Options(steering), Prompt);
        await process.WaitUntilHeldAsync(gate);
        (await steering.SteerAsync("s1", "   ")).ShouldBe(OmpSteerResult.NotSteerable);
        process.Release(gate);
        await run.Completed;

        process.Written.Any(static line => line.Contains("\"type\":\"steer\"", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Test]
    public async Task A_second_steer_with_an_id_already_used_is_refused_and_the_first_still_gets_its_outcome()
    {
        var frames = Fixture("steer-text-stream");
        var steerGate = IndexOfLine(frames, IsQueueUpdateWithSteers);
        var idleGate = IndexOfFrameAfter(frames, steerGate + 1, static frame => ReadType(frame) == "queue_update" && !HasSteers(frame));
        var process = new ReplayProcess(frames)
            .HoldBefore(steerGate, "steer")
            .HoldBefore(idleGate, null);
        var provider = new ReplayProvider(process);
        var steering = new OmpRunSteering();

        var run = Start(provider, Options(steering), Prompt);
        await process.WaitUntilHeldAsync(steerGate);
        (await steering.SteerAsync("dup", Pineapple)).ShouldBe(OmpSteerResult.Sent);
        await process.WaitUntilHeldAsync(idleGate);
        (await steering.SteerAsync("dup", Late)).ShouldBe(OmpSteerResult.NotSteerable);
        process.Release(idleGate);
        await run.Completed;

        process.Written.Count(static line => line.Contains("\"type\":\"steer\"", StringComparison.Ordinal)).ShouldBe(1);
        run.Log.Where(static entry => entry.StartsWith("outcome:", StringComparison.Ordinal)).ToArray()
            .ShouldBe(["outcome:dup:Consumed"]);
    }

    // ---------------------------------------------------------------- lifetime and reuse (3.3, 3.5)

    [Test]
    public async Task One_handle_steers_two_executions_in_turn_and_each_execution_reports_its_own_outcome()
    {
        var steering = new OmpRunSteering();
        var outcomes = new List<string>();
        steering.OutcomeObserved += (_, args) => outcomes.Add($"{args.SteerId}:{args.Outcome}");

        for (var round = 0; round < 2; round++)
        {
            var frames = Fixture("steer-text-stream");
            var gate = IndexOfLine(frames, IsQueueUpdateWithSteers);
            var process = new ReplayProcess(frames).HoldBefore(gate, "steer");
            var run = Start(new ReplayProvider(process), Options(steering), Prompt);
            await process.WaitUntilHeldAsync(gate);
            (await steering.SteerAsync("s1", Pineapple)).ShouldBe(OmpSteerResult.Sent);
            await run.Completed;
        }

        outcomes.ShouldBe(["s1:Consumed", "s1:Consumed"]);
    }

    [Test]
    public async Task A_steer_is_not_steerable_on_a_run_whose_ask_dialog_the_build_rejected_and_the_handle_is_never_attached()
    {
        var frames = new[]
        {
            "{\"type\":\"ready\",\"protocolVersion\":1,\"supportedProtocolVersions\":[1,2]}",
            "{\"id\":\"hagicode-set-ask-dialog\",\"type\":\"response\",\"command\":\"set_ask_dialog\",\"success\":false,\"error\":\"unknown command\"}"
        };
        var first = new ReplayProcess(frames);
        var provider = new ReplayProvider(first);
        var steering = new OmpRunSteering();

        var run = Start(provider, Options(steering), Prompt);
        await run.Completed;

        provider.PrintContexts.Count.ShouldBe(1, "the request reruns in JSON print mode");
        (await steering.SteerAsync("s1", Pineapple)).ShouldBe(OmpSteerResult.NotSteerable);
        first.Written.Any(static line => line.Contains("\"type\":\"steer\"", StringComparison.Ordinal)).ShouldBeFalse();
    }

    // ---------------------------------------------------------------- the one-shot path and the wire (4.1, 2.2, 3.4)

    [Test]
    public async Task A_json_print_run_with_a_handle_is_not_steerable_and_starts_no_rpc_process()
    {
        var process = new ReplayProcess([]);
        var provider = new ReplayProvider(process);
        var steering = new OmpRunSteering();

        // No user-input handler: the request runs in JSON print mode, and the handle is never attached.
        var run = Start(provider, new OmpOptions { Model = "mock-model", NoSession = true, Steering = steering }, Prompt);
        await run.Completed;

        provider.RpcStarts.ShouldBe(0);
        provider.PrintContexts.Count.ShouldBe(1);
        (await steering.SteerAsync("s1", Pineapple)).ShouldBe(OmpSteerResult.NotSteerable);
        process.Written.ShouldBeEmpty();
    }

    [Test]
    public async Task The_rpc_command_line_and_the_messages_are_the_same_with_and_without_a_handle_when_nothing_is_steered()
    {
        var frames = Fixture("steer-baseline-no-steer");

        var plainProcess = new ReplayProcess(frames);
        var plainProvider = new ReplayProvider(plainProcess);
        var plain = Start(plainProvider, Options(), Prompt);
        await plain.Completed;

        var steeredProcess = new ReplayProcess(frames);
        var steeredProvider = new ReplayProvider(steeredProcess);
        var steered = Start(steeredProvider, Options(new OmpRunSteering()), Prompt);
        await steered.Completed;

        steeredProvider.RpcContexts[0].Arguments.ShouldBe(plainProvider.RpcContexts[0].Arguments);
        Describe(steered.Messages).ShouldBe(Describe(plain.Messages));
        Commands(steeredProcess.Written).ShouldBe(["set_ask_dialog", "get_state", "set_interrupt_mode", "prompt"]);
        Commands(plainProcess.Written).ShouldBe(["set_ask_dialog", "get_state", "prompt"]);
    }

    [Test]
    public async Task Steer_writes_and_the_question_answer_never_interleave_on_the_wire()
    {
        var frames = Fixture("steer-ask-open");
        var gate = IndexOfLine(frames, IsQueueUpdateWithSteers);
        var idleGate = IndexOfFrameAfter(frames, gate + 1, static frame => ReadType(frame) == "queue_update" && !HasSteers(frame));
        var process = new ReplayProcess(frames) { WriteDelay = TimeSpan.FromMilliseconds(120) }
            .HoldBefore(gate, "steer")
            .HoldBefore(idleGate, null);
        var provider = new ReplayProvider(process);
        var steering = new OmpRunSteering();
        var answer = new OmpUserInputResponse(new Dictionary<string, IReadOnlyList<string>> { [Colour] = ["Red"] });

        // The answer waits until the steer write is in progress, so the two writes meet at the write gate.
        async Task<OmpUserInputResponse?> AnswerWhenSteerIsWritten(OmpUserInputRequest _, CancellationToken cancellationToken)
        {
            await process.SteerWriteStarted.Task.WaitAsync(TestTimeout, cancellationToken);
            return answer;
        }

        var run = Start(provider, Options(steering, handler: AnswerWhenSteerIsWritten), "Ask me the colour.");
        await process.WaitUntilHeldAsync(gate);
        (await steering.SteerAsync("s1", Pineapple)).ShouldBe(OmpSteerResult.Sent);
        await process.WaitForWriteAsync(static line => line.Contains("\"extension_ui_response\"", StringComparison.Ordinal));
        await process.WaitUntilHeldAsync(idleGate);
        process.Release(idleGate);
        await run.Completed;

        process.MaxConcurrentWrites.ShouldBe(1);
        foreach (var line in process.Written)
        {
            using var document = JsonDocument.Parse(line);
            document.RootElement.ValueKind.ShouldBe(JsonValueKind.Object);
        }

        process.Written.Any(static line => line.Contains("\"extension_ui_response\"", StringComparison.Ordinal)).ShouldBeTrue();
        process.Written.Any(static line => line.Contains("\"type\":\"steer\"", StringComparison.Ordinal)).ShouldBeTrue();
    }

    // ---------------------------------------------------------------- validation (2.2)

    [Test]
    public async Task A_zero_or_negative_delivery_timeout_on_an_rpc_run_with_a_handle_is_rejected_before_any_process_starts()
    {
        foreach (var timeout in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(-1) })
        {
            var process = new ReplayProcess(Fixture("steer-text-stream"));
            var provider = new ReplayProvider(process);

            await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
            {
                await foreach (var _ in provider.ExecuteAsync(Options(new OmpRunSteering(), timeout: timeout), Prompt))
                {
                }
            });

            provider.RpcStarts.ShouldBe(0);
        }
    }

    [Test]
    public async Task A_zero_delivery_timeout_without_a_handle_is_not_validated()
    {
        var provider = new ReplayProvider(new ReplayProcess([]));

        var messages = new List<CliMessage>();
        await foreach (var message in provider.ExecuteAsync(new OmpOptions { Model = "mock-model", NoSession = true, SteerDeliveryTimeout = TimeSpan.Zero }, Prompt))
        {
            messages.Add(message);
        }

        messages.ShouldNotBeEmpty();
        provider.RpcStarts.ShouldBe(0);
    }

    // ---------------------------------------------------------------- helpers

    private sealed class Run
    {
        public List<CliMessage> Messages { get; } = [];

        public List<string> Log { get; } = [];

        public Task Completed { get; set; } = Task.CompletedTask;

        public Exception? Failure { get; set; }
    }

    private static Run Start(OmpProvider provider, OmpOptions options, string prompt, CancellationToken cancellationToken = default)
    {
        var run = new Run();
        if (options.Steering is { } steering)
        {
            steering.OutcomeObserved += (_, args) => run.Log.Add($"outcome:{args.SteerId}:{args.Outcome}");
        }

        run.Completed = Task.Run(async () =>
        {
            try
            {
                await foreach (var message in provider.ExecuteAsync(options, prompt, cancellationToken))
                {
                    run.Messages.Add(message);
                    run.Log.Add("message:" + message.Type);
                }
            }
            catch (Exception ex)
            {
                run.Failure = ex;
            }
        });
        return run;
    }

    private static OmpOptions Options(
        OmpRunSteering? steering = null,
        TimeSpan? timeout = null,
        Func<OmpUserInputRequest, CancellationToken, Task<OmpUserInputResponse?>>? handler = null)
    {
        return new OmpOptions
        {
            Model = "mock-model",
            NoSession = true,
            UserInputHandler = handler ?? NoAnswer,
            Steering = steering,
            SteerDeliveryTimeout = timeout ?? OmpOptions.DefaultSteerDeliveryTimeout
        };
    }

    private static List<string> Fixture(string name) => OmpRpcCapture.ReadFixture(name).ToList();

    private static JsonElement Parse(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }

    private static string? ReadType(string line) => ReadType(Parse(line));

    private static string? ReadType(JsonElement frame) => OmpRpcProtocol.ReadString(frame, "type");

    private static bool IsQueueUpdateWithSteers(string line) => HasSteers(Parse(line)) && ReadType(line) == "queue_update";

    private static bool HasSteers(JsonElement frame)
    {
        return frame.TryGetProperty("steering", out var steers)
               && steers.ValueKind == JsonValueKind.Array
               && steers.GetArrayLength() > 0;
    }

    private static int IndexOfLine(IReadOnlyList<string> frames, Func<string, bool> predicate)
    {
        return IndexOfLineAfter(frames, 0, predicate);
    }

    private static int IndexOfFrame(IReadOnlyList<string> frames, Func<JsonElement, bool> predicate)
    {
        return IndexOfLineAfter(frames, 0, line => predicate(Parse(line)));
    }

    private static int IndexOfFrameAfter(IReadOnlyList<string> frames, int start, Func<JsonElement, bool> predicate)
    {
        return IndexOfLineAfter(frames, start, line => predicate(Parse(line)));
    }

    private static int IndexOfLineAfter(IReadOnlyList<string> frames, int start, Func<string, bool> predicate)
    {
        for (var index = start; index < frames.Count; index++)
        {
            if (predicate(frames[index]))
            {
                return index;
            }
        }

        throw new InvalidOperationException("The fixture has no frame that matches.");
    }

    /// <summary>The capture without the steer replies and the steering messages, as a run that was never steered.</summary>
    private static List<string> WithoutSteerFrames(IEnumerable<string> frames)
    {
        return frames.Where(static line =>
        {
            var frame = Parse(line);
            return ReadType(frame) != "queue_update"
                   && !OmpRpcProtocol.TryReadSteerReply(frame, out _)
                   && !IsSteeringMessageFrame(frame);
        }).ToList();
    }

    private static List<string> WithoutSteeringMessages(IEnumerable<string> frames)
    {
        return frames.Where(static line => !IsSteeringMessageFrame(Parse(line))).ToList();
    }

    private static List<string> WithRefusedSteerReplies(IEnumerable<string> frames)
    {
        return frames.Select(static line => OmpRpcProtocol.TryReadSteerReply(Parse(line), out var reply)
            ? JsonSerializer.Serialize(new { id = reply.RequestId, type = "response", command = "steer", success = false, error = "refused" })
            : line).ToList();
    }

    private static List<string> WithoutSteerReplies(IEnumerable<string> frames)
    {
        return frames.Where(static line => !OmpRpcProtocol.TryReadSteerReply(Parse(line), out _)).ToList();
    }

    private static bool IsSteeringMessageFrame(JsonElement frame)
    {
        return OmpRpcProtocol.ReadSteeringUserMessageText(frame) is not null
               || (ReadType(frame) is "message_start" or "message_end"
                   && frame.TryGetProperty("message", out var message)
                   && message.TryGetProperty("steering", out var steering)
                   && steering.ValueKind == JsonValueKind.True);
    }

    private static IReadOnlyList<string> Commands(IEnumerable<string> written)
    {
        return written.Select(static line => ReadType(line) ?? string.Empty).ToArray();
    }

    private static List<string> Describe(IEnumerable<CliMessage> messages)
    {
        return messages.Select(static message => message.Type + " " + message.Content.GetRawText()).ToList();
    }

    private sealed class Gate(string? releaseOnWriteOf)
    {
        public TaskCompletionSource<bool> Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? ReleaseOnWriteOf { get; } = releaseOnWriteOf;
    }

    /// <summary>
    /// Plays one capture: the frames in order, with gates that hold the stream before a frame until the host writes the command
    /// the gate names (or the test releases it), and optional delays. Every write is recorded as one whole line.
    /// </summary>
    private sealed class ReplayProcess(IReadOnlyList<string> frames) : IOmpRpcProcess
    {
        private readonly object _gate = new();
        private readonly Dictionary<int, Gate> _gates = [];
        private readonly Dictionary<int, TimeSpan> _delays = [];
        private readonly List<string> _written = [];
        private int _next;
        private int _inFlight;
        private int _maxInFlight;

        public Func<string, bool>? FailWrite { get; init; }

        public TimeSpan WriteDelay { get; init; }

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

        public int MaxConcurrentWrites => Volatile.Read(ref _maxInFlight);

        public Task<string> StandardError => Task.FromResult(string.Empty);

        /// <summary>Completes when the host starts to write a steer, before the write takes effect.</summary>
        public TaskCompletionSource<bool> SteerWriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ReplayProcess HoldBefore(int index, string? releaseOnWriteOf)
        {
            _gates[index] = new Gate(releaseOnWriteOf);
            return this;
        }

        public ReplayProcess DelayBefore(int index, TimeSpan delay)
        {
            _delays[index] = delay;
            return this;
        }

        public Task WaitUntilHeldAsync(int index) => _gates[index].Reached.Task.WaitAsync(TestTimeout);

        public void Release(int index) => _gates[index].Release.TrySetResult(true);

        public async Task WaitForWriteAsync(Func<string, bool> predicate)
        {
            var deadline = DateTime.UtcNow + TestTimeout;
            while (!Written.Any(predicate))
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("The host did not write the expected command.");
                }

                await Task.Delay(10);
            }
        }

        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            int index;
            lock (_gate)
            {
                index = _next;
            }

            if (index >= frames.Count)
            {
                return null;
            }

            if (_gates.TryGetValue(index, out var gate))
            {
                gate.Reached.TrySetResult(true);
                await gate.Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_delays.TryGetValue(index, out var delay))
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            lock (_gate)
            {
                _next = index + 1;
            }

            return frames[index];
        }

        public async ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            var inFlight = Interlocked.Increment(ref _inFlight);
            try
            {
                lock (_gate)
                {
                    _maxInFlight = Math.Max(_maxInFlight, inFlight);
                }

                ReleaseGatesFor(ReadType(line));
                if (ReadType(line) == "steer")
                {
                    SteerWriteStarted.TrySetResult(true);
                }

                if (WriteDelay > TimeSpan.Zero)
                {
                    await Task.Delay(WriteDelay, cancellationToken).ConfigureAwait(false);
                }

                if (FailWrite?.Invoke(line) == true)
                {
                    throw new IOException("omp closed its input");
                }

                lock (_gate)
                {
                    _written.Add(line);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public void CloseInput()
        {
        }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => Task.FromResult(0);

        public Task StopAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void ReleaseGatesFor(string? type)
        {
            lock (_gate)
            {
                foreach (var gate in _gates.Values)
                {
                    if (gate.ReleaseOnWriteOf is not null && gate.ReleaseOnWriteOf == type)
                    {
                        gate.Release.TrySetResult(true);
                    }
                }
            }
        }
    }

    private sealed class ReplayProvider(params ReplayProcess[] processes) : OmpProvider(new StubExecutableResolver(), new CliProcessManager())
    {
        private readonly Queue<ReplayProcess> _processes = new(processes);

        public List<ProcessStartContext> RpcContexts { get; } = [];

        public List<ProcessStartContext> PrintContexts { get; } = [];

        public int RpcStarts => RpcContexts.Count;

        internal override ValueTask<IOmpRpcProcess> StartRpcProcessAsync(ProcessStartContext startContext, CancellationToken cancellationToken)
        {
            RpcContexts.Add(startContext);
            return ValueTask.FromResult<IOmpRpcProcess>(_processes.Dequeue());
        }

        internal override async IAsyncEnumerable<CliMessage> ExecuteProcessAsync(
            ProcessStartContext startContext,
            string? requestedSessionId,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            PrintContexts.Add(startContext);
            await Task.CompletedTask;
            yield return new CliMessage(
                "terminal.completed",
                JsonSerializer.SerializeToElement(new { type = "terminal.completed", text = "print path" }));
        }

        internal override Task<string?> ReadVersionAsync(ProcessStartContext startContext, CancellationToken cancellationToken)
            => Task.FromResult<string?>("omp/0.0.0-test");
    }

    private sealed class StubExecutableResolver : CliExecutableResolver
    {
        public override string? ResolveExecutablePath(string? executableName, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => executableName;

        public override string? ResolveFirstAvailablePath(IEnumerable<string> executableNames, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => executableNames.FirstOrDefault();
    }
}
