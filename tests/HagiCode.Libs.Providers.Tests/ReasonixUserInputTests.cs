using System.Text.Json;
using System.Text.Json.Nodes;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Providers.Reasonix;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Question detection and parsing, answer-to-outcome mapping and the pending-question runner of the Reasonix provider,
/// checked against the permission requests recorded from Reasonix v2.41.0.
/// </summary>
public sealed class ReasonixUserInputTests
{
    public static TheoryData<string> AskFixtures => new()
    {
        "ask-single", "ask-two-questions", "ask-multi", "ask-reject-option", "ask-cancelled-outcome", "ask-two-turns",
        "ask-delayed-answer", "ask-stop-pending", "ask-resume-pending-part1", "ask-resume-pending"
    };

    [Theory]
    [MemberData(nameof(AskFixtures))]
    public void Every_ask_fixture_loads_and_each_recorded_question_request_is_recognized(string fixture)
    {
        var records = ReasonixTranscriptFixtures.Load(fixture);

        records.ShouldNotBeEmpty();
        records[0].Method.ShouldBe("initialize");
        foreach (var parameters in PermissionParameters(records))
        {
            var permission = AcpPermissionRequest.FromParameters(parameters);
            ReasonixUserInputRequest.IsQuestionRequest(permission).ShouldBeTrue(permission.ToolCallId);
            ReasonixUserInputRequest.TryParse(permission, out var request).ShouldBeTrue(permission.ToolCallId);
            request.RequestId.ShouldMatch(@"^ask-[0-9a-f]+-\d+-q\d+$");
            request.Question.Options.ShouldNotBeEmpty();
        }
    }

    [Fact]
    public void A_single_question_is_read_with_header_text_descriptions_and_without_the_cancel_option()
    {
        var request = ParseOnly("ask-single");

        request.RequestId.ShouldBe("ask-1f339eb0-1-q1");
        request.Question.Id.ShouldBe("q1");
        request.Question.Question.ShouldBe("Which color should I use?");
        request.Question.Header.ShouldBe("Color");
        request.Question.MultiSelect.ShouldBeFalse();
        request.Question.Options.ShouldBe(
        [
            new ReasonixUserQuestionOption("Red", "Warm color"),
            new ReasonixUserQuestionOption("Blue", "Cool color")
        ]);
        request.OptionIdsByLabel.Count.ShouldBe(2);
        request.OptionIdsByLabel["Red"].ShouldBe("q1:1");
        request.OptionIdsByLabel["Blue"].ShouldBe("q1:2");
        request.CancelOptionId.ShouldBe("q1:cancel");
    }

    [Fact]
    public void A_multi_select_question_keeps_the_flag_as_reasonix_reported_it()
    {
        var request = ParseOnly("ask-multi");

        request.Question.MultiSelect.ShouldBeTrue();
        request.Question.Options.Select(static option => option.Label).ShouldBe(["Cheese", "Olives"]);
        request.Question.Options.ShouldAllBe(static option => option.Description == string.Empty);
    }

    [Fact]
    public void Raw_input_keys_are_read_case_insensitively()
    {
        var parameters = PermissionNode("ask-single");
        var rawOptions = (JsonArray)parameters["toolCall"]!["rawInput"]!["options"]!;
        rawOptions[0] = new JsonObject { ["label"] = "Red", ["description"] = "lower-case keys" };
        parameters["toolCall"]!["rawInput"]!.AsObject().Remove("multi");
        parameters["toolCall"]!["rawInput"]!["MULTI"] = true;

        var request = Parse(parameters);

        request.Question.Options[0].ShouldBe(new ReasonixUserQuestionOption("Red", "lower-case keys"));
        request.Question.MultiSelect.ShouldBeTrue();
    }

    [Fact]
    public void Without_raw_input_the_option_names_become_labels_with_empty_descriptions()
    {
        var parameters = PermissionNode("ask-single");
        parameters["toolCall"]!.AsObject().Remove("rawInput");

        var request = Parse(parameters);

        request.Question.Question.ShouldBe("Which color should I use?");
        request.Question.Id.ShouldBe("q1");
        request.Question.Options.ShouldBe(
        [
            new ReasonixUserQuestionOption("Red - Warm color", string.Empty),
            new ReasonixUserQuestionOption("Blue - Cool color", string.Empty)
        ]);
    }

    [Fact]
    public void A_question_without_a_selectable_option_is_detected_but_not_readable_and_declines_with_the_cancel_option()
    {
        var parameters = PermissionNode("ask-single");
        parameters["options"] = new JsonArray(new JsonObject
        {
            ["optionId"] = "q1:cancel",
            ["name"] = "Cancel",
            ["kind"] = "reject_once"
        });
        var permission = AcpPermissionRequest.FromParameters(ToElement(parameters));

        ReasonixUserInputRequest.IsQuestionRequest(permission).ShouldBeTrue();
        ReasonixUserInputRequest.TryParse(permission, out _).ShouldBeFalse();
        ReasonixUserInputRequest.CreateDecline(permission).SelectedOptionId.ShouldBe("q1:cancel");
    }

    [Fact]
    public void A_question_without_any_option_declines_with_the_cancelled_outcome()
    {
        var parameters = PermissionNode("ask-single");
        parameters["options"] = new JsonArray();
        var permission = AcpPermissionRequest.FromParameters(ToElement(parameters));

        ReasonixUserInputRequest.IsQuestionRequest(permission).ShouldBeTrue();
        ReasonixUserInputRequest.TryParse(permission, out _).ShouldBeFalse();
        ReasonixUserInputRequest.CreateDecline(permission).IsCancelled.ShouldBeTrue();
    }

    [Fact]
    public void A_gate_tool_approval_is_not_a_question()
    {
        var permission = AcpPermissionRequest.FromParameters(PermissionParameters(ReasonixTranscriptFixtures.Load("permission-turn")).First());

        permission.ToolCallId.ShouldStartWith("gate-");
        ReasonixUserInputRequest.IsQuestionRequest(permission).ShouldBeFalse();
        ReasonixUserInputRequest.TryParse(permission, out _).ShouldBeFalse();
    }

    [Fact]
    public void An_ask_tool_call_id_with_non_question_option_ids_is_not_a_question()
    {
        var parameters = PermissionNode("ask-single");
        parameters["options"] = new JsonArray(
            new JsonObject { ["optionId"] = "allow_once", ["name"] = "Allow", ["kind"] = "allow_once" },
            new JsonObject { ["optionId"] = "q1:2", ["name"] = "Blue", ["kind"] = "allow_once" });

        ReasonixUserInputRequest.IsQuestionRequest(AcpPermissionRequest.FromParameters(ToElement(parameters))).ShouldBeFalse();
    }

    [Theory]
    [InlineData("ask-1-q1")]
    [InlineData("ask-1f339eb0-12-q1")]
    public void Both_the_short_and_the_recorded_tool_call_id_forms_are_questions(string toolCallId)
    {
        var parameters = PermissionNode("ask-single");
        parameters["toolCall"]!["toolCallId"] = toolCallId;

        Parse(parameters).RequestId.ShouldBe(toolCallId);
    }

    [Fact]
    public void A_routed_question_notification_becomes_one_question_message_that_reads_back_the_same_question()
    {
        var notification = new AcpNotification("session/request_permission", ToElement(PermissionNode("ask-single")));

        var message = ReasonixAcpMessageMapper.NormalizeNotification(notification, routeQuestions: true).ShouldHaveSingleItem();

        message.Type.ShouldBe(ReasonixUserInputRequest.MessageType);
        message.Content.GetProperty("request_id").GetString().ShouldBe("ask-1f339eb0-1-q1");
        ReasonixUserInputRequest.TryFromCliMessage(message, out var request).ShouldBeTrue();
        request.Question.Question.ShouldBe("Which color should I use?");
        request.OptionIdsByLabel["Blue"].ShouldBe("q1:2");
        ReasonixAcpMessageMapper.NormalizeNotification(notification).ShouldHaveSingleItem().Type.ShouldBe("session.notification");
    }

    [Fact]
    public void TryFromCliMessage_rejects_other_message_types()
    {
        var message = new HagiCode.Libs.Core.Transport.CliMessage("session.notification", ToElement(new JsonObject
        {
            ["params"] = PermissionNode("ask-single")
        }));

        ReasonixUserInputRequest.TryFromCliMessage(message, out _).ShouldBeFalse();
    }

    // ------------------------------------------------------------ answer mapping

    [Theory]
    [InlineData("Red", "q1:1")]
    [InlineData("Blue", "q1:2")]
    public void An_exact_label_selects_its_option(string label, string optionId)
    {
        var request = ParseOnly("ask-single");

        Answer(request, label).ToOutcome(request).SelectedOptionId.ShouldBe(optionId);
    }

    [Theory]
    [InlineData("Purple")]
    [InlineData("blue")]
    [InlineData("Blue ")]
    [InlineData("")]
    public void Text_that_is_not_an_offered_label_declines_with_the_cancel_option(string text)
    {
        var request = ParseOnly("ask-single");

        Answer(request, text).ToOutcome(request).SelectedOptionId.ShouldBe("q1:cancel");
    }

    [Fact]
    public void An_empty_answer_more_than_one_value_or_a_missing_question_declines()
    {
        var request = ParseOnly("ask-single");

        Answer(request).ToOutcome(request).SelectedOptionId.ShouldBe("q1:cancel");
        Answer(request, "Red", "Blue").ToOutcome(request).SelectedOptionId.ShouldBe("q1:cancel");
        Answer(request, "Blue", "Blue").ToOutcome(request).SelectedOptionId.ShouldBe("q1:cancel");
        new ReasonixUserInputResponse(new Dictionary<string, IReadOnlyList<string>> { ["Another question?"] = ["Blue"] })
            .ToOutcome(request).SelectedOptionId.ShouldBe("q1:cancel");
    }

    [Fact]
    public void A_multi_select_question_with_two_labels_declines_instead_of_sending_one_of_them()
    {
        var request = ParseOnly("ask-multi");

        Answer(request, "Cheese").ToOutcome(request).SelectedOptionId.ShouldBe("q1:1");
        Answer(request, "Cheese", "Olives").ToOutcome(request).SelectedOptionId.ShouldBe("q1:cancel");
    }

    [Fact]
    public void A_duplicate_label_selects_the_first_option_with_that_label()
    {
        var request = Parse(WithRawOptions(PermissionNode("ask-single"), ("Same", "first"), ("Same", "second")));

        request.Question.Options.Select(static option => option.Description).ShouldBe(["first", "second"]);
        Answer(request, "Same").ToOutcome(request).SelectedOptionId.ShouldBe("q1:1");
    }

    [Fact]
    public void A_label_that_contains_a_comma_is_matched_as_a_whole()
    {
        var request = Parse(WithRawOptions(PermissionNode("ask-single"), ("Red, dark", ""), ("Blue", "")));

        Answer(request, "Red, dark").ToOutcome(request).SelectedOptionId.ShouldBe("q1:1");
        Answer(request, "Red", "dark").ToOutcome(request).SelectedOptionId.ShouldBe("q1:cancel");
        Answer(request, "Red").ToOutcome(request).SelectedOptionId.ShouldBe("q1:cancel");
    }

    [Fact]
    public void Without_a_cancel_option_a_decline_is_the_cancelled_outcome()
    {
        var parameters = PermissionNode("ask-single");
        var options = (JsonArray)parameters["options"]!;
        options.RemoveAt(options.Count - 1);
        var request = Parse(parameters);

        request.CancelOptionId.ShouldBeNull();
        Answer(request, "Purple").ToOutcome(request).IsCancelled.ShouldBeTrue();
        Answer(request, "Red").ToOutcome(request).SelectedOptionId.ShouldBe("q1:1");
    }

    // ------------------------------------------------------------ pending questions

    [Fact]
    public async Task An_answer_selects_the_option_and_is_not_a_decline()
    {
        ReasonixUserInputRequest? seen = null;
        var questions = CreatePending((request, _) =>
        {
            seen = request;
            return Task.FromResult<ReasonixUserInputResponse?>(Answer(request, "Blue"));
        });

        var decision = await questions.HandleAsync(Permission("ask-single"), CancellationToken.None);

        decision.ShouldNotBeNull().SelectedOptionId.ShouldBe("q1:2");
        seen.ShouldNotBeNull().RequestId.ShouldBe("ask-1f339eb0-1-q1");
        questions.HasDeclinedQuestion.ShouldBeFalse();
    }

    [Fact]
    public async Task A_null_answer_declines_and_is_recorded()
    {
        var questions = CreatePending((_, _) => Task.FromResult<ReasonixUserInputResponse?>(null));

        var decision = await questions.HandleAsync(Permission("ask-single"), CancellationToken.None);

        decision.ShouldNotBeNull().SelectedOptionId.ShouldBe("q1:cancel");
        questions.HasDeclinedQuestion.ShouldBeTrue();
    }

    [Fact]
    public async Task The_question_timeout_cancels_the_handler_and_declines()
    {
        CancellationToken handlerToken = default;
        var questions = CreatePending(
            async (_, token) =>
            {
                handlerToken = token;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            },
            TimeSpan.FromMilliseconds(100));

        var decision = await questions.HandleAsync(Permission("ask-single"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        decision.ShouldNotBeNull().SelectedOptionId.ShouldBe("q1:cancel");
        handlerToken.IsCancellationRequested.ShouldBeTrue();
        questions.HasDeclinedQuestion.ShouldBeTrue();
    }

    [Fact]
    public async Task The_question_timeout_also_ends_a_handler_that_ignores_its_token()
    {
        var questions = CreatePending(
            (_, _) => new TaskCompletionSource<ReasonixUserInputResponse?>().Task,
            TimeSpan.FromMilliseconds(100));

        var decision = await questions.HandleAsync(Permission("ask-single"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        decision.ShouldNotBeNull().SelectedOptionId.ShouldBe("q1:cancel");
        questions.HasDeclinedQuestion.ShouldBeTrue();
    }

    [Fact]
    public async Task A_failing_handler_declines()
    {
        var questions = CreatePending((_, _) => throw new InvalidOperationException("handler broke"));

        var decision = await questions.HandleAsync(Permission("ask-single"), CancellationToken.None);

        decision.ShouldNotBeNull().SelectedOptionId.ShouldBe("q1:cancel");
        questions.HasDeclinedQuestion.ShouldBeTrue();
    }

    [Fact]
    public async Task Caller_cancellation_answers_cancelled_without_recording_a_decline()
    {
        using var caller = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var questions = CreatePending(
            async (_, token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            },
            callerToken: caller.Token);

        var pending = questions.HandleAsync(Permission("ask-single"), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await caller.CancelAsync();
        var decision = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        decision.ShouldNotBeNull().IsCancelled.ShouldBeTrue();
        questions.HasDeclinedQuestion.ShouldBeFalse();
    }

    [Fact]
    public async Task Closing_the_client_answers_cancelled_without_recording_a_decline()
    {
        using var client = new CancellationTokenSource();
        var questions = CreatePending(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        });

        var pending = questions.HandleAsync(Permission("ask-single"), client.Token);
        await client.CancelAsync();
        var decision = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        decision.ShouldNotBeNull().IsCancelled.ShouldBeTrue();
        questions.HasDeclinedQuestion.ShouldBeFalse();
    }

    [Fact]
    public async Task A_tool_approval_is_left_to_the_default_answer_and_never_reaches_the_handler()
    {
        var calls = 0;
        var questions = CreatePending((_, _) =>
        {
            calls++;
            return Task.FromResult<ReasonixUserInputResponse?>(null);
        });
        var gate = AcpPermissionRequest.FromParameters(PermissionParameters(ReasonixTranscriptFixtures.Load("permission-turn")).First());

        (await questions.HandleAsync(gate, CancellationToken.None)).ShouldBeNull();
        calls.ShouldBe(0);
        questions.HasDeclinedQuestion.ShouldBeFalse();
    }

    [Fact]
    public async Task An_unreadable_question_is_declined_without_calling_the_handler()
    {
        var calls = 0;
        var questions = CreatePending((_, _) =>
        {
            calls++;
            return Task.FromResult<ReasonixUserInputResponse?>(null);
        });
        var parameters = PermissionNode("ask-single");
        parameters["options"] = new JsonArray(new JsonObject { ["optionId"] = "q1:cancel", ["name"] = "Cancel", ["kind"] = "reject_once" });

        var decision = await questions.HandleAsync(AcpPermissionRequest.FromParameters(ToElement(parameters)), CancellationToken.None);

        decision.ShouldNotBeNull().SelectedOptionId.ShouldBe("q1:cancel");
        calls.ShouldBe(0);
        questions.HasDeclinedQuestion.ShouldBeTrue();
    }

    // ------------------------------------------------------------ helpers

    private static ReasonixPendingQuestions CreatePending(
        Func<ReasonixUserInputRequest, CancellationToken, Task<ReasonixUserInputResponse?>> handler,
        TimeSpan? timeout = null,
        CancellationToken callerToken = default)
    {
        return new ReasonixPendingQuestions(handler, timeout ?? TimeSpan.FromMinutes(5), callerToken, NullLogger.Instance);
    }

    private static ReasonixUserInputResponse Answer(ReasonixUserInputRequest request, params string[] values)
    {
        return new ReasonixUserInputResponse(new Dictionary<string, IReadOnlyList<string>> { [request.Question.Question] = values });
    }

    private static ReasonixUserInputRequest ParseOnly(string fixture) => Parse(PermissionNode(fixture));

    private static ReasonixUserInputRequest Parse(JsonObject parameters)
    {
        ReasonixUserInputRequest.TryParse(AcpPermissionRequest.FromParameters(ToElement(parameters)), out var request).ShouldBeTrue();
        return request;
    }

    private static AcpPermissionRequest Permission(string fixture) => AcpPermissionRequest.FromParameters(ToElement(PermissionNode(fixture)));

    private static JsonObject WithRawOptions(JsonObject parameters, params (string Label, string Description)[] options)
    {
        var rawOptions = new JsonArray();
        var optionIds = new JsonArray();
        for (var index = 0; index < options.Length; index++)
        {
            rawOptions.Add(new JsonObject { ["Label"] = options[index].Label, ["Description"] = options[index].Description });
            optionIds.Add(new JsonObject { ["optionId"] = $"q1:{index + 1}", ["name"] = options[index].Label, ["kind"] = "allow_once" });
        }

        optionIds.Add(new JsonObject { ["optionId"] = "q1:cancel", ["name"] = "Cancel", ["kind"] = "reject_once" });
        parameters["toolCall"]!["rawInput"]!["options"] = rawOptions;
        parameters["options"] = optionIds;
        return parameters;
    }

    /// <summary>Returns a mutable copy of the first recorded question request of a fixture.</summary>
    private static JsonObject PermissionNode(string fixture)
    {
        return (JsonObject)ReasonixTranscriptFixtures.Load(fixture)
            .First(static record => record.Direction == "in" && record.Method == "session/request_permission")
            .Message["params"]!
            .DeepClone();
    }

    private static IEnumerable<JsonElement> PermissionParameters(IEnumerable<ReasonixTranscriptRecord> records)
    {
        return records
            .Where(static record => record.Direction == "in" && record.Method == "session/request_permission")
            .Select(static record => ToElement(record.Message["params"]!));
    }

    private static JsonElement ToElement(JsonNode node)
    {
        return JsonSerializer.Deserialize<JsonElement>(node.ToJsonString());
    }
}
