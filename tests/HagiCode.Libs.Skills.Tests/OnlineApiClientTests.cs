using System.Net;
using HagiCode.Libs.Skills.OnlineApi;
using HagiCode.Libs.Skills.OnlineApi.Models;
using HagiCode.Libs.Skills.OnlineApi.Providers;
using Microsoft.Extensions.Options;
using Shouldly;

namespace HagiCode.Libs.Skills.Tests;

public sealed class OnlineApiClientTests
{
    [Fact]
    public async Task SearchAsync_builds_documented_query_and_deserializes_response()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            request.RequestUri.ShouldNotBeNull();
            request.RequestUri!.AbsoluteUri.ShouldBe("https://skills.sh/api/search?q=codex&limit=5");
            return Task.FromResult(TestHttpMessageHandler.Json(
                HttpStatusCode.OK,
                """
                {"skills":[
                  {"id":"b","name":"b","installs":5,"source":"demo/b"},
                  {"id":"a","name":"a","installs":15,"source":"demo/a"}
                ]}
                """));
        });
        var client = CreateClient(handler);

        var response = await client.SearchAsync(new SearchSkillsRequest
        {
            Query = "codex",
            Limit = 5,
        });

        response.Skills.Select(static skill => skill.Id).ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task DiscoverWellKnownAsync_deserializes_valid_index_response()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            request.RequestUri.ShouldNotBeNull();
            request.RequestUri!.AbsoluteUri.ShouldBe("https://example.com/docs/.well-known/skills/index.json");
            return Task.FromResult(TestHttpMessageHandler.Json(
                HttpStatusCode.OK,
                """
                {
                  "skills": [
                    {
                      "name": "my-skill",
                      "description": "Example skill",
                      "files": ["SKILL.md", "docs/guide.md"]
                    }
                  ]
                }
                """));
        });
        var client = CreateClient(handler);

        var response = await client.DiscoverWellKnownAsync(new WellKnownDiscoveryRequest
        {
            SourceUrl = "https://example.com/docs",
        });

        response.ResolvedIndexUri.AbsoluteUri.ShouldBe("https://example.com/docs/.well-known/skills/index.json");
        response.Skills.Count.ShouldBe(1);
        response.Skills[0].Name.ShouldBe("my-skill");
    }

    [Fact]
    public async Task SearchAsync_throws_online_api_http_exception_for_non_success_status_codes()
    {
        var handler = new TestHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("upstream failed"),
            }));
        var client = CreateClient(handler);

        var exception = await Should.ThrowAsync<OnlineApiHttpException>(() => client.SearchAsync(new SearchSkillsRequest
        {
            Query = "codex",
        }));

        exception.Operation.ShouldBe(OnlineApiOperation.Search);
        exception.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        exception.ResponseBody.ShouldNotBeNull();
        exception.ResponseBody.ShouldContain("upstream failed");
    }

    [Fact]
    public async Task SearchAsync_honors_cancellation_tokens()
    {
        var handler = new TestHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return TestHttpMessageHandler.Json(HttpStatusCode.OK, "{\"skills\":[]}");
        });
        var client = CreateClient(handler);
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => client.SearchAsync(
            new SearchSkillsRequest { Query = "codex" },
            cancellationTokenSource.Token));
    }

    [Fact]
    public async Task TrackTelemetryAsync_returns_skipped_result_when_disabled()
    {
        var handler = new TestHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("Telemetry should not hit the network when disabled."));
        var client = CreateClient(handler, configure: options =>
        {
            options.DisableTelemetry = true;
            options.TelemetryVersion = "1.2.3";
            options.TelemetryIsCi = true;
        });

        var result = await client.TrackTelemetryAsync(new FindTelemetryEventRequest("skills", "3", Interactive: true));

        result.IsSkipped.ShouldBeTrue();
        result.RequestUri.AbsoluteUri.ShouldBe("https://add-skill.vercel.sh/t?event=find&query=skills&resultCount=3&interactive=1&v=1.2.3&ci=1");
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetGitHubRepositoryMetadataAsync_shapes_request_headers_and_path()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            request.RequestUri.ShouldNotBeNull();
            request.RequestUri!.AbsoluteUri.ShouldBe("https://api.github.com/repos/octo/demo");
            request.Headers.Accept.Single().MediaType.ShouldBe("application/vnd.github.v3+json");
            request.Headers.UserAgent.ToString().ShouldContain("skills-tests");
            request.Headers.Authorization.ShouldNotBeNull();
            request.Headers.Authorization!.Scheme.ShouldBe("Bearer");
            request.Headers.Authorization.Parameter.ShouldBe("token-123");
            return Task.FromResult(TestHttpMessageHandler.Json(
                HttpStatusCode.OK,
                """
                {
                  "private": false,
                  "name": "demo",
                  "full_name": "octo/demo",
                  "default_branch": "main",
                  "html_url": "https://github.com/octo/demo"
                }
                """));
        });
        var client = CreateClient(handler, configure: options => options.GitHubUserAgent = "skills-tests");

        var response = await client.GetGitHubRepositoryMetadataAsync(new GitHubRepositoryMetadataRequest
        {
            Owner = "octo",
            Repository = "demo",
            Token = "token-123",
        });

        response.FullName.ShouldBe("octo/demo");
        response.Private.ShouldBeFalse();
    }

    [Fact]
    public async Task DiscoverWellKnownAsync_rejects_invalid_payloads_with_validation_details()
    {
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri == "https://example.com/docs/.well-known/skills/index.json")
            {
                return Task.FromResult(TestHttpMessageHandler.Json(
                    HttpStatusCode.OK,
                    """
                    {
                      "skills": [
                        {
                          "name": "my-skill",
                          "description": "Example skill",
                          "files": ["docs/guide.md"]
                        }
                      ]
                    }
                    """));
            }

            return Task.FromResult(TestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"));
        });
        var client = CreateClient(handler);

        var exception = await Should.ThrowAsync<OnlineApiValidationException>(() => client.DiscoverWellKnownAsync(new WellKnownDiscoveryRequest
        {
            SourceUrl = "https://example.com/docs",
        }));

        exception.Message.ShouldContain("SKILL.md");
    }

    private static OnlineApiClient CreateClient(
        TestHttpMessageHandler handler,
        Action<OnlineApiOptions>? configure = null)
    {
        var options = new OnlineApiOptions();
        configure?.Invoke(options);
        var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5),
        };

        return new OnlineApiClient(
            httpClient,
            new VercelOnlineApiEndpointProvider(Options.Create(options)),
            Options.Create(options));
    }
}
