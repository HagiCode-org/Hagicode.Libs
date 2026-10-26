using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Codebuddy;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

// CodeBuddy steering over ACP (codebuddy-run-steering, tasks 3A.4 to 3A.8). The fixtures in Fixtures/Codebuddy are real CodeBuddy
// 2.169.0 captures, replayed by ScriptedAcpClient: its notifications arrive in the recorded order, and the steer reply and the
// injection are released when the provider writes the steer. Nothing here needs the CLI, credentials or a model.
public sealed class CodebuddyProviderSteeringTests
{
    private static readonly Version SupportedCliVersion = new(2, 169, 0);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);

    private const string ToolCallSteerId = "probe-steer-1";
    private const string ToolCallSteerText = "Also append the word STEERED-B to your final reply.";

    [Test]
    public async Task A_steer_during_a_tool_call_is_sent_and_consumed_exactly_once_and_its_echo_is_not_output()
    {
        var run = LoadFixture("steer-acp-tool-call.jsonl");

        var result = await RunSteeredAsync(run, ToolCallSteerId, ToolCallSteerText, SupportedCliVersion);

        result.SteerResult.ShouldBe(CodebuddySteerResult.Sent);
        result.Outcomes.ShouldBe([(ToolCallSteerId, CodebuddySteerOutcome.Consumed)]);
        result.Messages.Any(message => message.Content.GetRawText().Contains("Also append the word STEERED-B")).ShouldBeFalse();
        result.Messages[^1].Type.ShouldBe("terminal.completed");
        result.Client.SteerRequests.Count.ShouldBe(1);
    }

    [Test]
    public async Task Without_the_client_message_id_the_echo_is_missing_so_the_steer_is_not_consumed()
    {
        var run = LoadFixture("steer-acp-tool-call-no-client-id.jsonl");

        var result = await RunSteeredAsync(run, ToolCallSteerId, ToolCallSteerText, SupportedCliVersion);

        result.SteerResult.ShouldBe(CodebuddySteerResult.Sent);
        result.Outcomes.ShouldBe([(ToolCallSteerId, CodebuddySteerOutcome.NotConsumed)]);
    }

    [Test]
    public async Task A_steer_during_text_generation_is_consumed_at_the_next_model_request()
    {
        var run = LoadFixture("steer-acp-text-stream.jsonl");

        var result = await RunSteeredAsync(run, "probe-steer-held", "Also end your reply with the word HELD-X.", SupportedCliVersion);

        result.SteerResult.ShouldBe(CodebuddySteerResult.Sent);
        result.Outcomes.ShouldBe([("probe-steer-held", CodebuddySteerOutcome.Consumed)]);
    }

    [Test]
    public async Task A_steer_held_through_a_cancel_is_reported_not_consumed_and_is_not_run_later()
    {
        var run = LoadFixture("steer-acp-cancel-held.jsonl");

        var result = await RunSteeredAsync(run, "probe-held-z", "Also append the word HELDZ to your final reply.", SupportedCliVersion);

        result.SteerResult.ShouldBe(CodebuddySteerResult.Sent);
        result.Outcomes.ShouldBe([("probe-held-z", CodebuddySteerOutcome.NotConsumed)]);
    }

    [Test]
    public async Task A_steer_on_a_resumed_session_is_consumed_and_the_replayed_history_is_not_output()
    {
        var run = new ScriptedRun(
            SessionId: "session-1",
            IsResumed: true,
            Pre:
            [
                Update("agent_thought_chunk", Text("ready")),
                Update("session_info_update", ReplayMarker("start")),
                Update("agent_message_chunk", Text("old history")),
                Update("session_info_update", ReplayMarker("end"))
            ],
            Post:
            [
                Echo("resume-steer", "Resume steer."),
                Update("agent_message_chunk", Text("new answer"))
            ],
            SteerReply: JsonSerializer.SerializeToElement(new { steered = true }),
            PromptResult: JsonSerializer.SerializeToElement(new { stopReason = "end_turn" }));

        var result = await RunSteeredAsync(run, "resume-steer", "Resume steer.", SupportedCliVersion);

        result.SteerResult.ShouldBe(CodebuddySteerResult.Sent);
        result.Outcomes.ShouldBe([("resume-steer", CodebuddySteerOutcome.Consumed)]);
        result.Messages.Any(message => message.Content.GetRawText().Contains("new answer")).ShouldBeTrue();
        result.Messages.Any(message => message.Content.GetRawText().Contains("old history")).ShouldBeFalse();
        result.Messages.Any(message => message.Content.GetRawText().Contains("Resume steer.")).ShouldBeFalse();
    }

    [Test]
    public async Task The_steer_request_carries_the_recorded_content_blocks_and_message_id()
    {
        var result = await RunSteeredAsync(LoadFixture("steer-acp-tool-call.jsonl"), ToolCallSteerId, ToolCallSteerText, SupportedCliVersion);

        var written = JsonDocument.Parse(result.Client.SteerRequests.Single()).RootElement;
        var recorded = RecordedSteerParams("steer-acp-tool-call.jsonl");
        written.GetProperty("contentBlocks").GetRawText().ShouldBe(recorded.GetProperty("contentBlocks").GetRawText());
        written.GetProperty("clientUserMessageId").GetString().ShouldBe(recorded.GetProperty("clientUserMessageId").GetString());
    }

    [Test]
    public async Task Outcomes_are_reported_only_after_the_cli_client_is_disposed()
    {
        var run = LoadFixture("steer-acp-tool-call.jsonl");
        var steering = new CodebuddyRunSteering();
        var client = new ScriptedAcpClient(run);
        var disposedAtReport = new List<bool>();
        steering.OutcomeObserved += (_, _) => disposedAtReport.Add(client.Disposed);

        var provider = new SteeringTestProvider(client, SupportedCliVersion);
        var index = 0;
        using var cts = new CancellationTokenSource(RunTimeout);
        await foreach (var message in provider.ExecuteAsync(new CodebuddyOptions { ExecutablePath = "fake-codebuddy", Steering = steering }, "prompt", cts.Token))
        {
            if (++index == 2)
            {
                (await steering.SteerAsync(ToolCallSteerId, ToolCallSteerText)).ShouldBe(CodebuddySteerResult.Sent);
                client.Release();
            }
        }

        disposedAtReport.ShouldBe([true]);
    }

    [TestCase("2.169.0", true)]
    [TestCase("2.147.0", true)]
    [TestCase("2.146.0", true)]
    [TestCase("2.145.0", false)]
    [TestCase(null, false)]
    public async Task The_version_gate_attaches_only_at_or_above_the_verified_minimum(string? version, bool attached)
    {
        var run = LoadFixture("steer-acp-tool-call.jsonl");

        var result = await RunSteeredAsync(run, ToolCallSteerId, ToolCallSteerText, version is null ? null : Version.Parse(version));

        if (attached)
        {
            result.SteerResult.ShouldBe(CodebuddySteerResult.Sent);
            result.Outcomes.ShouldBe([(ToolCallSteerId, CodebuddySteerOutcome.Consumed)]);
        }
        else
        {
            result.SteerResult.ShouldBe(CodebuddySteerResult.NotSteerable);
            result.Outcomes.ShouldBeEmpty();
            result.Client.SteerRequests.ShouldBeEmpty();
        }
    }

    [Test]
    public async Task A_run_without_a_handle_never_writes_a_steer()
    {
        var run = LoadFixture("steer-acp-tool-call.jsonl");

        var result = await RunSteeredAsync(run, ToolCallSteerId, null, SupportedCliVersion, steering: null);

        result.Client.SteerRequests.ShouldBeEmpty();
        result.Messages[^1].Type.ShouldBe("terminal.completed");
    }

    [Test]
    public async Task A_steer_after_the_run_ended_is_not_steerable_and_writes_nothing()
    {
        var run = LoadFixture("steer-acp-tool-call.jsonl");
        var result = await RunSteeredAsync(run, ToolCallSteerId, ToolCallSteerText, SupportedCliVersion);

        var late = await result.Steering.SteerAsync("late-steer", "after the end");

        late.ShouldBe(CodebuddySteerResult.NotSteerable);
        result.Client.SteerRequests.Count.ShouldBe(1);
    }

    [Test]
    public async Task A_steer_after_the_enumeration_was_abandoned_is_not_steerable_and_writes_nothing()
    {
        var run = LoadFixture("steer-acp-tool-call.jsonl");
        var steering = new CodebuddyRunSteering();
        var outcomes = new ConcurrentQueue<CodebuddySteerOutcome>();
        steering.OutcomeObserved += (_, args) => outcomes.Enqueue(args.Outcome);
        var client = new ScriptedAcpClient(run);
        var provider = new SteeringTestProvider(client, SupportedCliVersion);

        using var cts = new CancellationTokenSource(RunTimeout);
        {
            await using var enumerator = provider.ExecuteAsync(new CodebuddyOptions { ExecutablePath = "fake-codebuddy", Steering = steering }, "prompt", cts.Token).GetAsyncEnumerator(cts.Token);
            (await enumerator.MoveNextAsync()).ShouldBeTrue();
            (await enumerator.MoveNextAsync()).ShouldBeTrue();
        }

        var late = await steering.SteerAsync(ToolCallSteerId, ToolCallSteerText);

        late.ShouldBe(CodebuddySteerResult.NotSteerable);
        client.SteerRequests.ShouldBeEmpty();
        outcomes.ShouldBeEmpty();
    }

    [Test]
    public async Task A_handle_reused_for_a_second_run_is_attached_again_and_reports_its_own_outcome()
    {
        var steering = new CodebuddyRunSteering();
        var outcomes = new ConcurrentQueue<(string Id, CodebuddySteerOutcome Outcome)>();
        steering.OutcomeObserved += (_, args) => outcomes.Enqueue((args.SteerId, args.Outcome));

        var first = await RunWithHandleAsync(steering, LoadFixture("steer-acp-tool-call.jsonl"), ToolCallSteerId, ToolCallSteerText);
        var second = await RunWithHandleAsync(steering, LoadFixture("steer-acp-tool-call.jsonl"), ToolCallSteerId, ToolCallSteerText);

        first.ShouldBe(CodebuddySteerResult.Sent);
        second.ShouldBe(CodebuddySteerResult.Sent);
        outcomes.ToArray().ShouldBe(
        [
            (ToolCallSteerId, CodebuddySteerOutcome.Consumed),
            (ToolCallSteerId, CodebuddySteerOutcome.Consumed)
        ]);
    }

    private static async Task<CodebuddySteerResult> RunWithHandleAsync(CodebuddyRunSteering steering, ScriptedRun run, string steerId, string steerText)
    {
        var client = new ScriptedAcpClient(run);
        var provider = new SteeringTestProvider(client, SupportedCliVersion);
        CodebuddySteerResult? steerResult = null;
        using var cts = new CancellationTokenSource(RunTimeout);
        var index = 0;
        await foreach (var message in provider.ExecuteAsync(new CodebuddyOptions { ExecutablePath = "fake-codebuddy", Steering = steering }, "prompt", cts.Token))
        {
            if (++index == 2)
            {
                steerResult = await steering.SteerAsync(steerId, steerText);
                client.Release();
            }
        }

        return steerResult ?? CodebuddySteerResult.NotSteerable;
    }

    private static async Task<SteeredRun> RunSteeredAsync(ScriptedRun run, string steerId, string? steerText, Version? cliVersion, CodebuddyRunSteering? steering = null)
    {
        steering ??= new CodebuddyRunSteering();
        var outcomes = new List<(string SteerId, CodebuddySteerOutcome Outcome)>();
        steering.OutcomeObserved += (_, args) => outcomes.Add((args.SteerId, args.Outcome));

        var client = new ScriptedAcpClient(run);
        var provider = new SteeringTestProvider(client, cliVersion);
        var messages = new List<CliMessage>();
        CodebuddySteerResult? steerResult = null;
        using var cts = new CancellationTokenSource(RunTimeout);
        var index = 0;
        await foreach (var message in provider.ExecuteAsync(new CodebuddyOptions { ExecutablePath = "fake-codebuddy", Steering = steering }, "prompt", cts.Token))
        {
            messages.Add(message);
            if (++index == 2)
            {
                if (steerText is not null)
                {
                    steerResult = await steering.SteerAsync(steerId, steerText);
                }

                client.Release();
            }
        }

        return new SteeredRun(messages, steerResult, outcomes, client, steering);
    }

    private static ScriptedRun LoadFixture(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Codebuddy", fileName);
        // Keep the wire frames only ("in" and "out"); the harness also wrote summary lines for the steer and prompt replies.
        var frames = File.ReadLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement)
            .Where(root => root.TryGetProperty("msg", out _) && root.GetProperty("dir").GetString() is "in" or "out")
            .Select(root => (Direction: root.GetProperty("dir").GetString(), Message: root.GetProperty("msg").Clone()))
            .ToList();

        var steerRequestId = frames.Single(frame => frame.Direction == "out" && MethodOf(frame.Message) == "session/steer").Message.GetProperty("id").GetInt32();
        // The first prompt is the run under test. A capture can hold a later prompt after it; those frames are not replayed.
        var promptRequestId = frames.First(frame => frame.Direction == "out" && MethodOf(frame.Message) == "session/prompt").Message.GetProperty("id").GetInt32();
        var promptReplyIndex = frames.FindIndex(frame => frame.Direction == "in" && IdOf(frame.Message) == promptRequestId);
        var steerReplyIndex = frames.FindIndex(frame => frame.Direction == "in" && IdOf(frame.Message) == steerRequestId && frame.Message.TryGetProperty("result", out _));
        var steerReply = frames[steerReplyIndex].Message.GetProperty("result").Clone();
        var promptReply = frames[promptReplyIndex].Message.GetProperty("result").Clone();

        var pre = new List<AcpNotification>();
        var post = new List<AcpNotification>();
        for (var i = 0; i < promptReplyIndex; i++)
        {
            var frame = frames[i];
            if (frame.Direction != "in" || MethodOf(frame.Message) != "session/update")
            {
                continue;
            }

            var notification = new AcpNotification("session/update", frame.Message.GetProperty("params").Clone());
            (i < steerReplyIndex ? pre : post).Add(notification);
        }

        return new ScriptedRun(
            SessionId: "session-1",
            IsResumed: false,
            Pre: pre,
            Post: post,
            SteerReply: steerReply,
            PromptResult: promptReply);
    }

    /// <summary>The params of the recorded <c>session/steer</c> request in a fixture.</summary>
    private static JsonElement RecordedSteerParams(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Codebuddy", fileName);
        return File.ReadLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement)
            .Where(root => root.TryGetProperty("msg", out _) && root.GetProperty("dir").GetString() == "out")
            .Select(root => root.GetProperty("msg"))
            .Single(message => MethodOf(message) == "session/steer")
            .GetProperty("params")
            .Clone();
    }

    private static string? MethodOf(JsonElement message)
    {
        return message.TryGetProperty("method", out var method) ? method.GetString() : null;
    }

    private static int? IdOf(JsonElement message)
    {
        return message.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt32() : null;
    }

    private static AcpNotification Update(string kind, Dictionary<string, object?> fields)
    {
        var update = new Dictionary<string, object?>(fields) { ["sessionUpdate"] = kind };
        return new AcpNotification("session/update", JsonSerializer.SerializeToElement(new { sessionId = "session-1", update }));
    }

    private static Dictionary<string, object?> Text(string text) => new() { ["content"] = new { type = "text", text } };

    private static Dictionary<string, object?> ReplayMarker(string value) =>
        new() { ["_meta"] = new Dictionary<string, object?> { ["codebuddy.ai/historyReplay"] = value } };

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

    private sealed record ScriptedRun(
        string SessionId,
        bool IsResumed,
        IReadOnlyList<AcpNotification> Pre,
        IReadOnlyList<AcpNotification> Post,
        JsonElement SteerReply,
        JsonElement PromptResult);

    private sealed record SteeredRun(
        List<CliMessage> Messages,
        CodebuddySteerResult? SteerResult,
        List<(string SteerId, CodebuddySteerOutcome Outcome)> Outcomes,
        ScriptedAcpClient Client,
        CodebuddyRunSteering Steering);

    private sealed class SteeringTestProvider(ScriptedAcpClient client, Version? cliVersion)
        : CodebuddyProvider(new StubExecutableResolver(), new CliProcessManager(), new StubRuntimeEnvironmentResolver())
    {
        protected override IAcpSessionClient CreateSessionClient(ProcessStartContext startContext) => client;

        protected override Task<Version?> ReadCliVersionAsync(string executablePath, CancellationToken cancellationToken) =>
            Task.FromResult(cliVersion);
    }

    /// <summary>
    /// Plays one recorded run. Pre notifications arrive when the prompt starts. The steer reply and the post notifications are
    /// released together when the provider writes the steer, in the recorded order. Release ends the run when no steer was written.
    /// </summary>
    private sealed class ScriptedAcpClient(ScriptedRun run) : IAcpSessionClient
    {
        private readonly Channel<AcpNotification> _notifications = Channel.CreateUnbounded<AcpNotification>();
        private readonly TaskCompletionSource<JsonElement> _prompt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _released;

        public List<string> SteerRequests { get; } = [];

        public bool Disposed { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<JsonElement> InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.SerializeToElement(new { protocolVersion = 1 }));

        public Task<JsonElement> InvokeBootstrapMethodAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(method, "session/steer", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
            }

            SteerRequests.Add(JsonSerializer.Serialize(parameters));
            Release();
            return Task.FromResult(run.SteerReply);
        }

        public Task<AcpSessionHandle> StartSessionAsync(string workingDirectory, string? sessionId, string? model, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AcpSessionHandle(run.SessionId, run.IsResumed, JsonSerializer.SerializeToElement(new { sessionId = run.SessionId })));

        public Task<JsonElement> SetModeAsync(string sessionId, string modeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.SerializeToElement(new { }));

        public Task<JsonElement> SendPromptAsync(string sessionId, string prompt, CancellationToken cancellationToken = default)
        {
            foreach (var notification in run.Pre)
            {
                _notifications.Writer.TryWrite(notification);
            }

            return _prompt.Task;
        }

        /// <summary>
        /// Releases the post notifications and completes the prompt, once. A run that wrote a steer has already released it.
        /// </summary>
        public void Release()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            foreach (var notification in run.Post)
            {
                _notifications.Writer.TryWrite(notification);
            }

            _notifications.Writer.TryWrite(new AcpNotification("session/update", JsonSerializer.SerializeToElement(new
            {
                sessionId = run.SessionId,
                update = new { sessionUpdate = "prompt_completed", stopReason = "end_turn" },
                result = run.PromptResult
            })));
            _prompt.TrySetResult(run.PromptResult);
        }

        public async IAsyncEnumerable<AcpNotification> ReceiveNotificationsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var notification in _notifications.Reader.ReadAllAsync(cancellationToken))
            {
                yield return notification;
            }
        }

        public string? GetDiagnosticSummary() => null;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _notifications.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubExecutableResolver : CliExecutableResolver
    {
        public override string? ResolveExecutablePath(string? executableName, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => executableName;

        public override string? ResolveFirstAvailablePath(IEnumerable<string> executableNames, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => executableNames.FirstOrDefault();
    }

    private sealed class StubRuntimeEnvironmentResolver : IRuntimeEnvironmentResolver
    {
        public Task<IReadOnlyDictionary<string, string?>> ResolveAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?>
            {
                ["PATH"] = "/tmp/bin"
            });
        }
    }
}
