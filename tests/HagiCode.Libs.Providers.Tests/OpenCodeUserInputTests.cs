using System.Text.Json;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.OpenCode;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Unit contract of the OpenCode question mapping (<see cref="OpenCodeUserInput"/>): form detection, fields to questions by
/// position, answer encoding by field type, the question message, and the <see cref="OpenCodeOptions"/> question settings.
/// </summary>
public sealed class OpenCodeUserInputTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    [Test]
    public void Question_form_is_detected_and_other_kinds_are_not()
    {
        var questionForm = ReadFirstForm();
        var mcpForm = new OpenCodeFormInfo
        {
            Id = questionForm.Id,
            SessionId = questionForm.SessionId,
            Metadata = new OpenCodeFormMetadata { Kind = OpenCodeFormKinds.Mcp },
            Fields = questionForm.Fields,
        };
        var fieldless = new OpenCodeFormInfo
        {
            Id = questionForm.Id,
            SessionId = questionForm.SessionId,
            Metadata = questionForm.Metadata,
        };

        OpenCodeUserInput.TryCreateRequest(questionForm, out _).ShouldBeTrue();
        OpenCodeUserInput.TryCreateRequest(mcpForm, out _).ShouldBeFalse();
        OpenCodeUserInput.TryCreateRequest(fieldless, out _).ShouldBeFalse();
    }

    [Test]
    public void Fields_map_to_questions_by_position_with_header_description_options_and_multi_select()
    {
        var form = ReadFirstForm();

        OpenCodeUserInput.TryCreateRequest(form, out var request).ShouldBeTrue();

        request.RequestId.ShouldBe(form.Id);
        request.RequestId.ShouldStartWith("frm_");
        request.SessionId.ShouldBe("ses_eb3c4c8ebffeoPuTSmM55ew47v");
        request.ToolCallId.ShouldBe("call_97424");
        request.Questions.Count.ShouldBe(2);

        request.Questions[0].Question.ShouldBe("Which color do you prefer?");
        request.Questions[0].Header.ShouldBe("Color");
        request.Questions[0].MultiSelect.ShouldBeFalse();
        request.Questions[0].Options.ShouldBe([
            new OpenCodeUserQuestionOption("Red", "warm"),
            new OpenCodeUserQuestionOption("Blue", "cool"),
        ]);

        request.Questions[1].Question.ShouldBe("Which pets do you like?");
        request.Questions[1].Header.ShouldBe("Pets");
        request.Questions[1].MultiSelect.ShouldBeTrue();
        request.Questions[1].Options.Select(static option => option.Label).ShouldBe(["Cats", "Dogs"]);
    }

    [Test]
    public void Single_select_is_encoded_as_a_string_and_multi_select_as_a_list()
    {
        var request = CreateRequest(
            Question("Which color do you prefer?", multiSelect: false, "Red", "Blue"),
            Question("Which pets do you like?", multiSelect: true, "Cats", "Dogs", "Hamsters"));

        var answer = OpenCodeUserInput.CreateFormAnswer(
            request,
            Response(
                ("Which color do you prefer?", ["Red"]),
                ("Which pets do you like?", ["Cats", "Hamsters"])));

        Serialize(answer).ShouldBe("{\"q0\":\"Red\",\"q1\":[\"Cats\",\"Hamsters\"]}");
    }

    [Test]
    public void Free_text_is_passed_through_in_a_string_field_and_as_an_extra_item_in_a_list_field()
    {
        var request = CreateRequest(
            Question("Which color do you prefer?", multiSelect: false, "Red", "Blue"),
            Question("Which pets do you like?", multiSelect: true, "Cats", "Dogs"));

        var answer = OpenCodeUserInput.CreateFormAnswer(
            request,
            Response(
                ("Which color do you prefer?", ["Purple"]),
                ("Which pets do you like?", ["Cats", "Hamsters"])));

        Serialize(answer).ShouldBe("{\"q0\":\"Purple\",\"q1\":[\"Cats\",\"Hamsters\"]}");
    }

    [Test]
    public void Several_values_for_a_single_select_question_are_joined_with_comma_and_space()
    {
        var request = CreateRequest(Question("Which color do you prefer?", multiSelect: false, "Red", "Blue"));

        var answer = OpenCodeUserInput.CreateFormAnswer(request, Response(("Which color do you prefer?", ["Red", "Purple"])));

        Serialize(answer).ShouldBe("{\"q0\":\"Red, Purple\"}");
    }

    [Test]
    public void Several_questions_are_keyed_by_their_position_not_by_their_text()
    {
        var request = CreateRequest(
            Question("First?", multiSelect: false, "a"),
            Question("Second?", multiSelect: true, "b"),
            Question("Third?", multiSelect: false, "c"));

        var answer = OpenCodeUserInput.CreateFormAnswer(
            request,
            Response(("Third?", ["c"]), ("First?", ["a"]), ("Second?", ["b"])));

        Serialize(answer).ShouldBe("{\"q0\":\"a\",\"q1\":[\"b\"],\"q2\":\"c\"}");
    }

    [Test]
    public void Duplicate_question_texts_are_both_answered_from_the_shared_entry_at_their_own_positions()
    {
        var request = CreateRequest(
            Question("Pick one?", multiSelect: false, "A", "B"),
            Question("Pick one?", multiSelect: false, "A", "B"));

        request.Questions.Select(static question => question.Question).ShouldBe(["Pick one?", "Pick one?"]);

        var answer = OpenCodeUserInput.CreateFormAnswer(request, Response(("Pick one?", ["A"])));

        // Answers are keyed by text, so the two identical questions share one answer. Each still gets its own field.
        Serialize(answer).ShouldBe("{\"q0\":\"A\",\"q1\":\"A\"}");
    }

    [Test]
    public void Missing_and_empty_answers_are_omitted_so_the_question_stays_unanswered()
    {
        var request = CreateRequest(
            Question("Which color do you prefer?", multiSelect: false, "Red", "Blue"),
            Question("Which pets do you like?", multiSelect: true, "Cats", "Dogs"),
            Question("Anything else?", multiSelect: false));

        var answer = OpenCodeUserInput.CreateFormAnswer(
            request,
            Response(
                ("Which color do you prefer?", ["Red"]),
                ("Which pets do you like?", []),
                ("Anything else?", ["  "])));

        Serialize(answer).ShouldBe("{\"q0\":\"Red\"}");
    }

    [Test]
    public void Response_without_any_answer_produces_an_empty_answer()
    {
        var request = CreateRequest(Question("Which color do you prefer?", multiSelect: false, "Red"));

        var answer = OpenCodeUserInput.CreateFormAnswer(request, new OpenCodeUserInputResponse(new Dictionary<string, IReadOnlyList<string>>()));

        answer.ShouldBeEmpty();
    }

    [Test]
    public void Question_message_round_trips_the_request_through_the_stream_message()
    {
        var request = CreateRequest(
            Question("Which color do you prefer?", multiSelect: false, "Red", "Blue"),
            Question("Which pets do you like?", multiSelect: true, "Cats", "Dogs"));

        var message = OpenCodeMessageMapper.CreateQuestionMessage(request);

        message.Type.ShouldBe(OpenCodeUserInputRequest.MessageType);
        OpenCodeUserInputRequest.TryFromCliMessage(message, out var parsed).ShouldBeTrue();
        parsed.RequestId.ShouldBe(request.RequestId);
        parsed.SessionId.ShouldBe(request.SessionId);
        parsed.ToolCallId.ShouldBe(request.ToolCallId);
        parsed.Questions.Count.ShouldBe(request.Questions.Count);
        for (var index = 0; index < request.Questions.Count; index++)
        {
            parsed.Questions[index].Question.ShouldBe(request.Questions[index].Question);
            parsed.Questions[index].Header.ShouldBe(request.Questions[index].Header);
            parsed.Questions[index].MultiSelect.ShouldBe(request.Questions[index].MultiSelect);
            parsed.Questions[index].Options.Select(static option => option.Label)
                .ShouldBe(request.Questions[index].Options.Select(static option => option.Label));
        }
    }

    [Test]
    public void Other_messages_are_not_question_messages()
    {
        var message = new CliMessage("assistant", JsonSerializer.SerializeToElement(new { text = "hi" }));

        OpenCodeUserInputRequest.TryFromCliMessage(message, out _).ShouldBeFalse();
    }

    [Test]
    public void Question_timeout_must_be_positive_or_infinite()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => OpenCodeUserInput.ValidateQuestionTimeout(TimeSpan.Zero, "QuestionTimeout"));
        Should.Throw<ArgumentOutOfRangeException>(() => OpenCodeUserInput.ValidateQuestionTimeout(TimeSpan.FromSeconds(-1), "QuestionTimeout"));

        Should.NotThrow(() => OpenCodeUserInput.ValidateQuestionTimeout(TimeSpan.FromMilliseconds(1), "QuestionTimeout"));
        Should.NotThrow(() => OpenCodeUserInput.ValidateQuestionTimeout(Timeout.InfiniteTimeSpan, "QuestionTimeout"));
    }

    [Test]
    public void Options_default_the_question_timeout_to_thirty_minutes_and_no_handler()
    {
        var options = new OpenCodeOptions();

        options.QuestionTimeout.ShouldBe(TimeSpan.FromMinutes(30));
        OpenCodeOptions.DefaultQuestionTimeout.ShouldBe(TimeSpan.FromMinutes(30));
        options.UserInputHandler.ShouldBeNull();
    }

    [Test]
    public void Options_text_reports_the_handler_without_exposing_the_password()
    {
        var options = new OpenCodeOptions
        {
            ServerPassword = "opencode-super-secret",
            UserInputHandler = static (_, _) => Task.FromResult<OpenCodeUserInputResponse?>(null),
            QuestionTimeout = TimeSpan.FromMinutes(5),
        };

        var text = options.ToString();

        text.ShouldNotContain("opencode-super-secret");
        text.ShouldContain("UserInputHandler = set");
        text.ShouldContain("QuestionTimeout = 00:05:00");
    }

    [Test]
    public async Task Provider_rejects_an_invalid_question_timeout_before_any_request_is_sent()
    {
        await using var provider = new OpenCodeProvider(new CliExecutableResolver());
        var options = new OpenCodeOptions
        {
            // Nothing listens on this port: a request would fail with a connection error instead of the argument error.
            BaseUrl = "http://127.0.0.1:1/",
            ServerPassword = "unused",
            QuestionTimeout = TimeSpan.Zero,
            UserInputHandler = static (_, _) => Task.FromResult<OpenCodeUserInputResponse?>(null),
        };

        var exception = await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (var _ in provider.ExecuteAsync(options, "hello"))
            {
            }
        });

        exception.ParamName.ShouldBe("QuestionTimeout");
    }

    private static OpenCodeFormInfo ReadFirstForm()
    {
        var root = JsonSerializer.Deserialize<OpenCodeDataResponse<List<OpenCodeFormInfo>>>(ReadSample("form-created-list.json"), JsonOptions);
        return root!.Data!.Single();
    }

    private static string ReadSample(string name)
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "OpenCode", "v2.0.32", name));
    }

    private static OpenCodeUserInputRequest CreateRequest(params OpenCodeUserQuestion[] questions)
    {
        return new OpenCodeUserInputRequest("frm_test", "ses_test", "call_test", questions);
    }

    private static OpenCodeUserQuestion Question(string text, bool multiSelect, params string[] labels)
    {
        return new OpenCodeUserQuestion(
            text,
            "Header",
            multiSelect,
            labels.Select(static label => new OpenCodeUserQuestionOption(label, string.Empty)).ToArray());
    }

    private static OpenCodeUserInputResponse Response(params (string Question, string[] Answers)[] answers)
    {
        return new OpenCodeUserInputResponse(answers.ToDictionary(
            static pair => pair.Question,
            static pair => (IReadOnlyList<string>)pair.Answers,
            StringComparer.Ordinal));
    }

    private static string Serialize(IReadOnlyDictionary<string, object> answer)
    {
        return JsonSerializer.Serialize(answer);
    }
}
