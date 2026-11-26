// Feature 2A.6 `steer`, failure path: a steered prompt that fails after the first turn has ended must not look like a completed run.
// HERMETIC: uses the scripted agent (SYNTHETIC, not agy). The scripted agent ends the open turn when the steer arrives, then answers
// the steered prompt with an error, the shape of agy's quota failure (HTTP 429, RESOURCE_EXHAUSTED) seen on 2026-11-25.
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Antigravity;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class AntigravitySteerFailureFeatureTests
{
    [Test]
    public async Task A_steered_prompt_that_fails_after_the_first_turn_ended_is_reported_as_a_failure()
    {
        var agent = new AntigravityScriptedAgent { SteerOpenTurns = true }
            .Enqueue(
                new AntigravityTurnScript { Frames = [AntigravityScriptedFrames.Chunk("Counting 1, ")] },
                new AntigravityTurnScript { Error = JsonNode.Parse("{\"code\":429,\"message\":\"Individual quota reached\"}") });
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

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var result = await steering.SteerAsync("steer-failure", "stop");
            if (result == AntigravitySteerResult.Sent || DateTime.UtcNow > deadline)
            {
                break;
            }

            await Task.Delay(20);
        }

        await run.WaitAsync(TimeSpan.FromSeconds(20));

        outcomes.Count.ShouldBe(1);
        outcomes.TryPeek(out var outcome).ShouldBeTrue();
        outcome.Outcome.ShouldBe(AntigravitySteerOutcome.NotConsumed);
        messages.Any(message => message.Type == "terminal.failed").ShouldBeTrue();
        messages.Any(message => message.Type == "terminal.completed").ShouldBeFalse();
    }
}
