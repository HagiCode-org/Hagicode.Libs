using System.Collections.Concurrent;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.OpenCode;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Checks steering of a running OpenCode turn against the scripted fixture server: the steer request, its outcome events and the
/// turn's own messages.
/// </summary>
public sealed class OpenCodeProviderSteeringTests
{
    private const string SteerModel = "fixture/steer-model";
    private const string QuestionModel = "fixture/question-model";
    private static readonly TimeSpan SteerAwaitLimit = TimeSpan.FromSeconds(10);

    [Test]
    public async Task A_steer_reaches_the_active_turn_and_is_reported_consumed_once()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            SteerModelIds = [SteerModel],
            SteerScript = new OpenCodeFixtureSteerScript { StepDelay = TimeSpan.FromSeconds(1.5) },
        });
        await using var provider = new OpenCodeProvider(new CliExecutableResolver());
        var steering = new OpenCodeRunSteering();
        var outcomes = Observe(steering);

        var run = DrainAsync(provider.ExecuteAsync(SteerOptions(fixture, SteerModel, steering), "Start the work."));
        (await SteerWhenRunningAsync(steering, fixture, "msg_steer_one", "Also end with STEERED.")).ShouldBe(OpenCodeSteerResult.Sent);

        var messages = await run;
        fixture.SteerRequestIds.ShouldBe(["msg_steer_one"]);
        outcomes.ShouldBe([("msg_steer_one", OpenCodeSteerOutcome.Consumed)]);
        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
        messages.Single(static message => message.Type == "assistant").Content.GetProperty("text").GetString()
            .ShouldBe($"STEER-STEP-1{Environment.NewLine}STEER-STEP-2");
    }

    [Test]
    public async Task A_run_without_a_handle_sends_no_steer_and_yields_the_same_messages()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            SteerModelIds = [SteerModel],
        });
        await using var provider = new OpenCodeProvider(new CliExecutableResolver());

        var messages = await DrainAsync(provider.ExecuteAsync(Options(fixture, SteerModel), "Say hello."));

        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
        fixture.SteerRequestIds.ShouldBeEmpty();
    }

    [Test]
    public async Task A_steer_after_the_run_ended_is_not_steerable_and_sends_nothing()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            SteerModelIds = [SteerModel],
        });
        await using var provider = new OpenCodeProvider(new CliExecutableResolver());
        var steering = new OpenCodeRunSteering();
        var outcomes = Observe(steering);

        await DrainAsync(provider.ExecuteAsync(SteerOptions(fixture, SteerModel, steering), "Say hello."));
        var late = await steering.SteerAsync("msg_late", "too late");

        late.ShouldBe(OpenCodeSteerResult.NotSteerable);
        fixture.SteerRequestIds.ShouldBeEmpty();
        outcomes.ShouldBeEmpty();
    }

    [Test]
    public async Task A_server_refusal_is_not_steerable_and_reports_no_outcome()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            SteerModelIds = [SteerModel],
            SteerScript = new OpenCodeFixtureSteerScript { StepDelay = TimeSpan.FromSeconds(1.5), RefuseSteers = true },
        });
        await using var provider = new OpenCodeProvider(new CliExecutableResolver());
        var steering = new OpenCodeRunSteering();
        var outcomes = Observe(steering);

        var run = DrainAsync(provider.ExecuteAsync(SteerOptions(fixture, SteerModel, steering), "Start the work."));
        (await SteerWhenRunningAsync(steering, fixture, "msg_refused", "Refused text.")).ShouldBe(OpenCodeSteerResult.NotSteerable);

        var messages = await run;
        fixture.SteerRequestIds.ShouldBe(["msg_refused"]);
        outcomes.ShouldBeEmpty();
        messages.Last().Type.ShouldBe("terminal.completed");
    }

    [Test]
    public async Task A_request_that_succeeds_without_a_delivery_event_is_not_consumed()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            SteerModelIds = [SteerModel],
            SteerScript = new OpenCodeFixtureSteerScript { StepDelay = TimeSpan.FromSeconds(1), DeliverSteers = false },
        });
        await using var provider = new OpenCodeProvider(new CliExecutableResolver());
        var steering = new OpenCodeRunSteering();
        var outcomes = Observe(steering);

        var run = DrainAsync(provider.ExecuteAsync(SteerOptions(fixture, SteerModel, steering), "Start the work."));
        (await SteerWhenRunningAsync(steering, fixture, "msg_undelivered", "Never delivered.")).ShouldBe(OpenCodeSteerResult.Sent);
        await run;

        outcomes.ShouldBe([("msg_undelivered", OpenCodeSteerOutcome.NotConsumed)]);
    }

    [Test]
    public async Task A_deadline_resolves_the_steer_once_even_when_delivery_comes_later()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            SteerModelIds = [SteerModel],
            SteerScript = new OpenCodeFixtureSteerScript { StepDelay = TimeSpan.FromSeconds(1.5) },
        });
        await using var provider = new OpenCodeProvider(new CliExecutableResolver());
        var steering = new OpenCodeRunSteering();
        var outcomes = Observe(steering);

        var run = DrainAsync(provider.ExecuteAsync(SteerOptions(fixture, SteerModel, steering, TimeSpan.FromMilliseconds(200)), "Start the work."));
        (await SteerWhenRunningAsync(steering, fixture, "msg_deadline", "Late delivery.")).ShouldBe(OpenCodeSteerResult.Sent);
        await run;

        // The step boundary delivers the steer after its deadline: the outcome stays NotConsumed and is not reported a second time.
        outcomes.ShouldBe([("msg_deadline", OpenCodeSteerOutcome.NotConsumed)]);
    }

    [Test]
    public async Task Steers_with_equal_text_are_attributed_by_message_id_not_by_text()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            SteerModelIds = [SteerModel],
            SteerScript = new OpenCodeFixtureSteerScript { StepDelay = TimeSpan.FromSeconds(1.5) },
        });
        await using var provider = new OpenCodeProvider(new CliExecutableResolver());
        var steering = new OpenCodeRunSteering();
        var outcomes = Observe(steering);

        var run = DrainAsync(provider.ExecuteAsync(SteerOptions(fixture, SteerModel, steering), "same"));
        (await SteerWhenRunningAsync(steering, fixture, "msg_same_a", "same")).ShouldBe(OpenCodeSteerResult.Sent);
        (await SteerWhenRunningAsync(steering, fixture, "msg_same_b", "same")).ShouldBe(OpenCodeSteerResult.Sent);
        await run;

        outcomes.ShouldBe([("msg_same_a", OpenCodeSteerOutcome.Consumed), ("msg_same_b", OpenCodeSteerOutcome.Consumed)], ignoreOrder: true);
    }

    [Test]
    public async Task Cancelling_the_run_resolves_a_pending_steer_as_not_consumed_exactly_once()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            SteerModelIds = [SteerModel],
            SteerScript = new OpenCodeFixtureSteerScript { StepDelay = TimeSpan.FromSeconds(30) },
        });
        await using var provider = new OpenCodeProvider(new CliExecutableResolver());
        var steering = new OpenCodeRunSteering();
        var outcomes = Observe(steering);
        using var cancellation = new CancellationTokenSource();

        var run = DrainAsync(provider.ExecuteAsync(SteerOptions(fixture, SteerModel, steering), "Start the work.", cancellation.Token));
        (await SteerWhenRunningAsync(steering, fixture, "msg_cancelled", "Pending when cancelled.")).ShouldBe(OpenCodeSteerResult.Sent);
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => run);

        // The prompt task resolves the leftovers as its own last step, so the outcome can follow the exception.
        await WaitUntilAsync(() => outcomes.Count >= 1);
        await Task.Delay(100);
        outcomes.ShouldBe(new[] { ("msg_cancelled", OpenCodeSteerOutcome.NotConsumed) });
    }

    [Test]
    public async Task A_delivery_event_that_arrives_before_the_steer_response_still_reports_consumed()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            SteerModelIds = [SteerModel],
            SteerScript = new OpenCodeFixtureSteerScript { StepDelay = TimeSpan.FromSeconds(1.5), DeliverSteersImmediately = true },
        });
        await using var provider = new OpenCodeProvider(new CliExecutableResolver());
        var steering = new OpenCodeRunSteering();
        var outcomes = Observe(steering);

        var run = DrainAsync(provider.ExecuteAsync(SteerOptions(fixture, SteerModel, steering), "Start the work."));
        (await SteerWhenRunningAsync(steering, fixture, "msg_fast", "Delivered at once.")).ShouldBe(OpenCodeSteerResult.Sent);
        await run;

        // The event came during the request, so the steer was already registered when it arrived.
        outcomes.ShouldBe(new[] { ("msg_fast", OpenCodeSteerOutcome.Consumed) });
    }

    [Test]
    public async Task Steers_racing_the_end_of_the_turn_each_report_exactly_one_outcome_and_refused_ones_none()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            SteerModelIds = [SteerModel],
            SteerScript = new OpenCodeFixtureSteerScript { StepDelay = TimeSpan.FromMilliseconds(200) },
        });
        await using var provider = new OpenCodeProvider(new CliExecutableResolver());
        var steering = new OpenCodeRunSteering();
        var outcomes = Observe(steering);

        var run = DrainAsync(provider.ExecuteAsync(SteerOptions(fixture, SteerModel, steering), "Start the work."));
        var sent = new List<string>();
        for (var index = 0; index < 200 && !run.IsCompleted; index++)
        {
            var steerId = $"msg_race_{index}";
            if (await steering.SteerAsync(steerId, $"race {index}") == OpenCodeSteerResult.Sent)
            {
                sent.Add(steerId);
            }

            await Task.Delay(5);
        }

        await run;
        await WaitUntilAsync(() => outcomes.Count >= sent.Count);
        await Task.Delay(100);

        sent.ShouldNotBeEmpty();
        outcomes.Select(static outcome => outcome.SteerId).OrderBy(static id => id, StringComparer.Ordinal)
            .ShouldBe(sent.OrderBy(static id => id, StringComparer.Ordinal));
    }

    [Test]
    public async Task A_turn_that_fails_with_a_steer_pending_reports_it_not_consumed_once()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            SteerModelIds = [SteerModel],
            SteerScript = new OpenCodeFixtureSteerScript { StepDelay = TimeSpan.FromSeconds(1), DeliverSteers = false, FailTurn = true },
        });
        await using var provider = new OpenCodeProvider(new CliExecutableResolver());
        var steering = new OpenCodeRunSteering();
        var outcomes = Observe(steering);

        var run = DrainAsync(provider.ExecuteAsync(SteerOptions(fixture, SteerModel, steering), "Start the work."));
        (await SteerWhenRunningAsync(steering, fixture, "msg_failing", "Pending when the turn fails.")).ShouldBe(OpenCodeSteerResult.Sent);

        // A failed turn without readable text throws, as it does without steering. The steer is still resolved once.
        await Should.ThrowAsync<InvalidOperationException>(() => run);
        await WaitUntilAsync(() => outcomes.Count >= 1);
        await Task.Delay(100);
        outcomes.ShouldBe(new[] { ("msg_failing", OpenCodeSteerOutcome.NotConsumed) });
    }

    [Test]
    public async Task A_broken_event_stream_resolves_a_pending_steer_once_and_fails_the_run()
    {
        var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            SteerModelIds = [SteerModel],
            SteerScript = new OpenCodeFixtureSteerScript { StepDelay = TimeSpan.FromSeconds(30) },
        });
        await using var provider = new OpenCodeProvider(new CliExecutableResolver());
        var steering = new OpenCodeRunSteering();
        var outcomes = Observe(steering);

        var run = DrainAsync(provider.ExecuteAsync(SteerOptions(fixture, SteerModel, steering), "Start the work."));
        (await SteerWhenRunningAsync(steering, fixture, "msg_broken", "Pending when the stream breaks.")).ShouldBe(OpenCodeSteerResult.Sent);
        await fixture.DisposeAsync();

        Exception? error = null;
        try
        {
            await run;
        }
        catch (Exception exception)
        {
            error = exception;
        }

        error.ShouldNotBeNull();
        await WaitUntilAsync(() => outcomes.Count >= 1);
        outcomes.ShouldBe(new[] { ("msg_broken", OpenCodeSteerOutcome.NotConsumed) });
    }

    [Test]
    public async Task Stopping_the_stream_early_ends_the_steerable_turn_so_a_later_steer_sends_nothing()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            QuestionModelIds = [QuestionModel],
        });
        await using var provider = new OpenCodeProvider(new CliExecutableResolver());
        var steering = new OpenCodeRunSteering();
        var options = SteerOptions(fixture, QuestionModel, steering) with
        {
            UserInputHandler = static async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return null;
            },
        };

        // The consumer stops at the first question, while the turn is still running and the handle is attached.
        await foreach (var message in provider.ExecuteAsync(options, "Ask me a question."))
        {
            if (message.Type == OpenCodeUserInputRequest.MessageType)
            {
                break;
            }
        }

        // The turn's teardown ends steering before it closes the pending form, so once the form is closed a steer is refused.
        await WaitUntilAsync(() => fixture.GetForms().All(static form => form.State != OpenCodeFormStates.Pending));
        (await steering.SteerAsync("msg_after_stop", "Too late.")).ShouldBe(OpenCodeSteerResult.NotSteerable);
        fixture.SteerRequestIds.ShouldBeEmpty();
    }

    [Test]
    public async Task A_steer_sent_while_a_question_is_pending_is_not_steerable_and_leaves_the_form_pending()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            QuestionModelIds = [QuestionModel],
        });
        await using var provider = new OpenCodeProvider(new CliExecutableResolver());
        var steering = new OpenCodeRunSteering();
        var outcomes = Observe(steering);

        // The handler holds the question open until the test has checked the steer, so the form stays pending meanwhile.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = SteerOptions(fixture, QuestionModel, steering) with
        {
            UserInputHandler = async (_, _) =>
            {
                await release.Task;
                return null;
            },
        };

        var run = DrainAsync(provider.ExecuteAsync(options, "Ask me a question."));
        await WaitUntilAsync(() => fixture.GetForms().Any(static form => form.State == OpenCodeFormStates.Pending));

        (await steering.SteerAsync("msg_during_form", "Blocked by the question.")).ShouldBe(OpenCodeSteerResult.NotSteerable);
        fixture.SteerRequestIds.ShouldBeEmpty();
        outcomes.ShouldBeEmpty();
        fixture.GetForms().Count(static form => form.State == OpenCodeFormStates.Pending).ShouldBe(1);

        release.SetResult();
        await run;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + SteerAwaitLimit;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    private static OpenCodeOptions Options(OpenCodeFixtureServerHandle fixture, string model)
    {
        return new OpenCodeOptions
        {
            BaseUrl = fixture.BaseUri.ToString(),
            ServerPassword = fixture.ServerPassword,
            Model = model,
            AgentWorkWaitTimeout = TimeSpan.FromSeconds(30),
            AgentWorkSettleInterval = TimeSpan.FromMilliseconds(200),
        };
    }

    private static OpenCodeOptions SteerOptions(
        OpenCodeFixtureServerHandle fixture,
        string model,
        OpenCodeRunSteering steering,
        TimeSpan? deliveryTimeout = null)
    {
        return Options(fixture, model) with
        {
            Steering = steering,
            SteerDeliveryTimeout = deliveryTimeout ?? TimeSpan.FromSeconds(30),
        };
    }

    /// <summary>
    /// Sends a steer until the turn accepts it, or until the fixture has received it (a refusal is final). A steer refused before the
    /// turn is running sends nothing, so retrying is safe.
    /// </summary>
    private static async Task<OpenCodeSteerResult> SteerWhenRunningAsync(
        OpenCodeRunSteering steering,
        OpenCodeFixtureServerHandle fixture,
        string steerId,
        string text)
    {
        var deadline = DateTime.UtcNow + SteerAwaitLimit;
        while (true)
        {
            var result = await steering.SteerAsync(steerId, text);
            if (result == OpenCodeSteerResult.Sent || fixture.SteerRequestIds.Contains(steerId) || DateTime.UtcNow > deadline)
            {
                return result;
            }

            await Task.Delay(20);
        }
    }

    private static ConcurrentQueue<(string SteerId, OpenCodeSteerOutcome Outcome)> Observe(OpenCodeRunSteering steering)
    {
        var outcomes = new ConcurrentQueue<(string SteerId, OpenCodeSteerOutcome Outcome)>();
        steering.OutcomeObserved += (_, args) => outcomes.Enqueue((args.SteerId, args.Outcome));
        return outcomes;
    }

    private static async Task<List<CliMessage>> DrainAsync(IAsyncEnumerable<CliMessage> stream)
    {
        var messages = new List<CliMessage>();
        await foreach (var message in stream)
        {
            messages.Add(message);
        }

        return messages;
    }
}
