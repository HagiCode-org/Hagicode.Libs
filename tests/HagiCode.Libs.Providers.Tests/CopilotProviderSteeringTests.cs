using System.Reflection;
using System.Text.Json.Serialization;
using GitHub.Copilot;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Copilot;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

// Copilot requests rewrite process-wide environment variables, so these classes must not overlap with other Copilot tests.
[NonParallelizable]
public sealed class CopilotProviderSteeringTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    // A 1x1 PNG, used only to check that the original message images are not sent again with a steer.
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    [Test]
    public async Task Steer_while_the_turn_runs_is_sent_immediate_as_text_only_and_consumed_once_on_its_echo()
    {
        using var harness = new SteerHarness();
        var run = harness.Stream(new CopilotOptions());
        var session = await harness.TurnStartedAsync();

        (await harness.Steering.SteerAsync("s1", "use the other file")).ShouldBe(CopilotSteerResult.Sent);

        var steer = session.SteerMessages.ShouldHaveSingleItem();
        steer.Prompt.ShouldBe("use the other file");
        steer.Mode.ShouldBe("immediate");
        steer.Attachments.ShouldBeNull();

        session.EmitUserMessage("use the other file");
        await harness.Outcomes.WaitForCountAsync(1);
        harness.Outcomes.Snapshot().ShouldBe([("s1", CopilotSteerOutcome.Consumed)]);

        session.EndTurn();
        await run.WaitAsync(Limit);
        harness.Outcomes.Snapshot().ShouldBe([("s1", CopilotSteerOutcome.Consumed)]);
    }

    [Test]
    public async Task Two_identical_steers_are_matched_to_echoes_in_submission_order_so_one_echo_consumes_only_the_first()
    {
        using var harness = new SteerHarness();
        var run = harness.Stream(new CopilotOptions());
        var session = await harness.TurnStartedAsync();

        (await harness.Steering.SteerAsync("s1", "same text")).ShouldBe(CopilotSteerResult.Sent);
        (await harness.Steering.SteerAsync("s2", "same text")).ShouldBe(CopilotSteerResult.Sent);

        // A second steer with an id already in use is refused without a send.
        (await harness.Steering.SteerAsync("s1", "same text")).ShouldBe(CopilotSteerResult.NotSteerable);
        session.SteerMessages.Count.ShouldBe(2);

        session.EmitUserMessage("same text");
        await harness.Outcomes.WaitForCountAsync(1);
        harness.Outcomes.Snapshot().ShouldBe([("s1", CopilotSteerOutcome.Consumed)]);

        session.EndTurn();
        await run.WaitAsync(Limit);
        harness.Outcomes.Snapshot().ShouldBe(
        [
            ("s1", CopilotSteerOutcome.Consumed),
            ("s2", CopilotSteerOutcome.NotConsumed)
        ]);
    }

    [Test]
    public async Task Steer_without_an_echo_before_the_turn_ends_is_reported_not_consumed_once_and_a_late_echo_changes_nothing()
    {
        using var harness = new SteerHarness();
        var run = harness.Stream(new CopilotOptions());
        var session = await harness.TurnStartedAsync();

        (await harness.Steering.SteerAsync("s1", "never echoed")).ShouldBe(CopilotSteerResult.Sent);

        session.EndTurn();
        await run.WaitAsync(Limit);
        await harness.Outcomes.WaitForCountAsync(1);

        session.EmitUserMessage("never echoed");
        await Task.Delay(50);
        harness.Outcomes.Snapshot().ShouldBe([("s1", CopilotSteerOutcome.NotConsumed)]);
    }

    [Test]
    public async Task Steer_past_the_delivery_timeout_is_reported_not_consumed_and_its_late_echo_does_not_take_a_later_identical_steer()
    {
        using var harness = new SteerHarness();
        var run = harness.Stream(new CopilotOptions { SteerDeliveryTimeout = TimeSpan.FromMilliseconds(200) });
        var session = await harness.TurnStartedAsync();

        (await harness.Steering.SteerAsync("s1", "same text")).ShouldBe(CopilotSteerResult.Sent);
        await harness.Outcomes.WaitForCountAsync(1);
        harness.Outcomes.Snapshot().ShouldBe([("s1", CopilotSteerOutcome.NotConsumed)]);

        // The run is still going, so a second identical steer is accepted. The late echo of the first one must not consume it.
        (await harness.Steering.SteerAsync("s2", "same text")).ShouldBe(CopilotSteerResult.Sent);
        session.EmitUserMessage("same text");
        session.EmitUserMessage("same text");
        await harness.Outcomes.WaitForCountAsync(2);

        session.EndTurn();
        await run.WaitAsync(Limit);
        harness.Outcomes.Snapshot().ShouldBe(
        [
            ("s1", CopilotSteerOutcome.NotConsumed),
            ("s2", CopilotSteerOutcome.Consumed)
        ]);
    }

    [Test]
    public async Task Steer_the_runtime_refuses_is_not_steerable_and_reports_no_outcome()
    {
        using var harness = new SteerHarness();
        var run = harness.Stream(new CopilotOptions());
        var session = await harness.TurnStartedAsync();
        session.RefuseSteers = true;

        (await harness.Steering.SteerAsync("s1", "refused")).ShouldBe(CopilotSteerResult.NotSteerable);
        session.SteerMessages.ShouldHaveSingleItem();

        session.EndTurn();
        await run.WaitAsync(Limit);
        harness.Outcomes.Snapshot().ShouldBeEmpty();
    }

    [Test]
    public async Task Steer_before_the_run_starts_or_after_it_ends_is_not_steerable_and_sends_nothing()
    {
        using var harness = new SteerHarness();

        (await harness.Steering.SteerAsync("early", "too early")).ShouldBe(CopilotSteerResult.NotSteerable);
        harness.Factory.FirstSession.Task.IsCompleted.ShouldBeFalse();

        var run = harness.Stream(new CopilotOptions());
        var session = await harness.TurnStartedAsync();
        session.EndTurn();
        await run.WaitAsync(Limit);

        (await harness.Steering.SteerAsync("late", "too late")).ShouldBe(CopilotSteerResult.NotSteerable);
        session.SteerMessages.ShouldBeEmpty();
        harness.Outcomes.Snapshot().ShouldBeEmpty();
    }

    [Test]
    public async Task Handle_is_detached_when_the_consumer_stops_early_so_a_later_steer_is_refused_and_nothing_is_sent()
    {
        using var harness = new SteerHarness();
        var consumed = Task.Run(async () =>
        {
            await foreach (var message in harness.Messages(new CopilotOptions()))
            {
                // The raw event is the first message after the turn has started. Stopping here disposes the stream.
                if (message.Type == "event.raw")
                {
                    break;
                }
            }
        });
        var session = await harness.TurnStartedAsync();
        session.EmitUserMessage("unrelated message");
        await consumed.WaitAsync(Limit);

        (await harness.Steering.SteerAsync("late", "after the consumer left")).ShouldBe(CopilotSteerResult.NotSteerable);
        session.SteerMessages.ShouldBeEmpty();

        session.EndTurn();
    }

    [Test]
    public async Task Steer_racing_the_end_of_the_turn_is_tracked_when_sent_before_the_end_and_never_sent_after_it()
    {
        using var harness = new SteerHarness();
        var run = harness.Stream(new CopilotOptions());
        var session = await harness.TurnStartedAsync();

        // The steer's send is still in flight while the turn ends. It was initiated before the end, so it is tracked, and
        // its outcome is reported once the send completes.
        session.SteerInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var steer = harness.Steering.SteerAsync("s1", "in flight at the end");
        session.SteerMessages.Count.ShouldBe(1);

        session.EndTurn();
        await run.WaitAsync(Limit);
        harness.Outcomes.Snapshot().ShouldBeEmpty();

        session.SteerInFlight.SetResult();
        (await steer.WaitAsync(Limit)).ShouldBe(CopilotSteerResult.Sent);
        await harness.Outcomes.WaitForCountAsync(1);
        harness.Outcomes.Snapshot().ShouldBe([("s1", CopilotSteerOutcome.NotConsumed)]);

        // A steer after the end is refused without a send.
        (await harness.Steering.SteerAsync("s2", "after the end")).ShouldBe(CopilotSteerResult.NotSteerable);
        session.SteerMessages.Count.ShouldBe(1);
    }

    [Test]
    public async Task Steer_whose_send_fails_after_the_turn_ended_is_not_steerable_and_reports_no_outcome()
    {
        using var harness = new SteerHarness();
        var run = harness.Stream(new CopilotOptions());
        var session = await harness.TurnStartedAsync();

        session.SteerInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var steer = harness.Steering.SteerAsync("s1", "fails in flight");

        session.EndTurn();
        await run.WaitAsync(Limit);

        session.SteerInFlight.SetException(new InvalidOperationException("the session is gone"));
        (await steer.WaitAsync(Limit)).ShouldBe(CopilotSteerResult.NotSteerable);
        harness.Outcomes.Snapshot().ShouldBeEmpty();
    }

    [Test]
    public async Task Steer_while_a_question_is_pending_leaves_the_question_pending_and_is_reported_only_on_its_echo()
    {
        var answer = new TaskCompletionSource<CopilotUserInputResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var questionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var questionCancelled = false;
        using var harness = new SteerHarness();
        var run = harness.Stream(new CopilotOptions
        {
            UserInputHandler = (_, token) =>
            {
                token.Register(() => questionCancelled = true);
                questionStarted.TrySetResult();
                return answer.Task.WaitAsync(token);
            }
        });
        var session = await harness.TurnStartedAsync();

        var asked = session.AskAsync("Which file?", "request-1", "tool-1");
        await questionStarted.Task.WaitAsync(Limit);

        (await harness.Steering.SteerAsync("s1", "use the other file")).ShouldBe(CopilotSteerResult.Sent);
        session.SteerMessages.ShouldHaveSingleItem().Prompt.ShouldBe("use the other file");

        // The steer neither answers nor declines the question, and it is not reported consumed before the echo.
        asked.IsCompleted.ShouldBeFalse();
        questionCancelled.ShouldBeFalse();
        harness.Outcomes.Snapshot().ShouldBeEmpty();

        session.EmitUserMessage("use the other file");
        await harness.Outcomes.WaitForCountAsync(1);
        harness.Outcomes.Snapshot().ShouldBe([("s1", CopilotSteerOutcome.Consumed)]);
        asked.IsCompleted.ShouldBeFalse();

        answer.SetResult(null);
        session.EndTurn();
        await run.WaitAsync(Limit);
    }

    [Test]
    public async Task Steer_carries_no_attachments_and_the_original_images_are_not_sent_again()
    {
        using var harness = new SteerHarness();
        var run = harness.Stream(new CopilotOptions
        {
            Images = [new CopilotImageAttachment("image/png", PngBytes, "a.png")]
        });
        var session = await harness.TurnStartedAsync();

        (await harness.Steering.SteerAsync("s1", "describe the second part")).ShouldBe(CopilotSteerResult.Sent);
        session.EmitUserMessage("describe the second part");
        await harness.Outcomes.WaitForCountAsync(1);

        session.EndTurn();
        await run.WaitAsync(Limit);

        // The run's one send carries the image, and the steer is text only.
        session.TurnMessages.ShouldHaveSingleItem().Attachments.ShouldNotBeNull().Count.ShouldBe(1);
        session.SteerMessages.ShouldHaveSingleItem().Attachments.ShouldBeNull();
    }

    [Test]
    public async Task A_blank_steer_id_or_text_is_rejected_before_any_send()
    {
        var handle = new CopilotRunSteering();

        Should.Throw<ArgumentException>(() => { _ = handle.SteerAsync(" ", "text"); });
        Should.Throw<ArgumentException>(() => { _ = handle.SteerAsync("s1", " "); });
    }

    [Test]
    public async Task A_non_positive_steer_delivery_timeout_is_rejected_before_any_sdk_call()
    {
        using var harness = new SteerHarness();

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await harness.Stream(new CopilotOptions { SteerDeliveryTimeout = TimeSpan.Zero }).WaitAsync(Limit));
        harness.Factory.FirstSession.Task.IsCompleted.ShouldBeFalse();
    }

    [Test]
    public void The_steering_handle_is_not_serialized_and_options_text_shows_only_that_it_is_set()
    {
        var property = typeof(CopilotOptions).GetProperty(nameof(CopilotOptions.Steering));
        property.ShouldNotBeNull();
        property.GetCustomAttribute<JsonIgnoreAttribute>().ShouldNotBeNull();

        var options = new CopilotOptions { Steering = new CopilotRunSteering(), SteerDeliveryTimeout = TimeSpan.FromSeconds(7) };
        var text = options.ToString();
        text.ShouldContain("Steering = set");
        text.ShouldContain("SteerDeliveryTimeout = 00:00:07");
        new CopilotOptions().ToString().ShouldContain("Steering = null");
    }

    // Starts a run on a fake Copilot runtime and exposes the outcomes the handle reports.
    private sealed class SteerHarness : IDisposable
    {
        private readonly CopilotProvider _provider;

        public SteerHarness()
        {
            Steering.OutcomeObserved += (_, args) => Outcomes.Add(args.SteerId, args.Outcome);
            _provider = new CopilotProvider(
                new StubExecutableResolver(),
                new CliProcessManager(),
                new GitHubCopilotSdkGateway(Factory, new CopilotSdkEnvironmentLeaseCoordinator()),
                new EmptyRuntimeEnvironmentResolver());
        }

        public SteerFakeClientFactory Factory { get; } = new();

        public CopilotRunSteering Steering { get; } = new();

        public OutcomeLog Outcomes { get; } = new();

        // The stream is driven by a background task, so the test can steer while the turn is running.
        public Task Stream(CopilotOptions options)
        {
            var configured = options with { ExecutablePath = "/custom/copilot", Steering = Steering };
            return Task.Run(async () =>
            {
                await foreach (var _ in _provider.ExecuteAsync(configured, "describe"))
                {
                }
            });
        }

        // Exposes the raw stream so a test can stop consuming it early.
        public IAsyncEnumerable<CliMessage> Messages(CopilotOptions options)
        {
            return _provider.ExecuteAsync(options with { ExecutablePath = "/custom/copilot", Steering = Steering }, "describe");
        }

        public async Task<SteerFakeSession> TurnStartedAsync()
        {
            var session = await Factory.FirstSession.Task.WaitAsync(Limit);
            await session.TurnStarted.Task.WaitAsync(Limit);
            return session;
        }

        public void Dispose()
        {
            if (Factory.FirstSession.Task.IsCompletedSuccessfully)
            {
                Factory.FirstSession.Task.Result.EndTurn();
            }
        }
    }

    private sealed class OutcomeLog
    {
        private readonly List<(string SteerId, CopilotSteerOutcome Outcome)> _entries = [];

        public void Add(string steerId, CopilotSteerOutcome outcome)
        {
            lock (_entries)
            {
                _entries.Add((steerId, outcome));
            }
        }

        public (string SteerId, CopilotSteerOutcome Outcome)[] Snapshot()
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }

        public async Task WaitForCountAsync(int count)
        {
            var deadline = DateTime.UtcNow + Limit;
            while (Snapshot().Length < count)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"Expected {count} steer outcome(s) but saw {Snapshot().Length}.");
                }

                await Task.Delay(10);
            }
        }
    }

    private sealed class SteerFakeClientFactory : ICopilotSdkClientFactory
    {
        public TaskCompletionSource<SteerFakeSession> FirstSession { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ICopilotSdkClient Create(CopilotClientOptions options) => new SteerFakeClient(this);

        private sealed class SteerFakeClient(SteerFakeClientFactory owner) : ICopilotSdkClient
        {
            public Task<ICopilotSdkSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken)
            {
                var session = new SteerFakeSession(config.SessionId ?? "steer-session", config.OnUserInputRequest);
                owner.FirstSession.TrySetResult(session);
                return Task.FromResult<ICopilotSdkSession>(session);
            }

            public Task<ICopilotSdkSession> ResumeSessionAsync(string sessionId, ResumeSessionConfig config, CancellationToken cancellationToken)
                => Task.FromException<ICopilotSdkSession>(new NotSupportedException("These tests start fresh sessions only."));

            public Task<IReadOnlyList<CopilotSdkModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<CopilotSdkModelInfo>>([]);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    // A session whose turn lasts until the test ends it. Steer sends and session events are scripted by the test.
    private sealed class SteerFakeSession(
        string sessionId,
        Func<UserInputRequest, UserInputInvocation, Task<UserInputResponse>>? userInput) : ICopilotSdkSession
    {
        private readonly object _sync = new();
        private readonly TaskCompletionSource _turnEnd = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<MessageOptions> _turnMessages = [];
        private readonly List<MessageOptions> _steerMessages = [];
        private event Action<SessionEvent>? SessionEventReceived;

        public TaskCompletionSource TurnStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // When set, a steer send completes only when the test completes this source, which holds the send in flight.
        public TaskCompletionSource? SteerInFlight { get; set; }

        public bool RefuseSteers { get; set; }

        public string SessionId => sessionId;

        public IReadOnlyList<MessageOptions> SteerMessages
        {
            get
            {
                lock (_sync)
                {
                    return [.. _steerMessages];
                }
            }
        }

        public IReadOnlyList<MessageOptions> TurnMessages
        {
            get
            {
                lock (_sync)
                {
                    return [.. _turnMessages];
                }
            }
        }

        public IDisposable On(Action<SessionEvent> handler)
        {
            SessionEventReceived += handler;
            return new DelegateDisposable(() => SessionEventReceived -= handler);
        }

        public Task SendAndWaitAsync(MessageOptions options, TimeSpan timeout, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _turnMessages.Add(options);
            }

            TurnStarted.TrySetResult();
            return _turnEnd.Task.WaitAsync(cancellationToken);
        }

        public Task SendAsync(MessageOptions options, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _steerMessages.Add(options);
            }

            if (RefuseSteers)
            {
                return Task.FromException(new InvalidOperationException("The runtime refused the steer."));
            }

            return SteerInFlight?.Task ?? Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void EndTurn() => _turnEnd.TrySetResult();

        // The session echoes a user message with its text, which is how a steer is confirmed.
        public void EmitUserMessage(string content)
        {
            SessionEventReceived?.Invoke(new UserMessageEvent { Data = new UserMessageData { Content = content } });
        }

        // Raises the question the way the SDK does, then starts the callback. The returned task is the pending answer.
        public Task<UserInputResponse> AskAsync(string question, string requestId, string toolCallId)
        {
            SessionEventReceived?.Invoke(new UserInputRequestedEvent
            {
                Data = new UserInputRequestedData
                {
                    RequestId = requestId,
                    ToolCallId = toolCallId,
                    Question = question,
                    Choices = ["yes"],
                    AllowFreeform = false
                }
            });

            if (userInput is null)
            {
                throw new InvalidOperationException("The session has no user-input callback.");
            }

            return userInput(
                new UserInputRequest { Question = question, Choices = ["yes"], AllowFreeform = false },
                new UserInputInvocation { SessionId = sessionId });
        }
    }

    private sealed class DelegateDisposable(Action disposeAction) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                disposeAction();
            }
        }
    }

    private sealed class StubExecutableResolver : CliExecutableResolver
    {
        public override IReadOnlyList<string> ResolveExecutablePaths(string? executableName, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => string.IsNullOrWhiteSpace(executableName) ? [] : [executableName];
    }

    private sealed class EmptyRuntimeEnvironmentResolver : IRuntimeEnvironmentResolver
    {
        public Task<IReadOnlyDictionary<string, string?>> ResolveAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?>());
    }
}
