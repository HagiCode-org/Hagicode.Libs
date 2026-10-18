using System.Net;
using System.Text;
using System.Text.Json;
using HagiCode.Libs.Providers.OpenCode;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class OpenCodeFixtureServerTests
{
    private const string FixturePassword = "fixture-test-secret";

    [Test]
    public async Task FixtureServer_supports_health_create_and_prompt_round_trip()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            ServerPassword = FixturePassword,
        });
        using var httpClient = new HttpClient { BaseAddress = fixture.BaseUri };
        using var client = new OpenCodeHttpClient(httpClient, directory: "/tmp/hagicode-libs-fixture", workspace: null, serverPassword: fixture.ServerPassword);

        var health = await client.HealthAsync();
        var session = await client.CreateSessionAsync("fixture-session");
        var response = await PromptAndCollectAsync(client, session.Id, "Reply with the single word READY.");

        health.Version.ShouldBe("2.0.32");
        health.Healthy.ShouldBeTrue();
        session.Title.ShouldBe("fixture-session");
        session.Location.ShouldNotBeNull();
        session.Location!.Directory.ShouldBe("/tmp/hagicode-libs-fixture");
        response.GetTextContent().ShouldBe("READY");
        response.Role.ShouldBe("assistant");
    }

    [Test]
    public async Task FixtureServer_rejects_requests_without_a_valid_credential()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            ServerPassword = FixturePassword,
        });
        using var anonymous = new HttpClient { BaseAddress = fixture.BaseUri };
        using var wrong = CreateAuthenticatedHttpClient(fixture.BaseUri, "not-the-password");

        using var withoutCredential = await anonymous.GetAsync("/api/info");
        using var withWrongCredential = await wrong.GetAsync("/api/info");

        withoutCredential.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        withWrongCredential.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task FixtureServer_serves_the_legacy_html_shell_with_200_on_global_health()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            ServerPassword = FixturePassword,
        });
        using var httpClient = CreateAuthenticatedHttpClient(fixture.BaseUri, fixture.ServerPassword);

        using var response = await httpClient.GetAsync("/global/health");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        body.ShouldContain("<html");
    }

    [Test]
    public async Task FixtureServer_reports_a_successful_turn_without_text_as_an_empty_reply()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            ServerPassword = FixturePassword,
            EmptyTextModelIds = ["catalog/model-a"],
        });
        using var httpClient = new HttpClient { BaseAddress = fixture.BaseUri };
        using var client = new OpenCodeHttpClient(httpClient, directory: null, workspace: null, serverPassword: fixture.ServerPassword);
        var session = await client.CreateSessionAsync("fixture-diagnostics");
        await client.SetSessionModelAsync(session.Id, new OpenCodeModelSelection { ProviderId = "catalog", ModelId = "model-a" });

        var response = await PromptAndCollectAsync(client, session.Id, "Reply with the single word READY.");

        response.GetTextContent().ShouldBeEmpty();
        var diagnostic = response.BuildDiagnosticSummary();
        diagnostic.ShouldContain("successful response with no readable text content");
        diagnostic.ShouldContain("partTypes=(none)");
    }

    [Test]
    public async Task FixtureServer_reports_a_failed_turn_with_its_error_message()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            ServerPassword = FixturePassword,
            FailingModelIds = ["catalog/model-b"],
        });
        using var httpClient = new HttpClient { BaseAddress = fixture.BaseUri };
        using var client = new OpenCodeHttpClient(httpClient, directory: null, workspace: null, serverPassword: fixture.ServerPassword);
        var session = await client.CreateSessionAsync("fixture-failure");
        await client.SetSessionModelAsync(session.Id, new OpenCodeModelSelection { ProviderId = "catalog", ModelId = "model-b" });

        var response = await PromptAndCollectAsync(client, session.Id, "hello");

        response.GetTextContent().ShouldBeEmpty();
        var diagnostic = response.BuildDiagnosticSummary();
        diagnostic.ShouldContain("failed turn");
        diagnostic.ShouldContain("error.message=Model unavailable: catalog/model-b");
    }

    [Test]
    public async Task FixtureServer_rejects_an_invalid_model_on_the_session_model_route()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            ServerPassword = FixturePassword,
            InvalidModelIds = ["catalog/missing"],
        });
        using var httpClient = new HttpClient { BaseAddress = fixture.BaseUri };
        using var client = new OpenCodeHttpClient(httpClient, directory: null, workspace: null, serverPassword: fixture.ServerPassword);
        var session = await client.CreateSessionAsync("fixture-invalid-model");

        var exception = await Should.ThrowAsync<OpenCodeApiException>(async () =>
            await client.SetSessionModelAsync(session.Id, new OpenCodeModelSelection { ProviderId = "catalog", ModelId = "missing" }));

        exception.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task FixtureServer_lists_models_with_their_input_capabilities_or_none()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            ServerPassword = FixturePassword,
            Models =
            [
                new OpenCodeFixtureModel("opencode", "text-only", ["text"]),
                new OpenCodeFixtureModel("opencode", "vision", ["text", "image"]),
                new OpenCodeFixtureModel("opencode", "bare", null),
            ],
        });
        using var httpClient = new HttpClient { BaseAddress = fixture.BaseUri };
        using var client = new OpenCodeHttpClient(httpClient, directory: null, workspace: null, serverPassword: fixture.ServerPassword);

        var models = await client.ListModelsAsync();

        models.Select(static model => (model.Id, model.AcceptsImage))
            .ShouldBe([("text-only", false), ("vision", true), ("bare", false)]);
        fixture.ModelListingRequestCount.ShouldBe(1);
    }

    [Test]
    public async Task FixtureServer_fails_only_the_model_listing_when_asked_to()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            ServerPassword = FixturePassword,
            FailModelListing = true,
        });
        using var httpClient = new HttpClient { BaseAddress = fixture.BaseUri };
        using var client = new OpenCodeHttpClient(httpClient, directory: null, workspace: null, serverPassword: fixture.ServerPassword);

        await Should.ThrowAsync<OpenCodeApiException>(async () => await client.ListModelsAsync());
        var session = await client.CreateSessionAsync("after-failed-listing");

        session.Title.ShouldBe("after-failed-listing");
    }

    [Test]
    public async Task FixtureServer_keeps_the_inline_files_of_the_last_prompt()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            ServerPassword = FixturePassword,
        });
        using var httpClient = new HttpClient { BaseAddress = fixture.BaseUri };
        using var client = new OpenCodeHttpClient(httpClient, directory: null, workspace: null, serverPassword: fixture.ServerPassword);
        var session = await client.CreateSessionAsync("image-session");
        var request = OpenCodeSessionPromptRequest.FromTextWithFiles(
            "Describe the image.",
            [new OpenCodePromptFile { Mime = "image/png", Uri = "data:image/png;base64,iVBORw0KGgo=", Name = "shot.png" }]);

        await client.PromptAsync(session.Id, request);

        using var document = JsonDocument.Parse(fixture.LastPromptBody!);
        document.RootElement.GetProperty("text").GetString().ShouldBe("Describe the image.");
        var file = document.RootElement.GetProperty("files").EnumerateArray().Single();
        file.GetProperty("mime").GetString().ShouldBe("image/png");
        file.GetProperty("uri").GetString().ShouldBe("data:image/png;base64,iVBORw0KGgo=");
        file.GetProperty("name").GetString().ShouldBe("shot.png");
    }

    [Test]
    public async Task FixtureServer_answers_a_malformed_attachment_with_the_existing_error_path()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            ServerPassword = FixturePassword,
        });
        using var httpClient = new HttpClient { BaseAddress = fixture.BaseUri };
        using var client = new OpenCodeHttpClient(httpClient, directory: null, workspace: null, serverPassword: fixture.ServerPassword);
        var session = await client.CreateSessionAsync("bad-attachment-session");
        var request = OpenCodeSessionPromptRequest.FromTextWithFiles(
            "Describe the image.",
            [new OpenCodePromptFile { Mime = "image/png", Uri = "data:image/png;base64,!!!not-base64", Name = "broken.png" }]);

        var exception = await Should.ThrowAsync<OpenCodeApiException>(async () => await client.PromptAsync(session.Id, request));
        var followUp = await PromptAndCollectAsync(client, session.Id, "Reply with the single word READY.");

        exception.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        exception.ResponseBody.ShouldContain("Invalid attachment data URL: broken.png");
        exception.ResponseBody.ShouldNotContain("not-base64");
        followUp.GetTextContent().ShouldBe("READY");
    }

    private static HttpClient CreateAuthenticatedHttpClient(Uri baseUri, string password)
    {
        var httpClient = new HttpClient { BaseAddress = baseUri };
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"opencode:{password}"));
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        return httpClient;
    }

    /// <summary>
    /// Runs one prompt through the event flow and returns the assembled assistant envelope.
    /// </summary>
    private static Task<OpenCodeMessageEnvelope> PromptAndCollectAsync(OpenCodeHttpClient client, string sessionId, string text)
    {
        return client.PromptAsync(sessionId, OpenCodeSessionPromptRequest.FromText(text));
    }
}
