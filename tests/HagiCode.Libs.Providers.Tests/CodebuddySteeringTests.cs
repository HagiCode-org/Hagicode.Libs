using System.Collections.Concurrent;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Codebuddy;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

// Steering contract on CodeBuddy (codebuddy-run-steering, tasks 2.1 to 2.3). These tests cover the handle and the options only:
// no transport is attached by a run yet, so every steer here is answered by the handle itself or by a scripted sink.
public sealed class CodebuddySteeringTests
{
    private const string SimplePrompt = "Reply with only the word OK.";

    // A path that cannot exist: a run that gets past validation fails at process start, and never launches a real CodeBuddy.
    private const string MissingExecutable = "/nonexistent/codebuddy-steering-test";

    // ---------------------------------------------------------------- CodebuddyRunSteering (2.1)

    [Test]
    public async Task SteerAsync_on_an_unattached_handle_is_not_steerable_and_raises_no_event()
    {
        var steering = new CodebuddyRunSteering();
        var events = new ConcurrentQueue<CodebuddySteerOutcomeEventArgs>();
        steering.OutcomeObserved += (_, args) => events.Enqueue(args);

        var result = await steering.SteerAsync("steer-1", "hello");

        result.ShouldBe(CodebuddySteerResult.NotSteerable);
        events.ShouldBeEmpty();
    }

    [Test]
    public async Task SteerAsync_with_a_blank_text_is_not_steerable_and_never_reaches_the_sink()
    {
        var steering = new CodebuddyRunSteering();
        var sinkCalls = 0;
        steering.Attach((_, _, _) =>
        {
            Interlocked.Increment(ref sinkCalls);
            return Task.FromResult(CodebuddySteerResult.Sent);
        });

        (await steering.SteerAsync("steer-1", "")).ShouldBe(CodebuddySteerResult.NotSteerable);
        (await steering.SteerAsync("steer-2", "   \t")).ShouldBe(CodebuddySteerResult.NotSteerable);

        sinkCalls.ShouldBe(0);
    }

    [Test]
    public async Task SteerAsync_with_a_blank_id_throws()
    {
        var steering = new CodebuddyRunSteering();

        await Should.ThrowAsync<ArgumentException>(() => steering.SteerAsync(" ", "hello"));
        await Should.ThrowAsync<ArgumentException>(() => steering.SteerAsync("", "hello"));
    }

    [Test]
    public async Task Attach_then_detach_restores_not_steerable()
    {
        var steering = new CodebuddyRunSteering();
        steering.Attach((_, _, _) => Task.FromResult(CodebuddySteerResult.Sent));

        (await steering.SteerAsync("steer-1", "hello")).ShouldBe(CodebuddySteerResult.Sent);

        steering.Detach();

        (await steering.SteerAsync("steer-2", "hello")).ShouldBe(CodebuddySteerResult.NotSteerable);
    }

    [Test]
    public void Outcome_event_fires_once_per_report_with_the_steer_id_and_outcome()
    {
        var steering = new CodebuddyRunSteering();
        var events = new ConcurrentQueue<CodebuddySteerOutcomeEventArgs>();
        steering.OutcomeObserved += (_, args) => events.Enqueue(args);

        steering.Report("steer-1", CodebuddySteerOutcome.Consumed);
        steering.Report("steer-2", CodebuddySteerOutcome.NotConsumed);

        events.Count.ShouldBe(2);
        var all = events.ToArray();
        all[0].SteerId.ShouldBe("steer-1");
        all[0].Outcome.ShouldBe(CodebuddySteerOutcome.Consumed);
        all[1].SteerId.ShouldBe("steer-2");
        all[1].Outcome.ShouldBe(CodebuddySteerOutcome.NotConsumed);
    }

    // ---------------------------------------------------------------- CodebuddyOptions (2.2)

    [Test]
    public void SteerDeliveryTimeout_defaults_to_thirty_seconds()
    {
        new CodebuddyOptions().SteerDeliveryTimeout.ShouldBe(TimeSpan.FromSeconds(30));
        CodebuddyOptions.DefaultSteerDeliveryTimeout.ShouldBe(TimeSpan.FromSeconds(30));
        new CodebuddyOptions().Steering.ShouldBeNull();
    }

    [Test]
    public async Task ExecuteAsync_rejects_a_zero_steer_timeout_with_a_handle_before_any_process_starts()
    {
        var provider = CreateProvider();

        var exception = await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            CollectAsync(provider, new CodebuddyOptions
            {
                ExecutablePath = MissingExecutable,
                Steering = new CodebuddyRunSteering(),
                SteerDeliveryTimeout = TimeSpan.Zero
            }));

        exception.ParamName.ShouldBe("options");
    }

    [Test]
    public async Task ExecuteAsync_rejects_a_negative_steer_timeout_with_a_handle_before_any_process_starts()
    {
        var provider = CreateProvider();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            CollectAsync(provider, new CodebuddyOptions
            {
                ExecutablePath = MissingExecutable,
                Steering = new CodebuddyRunSteering(),
                SteerDeliveryTimeout = TimeSpan.FromSeconds(-1)
            }));
    }

    [Test]
    public async Task ExecuteAsync_does_not_validate_the_steer_timeout_without_a_handle()
    {
        var provider = CreateProvider();

        // The timeout is not validated without a handle, so the run reaches process start and fails there, not in validation.
        var exception = await CaptureAsync(() =>
            CollectAsync(provider, new CodebuddyOptions { ExecutablePath = MissingExecutable, SteerDeliveryTimeout = TimeSpan.Zero }));

        exception.ShouldNotBeNull();
        exception.ShouldNotBeOfType<ArgumentOutOfRangeException>();
    }

    // ---------------------------------------------------------------- no handle leaves the command line alone (2.3)

    [Test]
    public void BuildCommandArguments_is_identical_with_and_without_a_steering_handle()
    {
        var provider = CreateProvider();
        var withoutHandle = provider.BuildCommandArguments(new CodebuddyOptions { Model = "glm-4.7", ExtraArguments = ["--verbose"] });
        var withHandle = provider.BuildCommandArguments(new CodebuddyOptions
        {
            Model = "glm-4.7",
            ExtraArguments = ["--verbose"],
            Steering = new CodebuddyRunSteering()
        });

        withHandle.ShouldBe(withoutHandle);
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task<List<CliMessage>> CollectAsync(CodebuddyProvider provider, CodebuddyOptions options, string prompt = SimplePrompt)
    {
        var messages = new List<CliMessage>();
        await foreach (var message in provider.ExecuteAsync(options, prompt))
        {
            messages.Add(message);
        }

        return messages;
    }

    private static CodebuddyProvider CreateProvider()
    {
        return new CodebuddyProvider(new CliExecutableResolver(), new CliProcessManager());
    }
}
