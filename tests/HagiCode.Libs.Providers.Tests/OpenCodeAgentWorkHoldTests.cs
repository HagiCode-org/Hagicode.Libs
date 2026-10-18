using HagiCode.Libs.Core.AgentWork;
using HagiCode.Libs.Providers.OpenCode;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Pins the agent work tracking and the hold-open rule of <see cref="OpenCodeHttpClient"/> against the scripted fixture server.
/// </summary>
public sealed class OpenCodeAgentWorkHoldTests
{
    private const string ChildModelId = "child-model";

    [Test]
    public async Task A_running_child_holds_the_turn_until_it_ends_and_the_final_snapshot_shows_it_completed()
    {
        await using var fixture = await ChildFixture.StartAsync(new OpenCodeFixtureChildSessionScript
        {
            ChildRunTime = TimeSpan.FromMilliseconds(400),
        });

        var snapshots = new List<AgentWorkSnapshot>();
        var response = await fixture.PromptAsync(snapshots, waitTimeout: TimeSpan.FromSeconds(30));

        // The child text stays out of the parent's transcript; the held turn returns the parent's own text.
        response.GetTextContent().ShouldBe("PARENT-READY");

        // The first snapshot in which the child blocks completion is the one taken once the parent reported success.
        var held = snapshots.First(snapshot => snapshot.Items.Any(item => item.BlocksCompletion));
        var heldItem = held.Items.Single();
        heldItem.Id.ShouldStartWith("opencode:");
        heldItem.Kind.ShouldBe(AgentWorkKinds.Subagent);
        heldItem.ParentId.ShouldBeNull();
        heldItem.Title.ShouldBe("count files");
        heldItem.Status.ShouldBe(AgentWorkStatuses.Running);

        var final = snapshots[^1].Items.Single();
        final.Status.ShouldBe(AgentWorkStatuses.Completed);
        final.BlocksCompletion.ShouldBeFalse();
        final.EndedAt.ShouldNotBeNull();
        final.Summary.ShouldBe("CHILD-ONLY-TEXT");
        final.Usage.ShouldNotBeNull();
        final.Usage.ToolUses.ShouldBe(1);
    }

    [Test]
    public async Task A_follow_up_turn_of_the_prompted_session_is_part_of_the_same_result()
    {
        await using var fixture = await ChildFixture.StartAsync(new OpenCodeFixtureChildSessionScript
        {
            ChildRunTime = TimeSpan.FromMilliseconds(300),
            FollowUpText = "FOLLOW-UP",
        });

        var snapshots = new List<AgentWorkSnapshot>();
        var response = await fixture.PromptAsync(snapshots, waitTimeout: TimeSpan.FromSeconds(30));

        response.GetTextContent().ShouldBe($"PARENT-READY{Environment.NewLine}FOLLOW-UP");
        snapshots[^1].Items.Single().Status.ShouldBe(AgentWorkStatuses.Completed);
    }

    [Test]
    public async Task A_child_that_outlives_the_wait_is_reported_stopped_when_the_wait_expires()
    {
        await using var fixture = await ChildFixture.StartAsync(new OpenCodeFixtureChildSessionScript
        {
            ChildNeverEnds = true,
        });

        var snapshots = new List<AgentWorkSnapshot>();
        var started = DateTimeOffset.UtcNow;
        var response = await fixture.PromptAsync(snapshots, waitTimeout: TimeSpan.FromMilliseconds(500), settleInterval: TimeSpan.FromMilliseconds(100));

        response.GetTextContent().ShouldBe("PARENT-READY");
        (DateTimeOffset.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(20));

        var final = snapshots[^1].Items.Single();
        final.Status.ShouldBe(AgentWorkStatuses.Stopped);
        final.BlocksCompletion.ShouldBeFalse();
        final.EndedAt.ShouldNotBeNull();
    }

    [Test]
    public async Task A_parent_interrupt_while_a_child_runs_ends_the_turn_at_once_and_stops_the_child()
    {
        await using var fixture = await ChildFixture.StartAsync(new OpenCodeFixtureChildSessionScript
        {
            ParentInterruptsWhileChildRuns = true,
        });

        var snapshots = new List<AgentWorkSnapshot>();
        var started = DateTimeOffset.UtcNow;
        var response = await fixture.PromptAsync(snapshots, waitTimeout: TimeSpan.FromSeconds(30));

        (DateTimeOffset.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(20));
        response.GetTextContent().ShouldBeEmpty();
        snapshots[^1].Items.Single().Status.ShouldBe(AgentWorkStatuses.Stopped);
    }

    [Test]
    public async Task Caller_cancellation_while_the_turn_is_held_stops_the_wait()
    {
        await using var fixture = await ChildFixture.StartAsync(new OpenCodeFixtureChildSessionScript
        {
            ChildNeverEnds = true,
        });

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        var snapshots = new List<AgentWorkSnapshot>();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await fixture.PromptAsync(snapshots, waitTimeout: TimeSpan.FromSeconds(30), cancellationToken: cancellation.Token));
    }

    [Test]
    public async Task A_run_without_child_sessions_emits_no_snapshot_and_keeps_its_text()
    {
        await using var fixture = await ChildFixture.StartAsync(new OpenCodeFixtureChildSessionScript());

        var snapshots = new List<AgentWorkSnapshot>();
        var response = await fixture.PromptAsync(snapshots, waitTimeout: TimeSpan.FromSeconds(30), modelId: "plain-model");

        response.GetTextContent().ShouldStartWith("READY");
        snapshots.ShouldBeEmpty();
    }

    [Test]
    public async Task A_question_raised_by_the_follow_up_turn_is_answered_while_the_turn_is_held()
    {
        await using var fixture = await ChildFixture.StartAsync(new OpenCodeFixtureChildSessionScript
        {
            ChildRunTime = TimeSpan.FromMilliseconds(300),
            FollowUpText = "FOLLOW-UP",
            FollowUpQuestion = true,
        });

        var asked = new List<OpenCodeUserInputRequest>();
        var interaction = new OpenCodeTurnInteraction(
            (request, _) =>
            {
                asked.Add(request);
                return Task.FromResult<OpenCodeUserInputResponse?>(new OpenCodeUserInputResponse(
                    new Dictionary<string, IReadOnlyList<string>> { ["Which color do you prefer?"] = ["Red"] }));
            },
            QuestionTimeout: TimeSpan.FromSeconds(30));

        var snapshots = new List<AgentWorkSnapshot>();
        var response = await fixture.PromptAsync(snapshots, waitTimeout: TimeSpan.FromSeconds(30), interaction: interaction);

        asked.Count.ShouldBe(1);
        fixture.Server.GetForms().Single().State.ShouldBe(OpenCodeFormStates.Replied);
        response.GetTextContent().ShouldBe($"PARENT-READY{Environment.NewLine}FOLLOW-UP");
    }

    /// <summary>
    /// A scripted fixture server, its client, and the prompted session of one run.
    /// </summary>
    private sealed class ChildFixture : IAsyncDisposable
    {
        private readonly HttpClient _http;

        private ChildFixture(OpenCodeFixtureServerHandle server, HttpClient http, OpenCodeHttpClient client, string sessionId)
        {
            Server = server;
            _http = http;
            Client = client;
            SessionId = sessionId;
        }

        public OpenCodeFixtureServerHandle Server { get; }

        public OpenCodeHttpClient Client { get; }

        public string SessionId { get; }

        public static async Task<ChildFixture> StartAsync(OpenCodeFixtureChildSessionScript script)
        {
            var server = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
            {
                ChildSessionModelIds = [$"fixture/{ChildModelId}"],
                ChildSessionScript = script,
            });
            var http = new HttpClient { BaseAddress = server.BaseUri, Timeout = TimeSpan.FromSeconds(60) };
            var client = new OpenCodeHttpClient(http, directory: null, workspace: null, server.ServerPassword);
            var session = await client.CreateSessionAsync("child sessions");
            await client.SetSessionModelAsync(session.Id, new OpenCodeModelSelection { ProviderId = "fixture", ModelId = ChildModelId });
            return new ChildFixture(server, http, client, session.Id);
        }

        /// <summary>
        /// Runs the prompt with the agent work of the run captured in <paramref name="snapshots"/>. A run on <paramref name="modelId"/>
        /// other than the child model takes the plain path, so it spawns nothing.
        /// </summary>
        public async Task<OpenCodeMessageEnvelope> PromptAsync(
            List<AgentWorkSnapshot> snapshots,
            TimeSpan waitTimeout,
            TimeSpan? settleInterval = null,
            OpenCodeTurnInteraction? interaction = null,
            CancellationToken cancellationToken = default,
            string? modelId = null)
        {
            if (modelId is not null)
            {
                await Client.SetSessionModelAsync(SessionId, new OpenCodeModelSelection { ProviderId = "fixture", ModelId = modelId }, cancellationToken);
            }

            var turn = new OpenCodeAgentWorkTurn(snapshots.Add, waitTimeout, settleInterval ?? TimeSpan.FromMilliseconds(200));
            return await Client.PromptAsync(
                SessionId,
                OpenCodeSessionPromptRequest.FromText("count the files"),
                interaction,
                turn,
                cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Client.Dispose();
            _http.Dispose();
            return Server.DisposeAsync();
        }
    }
}
