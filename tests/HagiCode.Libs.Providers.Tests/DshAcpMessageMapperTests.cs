using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Providers.Dsh;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class DshAcpMessageMapperTests
{
    [TestCase("agent_message_chunk", "assistant")]
    [TestCase("agent_thought_chunk", "assistant.thought")]
    [TestCase("tool_call", "tool.call")]
    [TestCase("tool_call_update", "tool.update")]
    [TestCase("plan_update", "session.update")]
    public void NormalizeNotification_maps_session_update_kinds(string sessionUpdate, string expectedType)
    {
        var messages = DshAcpMessageMapper.NormalizeNotification(SessionUpdate(new
        {
            sessionUpdate,
            content = new { type = "text", text = "chunk" }
        }));

        messages.Select(static message => message.Type).ShouldBe([expectedType]);
        messages[0].Content.GetProperty("session_id").GetString().ShouldBe("session-1");
    }

    [Test]
    public void NormalizeNotification_extracts_assistant_text_from_content_block()
    {
        var messages = DshAcpMessageMapper.NormalizeNotification(SessionUpdate(new
        {
            sessionUpdate = "agent_message_chunk",
            content = new { type = "text", text = "hello" }
        }));

        messages[0].Content.GetProperty("text").GetString().ShouldBe("hello");
    }

    [Test]
    public void NormalizeNotification_maps_error_update_to_terminal_failure()
    {
        var messages = DshAcpMessageMapper.NormalizeNotification(SessionUpdate(new
        {
            sessionUpdate = "error",
            message = "quota exceeded"
        }));

        messages.Select(static message => message.Type).ShouldBe(["terminal.failed"]);
        messages[0].Content.GetProperty("message").GetString().ShouldBe("quota exceeded");
    }

    [TestCase("end_turn", "terminal.completed")]
    [TestCase("cancelled", "terminal.failed")]
    public void NormalizeNotification_maps_prompt_completed_by_stop_reason(string stopReason, string expectedType)
    {
        var messages = DshAcpMessageMapper.NormalizeNotification(SessionUpdate(new
        {
            sessionUpdate = "prompt_completed",
            stopReason
        }));

        messages.Select(static message => message.Type).ShouldBe([expectedType]);
        messages[0].Content.GetProperty("stop_reason").GetString().ShouldBe(stopReason);
    }

    [Test]
    public void NormalizeNotification_wraps_other_methods_as_session_notifications()
    {
        var messages = DshAcpMessageMapper.NormalizeNotification(new AcpNotification(
            "session/request_permission",
            JsonSerializer.SerializeToElement(new { id = 7 })));

        messages.Select(static message => message.Type).ShouldBe(["session.notification"]);
        messages[0].Content.GetProperty("method").GetString().ShouldBe("session/request_permission");
    }

    [Test]
    public void CreateTerminalMessage_uses_stop_reason_and_result_text()
    {
        var promptResult = JsonSerializer.SerializeToElement(new { stopReason = "end_turn", text = "done" });

        DshAcpMessageMapper.IsSuccessfulStopReason(promptResult).ShouldBeTrue();
        var message = DshAcpMessageMapper.CreateTerminalMessage("session-1", promptResult);

        message.Type.ShouldBe("terminal.completed");
        message.Content.GetProperty("text").GetString().ShouldBe("done");
    }

    [Test]
    public void Result_helpers_tolerate_empty_elements()
    {
        DshAcpMessageMapper.IsSuccessfulStopReason(default).ShouldBeFalse();
        DshAcpMessageMapper.TryGetResultText(default, out var text).ShouldBeFalse();
        text.ShouldBeNull();
    }

    [TestCase(false, "session.started")]
    [TestCase(true, "session.resumed")]
    public void CreateSessionLifecycleMessage_distinguishes_new_and_resumed_sessions(bool isResumed, string expectedType)
    {
        var message = DshAcpMessageMapper.CreateSessionLifecycleMessage(new AcpSessionHandle("session-1", isResumed, default));

        message.Type.ShouldBe(expectedType);
        message.Content.GetProperty("session_id").GetString().ShouldBe("session-1");
    }

    private static AcpNotification SessionUpdate(object update)
    {
        return new AcpNotification("session/update", JsonSerializer.SerializeToElement(new { sessionId = "session-1", update }));
    }
}
