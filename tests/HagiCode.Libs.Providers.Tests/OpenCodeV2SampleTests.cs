using System.Text.Json;
using HagiCode.Libs.Providers.OpenCode;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Deserializes the sanitized OpenCode 2.0.32 captures (see Fixtures/OpenCode/v2.0.32) with the 2.0 DTOs.
/// </summary>
public sealed class OpenCodeV2SampleTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    [Test]
    public void Health_sample_deserializes_from_the_2_0_info_route()
    {
        var health = JsonSerializer.Deserialize<OpenCodeHealthResponse>(ReadSample("health-info.json"), JsonOptions);

        health.ShouldNotBeNull();
        health!.Version.ShouldBe("2.0.32");
        health.Healthy.ShouldBeTrue();
        health.Urls.ShouldHaveSingleItem();
    }

    [Test]
    public void Session_create_and_list_samples_deserialize()
    {
        var created = JsonSerializer.Deserialize<OpenCodeDataResponse<OpenCodeSession>>(ReadSample("session-create-response.json"), JsonOptions);
        var listed = JsonSerializer.Deserialize<OpenCodeSessionPage>(ReadSample("session-list.json"), JsonOptions);

        created.ShouldNotBeNull();
        created!.Data.ShouldNotBeNull();
        created.Data!.Id.ShouldStartWith("ses_");
        created.Data.Location.ShouldNotBeNull();
        created.Data.Location!.Directory.ShouldBe("/sample/workspace");
        listed.ShouldNotBeNull();
        listed!.Data.ShouldNotBeEmpty();
    }

    [Test]
    public void Prompt_response_sample_deserializes_as_the_queued_inbox_user()
    {
        var ack = JsonSerializer.Deserialize<OpenCodeDataResponse<OpenCodeInboxUser>>(ReadSample("prompt-response-inbox-user.json"), JsonOptions);

        ack.ShouldNotBeNull();
        ack!.Data.ShouldNotBeNull();
        ack.Data!.Id.ShouldStartWith("msg_");
        ack.Data.Type.ShouldBe("user");
        ack.Data.Delivery.ShouldBe("steer");
    }

    [Test]
    public void Message_list_samples_expose_the_assistant_text_and_the_failed_outcome()
    {
        using var success = JsonDocument.Parse(ReadSample("message-list-success.json"));
        using var failed = JsonDocument.Parse(ReadSample("message-list-failed.json"));

        var assistant = success.RootElement.GetProperty("data").EnumerateArray()
            .Single(static item => item.GetProperty("type").GetString() == "assistant");
        assistant.GetProperty("content").EnumerateArray()
            .Single(static part => part.GetProperty("type").GetString() == "text")
            .GetProperty("text").GetString().ShouldBe("pong");

        failed.RootElement.GetProperty("data").EnumerateArray()
            .Single(static item => item.GetProperty("type").GetString() == "idle")
            .GetProperty("outcome").GetString().ShouldBe("failed");
    }

    [Test]
    public void Success_event_sample_carries_the_turn_start_text_and_completion_for_one_session()
    {
        var events = ReadEvents("events-success.jsonl");

        events.Select(static envelope => envelope.EventType).ShouldContain("session.execution.started");
        events.Select(static envelope => envelope.EventType).ShouldContain("session.execution.succeeded");
        events.Where(static envelope => envelope.EventType == "session.text.ended")
            .Select(static envelope => envelope.Payload.GetProperty("text").GetString())
            .ShouldBe(["pong"]);
        events.Where(static envelope => envelope.SessionId is not null)
            .Select(static envelope => envelope.SessionId)
            .Distinct()
            .ShouldHaveSingleItem();
    }

    [Test]
    public void Failed_event_sample_reports_the_turn_failure_with_its_error()
    {
        var failed = ReadEvents("events-failed.jsonl").Single(static envelope => envelope.EventType == "session.execution.failed");

        failed.Payload.GetProperty("error").GetProperty("type").GetString().ShouldBe("provider.no-route");
        failed.Payload.GetProperty("error").GetProperty("message").GetString().ShouldContain("Model unavailable");
    }

    [Test]
    public void Unauthorized_and_legacy_samples_keep_their_documented_shapes()
    {
        ReadSample("unauthorized-401.json").ShouldContain("UnauthorizedError");
        ReadSample("legacy-global-health.html").ShouldContain("<html");
    }

    [Test]
    public void Samples_contain_no_password_line_and_no_user_home_path()
    {
        var samples = Directory.GetFiles(SampleDirectory());

        samples.ShouldNotBeEmpty();
        foreach (var sample in samples)
        {
            var text = File.ReadAllText(sample);
            text.ShouldNotContain("server password");
            text.ShouldNotContain("/home/");
        }
    }

    [Test]
    public void Form_created_event_resolves_to_the_session_inside_the_form()
    {
        var created = ReadEvents("events-question-reply.jsonl").Single(static envelope => envelope.EventType == "form.created");

        created.Payload.TryGetProperty("sessionID", out _).ShouldBeFalse();
        created.SessionId.ShouldBe("ses_eb3c4c8ebffeoPuTSmM55ew47v");
        created.Form.ShouldNotBeNull();
        created.Form!.Id.ShouldStartWith("frm_");
        created.Form.SessionId.ShouldBe(created.SessionId);
        created.Form.Kind.ShouldBe(OpenCodeFormKinds.Question);
        created.Form.Metadata!.Tool!.Id.ShouldBe("call_97424");
        created.Form.Fields.Select(static field => field.Key).ShouldBe(["q0", "q1"]);
        created.Form.Fields[0].Type.ShouldBe("string");
        created.Form.Fields[0].Title.ShouldBe("Color");
        created.Form.Fields[0].Description.ShouldBe("Which color do you prefer?");
        created.Form.Fields[0].Options.Select(static option => option.Label).ShouldBe(["Red", "Blue"]);
        created.Form.Fields[1].Type.ShouldBe("multiselect");
        created.Form.Fields[1].Options.Select(static option => option.Label).ShouldBe(["Cats", "Dogs"]);
    }

    [Test]
    public void Events_of_an_existing_shape_resolve_their_session_as_before()
    {
        var success = ReadEvents("events-success.jsonl");
        var replied = ReadEvents("events-question-reply.jsonl").Single(static envelope => envelope.EventType == "form.replied");
        var cancelled = ReadEvents("events-question-cancel.jsonl").Single(static envelope => envelope.EventType == "form.cancelled");

        var turnSession = success.Single(static envelope => envelope.EventType == "session.execution.succeeded").SessionId;
        turnSession.ShouldNotBeNull();
        turnSession!.ShouldStartWith("ses_");
        success.Where(static envelope => envelope.SessionId is not null).Select(static envelope => envelope.SessionId).Distinct().ShouldHaveSingleItem();
        replied.SessionId.ShouldBe("ses_eb3c4c8ebffeoPuTSmM55ew47v");
        replied.Form.ShouldBeNull();
        cancelled.SessionId.ShouldBe("ses_eb3c490a4ffel4SWTzvJhqgvZM");
        cancelled.Form.ShouldBeNull();
        cancelled.Payload.GetProperty("id").GetString().ShouldStartWith("frm_");
    }

    [Test]
    public void Interrupted_question_turn_ends_with_a_terminal_interrupt_event()
    {
        var events = ReadEvents("events-question-interrupt.jsonl");
        var interrupted = events.Single(static envelope => envelope.EventType == "session.execution.interrupted");

        interrupted.SessionId.ShouldBe("ses_eb3c2ff9effenFhctIrlmRbAZO");
        interrupted.Payload.GetProperty("reason").GetString().ShouldBe("user");
        events.Select(static envelope => envelope.EventType).ShouldNotContain("session.execution.succeeded");
        events.Where(static envelope => envelope.EventType == "form.cancelled")
            .Select(static envelope => envelope.SessionId)
            .ShouldBe(["ses_eb3c2ff9effenFhctIrlmRbAZO"]);
    }

    private static string SampleDirectory()
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", "OpenCode", "v2.0.32");
    }

    private static string ReadSample(string name)
    {
        return File.ReadAllText(Path.Combine(SampleDirectory(), name));
    }

    private static List<OpenCodeEventEnvelope> ReadEvents(string name)
    {
        return File.ReadAllLines(Path.Combine(SampleDirectory(), name))
            .Where(static line => line.Length > 0)
            .Select(static line =>
            {
                using var document = JsonDocument.Parse(line);
                return new OpenCodeEventEnvelope { Data = document.RootElement.Clone() };
            })
            .ToList();
    }
}
