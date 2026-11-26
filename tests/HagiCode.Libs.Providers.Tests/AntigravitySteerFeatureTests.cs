// Feature 2A.6 `steer`: is a message sent during a running turn delivered to that turn.
//
// ACP has no interrupt, so the steer is a second session/prompt on the running session. Observed on agy 1.5.17 (2026-11-25):
// the first prompt ends with end_turn at the moment the steer arrives, the steered text runs as the next turn, and the run ends
// when that steered prompt ends. Probe logs: scratchpad agy-probe/steer3.jsonl.
// HERMETIC tests use the scripted agent (SYNTHETIC, not agy). They prove what the provider does with a steer: it is sent as a
// prompt on the same session, the run waits for it, and its outcome is reported. The scripted agent models the observed
// behaviour; it does not prove agy's own handling.
// REAL tests (Real_ prefix) need HAGICODE_REAL_CLI_TESTS=1 and agy on PATH, and skip with a reason otherwise.
using System.Collections.Concurrent;
using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Antigravity;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class AntigravitySteerFeatureTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task Prompt_sent_during_a_running_turn_is_written_while_the_first_turn_is_still_open()
    {
        var agent = new AntigravityScriptedAgent { SteerOpenTurns = true }
            .Enqueue(new AntigravityTurnScript { Frames = [AntigravityScriptedFrames.Chunk("Counting 1, ")] });
        await using var client = new AcpSessionClient(agent);
        await client.ConnectAsync();
        await client.InitializeAsync();
        var sessionId = (await client.StartSessionAsync(Directory.GetCurrentDirectory(), null, null)).SessionId;

        var streamed = new ConcurrentQueue<string>();
        using var readerCancellation = new CancellationTokenSource();
        var reader = ReadTextAsync(client, streamed, readerCancellation.Token);

        var firstTurn = client.SendPromptAsync(sessionId, "count to ten, one number per line");
        await WaitUntilAsync(() => streamed.Any(text => text.Contains("Counting 1,")));
        firstTurn.IsCompleted.ShouldBeFalse("the first turn must still be running when the steer is sent");

        var steer = await client.SendPromptAsync(sessionId, "switch to the word blue");
        var firstResult = await firstTurn.WaitAsync(WaitTimeout);

        agent.RequestsFor("session/prompt").Count.ShouldBe(2);
        firstResult.GetProperty("stopReason").GetString().ShouldBe("end_turn");
        steer.GetProperty("stopReason").GetString().ShouldBe("end_turn");
        await WaitUntilAsync(() => streamed.Any(text => text.Contains("[steered] switch to the word blue")));

        await readerCancellation.CancelAsync();
        await reader;
    }

    [Test]
    public async Task Steer_is_sent_as_a_second_prompt_and_the_run_ends_only_after_the_steered_prompt()
    {
        // The open turn ends at once when the steer arrives. The steered turn then answers ~1 s later, so the run must not end
        // at the first turn boundary, and the steered text must reach the caller before the run's terminal message.
        var agent = new AntigravityScriptedAgent { SteerOpenTurns = true }
            .Enqueue(
                new AntigravityTurnScript { Frames = [AntigravityScriptedFrames.Chunk("Counting 1, ")] },
                new AntigravityTurnScript
                {
                    Frames = [AntigravityScriptedFrames.Chunk("BLUE")],
                    Result = System.Text.Json.Nodes.JsonNode.Parse("""{"stopReason":"end_turn"}"""),
                    ResponseDelay = TimeSpan.FromSeconds(1)
                });
        var provider = new ScriptedAntigravityProvider(agent);
        var steering = new AntigravityRunSteering();
        var outcomes = new ConcurrentQueue<AntigravitySteerOutcomeEventArgs>();
        steering.OutcomeObserved += (_, args) => outcomes.Enqueue(args);

        var messages = new List<CliMessage>();
        var run = Task.Run(async () =>
        {
            await foreach (var message in provider.ExecuteAsync(new AntigravityOptions { Steering = steering }, "count to ten"))
            {
                messages.Add(message);
            }
        });

        var result = await SteerWhenStartedAsync(steering, "steer-1", "switch to the word blue");
        result.ShouldBe(AntigravitySteerResult.Sent);
        await run.WaitAsync(TimeSpan.FromSeconds(20));

        agent.RequestsFor("session/prompt").Count.ShouldBe(2);
        var text = AntigravityTestHelpers.TextOf(messages);
        text.ShouldContain("Counting 1, ");
        text.ShouldContain("[steered] switch to the word blue");
        text.ShouldContain("BLUE");
        messages.Count(message => message.Type == "terminal.completed").ShouldBe(1);
        messages[^1].Type.ShouldBe("terminal.completed");

        outcomes.Count.ShouldBe(1);
        outcomes.TryPeek(out var outcome).ShouldBeTrue();
        outcome.SteerId.ShouldBe("steer-1");
        outcome.Outcome.ShouldBe(AntigravitySteerOutcome.Consumed);
    }

    [Test]
    public async Task A_steer_whose_prompt_fails_is_reported_as_not_consumed()
    {
        var agent = new AntigravityScriptedAgent { SteerOpenTurns = true }
            .Enqueue(
                new AntigravityTurnScript { Frames = [AntigravityScriptedFrames.Chunk("Counting 1, ")] },
                AntigravityTurnScript.StopReason("cancelled"));
        var provider = new ScriptedAntigravityProvider(agent);
        var steering = new AntigravityRunSteering();
        var outcomes = new ConcurrentQueue<AntigravitySteerOutcomeEventArgs>();
        steering.OutcomeObserved += (_, args) => outcomes.Enqueue(args);

        var messages = new List<CliMessage>();
        var run = Task.Run(async () =>
        {
            await foreach (var message in provider.ExecuteAsync(new AntigravityOptions { Steering = steering }, "count to ten"))
            {
                messages.Add(message);
            }
        });

        (await SteerWhenStartedAsync(steering, "steer-2", "stop")).ShouldBe(AntigravitySteerResult.Sent);
        await run.WaitAsync(TimeSpan.FromSeconds(20));

        outcomes.Count.ShouldBe(1);
        outcomes.TryPeek(out var outcome).ShouldBeTrue();
        outcome.Outcome.ShouldBe(AntigravitySteerOutcome.NotConsumed);
        messages.Any(message => message.Type == "terminal.completed" || message.Type == "terminal.failed").ShouldBeTrue();
    }

    [Test]
    public async Task Steer_is_not_steerable_once_the_run_has_ended()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.EndTurn(AntigravityScriptedFrames.Chunk("Done.")));
        var provider = new ScriptedAntigravityProvider(agent);
        var steering = new AntigravityRunSteering();

        await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(new AntigravityOptions { Steering = steering }, "do it"));

        (await steering.SteerAsync("late", "too late")).ShouldBe(AntigravitySteerResult.NotSteerable);
        agent.RequestsFor("session/prompt").Count.ShouldBe(1, "a steer after the run ended must not write a prompt");
    }

    [Test]
    public async Task Steer_before_the_run_starts_and_empty_steer_text_are_never_written()
    {
        var agent = new AntigravityScriptedAgent { SteerOpenTurns = true }
            .Enqueue(new AntigravityTurnScript { Frames = [AntigravityScriptedFrames.Chunk("Counting 1, ")] });
        var provider = new ScriptedAntigravityProvider(agent);
        var steering = new AntigravityRunSteering();

        (await steering.SteerAsync("early", "before the run")).ShouldBe(AntigravitySteerResult.NotSteerable);

        var run = Task.Run(async () =>
        {
            await foreach (var _ in provider.ExecuteAsync(new AntigravityOptions { Steering = steering }, "count"))
            {
            }
        });
        await SteerWhenStartedAsync(steering, "blank-check", "placeholder");

        (await steering.SteerAsync("blank", "   ")).ShouldBe(AntigravitySteerResult.NotSteerable);
        agent.RequestsFor("session/prompt").Count.ShouldBe(2, "only the first prompt and the one real steer may be written");
        agent.SentFrames.Any(frame => frame["params"]?["prompt"]?[0]?["text"]?.GetValue<string>() == "   ").ShouldBeFalse();

        await run.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Test]
    public async Task Provider_sends_exactly_one_prompt_per_execution_so_it_never_steers_on_its_own()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.EndTurn(AntigravityScriptedFrames.Chunk("Done.")));
        var provider = new ScriptedAntigravityProvider(agent);

        await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "do it"));

        agent.RequestsFor("session/prompt").Count.ShouldBe(1);
    }

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task Real_prompt_during_a_running_turn_steers_that_turn()
    {
        var agyPath = AntigravityRealCli.RequireAgy();
        var workingDirectory = Directory.GetCurrentDirectory();
        await using var client = AntigravityRealCli.CreateSessionClient(agyPath, workingDirectory);
        await client.ConnectAsync();
        await client.InitializeAsync();
        var sessionId = (await client.StartSessionAsync(workingDirectory, null, AntigravityRealCli.RealModel)).SessionId;

        var streamed = new ConcurrentQueue<string>();
        using var readerCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var reader = ReadTextAsync(client, streamed, readerCancellation.Token);

        var firstTurn = client.SendPromptAsync(sessionId, "Count from 1 to 200, one number per line.");
        await WaitUntilAsync(() => !streamed.IsEmpty, TimeSpan.FromMinutes(2));
        firstTurn.IsCompleted.ShouldBeFalse("the first turn must still be running when the steer is sent");

        await client.SendPromptAsync(sessionId, "From now on answer only with the word BLUE and nothing else.");
        await firstTurn.WaitAsync(TimeSpan.FromMinutes(5));
        await Task.Delay(TimeSpan.FromSeconds(1));
        await readerCancellation.CancelAsync();
        await reader;

        string.Concat(streamed).ToLowerInvariant().ShouldContain("blue");
    }

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task Real_provider_steer_is_consumed_and_the_run_waits_for_it()
    {
        // Through the production provider, not the raw ACP client: the steer must come back as Consumed, and the run's
        // assistant text must carry the steered answer.
        var agyPath = AntigravityRealCli.RequireAgy();
        var workingDirectory = Directory.GetCurrentDirectory();
        var provider = AntigravityRealCli.CreateProvider();
        var steering = new AntigravityRunSteering();
        var outcomes = new ConcurrentQueue<AntigravitySteerOutcomeEventArgs>();
        steering.OutcomeObserved += (_, args) => outcomes.Enqueue(args);

        var options = new AntigravityOptions { ExecutablePath = agyPath, WorkingDirectory = workingDirectory, Model = AntigravityRealCli.RealModel, Steering = steering };
        var messages = new List<CliMessage>();
        using var runCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var run = Task.Run(async () =>
        {
            await foreach (var message in provider.ExecuteAsync(options, "Count from 1 to 200, one number per line.", runCancellation.Token))
            {
                lock (messages)
                {
                    messages.Add(message);
                }
            }
        });

        var result = await SteerWhenStartedAsync(steering, "real-steer-1", "From now on answer only with the word BLUE and nothing else.", TimeSpan.FromMinutes(2));
        result.ShouldBe(AntigravitySteerResult.Sent);
        await run.WaitAsync(TimeSpan.FromMinutes(5));

        outcomes.TryPeek(out var outcome).ShouldBeTrue("the steer outcome must be reported");
        outcome.Outcome.ShouldBe(AntigravitySteerOutcome.Consumed);
        string text;
        lock (messages)
        {
            text = AntigravityTestHelpers.TextOf(messages);
        }

        text.ToLowerInvariant().ShouldContain("blue");
    }

    private static async Task<AntigravitySteerResult> SteerWhenStartedAsync(
        AntigravityRunSteering steering,
        string steerId,
        string text,
        TimeSpan? timeout = null)
    {
        // The channel stays NotSteerable until the first prompt is running, so retry briefly while the run starts.
        var deadline = DateTime.UtcNow + (timeout ?? WaitTimeout);
        while (true)
        {
            var result = await steering.SteerAsync(steerId, text);
            if (result == AntigravitySteerResult.Sent || DateTime.UtcNow > deadline)
            {
                return result;
            }

            await Task.Delay(10);
        }
    }

    private static async Task ReadTextAsync(IAcpSessionClient client, ConcurrentQueue<string> streamed, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var notification in client.ReceiveNotificationsAsync(cancellationToken))
            {
                if (!string.Equals(notification.Method, "session/update", StringComparison.OrdinalIgnoreCase) ||
                    !notification.Parameters.TryGetProperty("update", out var update) ||
                    !update.TryGetProperty("content", out var content) ||
                    !content.TryGetProperty("text", out var text) ||
                    text.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                streamed.Enqueue(text.GetString()!);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? WaitTimeout);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            await Task.Delay(10);
        }
    }
}
