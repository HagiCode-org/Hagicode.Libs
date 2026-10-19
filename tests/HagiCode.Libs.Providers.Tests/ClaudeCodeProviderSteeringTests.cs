using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Shouldly;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.ClaudeCode;

namespace HagiCode.Libs.Providers.Tests;

public sealed partial class ClaudeCodeProviderTests
{
    private static readonly TimeSpan SteerAwaitLimit = TimeSpan.FromSeconds(10);

    [Test]
    public async Task Steer_echoed_within_its_turn_is_reported_consumed_and_the_run_completes_once()
    {
        var run = await StartSteerableRunAsync();
        run.Transport.Push(StreamMessage("system", new { subtype = "init" }));
        run.Transport.Push(PromptEchoMessage());
        (await run.Transport.NextWriteAsync()).Content.GetProperty("message").GetProperty("role").GetString().ShouldBe("user");

        (await run.Steering.SteerAsync("s1", "use v2")).ShouldBe(ClaudeSteerResult.Sent);
        var steerWrite = await run.Transport.NextWriteAsync();
        steerWrite.Content.GetProperty("uuid").GetString().ShouldBe("s1");

        run.Transport.Push(SteerEchoMessage("s1", "use v2"));
        run.Transport.Push(StreamMessage("result", new { result = "done" }));
        await run.Completion;

        run.Outcomes.Select(outcome => (outcome.SteerId, outcome.Outcome))
            .ShouldBe([("s1", ClaudeSteerOutcome.Consumed)]);
        run.Messages.Select(static message => message.Type).ShouldBe(["system", "result"]);
        run.Transport.WrittenPromptCount.ShouldBe(1);
    }

    [Test]
    public async Task Result_before_the_steer_echo_keeps_the_run_open_until_the_echo_and_its_turn_end()
    {
        var run = await StartSteerableRunAsync();
        run.Transport.Push(StreamMessage("system", new { subtype = "init" }));
        run.Transport.Push(PromptEchoMessage());
        await run.Transport.NextWriteAsync();
        (await run.Steering.SteerAsync("s1", "switch to lakes")).ShouldBe(ClaudeSteerResult.Sent);
        await run.Transport.NextWriteAsync();

        run.Transport.Push(StreamMessage("result", new { result = "first" }));
        await run.WaitForMessageCountAsync(2);
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        run.Completion.IsCompleted.ShouldBeFalse("the steer is still unconsumed, so the run must not end at the first result");

        run.Transport.Push(StreamMessage("system", new { subtype = "init" }));
        run.Transport.Push(SteerEchoMessage("s1", "switch to lakes"));
        run.Transport.Push(StreamMessage("result", new { result = "second" }));
        await run.Completion;

        run.Outcomes.Select(outcome => (outcome.SteerId, outcome.Outcome))
            .ShouldBe([("s1", ClaudeSteerOutcome.Consumed)]);
        run.Messages.Count(static message => message.Type == "result").ShouldBe(2);
    }

    [Test]
    public async Task Steer_never_echoed_is_reported_not_consumed_when_the_delivery_timeout_passes()
    {
        var run = await StartSteerableRunAsync(steerDeliveryTimeout: TimeSpan.FromMilliseconds(300));
        run.Transport.Push(StreamMessage("system", new { subtype = "init" }));
        run.Transport.Push(PromptEchoMessage());
        await run.Transport.NextWriteAsync();
        (await run.Steering.SteerAsync("s1", "late note")).ShouldBe(ClaudeSteerResult.Sent);
        await run.Transport.NextWriteAsync();

        run.Transport.Push(StreamMessage("result", new { result = "done" }));
        await run.Completion;

        run.Outcomes.Select(outcome => (outcome.SteerId, outcome.Outcome))
            .ShouldBe([("s1", ClaudeSteerOutcome.NotConsumed)]);
        run.Messages.Count(static message => message.Type == "result").ShouldBe(1);
    }

    [Test]
    public async Task Cancelling_the_run_with_a_steer_pending_reports_it_not_consumed()
    {
        var run = await StartSteerableRunAsync();
        run.Transport.Push(StreamMessage("system", new { subtype = "init" }));
        run.Transport.Push(PromptEchoMessage());
        await run.Transport.NextWriteAsync();
        (await run.Steering.SteerAsync("s1", "pending")).ShouldBe(ClaudeSteerResult.Sent);
        await run.Transport.NextWriteAsync();
        run.Transport.Push(StreamMessage("result", new { result = "first" }));
        await run.WaitForMessageCountAsync(2);

        run.Cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () => await run.Completion);
        run.Outcomes.Select(outcome => (outcome.SteerId, outcome.Outcome))
            .ShouldBe([("s1", ClaudeSteerOutcome.NotConsumed)]);
    }

    [Test]
    public async Task Cli_exit_with_a_steer_pending_reports_it_not_consumed()
    {
        var run = await StartSteerableRunAsync();
        run.Transport.Push(StreamMessage("system", new { subtype = "init" }));
        run.Transport.Push(PromptEchoMessage());
        await run.Transport.NextWriteAsync();
        (await run.Steering.SteerAsync("s1", "pending")).ShouldBe(ClaudeSteerResult.Sent);
        await run.Transport.NextWriteAsync();
        run.Transport.Push(StreamMessage("result", new { result = "first" }));
        await run.WaitForMessageCountAsync(2);

        run.Transport.EndOutput();

        await run.Completion;
        run.Outcomes.Select(outcome => (outcome.SteerId, outcome.Outcome))
            .ShouldBe([("s1", ClaudeSteerOutcome.NotConsumed)]);
    }

    [Test]
    public async Task Steer_echo_before_the_prompt_echo_does_not_resend_the_prompt()
    {
        var run = await StartSteerableRunAsync();
        run.Transport.Push(StreamMessage("system", new { subtype = "init" }));
        await run.Transport.NextWriteAsync(); // the prompt is written after the handle is attached
        (await run.Steering.SteerAsync("s1", "early")).ShouldBe(ClaudeSteerResult.Sent);
        await run.Transport.NextWriteAsync();

        run.Transport.Push(SteerEchoMessage("s1", "early"));
        run.Transport.Push(PromptEchoMessage());
        run.Transport.Push(StreamMessage("result", new { result = "done" }));
        await run.Completion;

        run.Transport.WrittenPromptCount.ShouldBe(1);
        run.Outcomes.Select(outcome => outcome.Outcome).ShouldBe([ClaudeSteerOutcome.Consumed]);
    }

    [Test]
    public async Task Steer_after_the_run_ended_is_not_steerable_and_writes_nothing()
    {
        var run = await StartSteerableRunAsync();
        run.Transport.Push(StreamMessage("system", new { subtype = "init" }));
        run.Transport.Push(PromptEchoMessage());
        await run.Transport.NextWriteAsync();
        run.Transport.Push(StreamMessage("result", new { result = "done" }));
        await run.Completion;

        (await run.Steering.SteerAsync("late", "too late")).ShouldBe(ClaudeSteerResult.NotSteerable);
        run.Transport.WrittenCount.ShouldBe(1);
        run.Outcomes.ShouldBeEmpty();
    }

    [Test]
    public async Task Steer_before_the_run_connects_is_not_steerable()
    {
        var steering = new ClaudeRunSteering();

        (await steering.SteerAsync("early", "too early")).ShouldBe(ClaudeSteerResult.NotSteerable);
    }

    [Test]
    public async Task Zero_or_negative_steer_delivery_timeout_is_rejected()
    {
        var provider = CreateProvider();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
            await CollectAsync(provider, new ClaudeCodeOptions { SteerDeliveryTimeout = TimeSpan.Zero }));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
            await CollectAsync(provider, new ClaudeCodeOptions { SteerDeliveryTimeout = TimeSpan.FromSeconds(-1) }));
    }

    private static CliMessage PromptEchoMessage()
    {
        return StreamMessage("user", new { isReplay = true, uuid = "cli-prompt-uuid", message = new { role = "user", content = "hello" } });
    }

    private static CliMessage SteerEchoMessage(string uuid, string text)
    {
        return StreamMessage("user", new { isReplay = true, uuid, message = new { role = "user", content = text } });
    }

    private static async Task<SteerableRun> StartSteerableRunAsync(TimeSpan? steerDeliveryTimeout = null)
    {
        var transport = new SteerScriptedTransport();
        var provider = new SteerScriptedProvider(transport);
        var steering = new ClaudeRunSteering();
        var cancellation = new CancellationTokenSource();
        var run = new SteerableRun(transport, steering, cancellation);
        steering.OutcomeObserved += (_, args) => run.Outcomes.Add(args);

        var options = new ClaudeCodeOptions
        {
            SessionId = "session-1",
            WorkingDirectory = "/tmp/project",
            Steering = steering,
            PromptStartTimeout = TimeSpan.FromSeconds(5),
            SteerDeliveryTimeout = steerDeliveryTimeout ?? TimeSpan.FromSeconds(10)
        };
        run.Completion = Task.Run(async () =>
        {
            await foreach (var message in provider.ExecuteAsync(options, "hello", cancellation.Token))
            {
                run.Messages.Add(message);
            }
        });

        // The handle is attached after the prompt is written and before the stream is read, so a steer may be sent only
        // once the stream has started.
        await transport.ReceiveStarted.WaitAsync(SteerAwaitLimit);
        return run;
    }

    private sealed class SteerableRun(SteerScriptedTransport transport, ClaudeRunSteering steering, CancellationTokenSource cancellation)
    {
        public SteerScriptedTransport Transport { get; } = transport;

        public ClaudeRunSteering Steering { get; } = steering;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public List<ClaudeSteerOutcomeEventArgs> Outcomes { get; } = [];

        public List<CliMessage> Messages { get; } = [];

        public Task Completion { get; set; } = Task.CompletedTask;

        public async Task WaitForMessageCountAsync(int count)
        {
            var deadline = DateTime.UtcNow + SteerAwaitLimit;
            while (Messages.Count < count)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"Expected {count} messages, got {Messages.Count}.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10));
            }
        }
    }

    private sealed class SteerScriptedProvider(SteerScriptedTransport transport)
        : ClaudeCodeProvider(new StubExecutableResolver(), new StubCliProcessManager(), new StubRuntimeEnvironmentResolver())
    {
        protected override ICliTransport CreateTransport(ProcessStartContext startContext) => transport;
    }

    /// <summary>
    /// A transport the test drives by hand: it yields what the test pushes, records every write, and ends its output on
    /// request. Reads and writes are awaited through channels so a test waits for the exact step it cares about.
    /// </summary>
    private sealed class SteerScriptedTransport : ICliTransport
    {
        private readonly Channel<CliMessage> _output = Channel.CreateUnbounded<CliMessage>();
        private readonly Channel<CliMessage> _writes = Channel.CreateUnbounded<CliMessage>();
        private readonly ConcurrentQueue<CliMessage> _written = new();
        private readonly TaskCompletionSource _receiveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsConnected { get; private set; }

        public Task ReceiveStarted => _receiveStarted.Task;

        public int WrittenCount => _written.Count;

        public int WrittenPromptCount => _written.Count(static message => message.Content.TryGetProperty("uuid", out _) is false);

        public void Push(CliMessage message) => _output.Writer.TryWrite(message);

        public void EndOutput() => _output.Writer.TryComplete();

        public async Task<CliMessage> NextWriteAsync()
        {
            return await _writes.Reader.ReadAsync().AsTask().WaitAsync(SteerAwaitLimit);
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task SendAsync(CliMessage message, CancellationToken cancellationToken = default)
        {
            _written.Enqueue(message);
            _writes.Writer.TryWrite(message);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<CliMessage> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _receiveStarted.TrySetResult();
            await foreach (var message in _output.Reader.ReadAllAsync(cancellationToken))
            {
                yield return message;
            }
        }

        public Task InterruptAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }
}
