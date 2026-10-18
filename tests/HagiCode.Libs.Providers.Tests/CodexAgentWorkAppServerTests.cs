using System.Text.Json;
using System.Text.Json.Nodes;
using HagiCode.Libs.Core.AgentWork;
using HagiCode.Libs.Providers.Codex.AppServer;
using ManagedCode.CodexSharpSDK.Models;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Drives <see cref="CodexAppServerClient"/> through a scripted app-server and checks that agent work snapshots arrive in stream
/// order while the transcript stays the one the event mapper produces on its own.
/// </summary>
public sealed class CodexAgentWorkAppServerTests
{
    private const string FeatureName = "default_mode_request_user_input";
    private const string SubagentFixture = "subagent-run-codex-0.163.0.json";

    [Test]
    public async Task Replayed_subagent_run_keeps_the_transcript_and_publishes_snapshots_in_order()
    {
        using var cts = Deadline();
        var capture = LoadCapture();
        var server = new CodexAppServerScriptedServer();
        await using var client = new CodexAppServerClient(server.ClientReader, server.ClientWriter, null, Timeout.InfiniteTimeSpan);
        client.Start();
        await Handshake(server, client, cts.Token);

        var events = CollectAsync(client.RunTurnAsync(capture.MainThreadId, Input(), new JsonObject(), cts.Token));
        await server.AnswerAsync("turn/start", TurnStartedResult(capture.MainTurnId), cts.Token);
        foreach (var notification in capture.Notifications)
        {
            server.Notify(notification.Method, (JsonObject)notification.Parameters.DeepClone());
        }

        var collected = await events;
        var expectedTranscript = ExpectedTranscript(capture);
        var transcript = collected.Where(threadEvent => threadEvent is not CodexAgentWorkSnapshotEvent).ToList();
        transcript.Select(threadEvent => threadEvent.GetType()).ShouldBe(expectedTranscript.Select(threadEvent => threadEvent.GetType()));
        transcript.OfType<ItemCompletedEvent>().Select(completed => completed.Item.Id)
            .ShouldBe(expectedTranscript.OfType<ItemCompletedEvent>().Select(completed => completed.Item.Id));

        var snapshots = collected.OfType<CodexAgentWorkSnapshotEvent>().ToList();
        snapshots.Count.ShouldBeGreaterThan(1);
        var finalIndex = collected.FindLastIndex(threadEvent => threadEvent is CodexAgentWorkSnapshotEvent);
        finalIndex.ShouldBeLessThan(collected.FindIndex(threadEvent => threadEvent is TurnCompletedEvent));

        var items = snapshots[^1].Snapshot.Items.ToDictionary(item => item.Id);
        items.Keys.ShouldBe(["codex:" + capture.Subagents.A, "codex:" + capture.Subagents.B, "codex:" + capture.Subagents.C, "codex:" + capture.Subagents.Gamma], ignoreOrder: true);
        items["codex:" + capture.Subagents.A].Status.ShouldBe(AgentWorkStatuses.Completed);
        items["codex:" + capture.Subagents.A].Summary.ShouldBe("ALPHA");
        items["codex:" + capture.Subagents.B].Status.ShouldBe(AgentWorkStatuses.Stopped);
        items["codex:" + capture.Subagents.C].Status.ShouldBe(AgentWorkStatuses.Completed);
        items["codex:" + capture.Subagents.C].Summary.ShouldBe("GAMMA");
        items["codex:" + capture.Subagents.Gamma].Status.ShouldBe(AgentWorkStatuses.Completed);
        items["codex:" + capture.Subagents.Gamma].ParentId.ShouldBe("codex:" + capture.Subagents.C);
        items["codex:" + capture.Subagents.A].ParentId.ShouldBeNull();
        items.Values.All(item => item.Kind == AgentWorkKinds.Subagent && !item.BlocksCompletion).ShouldBeTrue();
        items["codex:" + capture.Subagents.A].Title.ShouldBe("/root/a");
        items["codex:" + capture.Subagents.Gamma].Title.ShouldBe("/root/c/gamma");
    }

    [Test]
    public async Task Subagent_usage_is_summed_from_the_captured_child_thread_notifications()
    {
        using var cts = Deadline();
        var capture = LoadCapture();
        var server = new CodexAppServerScriptedServer();
        await using var client = new CodexAppServerClient(server.ClientReader, server.ClientWriter, null, Timeout.InfiniteTimeSpan);
        client.Start();
        await Handshake(server, client, cts.Token);

        var events = CollectAsync(client.RunTurnAsync(capture.MainThreadId, Input(), new JsonObject(), cts.Token));
        await server.AnswerAsync("turn/start", TurnStartedResult(capture.MainTurnId), cts.Token);
        foreach (var notification in capture.Notifications)
        {
            server.Notify(notification.Method, (JsonObject)notification.Parameters.DeepClone());
        }

        var collected = await events;
        var expected = capture.Notifications
            .Where(notification => notification.Method == "thread/tokenUsage/updated"
                                   && notification.Parameters["threadId"]!.GetValue<string>() == capture.Subagents.A)
            .Sum(notification => notification.Parameters["tokenUsage"]!["last"]!["totalTokens"]!.GetValue<long>());
        var usage = collected.OfType<CodexAgentWorkSnapshotEvent>().Last().Snapshot.Items
            .Single(item => item.Id == "codex:" + capture.Subagents.A).Usage.ShouldNotBeNull();
        usage.TotalTokens.ShouldBe(expected);
        usage.ToolUses.ShouldBeNull();
    }

    [Test]
    public async Task A_run_without_subagents_publishes_no_snapshot_and_its_transcript_is_unchanged()
    {
        using var cts = Deadline();
        var server = new CodexAppServerScriptedServer();
        await using var client = new CodexAppServerClient(server.ClientReader, server.ClientWriter, null, Timeout.InfiniteTimeSpan);
        client.Start();
        await Handshake(server, client, cts.Token);

        var events = CollectAsync(client.RunTurnAsync(MainThread, Input(), new JsonObject(), cts.Token));
        await server.AnswerAsync("turn/start", TurnStartedResult(MainTurn), cts.Token);
        server.Notify("turn/started", TurnNotification(MainThread, MainTurn, "inProgress"));
        server.Notify("item/completed", AgentMessage(MainThread, "msg-1", "hello"));
        server.Notify("turn/completed", TurnNotification(MainThread, MainTurn, "completed"));

        var collected = await events;
        collected.OfType<CodexAgentWorkSnapshotEvent>().ShouldBeEmpty();
        collected.Select(threadEvent => threadEvent.GetType()).ShouldBe([typeof(TurnStartedEvent), typeof(ItemCompletedEvent), typeof(TurnCompletedEvent)]);
    }

    [Test]
    public async Task A_malformed_collaboration_notification_leaves_the_transcript_and_the_previous_list_untouched()
    {
        using var cts = Deadline();
        var server = new CodexAppServerScriptedServer();
        await using var client = new CodexAppServerClient(server.ClientReader, server.ClientWriter, null, Timeout.InfiniteTimeSpan);
        client.Start();
        await Handshake(server, client, cts.Token);

        var events = CollectAsync(client.RunTurnAsync(MainThread, Input(), new JsonObject(), cts.Token));
        await server.AnswerAsync("turn/start", TurnStartedResult(MainTurn), cts.Token);
        server.Notify("item/started", Activity(MainThread, "T1", "started"));
        server.Notify("item/completed", new JsonObject
        {
            ["threadId"] = MainThread,
            ["turnId"] = MainTurn,
            ["item"] = new JsonObject { ["type"] = "collabAgentToolCall", ["id"] = "wait-1", ["tool"] = "wait", ["agentsStates"] = "broken" }
        });
        server.Notify("item/completed", AgentMessage(MainThread, "msg-1", "still here"));
        server.Notify("turn/completed", TurnNotification(MainThread, MainTurn, "completed"));

        var collected = await events;
        collected.OfType<CodexAgentWorkSnapshotEvent>().Count().ShouldBe(1);
        collected.OfType<ItemCompletedEvent>().Select(completed => completed.Item).OfType<AgentMessageItem>().Single().Text.ShouldBe("still here");
        collected.Last().ShouldBeOfType<TurnCompletedEvent>().Result.ShouldBe("still here");
    }

    [Test]
    public async Task A_change_observed_between_turns_is_delivered_before_the_next_turn_starts()
    {
        using var cts = Deadline();
        var server = new CodexAppServerScriptedServer();
        await using var client = new CodexAppServerClient(server.ClientReader, server.ClientWriter, null, Timeout.InfiniteTimeSpan);
        client.Start();
        await Handshake(server, client, cts.Token);

        // No turn is running here. The reply to the next request is read after the notification, so the notification is observed first.
        server.Notify("item/started", Activity(MainThread, "T1", "started"));
        var syncRequest = client.HasExperimentalFeatureAsync("sync-point", cts.Token);
        await server.AnswerAsync("experimentalFeature/list", new JsonObject { ["data"] = new JsonArray(), ["nextCursor"] = null }, cts.Token);
        (await syncRequest).ShouldBeFalse();

        var events = CollectAsync(client.RunTurnAsync(MainThread, Input(), new JsonObject(), cts.Token));
        await server.AnswerAsync("turn/start", TurnStartedResult(MainTurn), cts.Token);
        server.Notify("turn/started", TurnNotification(MainThread, MainTurn, "inProgress"));
        server.Notify("turn/completed", TurnNotification(MainThread, MainTurn, "completed"));

        var collected = await events;
        collected.First().ShouldBeOfType<CodexAgentWorkSnapshotEvent>();
        collected.Select(threadEvent => threadEvent.GetType()).Skip(1).ShouldBe([typeof(TurnStartedEvent), typeof(TurnCompletedEvent)]);
    }

    private static CodexAgentWorkCapture LoadCapture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Codex", "AppServer", SubagentFixture);
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var notifications = root["notifications"]!.AsArray()
            .Select(node => new CapturedNotification(node!["method"]!.GetValue<string>(), node["params"]!.AsObject()))
            .ToList();
        var main = root["mainThreadId"]!.GetValue<string>();
        return new CodexAgentWorkCapture(
            main,
            root["mainTurnId"]!.GetValue<string>(),
            notifications,
            new CapturedSubagents(
                A: "01a14d34-fb89-72e1-8ea6-0a0494314ffc",
                B: "01a14d35-166f-7f10-a817-7a859531af6e",
                C: "01a14d35-281c-7300-8746-6bdfaefbb796",
                Gamma: "01a14d35-3d7a-7071-b56b-37b1b636b139"));
    }

    private static List<ThreadEvent> ExpectedTranscript(CodexAgentWorkCapture capture)
    {
        // The pre-change mapping: every notification the captured run sent, through the event mapper alone.
        var mapper = new CodexAppServerEventMapper();
        var expected = new List<ThreadEvent>();
        foreach (var notification in capture.Notifications)
        {
            using var document = JsonDocument.Parse(notification.Parameters.ToJsonString());
            var mapped = mapper.Map(notification.Method, document.RootElement.Clone(), capture.MainTurnId);
            if (mapped is not null)
            {
                expected.Add(mapped);
            }
        }

        return expected;
    }

    private static async Task Handshake(CodexAppServerScriptedServer server, CodexAppServerClient client, CancellationToken cancellationToken)
    {
        var initialize = client.InitializeAsync(cancellationToken);
        await server.AnswerAsync("initialize", new JsonObject { ["userAgent"] = "scripted/0.163.0" }, cancellationToken);
        await server.ExpectNotificationAsync("initialized", cancellationToken);
        (await initialize).ShouldBe("scripted/0.163.0");

        var feature = client.HasExperimentalFeatureAsync(FeatureName, cancellationToken);
        await server.AnswerAsync("experimentalFeature/list", new JsonObject
        {
            ["data"] = new JsonArray(new JsonObject { ["name"] = FeatureName }),
            ["nextCursor"] = null
        }, cancellationToken);
        (await feature).ShouldBeTrue();
    }

    private const string MainThread = "01a14d34-ea6c-77b1-8be5-9416ec11937a";
    private const string MainTurn = "01a14d34-ea9c-7e40-b1ee-63347b33fbff";

    private static JsonArray Input() => new(new JsonObject { ["type"] = "text", ["text"] = "Run the subagents.", ["text_elements"] = new JsonArray() });

    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(30));

    private static JsonObject TurnStartedResult(string turnId) => new()
    {
        ["turn"] = new JsonObject { ["id"] = turnId, ["status"] = "inProgress", ["items"] = new JsonArray() }
    };

    private static JsonObject TurnNotification(string threadId, string turnId, string status) => new()
    {
        ["threadId"] = threadId,
        ["turn"] = new JsonObject { ["id"] = turnId, ["status"] = status, ["items"] = new JsonArray(), ["error"] = null }
    };

    private static JsonObject AgentMessage(string threadId, string id, string text) => new()
    {
        ["threadId"] = threadId,
        ["turnId"] = MainTurn,
        ["item"] = new JsonObject { ["type"] = "agentMessage", ["id"] = id, ["text"] = text }
    };

    private static JsonObject Activity(string reportingThreadId, string agentName, string kind) => new()
    {
        ["threadId"] = reportingThreadId,
        ["turnId"] = MainTurn,
        ["item"] = new JsonObject
        {
            ["type"] = "subAgentActivity",
            ["id"] = $"activity-{agentName}-{kind}",
            ["kind"] = kind,
            ["agentThreadId"] = "01a14d35-166f-7f10-a817-7a859531af6e",
            ["agentPath"] = "/root/" + agentName.ToLowerInvariant()
        }
    };

    private static async Task<List<ThreadEvent>> CollectAsync(IAsyncEnumerable<ThreadEvent> source)
    {
        var events = new List<ThreadEvent>();
        await foreach (var threadEvent in source)
        {
            events.Add(threadEvent);
        }

        return events;
    }

    private sealed record CapturedNotification(string Method, JsonObject Parameters);

    private sealed record CapturedSubagents(string A, string B, string C, string Gamma);

    private sealed record CodexAgentWorkCapture(
        string MainThreadId,
        string MainTurnId,
        IReadOnlyList<CapturedNotification> Notifications,
        CapturedSubagents Subagents);
}
