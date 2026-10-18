using HagiCode.Libs.Core.AgentWork;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.OpenCode;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Checks how <see cref="OpenCodeProvider"/> yields agent work snapshots in stream order, against the scripted fixture server.
/// </summary>
public sealed class OpenCodeAgentWorkProviderTests
{
    private const string ChildModel = "fixture/child-model";

    [Test]
    public async Task A_child_snapshot_is_yielded_between_the_session_start_and_the_terminal_message_in_stream_order()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            ChildSessionModelIds = [ChildModel],
            ChildSessionScript = new OpenCodeFixtureChildSessionScript { ChildRunTime = TimeSpan.FromMilliseconds(300) },
        });
        await using var provider = new OpenCodeProvider(new CliExecutableResolver());

        var messages = await DrainAsync(provider.ExecuteAsync(Options(fixture, ChildModel), "count the files"));

        var types = messages.Select(static message => message.Type).ToList();
        types.First().ShouldBe("session.started");
        types.Last().ShouldBe("terminal.completed");
        types[^3..].ShouldBe([AgentWorkSnapshot.MessageType, "assistant", "terminal.completed"]);

        var snapshots = messages.Where(static message => message.Type == AgentWorkSnapshot.MessageType).ToList();
        snapshots.Count.ShouldBeGreaterThanOrEqualTo(2);
        AgentWorkSnapshot.TryFromCliMessage(snapshots[^1], out var last).ShouldBeTrue();
        last.Items.Single().Status.ShouldBe(AgentWorkStatuses.Completed);
        last.Items.Single().Id.ShouldStartWith("opencode:");

        var assistant = messages.Single(static message => message.Type == "assistant");
        assistant.Content.GetProperty("text").GetString().ShouldBe("PARENT-READY");
    }

    [Test]
    public async Task A_run_without_child_sessions_yields_the_same_messages_as_before_and_no_snapshot()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            ChildSessionModelIds = [ChildModel],
        });
        await using var provider = new OpenCodeProvider(new CliExecutableResolver());

        var messages = await DrainMessagesAsync(provider, Options(fixture, "fixture/plain-model"), "Say hello.");

        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
    }

    [Test]
    public async Task With_snapshot_emission_turned_off_the_run_ends_at_the_parent_result_and_yields_no_snapshot()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            ChildSessionModelIds = [ChildModel],
            ChildSessionScript = new OpenCodeFixtureChildSessionScript { ChildRunTime = TimeSpan.FromMilliseconds(300) },
        });
        await using var provider = new OpenCodeProvider(new CliExecutableResolver());
        var options = Options(fixture, ChildModel) with { EmitAgentWorkSnapshots = false };

        var messages = await DrainAsync(provider.ExecuteAsync(options, "count the files"));

        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
        messages.Single(static message => message.Type == "assistant").Content.GetProperty("text").GetString().ShouldBe("PARENT-READY");
    }

    [Test]
    public async Task A_question_and_a_snapshot_in_one_run_are_both_yielded_in_stream_order()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            ChildSessionModelIds = [ChildModel],
            ChildSessionScript = new OpenCodeFixtureChildSessionScript
            {
                ChildRunTime = TimeSpan.FromMilliseconds(300),
                FollowUpText = "FOLLOW-UP",
                FollowUpQuestion = true,
            },
        });
        await using var provider = new OpenCodeProvider(new CliExecutableResolver());
        var handled = new List<OpenCodeUserInputRequest>();
        var options = Options(fixture, ChildModel) with
        {
            UserInputHandler = (request, _) =>
            {
                handled.Add(request);
                return Task.FromResult<OpenCodeUserInputResponse?>(new OpenCodeUserInputResponse(new Dictionary<string, IReadOnlyList<string>>
                {
                    ["Which color do you prefer?"] = ["Red"],
                }));
            },
        };

        var messages = await DrainAsync(provider.ExecuteAsync(options, "count the files"));

        handled.ShouldHaveSingleItem();
        var types = messages.Select(static message => message.Type).ToList();
        var question = types.IndexOf("question");
        var assistant = types.IndexOf("assistant");
        question.ShouldBeGreaterThan(0);
        assistant.ShouldBeGreaterThan(question);
        types.Where(static type => type == AgentWorkSnapshot.MessageType).ShouldNotBeEmpty();
        types.Last().ShouldBe("terminal.completed");
        messages[assistant].Content.GetProperty("text").GetString().ShouldBe($"PARENT-READY{Environment.NewLine}FOLLOW-UP");
    }

    [Test]
    public void The_agent_work_options_have_their_documented_defaults()
    {
        var options = new OpenCodeOptions();

        options.EmitAgentWorkSnapshots.ShouldBeTrue();
        options.AgentWorkWaitTimeout.ShouldBe(TimeSpan.FromMinutes(10));
        options.AgentWorkSettleInterval.ShouldBe(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task A_non_positive_wait_timeout_is_rejected_before_any_request_is_sent()
    {
        await using var provider = new OpenCodeProvider(new CliExecutableResolver());
        var options = new OpenCodeOptions
        {
            BaseUrl = "http://127.0.0.1:1",
            AgentWorkWaitTimeout = TimeSpan.Zero,
        };

        var exception = await Should.ThrowAsync<ArgumentOutOfRangeException>(() => DrainAsync(provider.ExecuteAsync(options, "hi")));
        exception.ParamName.ShouldBe("AgentWorkWaitTimeout");
    }

    [Test]
    public async Task A_negative_or_infinite_settle_interval_is_rejected_before_any_request_is_sent()
    {
        await using var provider = new OpenCodeProvider(new CliExecutableResolver());

        var negative = await Should.ThrowAsync<ArgumentOutOfRangeException>(() => DrainAsync(provider.ExecuteAsync(
            new OpenCodeOptions { BaseUrl = "http://127.0.0.1:1", AgentWorkSettleInterval = TimeSpan.FromMilliseconds(-1) },
            "hi")));
        negative.ParamName.ShouldBe("AgentWorkSettleInterval");

        var infinite = await Should.ThrowAsync<ArgumentOutOfRangeException>(() => DrainAsync(provider.ExecuteAsync(
            new OpenCodeOptions { BaseUrl = "http://127.0.0.1:1", AgentWorkSettleInterval = Timeout.InfiniteTimeSpan },
            "hi")));
        infinite.ParamName.ShouldBe("AgentWorkSettleInterval");
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

    private static Task<List<CliMessage>> DrainMessagesAsync(OpenCodeProvider provider, OpenCodeOptions options, string prompt)
    {
        return DrainAsync(provider.ExecuteAsync(options, prompt));
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
