// HERMETIC: the Hero's native model must reach agy through ACP session/set_model.
// agy's session/new ignores the --model flag and starts on its own default model, so without set_model a run
// on a Claude Hero would silently use Gemini. Magpie-prefixed selectors are not agy model ids; set_model rejects
// them with -32602, so those runs must not send set_model at all.
using System.Text.Json.Nodes;
using HagiCode.Libs.Providers.Antigravity;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class AntigravitySetModelFeatureTests
{
    [Test]
    public async Task Native_model_is_applied_to_the_new_session_with_session_set_model()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.EndTurn(AntigravityScriptedFrames.Chunk("ok")));
        var provider = new ScriptedAntigravityProvider(agent);

        await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(
            new AntigravityOptions { Model = "claude-sonnet-4-6" },
            "hello"));

        var requests = agent.RequestsFor("session/set_model");
        requests.Count.ShouldBe(1);
        requests[0]["params"]!["modelId"]!.GetValue<string>().ShouldBe("claude-sonnet-4-6");
    }

    [Test]
    public async Task Magpie_selector_is_not_sent_to_session_set_model()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.EndTurn(AntigravityScriptedFrames.Chunk("ok")));
        var provider = new ScriptedAntigravityProvider(agent);

        await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(
            new AntigravityOptions { Model = "magpie/claude/claude-haiku-5-5" },
            "hello"));

        agent.RequestsFor("session/set_model").ShouldBeEmpty();
    }

    [Test]
    public async Task No_model_sends_no_session_set_model()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.EndTurn(AntigravityScriptedFrames.Chunk("ok")));
        var provider = new ScriptedAntigravityProvider(agent);

        await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "hello"));

        agent.RequestsFor("session/set_model").ShouldBeEmpty();
    }
}
