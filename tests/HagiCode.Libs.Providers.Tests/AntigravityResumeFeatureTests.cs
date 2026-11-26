// Feature 2A.3 `resume`: a stored session id continues the conversation, and an unknown id fails clearly.
//
// HERMETIC tests run on the scripted ACP agent (SYNTHETIC, not agy). They check the session/load and session/prompt
// frames HagiCode sends and how it reads the replies. The unknown-id error is the verified -32002 "Session not found".
// REAL tests (Real_ prefix) need HAGICODE_REAL_CLI_TESTS=1 and agy on PATH and skip with a reason otherwise.
//   Real_unknown_session_id_fails_clearly needs no model turn, so it can pass on this machine now.
//   Real_resume_continues_a_stored_conversation needs a model turn. It passed on the 2026-11-25 run (agy 1.5.17).
using HagiCode.Libs.Providers.Antigravity;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class AntigravityResumeFeatureTests
{
    private const string UnknownSessionId = "missing-session-9";

    [Test]
    public async Task Stored_session_id_is_loaded_and_the_prompt_is_sent_on_that_same_id()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.EndTurn(AntigravityScriptedFrames.Chunk("Still here.")));
        var provider = new ScriptedAntigravityProvider(agent);
        var workingDirectory = Path.Combine(Path.GetTempPath(), "agy-resume-probe");

        var messages = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(
            new AntigravityOptions { SessionId = AntigravityFixtures.SessionId, WorkingDirectory = workingDirectory },
            "continue"));

        messages[0].Type.ShouldBe("session.resumed");
        AntigravityTestHelpers.TextOf(messages).ShouldBe("Still here.");

        var load = agent.RequestsFor("session/load").Single();
        load["params"]!["sessionId"]!.GetValue<string>().ShouldBe(AntigravityFixtures.SessionId);
        load["params"]!["cwd"]!.GetValue<string>().ShouldBe(Path.GetFullPath(workingDirectory));
        agent.RequestsFor("session/new").ShouldBeEmpty();

        var prompt = agent.RequestsFor("session/prompt").Single();
        prompt["params"]!["sessionId"]!.GetValue<string>().ShouldBe(AntigravityFixtures.SessionId);
    }

    [Test]
    public async Task Replayed_history_on_a_resumed_session_is_not_surfaced_as_new_output()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.FromFixture("resume-replay-turn.jsonl"));
        var provider = new ScriptedAntigravityProvider(agent);

        var messages = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(
            new AntigravityOptions { SessionId = AntigravityFixtures.SessionId },
            "continue"));

        AntigravityTestHelpers.TextOf(messages).ShouldBe("Welcome back.");
        messages[^1].Type.ShouldBe("terminal.completed");
    }

    [Test]
    public async Task Unknown_session_id_fails_with_session_not_found_and_no_prompt_is_sent()
    {
        var agent = new AntigravityScriptedAgent().WithUnknownSession(UnknownSessionId);
        var provider = new ScriptedAntigravityProvider(agent);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(
            new AntigravityOptions { SessionId = UnknownSessionId },
            "continue")));

        error.Message.ShouldContain("session/load");
        error.Message.ShouldContain("-32002");
        error.Message.ShouldContain($"Session not found: {UnknownSessionId}");
        agent.RequestsFor("session/prompt").ShouldBeEmpty();
    }

    [Test]
    public async Task Unknown_session_id_is_not_reported_as_a_sign_in_problem()
    {
        var agent = new AntigravityScriptedAgent().WithUnknownSession(UnknownSessionId);
        var provider = new ScriptedAntigravityProvider(agent);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(
            new AntigravityOptions { SessionId = UnknownSessionId },
            "continue")));

        error.Message.ShouldNotContain("agy acp --login");
    }

    [Test]
    public async Task Without_a_stored_id_the_session_is_created_with_session_new()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.EndTurn(AntigravityScriptedFrames.Chunk("Hi.")));
        var provider = new ScriptedAntigravityProvider(agent);

        var messages = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "hello"));

        messages[0].Type.ShouldBe("session.started");
        agent.RequestsFor("session/new").Count.ShouldBe(1);
        agent.RequestsFor("session/load").ShouldBeEmpty();
    }

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task Real_unknown_session_id_fails_clearly()
    {
        AntigravityRealCli.RequireAgy();
        var provider = AntigravityRealCli.CreateProvider();

        var error = await Should.ThrowAsync<InvalidOperationException>(() => AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(
            new AntigravityOptions
            {
                SessionId = "00000000-0000-0000-0000-000000000000",
                WorkingDirectory = Directory.GetCurrentDirectory()
            },
            "hello")));

        error.Message.ShouldContain("Session not found");
    }

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task Real_resume_continues_a_stored_conversation()
    {
        AntigravityRealCli.RequireAgy();
        var provider = AntigravityRealCli.CreateProvider();
        var workingDirectory = Directory.GetCurrentDirectory();

        var first = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(
            new AntigravityOptions { WorkingDirectory = workingDirectory },
            "Remember the secret word: ORCHID. Reply with ACK only."));
        var storedId = first.First(message => message.Type == "session.started").Content.GetProperty("session_id").GetString();
        storedId.ShouldNotBeNullOrWhiteSpace();

        var second = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(
            new AntigravityOptions { SessionId = storedId, WorkingDirectory = workingDirectory },
            "What was the secret word I told you earlier? Answer with the word only."));

        second[0].Type.ShouldBe("session.resumed");
        AntigravityTestHelpers.TextOf(second).ToUpperInvariant().ShouldContain("ORCHID");
    }
}
