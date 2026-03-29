using HagiCode.Libs.Providers.OpenCode;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class OpenCodeFixtureServerTests
{
    [Fact]
    public async Task FixtureServer_supports_health_create_and_prompt_round_trip()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync();
        using var httpClient = new HttpClient { BaseAddress = fixture.BaseUri };
        using var client = new OpenCodeHttpClient(httpClient, directory: "/tmp/hagicode-libs-fixture", workspace: "fixture-workspace");

        var health = await client.HealthAsync();
        var session = await client.CreateSessionAsync("fixture-session");
        var response = await client.PromptAsync(
            session.Id,
            OpenCodeSessionPromptRequest.FromText("Reply with the single word READY."));

        health.Healthy.ShouldBeTrue();
        session.Title.ShouldBe("fixture-session");
        session.Directory.ShouldBe("/tmp/hagicode-libs-fixture");
        session.WorkspaceId.ShouldBe("fixture-workspace");
        response.GetTextContent().ShouldBe("READY");
    }

    [Fact]
    public async Task FixtureServer_can_emit_structured_empty_text_diagnostics()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            EmptyTextModelIds = ["catalog/model-a"],
        });
        using var httpClient = new HttpClient { BaseAddress = fixture.BaseUri };
        using var client = new OpenCodeHttpClient(httpClient, directory: null, workspace: null);
        var session = await client.CreateSessionAsync("fixture-diagnostics");

        var response = await client.PromptAsync(
            session.Id,
            OpenCodeSessionPromptRequest.FromText(
                "Reply with the single word READY.",
                new OpenCodeModelSelection
                {
                    ProviderId = "catalog",
                    ModelId = "model-a",
                }));

        response.GetTextContent().ShouldBeEmpty();
        var diagnostic = response.BuildDiagnosticSummary();
        diagnostic.ShouldContain("no readable text content");
        diagnostic.ShouldContain("partTypes=step-finish, error");
        diagnostic.ShouldContain("OpenCode fixture emitted an assistant envelope without text content.");
    }
}
