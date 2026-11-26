// Feature 2A.2 `image`: does an image attachment reach the model as pixels, as a file path only, or not at all.
//
// HERMETIC tests: the wire side. AcpSessionClient must put an image block after the text block in session/prompt, and the
// Antigravity provider's own prompt path (which takes text only) must be checked as it is today. The scripted agent is
// SYNTHETIC; it is not agy. The initialize capability is the verified 1.5.17 fact, copied as a synthetic value.
// REAL tests (Real_ prefix): need HAGICODE_REAL_CLI_TESTS=1 and agy on PATH. They skip with a reason otherwise and are
// both passed on the 2026-11-25 run (agy 1.5.17). Read them as follows:
//   Real_agent_reads_the_pixels_of_an_attached_png: passes only if the model answers from the image bytes.
//   Real_agent_opens_the_image_from_its_path_alone: a control that uses text only. A pass shows the CLI can read the file
//   itself, which is a path route. Neither test can prove a negative. Pixel delivery is "supported" only if the first passes.
using System.Text.Json.Nodes;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Providers.Antigravity;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class AntigravityImageFeatureTests
{
    [Test]
    public async Task Image_block_follows_the_text_block_in_the_session_prompt_frame()
    {
        var agent = new AntigravityScriptedAgent();
        await using var client = new AcpSessionClient(agent);
        await client.ConnectAsync();
        await client.InitializeAsync();
        await client.StartSessionAsync(Directory.GetCurrentDirectory(), null, null);

        await client.SendPromptAsync(AntigravityFixtures.SessionId, "describe", [new AcpImageBlock("image/png", "AAEC")]);

        var blocks = agent.RequestsFor("session/prompt").Single()["params"]!["prompt"]!.AsArray();
        blocks.Select(static block => block!["type"]!.GetValue<string>()).ShouldBe(["text", "image"]);
        blocks[1]!["mimeType"]!.GetValue<string>().ShouldBe("image/png");
        blocks[1]!["data"]!.GetValue<string>().ShouldBe("AAEC");
    }

    [Test]
    public async Task Agent_advertises_image_prompt_capability_on_initialize()
    {
        // The verified 1.5.17 fact that makes an image block worth sending. Checked against the scripted initialize result.
        await using var client = new AcpSessionClient(new AntigravityScriptedAgent());
        await client.ConnectAsync();

        var initialize = await client.InitializeAsync();

        initialize.GetProperty("agentCapabilities").GetProperty("promptCapabilities").GetProperty("image").GetBoolean().ShouldBeTrue();
    }

    [Test]
    public async Task Provider_prompt_frame_carries_a_text_block_only_so_no_image_reaches_the_cli_through_libs()
    {
        // Gap, recorded in the matrix: AntigravityProvider.ExecuteAsync accepts text only and calls SendPromptAsync(text).
        // An image can therefore reach agy through HagiCode only if the provider gains an image input.
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.EndTurn());
        var provider = new ScriptedAntigravityProvider(agent);

        await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "describe the image"));

        var blocks = agent.RequestsFor("session/prompt").Single()["params"]!["prompt"]!.AsArray();
        blocks.Count.ShouldBe(1);
        blocks[0]!["type"]!.GetValue<string>().ShouldBe("text");
    }

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task Real_agent_reads_the_pixels_of_an_attached_png()
    {
        var agyPath = AntigravityRealCli.RequireAgy();
        await using var client = AntigravityRealCli.CreateSessionClient(agyPath, Directory.GetCurrentDirectory());
        await client.ConnectAsync();
        await client.InitializeAsync();
        var sessionId = (await client.StartSessionAsync(Directory.GetCurrentDirectory(), null, null)).SessionId;

        var answer = await AntigravityRealCli.PromptForTextAsync(
            client,
            sessionId,
            "What single colour is the attached image? Answer with one word.",
            [new AcpImageBlock("image/png", AntigravityRealCli.RedPixelPngBase64)]);

        answer.ToLowerInvariant().ShouldContain("red");
    }

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task Real_agent_opens_the_image_from_its_path_alone()
    {
        var agyPath = AntigravityRealCli.RequireAgy();
        var imagePath = Path.Combine(Path.GetTempPath(), "hagicode-agy-image-probe", "red-1x1.png");
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        await File.WriteAllBytesAsync(imagePath, Convert.FromBase64String(AntigravityRealCli.RedPixelPngBase64));

        await using var client = AntigravityRealCli.CreateSessionClient(agyPath, Directory.GetCurrentDirectory());
        await client.ConnectAsync();
        await client.InitializeAsync();
        var sessionId = (await client.StartSessionAsync(Directory.GetCurrentDirectory(), null, null)).SessionId;

        var answer = await AntigravityRealCli.PromptForTextAsync(
            client,
            sessionId,
            $"Open the image file at {imagePath} and tell me its single colour. Answer with one word.");

        answer.ToLowerInvariant().ShouldContain("red");
    }
}
