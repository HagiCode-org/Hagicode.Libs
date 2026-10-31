using System.Net;
using HagiCode.Libs.Skills.OnlineApi;
using HagiCode.Libs.Skills.OnlineApi.Models;
using HagiCode.Libs.Skills.OnlineApi.Providers;
using Microsoft.Extensions.Options;
using Shouldly;

namespace HagiCode.Libs.Skills.Tests;

public sealed class OnlineApiClientTests
{
    [Test]
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

    [Test]
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

    [Test]
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

    [Test]
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

    [Test]
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

    [Test]
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

    [Test]
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

    [Test]
    public async Task GetAuditMetadataAsync_requests_skills_sh_tele_audit_with_source_and_skill()
    {
        var handler = new TestHttpMessageHandler((_, _) => Task.FromResult(TestHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        var client = CreateClient(handler);

        await client.GetAuditMetadataAsync(new AuditMetadataRequest
        {
            Source = "vercel-labs/skills",
            Skills = ["find-skills"],
        });

        handler.Requests.Single().RequestUri!.AbsoluteUri.ShouldBe(
            "https://skills.sh/tele/audit?source=vercel-labs%2Fskills&skills=find-skills");
    }

    [Test]
    public async Task GetAuditMetadataAsync_joins_multiple_skill_slugs_with_commas()
    {
        var handler = new TestHttpMessageHandler((_, _) => Task.FromResult(TestHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        var client = CreateClient(handler);

        await client.GetAuditMetadataAsync(new AuditMetadataRequest
        {
            Source = "vercel-labs/skills",
            Skills = ["find-skills", "vercel-react-best-practices"],
        });

        // The shared query builder percent-encodes the comma; the decoded value is the comma-separated list.
        Uri.UnescapeDataString(handler.Requests.Single().RequestUri!.Query)
            .ShouldContain("skills=find-skills,vercel-react-best-practices");
    }

    [Test]
    public async Task GetAuditMetadataAsync_reads_all_three_providers_by_skill_then_provider()
    {
        var handler = new TestHttpMessageHandler((_, _) => Task.FromResult(TestHttpMessageHandler.Json(
            HttpStatusCode.OK,
            """
            {"find-skills":{
              "ath":{"risk":"safe","alerts":0,"score":100,"analyzedAt":"2026-10-30T00:00:00.000Z"},
              "socket":{"risk":"high","alerts":3,"score":62,"analyzedAt":"2026-10-30T00:00:00.000Z"},
              "snyk":{"risk":"low","alerts":1,"score":"80","analyzedAt":"2026-10-29"}
            }}
            """)));
        var client = CreateClient(handler);

        var response = await client.GetAuditMetadataAsync(new AuditMetadataRequest { Source = "vercel-labs/skills", Skills = ["find-skills"] });

        var providers = response.Skills["find-skills"];
        providers.Keys.OrderBy(static key => key).ShouldBe(["ath", "snyk", "socket"]);
        providers["socket"].Risk.ShouldBe("high");
        providers["socket"].Alerts.ShouldBe(3);
        providers["socket"].Score.ShouldBe(62);
        providers["socket"].AnalyzedAt.ShouldBe("2026-10-30T00:00:00.000Z");
        providers["snyk"].Score.ShouldBe(80);
    }

    [Test]
    public async Task GetAuditMetadataAsync_returns_only_the_providers_that_are_present()
    {
        var handler = new TestHttpMessageHandler((_, _) => Task.FromResult(TestHttpMessageHandler.Json(
            HttpStatusCode.OK,
            """{"find-skills":{"ath":{"risk":"safe"}}}""")));
        var client = CreateClient(handler);

        var response = await client.GetAuditMetadataAsync(new AuditMetadataRequest { Source = "vercel-labs/skills", Skills = ["find-skills"] });

        response.Skills["find-skills"].Keys.ShouldBe(["ath"]);
    }

    [Test]
    public async Task GetAuditMetadataAsync_keeps_unknown_risk_values_as_received()
    {
        var handler = new TestHttpMessageHandler((_, _) => Task.FromResult(TestHttpMessageHandler.Json(
            HttpStatusCode.OK,
            """{"find-skills":{"snyk":{"risk":"catastrophic"}}}""")));
        var client = CreateClient(handler);

        var response = await client.GetAuditMetadataAsync(new AuditMetadataRequest { Source = "vercel-labs/skills", Skills = ["find-skills"] });

        response.Skills["find-skills"]["snyk"].Risk.ShouldBe("catastrophic");
    }

    [Test]
    public async Task GetAuditMetadataAsync_drops_provider_values_that_are_not_objects()
    {
        var handler = new TestHttpMessageHandler((_, _) => Task.FromResult(TestHttpMessageHandler.Json(
            HttpStatusCode.OK,
            """{"find-skills":{"ath":"unavailable","socket":{"risk":"low"}}}""")));
        var client = CreateClient(handler);

        var response = await client.GetAuditMetadataAsync(new AuditMetadataRequest { Source = "vercel-labs/skills", Skills = ["find-skills"] });

        response.Skills["find-skills"].Keys.ShouldBe(["socket"]);
    }

    [Test]
    public async Task GetAuditMetadataAsync_fails_with_payload_failure_when_body_is_not_an_object()
    {
        var handler = new TestHttpMessageHandler((_, _) => Task.FromResult(TestHttpMessageHandler.Json(HttpStatusCode.OK, "[1,2,3]")));
        var client = CreateClient(handler);

        await Should.ThrowAsync<OnlineApiValidationException>(() => client.GetAuditMetadataAsync(new AuditMetadataRequest
        {
            Source = "vercel-labs/skills",
            Skills = ["find-skills"],
        }));
    }

    [Test]
    public async Task GetAuditMetadataAsync_fails_with_http_exception_on_server_error()
    {
        var handler = new TestHttpMessageHandler((_, _) => Task.FromResult(TestHttpMessageHandler.Json(HttpStatusCode.InternalServerError, "{}")));
        var client = CreateClient(handler);

        await Should.ThrowAsync<OnlineApiHttpException>(() => client.GetAuditMetadataAsync(new AuditMetadataRequest
        {
            Source = "vercel-labs/skills",
            Skills = ["find-skills"],
        }));
    }

    [Test]
    public async Task GetAuditMetadataAsync_parses_recorded_live_payload_for_find_skills()
    {
        // Recorded 2026-10-31 from GET https://skills.sh/tele/audit?source=vercel-labs%2Fskills&skills=find-skills.
        // ath and snyk omit alerts and score; snyk's analyzedAt has a +00:00 offset with microseconds.
        var handler = new TestHttpMessageHandler((_, _) => Task.FromResult(TestHttpMessageHandler.Json(
            HttpStatusCode.OK,
            """
            {"find-skills":{"ath":{"risk":"safe","analyzedAt":"2026-09-15T08:00:05.922Z"},"socket":{"risk":"safe","alerts":0,"score":90,"analyzedAt":"2026-09-15T08:00:19.647Z"},"snyk":{"risk":"medium","analyzedAt":"2026-09-15T07:59:46.904821+00:00"}}}
            """)));
        var client = CreateClient(handler);

        var response = await client.GetAuditMetadataAsync(new AuditMetadataRequest { Source = "vercel-labs/skills", Skills = ["find-skills"] });

        var providers = response.Skills["find-skills"];
        providers["ath"].Risk.ShouldBe("safe");
        providers["ath"].Alerts.ShouldBeNull();
        providers["ath"].Score.ShouldBeNull();
        providers["socket"].Score.ShouldBe(90);
        providers["snyk"].Risk.ShouldBe("medium");
        providers["snyk"].Alerts.ShouldBeNull();
        providers["snyk"].AnalyzedAt.ShouldBe("2026-09-15T07:59:46.904821+00:00");
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
