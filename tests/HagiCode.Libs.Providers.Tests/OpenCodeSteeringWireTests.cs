using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.OpenCode;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Checks the steering handle, its options, the wire models and the agent work tracker against the recorded OpenCode 2.0.32 steer
/// captures (see Fixtures/OpenCode/v2.0.32/steer-*.json), and checks that the fixture server emits only shapes those captures contain.
/// </summary>
public sealed class OpenCodeSteeringWireTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    [Test]
    public async Task A_detached_handle_is_not_steerable_and_invokes_no_sink()
    {
        var steering = new OpenCodeRunSteering();
        var sinkCalls = 0;
        steering.Attach((_, _, _) =>
        {
            sinkCalls++;
            return Task.FromResult(OpenCodeSteerResult.Sent);
        });
        steering.Detach();

        (await steering.SteerAsync("msg_detached", "text")).ShouldBe(OpenCodeSteerResult.NotSteerable);
        sinkCalls.ShouldBe(0);
    }

    [Test]
    public async Task An_attached_handle_forwards_the_steer_and_outcome_is_observed_once_per_report()
    {
        var steering = new OpenCodeRunSteering();
        steering.Attach((id, text, _) => Task.FromResult(id == "msg_one" && text == "hello" ? OpenCodeSteerResult.Sent : OpenCodeSteerResult.NotSteerable));
        var outcomes = new List<OpenCodeSteerOutcomeEventArgs>();
        steering.OutcomeObserved += (_, args) => outcomes.Add(args);

        (await steering.SteerAsync("msg_one", "hello")).ShouldBe(OpenCodeSteerResult.Sent);
        steering.Report("msg_one", OpenCodeSteerOutcome.Consumed);

        outcomes.Count.ShouldBe(1);
        outcomes[0].SteerId.ShouldBe("msg_one");
        outcomes[0].Outcome.ShouldBe(OpenCodeSteerOutcome.Consumed);
    }

    [Test]
    public async Task Blank_steer_input_is_rejected_before_the_sink_is_used()
    {
        var steering = new OpenCodeRunSteering();
        steering.Attach((_, _, _) => Task.FromResult(OpenCodeSteerResult.Sent));

        await Should.ThrowAsync<ArgumentException>(() => steering.SteerAsync(" ", "text"));
        await Should.ThrowAsync<ArgumentException>(() => steering.SteerAsync("msg_blank", " "));
    }

    [Test]
    public void The_steer_delivery_timeout_defaults_to_five_minutes_and_is_finite()
    {
        var options = new OpenCodeOptions();

        OpenCodeOptions.DefaultSteerDeliveryTimeout.ShouldBe(TimeSpan.FromMinutes(5));
        options.SteerDeliveryTimeout.ShouldBe(OpenCodeOptions.DefaultSteerDeliveryTimeout);
        options.Steering.ShouldBeNull();
    }

    [TestCase(0)]
    [TestCase(-1)]
    public async Task A_non_positive_steer_delivery_timeout_is_rejected_before_any_request_is_sent(double seconds)
    {
        await using var provider = new OpenCodeProvider(new CliExecutableResolver());
        var options = new OpenCodeOptions
        {
            BaseUrl = "http://127.0.0.1:1",
            Steering = new OpenCodeRunSteering(),
            SteerDeliveryTimeout = TimeSpan.FromSeconds(seconds),
        };

        var exception = await Should.ThrowAsync<ArgumentOutOfRangeException>(() => DrainAsync(provider.ExecuteAsync(options, "hi")));
        exception.ParamName.ShouldBe("SteerDeliveryTimeout");
    }

    [Test]
    public async Task An_infinite_steer_delivery_timeout_is_rejected_before_any_request_is_sent()
    {
        await using var provider = new OpenCodeProvider(new CliExecutableResolver());
        var options = new OpenCodeOptions
        {
            BaseUrl = "http://127.0.0.1:1",
            Steering = new OpenCodeRunSteering(),
            SteerDeliveryTimeout = Timeout.InfiniteTimeSpan,
        };

        var exception = await Should.ThrowAsync<ArgumentOutOfRangeException>(() => DrainAsync(provider.ExecuteAsync(options, "hi")));
        exception.ParamName.ShouldBe("SteerDeliveryTimeout");
    }

    [Test]
    public void The_options_text_never_contains_the_server_password()
    {
        var options = new OpenCodeOptions { ServerPassword = "steer-secret-password", Steering = new OpenCodeRunSteering() };

        options.ToString().ShouldNotContain("steer-secret-password");
    }

    [Test]
    public void The_recorded_prompt_ack_deserializes_with_the_steer_delivery()
    {
        var steer = ReadSample("steer-idle-delivery-explicit.json").GetProperty("steer");
        var ack = JsonSerializer.Deserialize<OpenCodeDataResponse<OpenCodeInboxUser>>(steer.GetProperty("response").GetRawText(), JsonOptions);

        ack.ShouldNotBeNull();
        ack!.Data!.Id.ShouldBe("msg_probe_idle_steer");
        ack.Data.Delivery.ShouldBe(OpenCodeInboxDelivery.Steer);
    }

    [Test]
    public void The_recorded_inbox_events_deserialize_to_the_steer_models()
    {
        var events = RecordedEvents("steer-busy-injected.json");

        var enqueued = events.Single(static e => Type(e) == "session.inbox.enqueued" && InboxId(e) == "msg_probe_steer_a");
        var enqueuedData = JsonSerializer.Deserialize<OpenCodeInboxEnqueuedEvent>(Payload(enqueued).GetRawText(), JsonOptions);
        enqueuedData!.InboxId.ShouldBe("msg_probe_steer_a");
        enqueuedData.Item!.Type.ShouldBe("user");
        enqueuedData.Item.Delivery.ShouldBe(OpenCodeInboxDelivery.Steer);

        var delivered = events.Single(static e => Type(e) == "session.inbox.delivered" && InboxId(e) == "msg_probe_steer_a");
        var deliveredData = JsonSerializer.Deserialize<OpenCodeInboxDeliveredEvent>(Payload(delivered).GetRawText(), JsonOptions);
        deliveredData!.InboxId.ShouldBe("msg_probe_steer_a");
        deliveredData.SessionId.ShouldNotBeNullOrEmpty();
    }

    [Test]
    public void The_agent_work_tracker_does_not_read_a_steer_as_a_child_or_as_work()
    {
        var events = RecordedEvents("steer-busy-injected.json");
        var promptedSession = Payload(events.Single(static e => Type(e) == "session.inbox.delivered" && InboxId(e) == "msg_probe_steer_a"))
            .GetProperty("sessionID").GetString()!;
        var tracker = new OpenCodeAgentWorkTracker(promptedSession);

        foreach (var envelope in events.Select(static e => new OpenCodeEventEnvelope { EventName = null, Data = e }))
        {
            tracker.Observe(envelope);
        }

        tracker.HasActiveWork.ShouldBeFalse();
        tracker.TakeSnapshot().ShouldBeNull();
    }

    [Test]
    public async Task The_fixture_server_emits_only_steer_event_shapes_the_real_server_was_seen_to_emit()
    {
        var recorded = RecordedShapes();

        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            SteerModelIds = ["fixture/steer-model"],
            SteerScript = new OpenCodeFixtureSteerScript { StepDelay = TimeSpan.FromSeconds(1.5) },
        });
        var emitted = new ConcurrentQueue<JsonElement>();
        using var stopReading = new CancellationTokenSource();
        var reader = ReadEventStreamAsync(fixture, emitted, stopReading.Token);
        await Task.Delay(300);

        await using var provider = new OpenCodeProvider(new CliExecutableResolver());
        var steering = new OpenCodeRunSteering();
        var options = new OpenCodeOptions
        {
            BaseUrl = fixture.BaseUri.ToString(),
            ServerPassword = fixture.ServerPassword,
            Model = "fixture/steer-model",
            Steering = steering,
        };
        var run = DrainAsync(provider.ExecuteAsync(options, "Start the work."));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (await steering.SteerAsync("msg_shape", "Also end with STEERED.") != OpenCodeSteerResult.Sent && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await run;
        await Task.Delay(200);
        stopReading.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => reader);

        var steerTypes = emitted.Select(static e => e.GetProperty("type").GetString()!)
            .Where(static type => type.StartsWith("session.inbox.", StringComparison.Ordinal) || type.StartsWith("session.execution.", StringComparison.Ordinal) || type.StartsWith("session.step.", StringComparison.Ordinal) || type == "session.text.ended")
            .Distinct()
            .ToList();
        steerTypes.ShouldNotBeEmpty();
        foreach (var type in steerTypes)
        {
            recorded.ShouldContainKey(type);
        }

        foreach (var envelope in emitted.Where(static e => e.GetProperty("type").GetString() is "session.inbox.enqueued" or "session.inbox.delivered" or "session.execution.started" or "session.execution.succeeded" or "session.step.ended" or "session.text.ended"))
        {
            var type = envelope.GetProperty("type").GetString()!;
            if (!recorded.TryGetValue(type, out var keys))
            {
                continue;
            }

            foreach (var key in envelope.GetProperty("data").EnumerateObject().Select(static property => property.Name))
            {
                keys.ShouldContain(key, $"fixture emitted '{key}' on {type}, which no recorded capture has");
            }
        }
    }

    [Test]
    public async Task A_duplicate_delivery_event_for_one_steer_yields_no_second_outcome()
    {
        using var http = new HttpClient(new AcceptingPromptHandler()) { BaseAddress = new Uri("http://127.0.0.1:1/") };
        using var client = new OpenCodeHttpClient(http, null, null);
        var steering = new OpenCodeRunSteering();
        var outcomes = new List<OpenCodeSteerOutcomeEventArgs>();
        steering.OutcomeObserved += (_, args) => outcomes.Add(args);
        using var turn = new OpenCodeSteerTurn(client, "ses_duplicate", steering, TimeSpan.FromMinutes(1));
        turn.Start(static () => false);

        (await steering.SteerAsync("dup-1", "text")).ShouldBe(OpenCodeSteerResult.Sent);
        turn.Delivered("dup-1");
        turn.Delivered("dup-1");
        await turn.EndAsync();

        outcomes.Select(static outcome => (outcome.SteerId, outcome.Outcome))
            .ShouldBe([("dup-1", OpenCodeSteerOutcome.Consumed)]);
    }

    /// <summary>Answers every prompt request with an accepted acknowledgement, so the steer is registered and sent.</summary>
    private sealed class AcceptingPromptHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":{\"id\":\"dup-1\"}}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private static async Task ReadEventStreamAsync(OpenCodeFixtureServerHandle fixture, ConcurrentQueue<JsonElement> sink, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"opencode:{fixture.ServerPassword}"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        using var response = await http.GetAsync(new Uri(fixture.BaseUri, "/api/event"), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                sink.Enqueue(JsonDocument.Parse(line[5..].Trim()).RootElement.Clone());
            }
        }
    }

    /// <summary>The top-level data keys of each event type across every recorded steer capture.</summary>
    private static Dictionary<string, HashSet<string>> RecordedShapes()
    {
        var shapes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(FixtureDirectory(), "steer-*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var envelope in EventsIn(document.RootElement))
            {
                var type = Type(envelope);
                if (type is null)
                {
                    continue;
                }

                if (!shapes.TryGetValue(type, out var keys))
                {
                    shapes[type] = keys = new HashSet<string>(StringComparer.Ordinal);
                }

                foreach (var property in Payload(envelope).EnumerateObject())
                {
                    keys.Add(property.Name);
                }
            }
        }

        return shapes;
    }

    private static IEnumerable<JsonElement> EventsIn(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                if (property.Name == "events" && property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("data", out var data))
                        {
                            yield return data;
                        }
                    }
                }
                else
                {
                    foreach (var nested in EventsIn(property.Value))
                    {
                        yield return nested;
                    }
                }
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                foreach (var nested in EventsIn(item))
                {
                    yield return nested;
                }
            }
        }
    }

    /// <summary>The event objects of a recorded capture, in their captured order, as the SSE data objects.</summary>
    private static List<JsonElement> RecordedEvents(string file)
    {
        var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDirectory(), file)));
        return EventsIn(document.RootElement).Select(static element => element.Clone()).ToList();
    }

    private static string? Type(JsonElement envelope)
    {
        return envelope.ValueKind == JsonValueKind.Object && envelope.TryGetProperty("type", out var type) ? type.GetString() : null;
    }

    private static JsonElement Payload(JsonElement envelope)
    {
        return envelope.GetProperty("data");
    }

    private static string? InboxId(JsonElement envelope)
    {
        return Payload(envelope).TryGetProperty("inboxID", out var id) ? id.GetString() : null;
    }

    private static JsonElement ReadSample(string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDirectory(), file)));
        return document.RootElement.Clone();
    }

    private static string FixtureDirectory()
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", "OpenCode", "v2.0.32");
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
